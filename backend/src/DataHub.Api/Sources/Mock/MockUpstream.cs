using System.Threading.Channels;
using DataHub.Api.Configuration;
using DataHub.Api.Domain;

namespace DataHub.Api.Sources.Mock;

public sealed class MockSourceUnavailableException(string message) : Exception(message);

/// <summary>
/// 進程內模擬嘅「遠端來源」：自己有一套真實狀態（instrument、source revision），
/// 即使 Hub 斷線都會繼續變動，所以 Hub 恢復連線後會見到真正嘅資料空檔，需要靠 resync 補返。
/// 所有資料都係隨機模擬，唔連任何外部服務，亦無任何 credentials。
/// </summary>
public sealed class MockUpstream
{
    private const int SubscriberQueueCapacity = 1024;

    private readonly object _gate = new();
    private readonly MockSourceOptions _options;
    private readonly TimeProvider _time;
    private readonly Random _random;
    private readonly Dictionary<string, Instrument> _instruments = new(StringComparer.Ordinal);
    private readonly List<string> _ids = [];
    private readonly Dictionary<string, int> _positions = new(StringComparer.Ordinal);
    private readonly List<MockSubscription> _subscribers = [];
    private readonly decimal _minTick;
    private long _revision;
    private int _nextInstrumentNumber;
    private DateTimeOffset? _outageUntilUtc;

    public MockUpstream(MockSourceOptions options, TimeProvider time)
    {
        _options = options;
        _time = time;
        _random = new Random(options.Seed);
        _minTick = decimal.One / (decimal)Math.Pow(10, options.PriceDecimals);

        var now = time.GetUtcNow();
        _revision = 1;
        for (var i = 1; i <= options.InstrumentCount; i++)
        {
            var price = decimal.Round((decimal)(10 + _random.NextDouble() * 490), options.PriceDecimals);
            Add(new Instrument($"MOCK{i:D5}", Math.Max(price, _minTick), now, _revision));
        }

        _nextInstrumentNumber = options.InstrumentCount;
    }

    public string SourceId => _options.SourceId;

    public TimeSpan TickInterval => TimeSpan.FromMilliseconds(_options.UpdateIntervalMs);

    public long Revision
    {
        get { lock (_gate) { return _revision; } }
    }

    public int InstrumentCount
    {
        get { lock (_gate) { return _ids.Count; } }
    }

    public bool IsInOutage
    {
        get { lock (_gate) { return IsOutageActive(); } }
    }

    /// <summary>推進一個模擬 tick：價格變動，或者（細機率）新上市／下市。回傳今次發佈到 stream 嘅 updates。</summary>
    public IReadOnlyList<PriceUpdate> Tick()
    {
        lock (_gate)
        {
            var now = _time.GetUtcNow();
            var batch = new List<PriceUpdate>(_options.UpdatesPerTick);

            for (var i = 0; i < _options.UpdatesPerTick; i++)
            {
                if (_options.MembershipChangeProbability > 0
                    && _random.NextDouble() < _options.MembershipChangeProbability)
                {
                    ChangeMembership(now, batch);
                    continue;
                }

                if (_ids.Count == 0)
                {
                    continue;
                }

                var instrument = _instruments[_ids[_random.Next(_ids.Count)]];
                instrument.Price = NextPrice(instrument.Price);
                instrument.UpdatedAtUtc = now;
                instrument.Revision = ++_revision;
                batch.Add(ToUpdate(instrument));
            }

            if (batch.Count > 0)
            {
                Publish(batch);
            }

            return batch;
        }
    }

