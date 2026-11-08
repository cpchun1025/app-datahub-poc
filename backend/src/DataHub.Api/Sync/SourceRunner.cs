using DataHub.Api.Configuration;
using DataHub.Api.Domain;
using DataHub.Api.Sources;
using DataHub.Api.Store;

namespace DataHub.Api.Sync;

public enum SnapshotTrigger
{
    Scheduled,
    Manual,
}

public enum SnapshotRefreshOutcome
{
    Succeeded,
    AlreadyRunning,
    Failed,
}

public sealed record SnapshotRefreshResult(
    SnapshotRefreshOutcome Outcome,
    string SourceId,
    SnapshotApplyResult? Applied = null,
    string? Error = null);

/// <summary>
/// 一個來源嘅同步引擎：負責 snapshot、streaming／polling、重連同 resync。每個來源獨立運作，
/// 一個來源失敗只會影響自己嘅 status，唔會拖慢或者拖垮其他來源。
/// <para>
/// 排序保證（見 technical-spec）：
/// (1) 先訂閱 stream，再攞第一個 snapshot；第一個 snapshot 完成之前，stream update 按 instrument 合併暫存（staging）；
/// (2) 第一個 snapshot 以 Replace 套用（舊 checkpoint 嘅 revision 屬於上一個 session，唔可以同新 session 比較），之後先排乾暫存；
/// (3) Feed 每次重新連線都視為可能漏 update，會即刻再攞一個 snapshot（Merge），期間 status 係 Recovering；
/// (4) 所有套用都用 source revision 比較，所以 snapshot 等待期間先到嘅新 update 唔會被舊 snapshot 蓋走。
/// </para>
/// </summary>
public sealed class SourceRunner
{
    private static readonly TimeSpan DefaultSchedulerTick = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan FirstFeedGrace = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan LoopRestartDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MinPollInterval = TimeSpan.FromMilliseconds(10);

    private readonly ISourceAdapter _adapter;
    private readonly ILatestStateStore _store;
    private readonly DataHubOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly TimeSpan _schedulerTick;
    private readonly SemaphoreSlim _snapshotGate = new(1, 1);
    private readonly object _stageLock = new();
    private readonly TaskCompletionSource _firstFeedAttempt = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _initialSnapshotApplied = new(TaskCreationOptions.RunContinuationsAsynchronously);

    // 非 null = 第一個 snapshot 未套用，stream update 暫存喺度（每個 instrument 只保留 revision 最高嗰個，所以大小受 universe 限制）。
    private Dictionary<string, PriceUpdate>? _staged;
    private bool _initialSnapshotDone;
    private long _feedGeneration;
    private long _pollCursor;

    public SourceRunner(
        ISourceAdapter adapter,
        ILatestStateStore store,
        DataHubOptions options,
        TimeProvider time,
        ILogger logger,
        TimeSpan? schedulerTick = null)
    {
        _adapter = adapter;
        _store = store;
        _options = options;
        _time = time;
        _logger = logger;
        _schedulerTick = schedulerTick ?? DefaultSchedulerTick;
        _staged = adapter is IStreamingSourceAdapter ? new Dictionary<string, PriceUpdate>(StringComparer.Ordinal) : null;
        StaleAfter = ComputeStaleAfter();
    }

    public ISourceAdapter Adapter => _adapter;

    public SourceDescriptor Descriptor => _adapter.Descriptor;

    public string SourceId => _adapter.Descriptor.SourceId;

    public SourceRuntime Runtime { get; } = new();

    /// <summary>實際生效嘅 stale 門檻（0 = 停用）。Polling 來源至少係 3 倍 poll 間隔，避免兩次 poll 之間誤報 Stale。</summary>
    public TimeSpan StaleAfter { get; }

    public void MarkRestoredFromCheckpoint()
        => Runtime.Update(state => state with { RestoredFromCheckpoint = true });

    public SourceStatus GetStatus(DateTimeOffset nowUtc)
        => SourceStatusEvaluator.Evaluate(Runtime.State, nowUtc, StaleAfter);

