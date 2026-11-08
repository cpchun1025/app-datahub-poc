using System.Threading.Channels;
using DataHub.Api.Domain;
using DataHub.Api.Sources;

namespace DataHub.Api.Tests.Support;

internal sealed class FakeSourceException(string message) : Exception(message);

/// <summary>
/// 完全由測試控制嘅「遠端來源」狀態：冇隨機、冇計時器。revision 係 source-local 單調遞增，同真實來源一樣。
/// </summary>
internal sealed class FakeUpstream(string currency = "USD")
{
    private readonly object _gate = new();
    private readonly Dictionary<string, PriceUpdate> _records = new(StringComparer.Ordinal);
    private long _revision;

    public long Revision
    {
        get
        {
            lock (_gate)
            {
                return _revision;
            }
        }
    }

    public PriceUpdate Set(string instrumentId, decimal price)
    {
        lock (_gate)
        {
            _revision++;
            var update = new PriceUpdate(instrumentId, price, currency, TestData.T0.AddSeconds(_revision), _revision);
            _records[instrumentId] = update;
            return update;
        }
    }

    /// <summary>下市：revision 照樣前進，但冇任何訊息；只有完整 snapshot 先睇得到。</summary>
    public void Remove(string instrumentId)
    {
        lock (_gate)
        {
            _records.Remove(instrumentId);
            _revision++;
        }
    }

    public SourceSnapshot Cut()
    {
        lock (_gate)
        {
            return new SourceSnapshot(
                _revision,
                TestData.T0.AddSeconds(_revision),
                _records.Values.OrderBy(record => record.InstrumentId, StringComparer.Ordinal).ToArray());
        }
    }

    public PollResult Delta(long afterRevision)
    {
        lock (_gate)
        {
            return new PollResult(_revision, _records.Values.Where(record => record.Revision > afterRevision).ToArray());
        }
    }
}

internal abstract class FakeSourceAdapter : ISourceAdapter
{
    private TaskCompletionSource? _snapshotGate;
    private int _snapshotCalls;
    private int _gatedCalls;
    private volatile bool _down;

    protected FakeSourceAdapter(string sourceId, SourceKind kind, FakeUpstream? upstream, int priceDecimals)
    {
        Upstream = upstream ?? new FakeUpstream();
        Descriptor = new SourceDescriptor(sourceId, $"Fake {sourceId}", kind, IsMock: false, priceDecimals, "Scripted test source.");
    }

    public FakeUpstream Upstream { get; }

    public SourceDescriptor Descriptor { get; }

    public int SnapshotCalls => Volatile.Read(ref _snapshotCalls);

    /// <summary>已經切好 snapshot 並停喺 gate 嘅次數；測試用佢確定「切點已經定咗」先去改 upstream。</summary>
    public int GatedSnapshotCalls => Volatile.Read(ref _gatedCalls);

    /// <summary>true 時 snapshot / poll / subscribe 全部失敗。</summary>
    public bool Down
    {
        get => _down;
        set => _down = value;
    }

    /// <summary>之後開始嘅 snapshot 會喺「切完 snapshot」之後停低，直到 <see cref="ReleaseSnapshots"/>；期間 upstream 可以繼續變動。</summary>
    public void HoldSnapshots()
        => Volatile.Write(ref _snapshotGate, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));

    public void ReleaseSnapshots()
        => Interlocked.Exchange(ref _snapshotGate, null)?.TrySetResult();

    public async Task<SourceSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _snapshotCalls);
        ThrowIfDown();
        var cut = Upstream.Cut();

        if (Volatile.Read(ref _snapshotGate) is { } gate)
        {
            Interlocked.Increment(ref _gatedCalls);
            await gate.Task.WaitAsync(cancellationToken);
        }

        ThrowIfDown();
        return cut;
    }

    protected void ThrowIfDown()
    {
        if (_down)
        {
            throw new FakeSourceException($"{Descriptor.SourceId} is down.");
        }
    }
}

internal sealed class FakeStreamingAdapter : FakeSourceAdapter, IStreamingSourceAdapter
{
    private readonly List<Subscription> _subscriptions = [];
    private int _subscribeCalls;
    private volatile bool _feedDown;