    public async Task<SourceSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
    {
        SourceSnapshot cut;
        lock (_gate)
        {
            ThrowIfOutage();
            var records = new PriceUpdate[_ids.Count];
            for (var i = 0; i < records.Length; i++)
            {
                records[i] = ToUpdate(_instruments[_ids[i]]);
            }

            cut = new SourceSnapshot(_revision, _time.GetUtcNow(), records);
        }

        // 先切 snapshot、後模擬傳輸時間：傳輸期間 upstream 繼續變動，重現 snapshot 同 update 嘅競爭情況。
        if (_options.SnapshotLatencyMs > 0)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(_options.SnapshotLatencyMs), _time, cancellationToken);
        }

        lock (_gate)
        {
            ThrowIfOutage();
        }

        return cut;
    }

    public PollResult Poll(long afterRevision)
    {
        lock (_gate)
        {
            ThrowIfOutage();
            var updates = new List<PriceUpdate>();
            foreach (var instrument in _instruments.Values)
            {
                if (instrument.Revision > afterRevision)
                {
                    updates.Add(ToUpdate(instrument));
                }
            }

            return new PollResult(_revision, updates);
        }
    }

    public MockSubscription Subscribe()
    {
        lock (_gate)
        {
            ThrowIfOutage();
            var channel = Channel.CreateBounded<IReadOnlyList<PriceUpdate>>(new BoundedChannelOptions(SubscriberQueueCapacity)
            {
                SingleReader = true,
                SingleWriter = true,
                FullMode = BoundedChannelFullMode.Wait,
            });
            var subscription = new MockSubscription(this, channel);
            _subscribers.Add(subscription);
            return subscription;
        }
    }

    /// <summary>
    /// 模擬來源中斷：所有現有訂閱即時失敗，期間 snapshot / poll / subscribe 都會失敗。
    /// duration 為 0 或以下代表立即結束中斷。Upstream 自身狀態喺中斷期間照常變動。
    /// </summary>
    public DateTimeOffset? SimulateOutage(TimeSpan duration)
    {
        lock (_gate)
        {
            if (duration <= TimeSpan.Zero)
            {
                _outageUntilUtc = null;
                return null;
            }

            _outageUntilUtc = _time.GetUtcNow() + duration;
            foreach (var subscriber in _subscribers)
            {
                subscriber.Fault(new MockSourceUnavailableException("Simulated outage: connection dropped."));
            }

            _subscribers.Clear();
            return _outageUntilUtc;
        }
    }

    internal void Unsubscribe(MockSubscription subscription)
    {
        lock (_gate)
        {
            _subscribers.Remove(subscription);
        }
    }

    private void ChangeMembership(DateTimeOffset now, List<PriceUpdate> batch)
    {
        if (_ids.Count == 0 || _random.Next(2) == 0)
        {
            // 新上市：會經 stream 通知。
            var price = decimal.Round((decimal)(10 + _random.NextDouble() * 490), _options.PriceDecimals);
            var instrument = new Instrument($"MOCK{++_nextInstrumentNumber:D5}", Math.Max(price, _minTick), now, ++_revision);
            Add(instrument);
            batch.Add(ToUpdate(instrument));
            return;
        }

        // 下市：stream 唔會通知，Hub 只可以喺下一次完整 snapshot 先知道。
        RemoveAt(_random.Next(_ids.Count));
        _revision++;
    }

    private decimal NextPrice(decimal current)
    {
        var move = (decimal)((_random.NextDouble() * 2 - 1) * _options.MaxMovePercent / 100.0);
        var next = decimal.Round(current * (1 + move), _options.PriceDecimals, MidpointRounding.AwayFromZero);
        if (next == current)
        {
            next = current + (_random.Next(2) == 0 ? _minTick : -_minTick);
        }

        return Math.Max(next, _minTick);
    }

    private void Publish(List<PriceUpdate> batch)
    {
        // 喺 _gate 內發佈，所以每個訂閱收到嘅 batch 次序同 revision 次序一致。
        for (var i = _subscribers.Count - 1; i >= 0; i--)
        {
            if (!_subscribers[i].TryPublish(batch))
            {
                _subscribers[i].Fault(new MockSourceUnavailableException("Subscriber too slow: update queue overflow."));
                _subscribers.RemoveAt(i);
            }
        }
    }

    private bool IsOutageActive() => _outageUntilUtc is { } until && _time.GetUtcNow() < until;

    private void ThrowIfOutage()
    {
        if (IsOutageActive())
        {
            throw new MockSourceUnavailableException($"Simulated outage active until {_outageUntilUtc:O}.");
        }
    }

    private void Add(Instrument instrument)
    {
        _instruments[instrument.Id] = instrument;
        _positions[instrument.Id] = _ids.Count;
        _ids.Add(instrument.Id);
    }

    private void RemoveAt(int index)
    {
        var id = _ids[index];
        var last = _ids.Count - 1;
        if (index != last)
        {
            _ids[index] = _ids[last];
            _positions[_ids[index]] = index;
        }

        _ids.RemoveAt(last);
        _positions.Remove(id);
        _instruments.Remove(id);
    }

    private PriceUpdate ToUpdate(Instrument instrument)
        => new(instrument.Id, instrument.Price, _options.Currency, instrument.UpdatedAtUtc, instrument.Revision);

    private sealed class Instrument(string id, decimal price, DateTimeOffset updatedAtUtc, long revision)
    {
        public string Id { get; } = id;
        public decimal Price { get; set; } = price;
        public DateTimeOffset UpdatedAtUtc { get; set; } = updatedAtUtc;
        public long Revision { get; set; } = revision;
    }
}

public sealed class MockSubscription : IUpdateSubscription
{
    private readonly MockUpstream _upstream;
    private readonly Channel<IReadOnlyList<PriceUpdate>> _channel;

    internal MockSubscription(MockUpstream upstream, Channel<IReadOnlyList<PriceUpdate>> channel)
    {
        _upstream = upstream;
        _channel = channel;
    }

    public IAsyncEnumerable<IReadOnlyList<PriceUpdate>> ReadUpdatesAsync(CancellationToken cancellationToken)
        => _channel.Reader.ReadAllAsync(cancellationToken);

    public ValueTask DisposeAsync()
    {
        _upstream.Unsubscribe(this);
        _channel.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }

    internal bool TryPublish(IReadOnlyList<PriceUpdate> batch) => _channel.Writer.TryWrite(batch);

    internal void Fault(Exception error) => _channel.Writer.TryComplete(error);
}