    /// <summary>啟動所有同步 loop，直到 <paramref name="cancellationToken"/> 取消先完成。</summary>
    public Task RunAsync(CancellationToken cancellationToken)
    {
        var loops = new List<Task> { Supervise("snapshot", RunSnapshotLoopAsync, cancellationToken) };
        switch (_adapter)
        {
            case IStreamingSourceAdapter streaming:
                loops.Add(Supervise("stream", ct => RunStreamingAsync(streaming, ct), cancellationToken));
                break;
            case IPollingSourceAdapter polling:
                loops.Add(Supervise("poll", ct => RunPollingAsync(polling, ct), cancellationToken));
                break;
        }

        return Task.WhenAll(loops);
    }

    /// <summary>
    /// 攞一次完整 snapshot 並套用。排程同手動共用呢個入口，由 per-source gate 保證同一時間只有一個 snapshot 喺行。
    /// </summary>
    public async Task<SnapshotRefreshResult> RefreshSnapshotAsync(SnapshotTrigger trigger, CancellationToken cancellationToken)
    {
        if (!await _snapshotGate.WaitAsync(0, cancellationToken))
        {
            return new SnapshotRefreshResult(SnapshotRefreshOutcome.AlreadyRunning, SourceId);
        }

        var isStreaming = _adapter is IStreamingSourceAdapter;
        var isPolling = _adapter is IPollingSourceAdapter;
        var generationAtStart = Interlocked.Read(ref _feedGeneration);
        var startedAt = _time.GetUtcNow();
        Runtime.Update(state => state with { SnapshotInProgress = true, LastSnapshotAttemptAtUtc = startedAt });

        try
        {
            var snapshot = await _adapter.GetSnapshotAsync(cancellationToken);
            var appliedAt = _time.GetUtcNow();
            var applied = ApplySnapshot(snapshot, appliedAt);
            if (isPolling)
            {
                AdvancePollCursor(snapshot.Revision);
            }

            Runtime.Update(state =>
            {
                // Snapshot 進行期間 feed 又重新連線過：呢個 snapshot 未必涵蓋新一輪空檔，要再攞一次。
                var resyncStillPending = isStreaming && generationAtStart < Interlocked.Read(ref _feedGeneration);
                return state with
                {
                    SyncedSinceStart = true,
                    SnapshotInProgress = false,
                    ResyncPending = resyncStillPending,
                    FeedConnected = isPolling || state.FeedConnected,
                    LastFeedActivityAtUtc = isPolling ? appliedAt : state.LastFeedActivityAtUtc,
                    ConsecutiveSnapshotFailures = 0,
                    LastSnapshotError = null,
                    LastSnapshotErrorAtUtc = null,
                    NextSnapshotDueAtUtc = resyncStillPending ? appliedAt : appliedAt + _options.Snapshot.Interval,
                };
            });

            _logger.LogInformation(
                "{SourceId}: snapshot applied ({Trigger}) revision={Revision} added={Added} updated={Updated} removed={Removed} keptNewer={KeptNewer} total={Total}{Skipped}",
                SourceId, trigger, snapshot.Revision, applied.Added, applied.Updated, applied.Removed, applied.KeptNewer, applied.TotalRecords,
                applied.Skipped ? " (skipped: older than current floor)" : string.Empty);

            return new SnapshotRefreshResult(SnapshotRefreshOutcome.Succeeded, SourceId, applied);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Runtime.Update(state => state with { SnapshotInProgress = false });
            throw;
        }
        catch (Exception ex)
        {
            var failedAt = _time.GetUtcNow();
            var failures = Runtime.Update(state => state with
            {
                SnapshotInProgress = false,
                ConsecutiveSnapshotFailures = state.ConsecutiveSnapshotFailures + 1,
                LastSnapshotError = ex.Message,
                LastSnapshotErrorAtUtc = failedAt,
            }).ConsecutiveSnapshotFailures;

            var retryAt = failedAt + Backoff(failures);
            Runtime.Update(state => state with { NextSnapshotDueAtUtc = retryAt });

            _logger.LogWarning(ex, "{SourceId}: snapshot failed ({Trigger}, attempt {Attempt}); retry at {RetryAt:O}.", SourceId, trigger, failures, retryAt);
            return new SnapshotRefreshResult(SnapshotRefreshOutcome.Failed, SourceId, Error: ex.Message);
        }
        finally
        {
            _snapshotGate.Release();
        }
    }

