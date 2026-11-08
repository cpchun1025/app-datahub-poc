using DataHub.Api.Domain;

namespace DataHub.Api.Sync;

/// <summary>
/// 一個來源嘅執行期狀態（不可變）。每次變更都替換成新 instance，所以 API 讀取時永遠見到一致嘅組合。
/// </summary>
public sealed record SourceRuntimeState
{
    /// <summary>資料係由 checkpoint 恢復（之後可能已經被 sync 取代，要配合 <see cref="SyncedSinceStart"/> 睇）。</summary>
    public bool RestoredFromCheckpoint { get; init; }

    /// <summary>今次進程啟動之後，係咪已經成功完成過一次來源 snapshot。恢復數據未 sync 之前一定係 false。</summary>
    public bool SyncedSinceStart { get; init; }

    /// <summary>Streaming：訂閱中；Polling：最近一次 poll／snapshot 成功。</summary>
    public bool FeedConnected { get; init; }

    /// <summary>Feed 重新連線後，仍然未有一個「喺連線之後先開始」嘅 snapshot 補返可能漏咗嘅 update。</summary>
    public bool ResyncPending { get; init; }

    public bool SnapshotInProgress { get; init; }
    public DateTimeOffset? LastFeedActivityAtUtc { get; init; }
    public DateTimeOffset? LastSnapshotAttemptAtUtc { get; init; }
    public DateTimeOffset? NextSnapshotDueAtUtc { get; init; }

    public string? LastSnapshotError { get; init; }
    public DateTimeOffset? LastSnapshotErrorAtUtc { get; init; }
    public int ConsecutiveSnapshotFailures { get; init; }

    public string? LastFeedError { get; init; }
    public DateTimeOffset? LastFeedErrorAtUtc { get; init; }
    public int ConsecutiveFeedFailures { get; init; }
}

public sealed class SourceRuntime
{
    private readonly object _gate = new();
    private SourceRuntimeState _state = new();

    public SourceRuntimeState State => Volatile.Read(ref _state);

    public SourceRuntimeState Update(Func<SourceRuntimeState, SourceRuntimeState> mutate)
    {
        lock (_gate)
        {
            var next = mutate(_state);
            Volatile.Write(ref _state, next);
            return next;
        }
    }
}

/// <summary>
/// 由 <see cref="SourceRuntimeState"/> 推導狀態嘅純函數。Stale 係用 <paramref name="nowUtc"/> 即時計算，
/// 所以就算冇任何事件發生，過咗期限一樣會變 Stale。
/// 優先次序：Disconnected &gt; Recovering &gt; Stale &gt; Live。未完成 sync 嘅來源（包括由 checkpoint 恢復嘅）永遠唔會係 Live。
/// </summary>
public static class SourceStatusEvaluator
{
    public static SourceStatus Evaluate(SourceRuntimeState state, DateTimeOffset nowUtc, TimeSpan staleAfter)
    {
        if (!state.SyncedSinceStart)
        {
            if (state.ConsecutiveSnapshotFailures > 0 || state.ConsecutiveFeedFailures > 0)
            {
                return SourceStatus.Disconnected;
            }

            return state.RestoredFromCheckpoint ? SourceStatus.Recovering : SourceStatus.Initializing;
        }

        if (!state.FeedConnected)
        {
            return SourceStatus.Disconnected;
        }

        if (state.ResyncPending)
        {
            return SourceStatus.Recovering;
        }

        if (staleAfter > TimeSpan.Zero
            && state.LastFeedActivityAtUtc is { } lastActivity
            && nowUtc - lastActivity > staleAfter)
        {
            return SourceStatus.Stale;
        }

        return SourceStatus.Live;
    }
}