    public FakeStreamingAdapter(string sourceId, FakeUpstream? upstream = null, int priceDecimals = 2)
        : base(sourceId, SourceKind.Streaming, upstream, priceDecimals)
    {
    }

    public int SubscribeCalls => Volatile.Read(ref _subscribeCalls);

    /// <summary>true 時只有 stream 連唔到，snapshot 照常可用。</summary>
    public bool FeedDown
    {
        get => _feedDown;
        set => _feedDown = value;
    }

    public int ActiveSubscriptions
    {
        get
        {
            lock (_subscriptions)
            {
                return _subscriptions.Count;
            }
        }
    }

    public ValueTask<IUpdateSubscription> SubscribeAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _subscribeCalls);
        ThrowIfDown();
        if (_feedDown)
        {
            throw new FakeSourceException($"{Descriptor.SourceId} stream is unavailable.");
        }

        var subscription = new Subscription(this);
        lock (_subscriptions)
        {
            _subscriptions.Add(subscription);
        }

        return ValueTask.FromResult<IUpdateSubscription>(subscription);
    }

    /// <summary>來源有變動，而且經 stream 發佈出去。</summary>
    public PriceUpdate Emit(string instrumentId, decimal price)
    {
        var update = Upstream.Set(instrumentId, price);
        Push([update]);
        return update;
    }

    /// <summary>來源有變動，但 stream 冇收到（模擬斷線期間漏咗嘅 update）。</summary>
    public PriceUpdate ChangeSilently(string instrumentId, decimal price) => Upstream.Set(instrumentId, price);

    /// <summary>直接發佈指定 update，唔經 upstream（模擬亂序、重複或遲到嘅訊息）。</summary>
    public void PushRaw(params PriceUpdate[] updates) => Push(updates);

    public void Heartbeat() => Push([]);

    public void DropConnection(string reason = "connection dropped")
    {
        Subscription[] dropped;
        lock (_subscriptions)
        {
            dropped = [.. _subscriptions];
            _subscriptions.Clear();
        }

        foreach (var subscription in dropped)
        {
            subscription.Channel.Writer.TryComplete(new FakeSourceException(reason));
        }
    }

    private void Push(IReadOnlyList<PriceUpdate> batch)
    {
        Subscription[] targets;
        lock (_subscriptions)
        {
            targets = [.. _subscriptions];
        }

        foreach (var subscription in targets)
        {
            subscription.Channel.Writer.TryWrite(batch);
        }
    }

    private void Remove(Subscription subscription)
    {
        lock (_subscriptions)
        {
            _subscriptions.Remove(subscription);
        }
    }

    private sealed class Subscription(FakeStreamingAdapter owner) : IUpdateSubscription
    {
        public Channel<IReadOnlyList<PriceUpdate>> Channel { get; } = System.Threading.Channels.Channel.CreateUnbounded<IReadOnlyList<PriceUpdate>>();

        public IAsyncEnumerable<IReadOnlyList<PriceUpdate>> ReadUpdatesAsync(CancellationToken cancellationToken)
            => Channel.Reader.ReadAllAsync(cancellationToken);

        public ValueTask DisposeAsync()
        {
            owner.Remove(this);
            Channel.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }
}

internal sealed class FakePollingAdapter : FakeSourceAdapter, IPollingSourceAdapter
{
    private readonly List<long> _cursors = [];

    public FakePollingAdapter(string sourceId, TimeSpan pollInterval, FakeUpstream? upstream = null, int priceDecimals = 2)
        : base(sourceId, SourceKind.Polling, upstream, priceDecimals)
    {
        PollInterval = pollInterval;
    }

    public TimeSpan PollInterval { get; }

    public IReadOnlyList<long> PollCursors
    {
        get
        {
            lock (_cursors)
            {
                return [.. _cursors];
            }
        }
    }

    public Task<PollResult> PollAsync(long afterRevision, CancellationToken cancellationToken)
    {
        lock (_cursors)
        {
            _cursors.Add(afterRevision);
        }

        ThrowIfDown();
        return Task.FromResult(Upstream.Delta(afterRevision));
    }
}

internal sealed class FakeCatalog(params ISourceAdapter[] adapters) : ISourceCatalog
{
    public IReadOnlyList<ISourceAdapter> Adapters { get; } = adapters;
}