    private async Task RunSnapshotLoopAsync(CancellationToken cancellationToken)
    {
        if (_adapter is IStreamingSourceAdapter)
        {
            // 等 stream 嘗試連線（成功或失敗都得，最多等 FirstFeedGrace）先攞第一個 snapshot：訂閱起點要早過 snapshot 切點。
            using var grace = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var delay = Task.Delay(FirstFeedGrace, _time, grace.Token);
            await Task.WhenAny(_firstFeedAttempt.Task, delay);
            await grace.CancelAsync();
            cancellationToken.ThrowIfCancellationRequested();
        }

        using var timer = new PeriodicTimer(_schedulerTick, _time);
        do
        {
            var due = Runtime.State.NextSnapshotDueAtUtc;
            if (due is null || _time.GetUtcNow() >= due)
            {
                await RefreshSnapshotAsync(SnapshotTrigger.Scheduled, cancellationToken);
            }
        }
        while (await timer.WaitForNextTickAsync(cancellationToken));
    }

    private async Task RunStreamingAsync(IStreamingSourceAdapter streaming, CancellationToken cancellationToken)
    {
        var failures = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await using var subscription = await streaming.SubscribeAsync(cancellationToken);

                var connectedAt = _time.GetUtcNow();
                Interlocked.Increment(ref _feedGeneration);
                failures = 0;
                Runtime.Update(state => state with
                {
                    FeedConnected = true,
                    ResyncPending = true,
                    LastFeedActivityAtUtc = connectedAt,
                    LastFeedError = null,
                    LastFeedErrorAtUtc = null,
                    ConsecutiveFeedFailures = 0,
                    NextSnapshotDueAtUtc = connectedAt,
                });
                _firstFeedAttempt.TrySetResult();
                _logger.LogInformation("{SourceId}: stream connected; resync requested.", SourceId);

                await foreach (var batch in subscription.ReadUpdatesAsync(cancellationToken))
                {
                    ApplyLive(batch);
                    var receivedAt = _time.GetUtcNow();
                    Runtime.Update(state => state with { LastFeedActivityAtUtc = receivedAt });
                }

                throw new IOException("Update stream was closed by the source.");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                failures++;
                var failedAt = _time.GetUtcNow();
                Runtime.Update(state => state with
                {
                    FeedConnected = false,
                    LastFeedError = ex.Message,
                    LastFeedErrorAtUtc = failedAt,
                    ConsecutiveFeedFailures = state.ConsecutiveFeedFailures + 1,
                });
                _firstFeedAttempt.TrySetResult();

                var delay = Backoff(failures);
                _logger.LogWarning("{SourceId}: stream unavailable ({Error}); reconnecting in {Delay}.", SourceId, ex.Message, delay);
                if (!await TryDelayAsync(delay, cancellationToken))
                {
                    return;
                }
            }
        }
    }

    private async Task RunPollingAsync(IPollingSourceAdapter polling, CancellationToken cancellationToken)
    {
        // Polling cursor 由第一個 snapshot 嘅 revision 開始，之後每次 poll 只攞增量。
        await _initialSnapshotApplied.Task.WaitAsync(cancellationToken);

        var failures = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var wait = failures == 0 ? polling.PollInterval : Backoff(failures);
                await Task.Delay(wait < MinPollInterval ? MinPollInterval : wait, _time, cancellationToken);

                var result = await polling.PollAsync(Interlocked.Read(ref _pollCursor), cancellationToken);
                var receivedAt = _time.GetUtcNow();
                if (result.Updates.Count > 0)
                {
                    _store.ApplyUpdates(SourceId, result.Updates, receivedAt);
                }

                AdvancePollCursor(result.Revision);
                failures = 0;
                Runtime.Update(state => state with
                {
                    FeedConnected = true,
                    LastFeedActivityAtUtc = receivedAt,
                    LastFeedError = null,
                    LastFeedErrorAtUtc = null,
                    ConsecutiveFeedFailures = 0,
                });
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                failures++;
                var failedAt = _time.GetUtcNow();
                Runtime.Update(state => state with
                {
                    FeedConnected = false,
                    LastFeedError = ex.Message,
                    LastFeedErrorAtUtc = failedAt,
                    ConsecutiveFeedFailures = state.ConsecutiveFeedFailures + 1,
                });
                _logger.LogWarning("{SourceId}: poll failed ({Error}); retrying in {Delay}.", SourceId, ex.Message, Backoff(failures));
            }
        }
    }

    private void ApplyLive(IReadOnlyList<PriceUpdate> batch)
    {
        if (batch.Count == 0)
        {
            return;
        }

        if (Volatile.Read(ref _staged) is not null)
        {
            lock (_stageLock)
            {
                if (_staged is not null)
                {
                    foreach (var update in batch)
                    {
                        if (string.IsNullOrWhiteSpace(update.InstrumentId))
                        {
                            continue;
                        }

                        if (!_staged.TryGetValue(update.InstrumentId, out var existing) || update.Revision > existing.Revision)
                        {
                            _staged[update.InstrumentId] = update;
                        }
                    }

                    return;
                }
            }
        }

        _store.ApplyUpdates(SourceId, batch, _time.GetUtcNow());
    }

    private SnapshotApplyResult ApplySnapshot(SourceSnapshot snapshot, DateTimeOffset appliedAt)
    {
        lock (_stageLock)
        {
            if (!_initialSnapshotDone)
            {
                var result = _store.ApplySnapshot(SourceId, snapshot, appliedAt, SnapshotApplyMode.Replace);
                if (_staged is { Count: > 0 })
                {
                    _store.ApplyUpdates(SourceId, _staged.Values.ToArray(), appliedAt);
                }

                _staged = null;
                _initialSnapshotDone = true;
                _initialSnapshotApplied.TrySetResult();
                return result;
            }
        }

        return _store.ApplySnapshot(SourceId, snapshot, appliedAt, SnapshotApplyMode.Merge);
    }

    private void AdvancePollCursor(long revision)
    {
        long current;
        do
        {
            current = Interlocked.Read(ref _pollCursor);
            if (revision <= current)
            {
                return;
            }
        }
        while (Interlocked.CompareExchange(ref _pollCursor, revision, current) != current);
    }

    private TimeSpan Backoff(int failures)
    {
        var exponent = Math.Clamp(failures - 1, 0, 16);
        var ticks = _options.Snapshot.RetryInitialDelay.Ticks * Math.Pow(2, exponent);
        return TimeSpan.FromTicks((long)Math.Min(ticks, _options.Snapshot.RetryMaxDelay.Ticks));
    }

    private TimeSpan ComputeStaleAfter()
    {
        var configured = _options.Freshness.StaleAfter;
        if (configured <= TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }

        return _adapter is IPollingSourceAdapter polling
            ? TimeSpan.FromTicks(Math.Max(configured.Ticks, polling.PollInterval.Ticks * 3))
            : configured;
    }

    private async Task<bool> TryDelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(delay, _time, cancellationToken);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    // loop 唔應該拋例外；萬一有 bug 都只係重啟呢個 loop，唔影響其他來源。
    private async Task Supervise(string name, Func<CancellationToken, Task> loop, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await loop(cancellationToken);
                return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogCritical(ex, "{SourceId}: {Loop} loop crashed unexpectedly; restarting in {Delay}.", SourceId, name, LoopRestartDelay);
                if (!await TryDelayAsync(LoopRestartDelay, cancellationToken))
                {
                    return;
                }
            }
        }
    }
}
