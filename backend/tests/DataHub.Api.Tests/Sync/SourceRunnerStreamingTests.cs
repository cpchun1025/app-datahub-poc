using DataHub.Api.Domain;
using DataHub.Api.Sync;
using DataHub.Api.Tests.Support;
using static DataHub.Api.Tests.Support.TestData;

namespace DataHub.Api.Tests.Sync;

/// <summary>
/// Streaming 來源嘅同步行為。所有時間（退避、排程、stale）都由 <see cref="RecordingTimeProvider"/> 推進，
/// 測試本身唔使真係等。
/// </summary>
public class SourceRunnerStreamingTests
{
    private static readonly TimeSpan HalfSecond = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan OneSecond = TimeSpan.FromSeconds(1);

    private static FakeStreamingAdapter NewAdapter(params (string Id, decimal Price)[] seed)
    {
        var adapter = new FakeStreamingAdapter("stream-a");
        foreach (var (id, price) in seed)
        {
            adapter.Upstream.Set(id, price);
        }

        return adapter;
    }

    [Fact]
    public async Task FirstSync_IsInitializingUntilTheSnapshotIsApplied_ThenLive()
    {
        var adapter = NewAdapter(("A", 100m));
        adapter.HoldSnapshots();
        await using var h = new RunnerHarness(adapter).Start();

        await h.WaitUntil(() => adapter.GatedSnapshotCalls == 1, "first snapshot cut and waiting");

        Assert.Equal(SourceStatus.Initializing, h.Status);
        Assert.Empty(h.Store.GetSource(h.SourceId)!.Records);

        adapter.ReleaseSnapshots();
        await h.WaitUntil(() => h.Status == SourceStatus.Live, "snapshot applied");

        Assert.Equal(100m, h.Record("A")!.Price);
        Assert.True(h.State.SyncedSinceStart);
    }

    [Fact]
    public async Task LiveUpdates_Apply_AndLateOrDuplicateRevisionsAreIgnored()
    {
        var adapter = NewAdapter(("A", 100m));
        await using var h = new RunnerHarness(adapter).Start();
        await h.WaitUntil(() => h.Status == SourceStatus.Live, "first sync");

        adapter.Emit("A", 101m);
        await h.WaitUntil(() => h.Record("A")?.Price == 101m, "update applied");

        // 遲到嘅舊 revision（1）同同一 revision（2）嘅重複訊息都唔可以令價格倒退。
        adapter.PushRaw(Update("A", 100m, 1), Update("A", 999m, 2));
        adapter.Emit("B", 5m);
        await h.WaitUntil(() => h.Record("B") is not null, "sentinel processed (same channel, FIFO)");

        var record = h.Record("A")!;
        Assert.Equal(101m, record.Price);
        Assert.Equal(2, record.SourceRevision);
    }

    [Fact]
    public async Task UpdatesArrivingBeforeTheFirstSnapshot_AreStagedAndLayeredOnTopOfIt()
    {
        var adapter = NewAdapter(("A", 100m));
        adapter.HoldSnapshots();
        await using var h = new RunnerHarness(adapter).Start();
        await h.WaitUntil(() => adapter.GatedSnapshotCalls == 1, "snapshot cut taken");

        // 假時間先前進，之後見到 LastFeedActivityAtUtc 追上，就代表兩個 batch 都已經被 runner 消化。
        h.Time.Advance(OneSecond);
        adapter.Emit("A", 120m);
        adapter.Emit("NEW", 7m);
        var consumedAt = h.Time.GetUtcNow();
        await h.WaitUntil(() => h.State.LastFeedActivityAtUtc == consumedAt, "stream batches consumed");

        Assert.Null(h.Record("NEW"));

        adapter.ReleaseSnapshots();
        await h.WaitUntil(() => h.Status == SourceStatus.Live, "snapshot applied");

        Assert.Equal(120m, h.Record("A")!.Price);
        Assert.Equal(7m, h.Record("NEW")!.Price);
    }

    [Fact]
    public async Task Heartbeat_KeepsTheSourceFresh_AndSilenceBecomesStale()
    {
        var adapter = NewAdapter(("A", 100m));
        var options = NewOptions(o => o.Freshness.StaleAfterSeconds = 10);
        await using var h = new RunnerHarness(adapter, options: options).Start();
        await h.WaitUntil(() => h.Status == SourceStatus.Live, "first sync");

        h.Time.Advance(TimeSpan.FromSeconds(9));
        Assert.Equal(SourceStatus.Live, h.Status);

        h.Time.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(SourceStatus.Stale, h.Status);
        Assert.Equal(100m, h.Record("A")!.Price);

        adapter.Heartbeat();
        var heartbeatAt = h.Time.GetUtcNow();
        await h.WaitUntil(() => h.State.LastFeedActivityAtUtc == heartbeatAt, "heartbeat consumed");

        Assert.Equal(SourceStatus.Live, h.Status);
        Assert.Equal(100m, h.Record("A")!.Price);
    }

    [Fact]
    public async Task DroppedConnection_ReconnectsAndNeverReportsLiveUntilResynced()
    {
        var adapter = NewAdapter(("A", 100m), ("B", 200m));
        await using var h = new RunnerHarness(adapter).Start();
        await h.WaitUntil(() => h.Status == SourceStatus.Live, "first sync");

        adapter.HoldSnapshots();
        adapter.DropConnection();
        await h.WaitUntil(() => h.Status == SourceStatus.Disconnected, "feed down");
        Assert.Equal(100m, h.Record("A")!.Price);

        // 斷線期間，來源有一個更新同一個下市，stream 兩樣都冇通知到。
        adapter.ChangeSilently("A", 150m);
        adapter.Upstream.Remove("B");

        await h.PumpUntil(() => adapter.GatedSnapshotCalls == 1, "resync snapshot started", step: OneSecond);

        Assert.Equal(2, adapter.SubscribeCalls);
        Assert.Equal(SourceStatus.Recovering, h.Status);
        Assert.Equal(100m, h.Record("A")!.Price);
        Assert.NotNull(h.Record("B"));

        adapter.ReleaseSnapshots();
        await h.WaitUntil(() => h.Status == SourceStatus.Live, "resynced");

        Assert.Equal(150m, h.Record("A")!.Price);
        Assert.Null(h.Record("B"));
    }

    [Fact]
    public async Task FeedDownAtStartup_StillTakesTheSnapshot_AndResyncsOnceTheFeedReturns()
    {
        var adapter = NewAdapter(("A", 100m));
        adapter.FeedDown = true;
        await using var h = new RunnerHarness(adapter).Start();

        await h.WaitUntil(() => h.State.SyncedSinceStart, "snapshot applied without a feed");
        Assert.Equal(SourceStatus.Disconnected, h.Status);
        Assert.Equal(100m, h.Record("A")!.Price);

        adapter.FeedDown = false;
        adapter.ChangeSilently("A", 110m);
        await h.PumpUntil(() => h.Status == SourceStatus.Live, "feed reconnected and resynced", step: OneSecond);

        Assert.Equal(110m, h.Record("A")!.Price);
        Assert.Equal(2, adapter.SnapshotCalls);
    }

    [Fact]
    public async Task StreamReconnect_BacksOffExponentiallyAndCapsAtTheMaximum()
    {
        var adapter = NewAdapter(("A", 100m));
        adapter.Down = true;
        await using var h = new RunnerHarness(adapter).Start();

        await h.PumpUntil(() => adapter.SubscribeCalls >= 6, "six connection attempts", step: HalfSecond);

        // 5 秒係 snapshot loop 等第一次 feed 連線嘅寬限，唔係退避。
        var backoff = h.Time.OneShotDelays.Where(delay => delay != TimeSpan.FromSeconds(5)).Take(5);
        Assert.Equal(
            [OneSecond, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(8)],
            backoff);
        Assert.Equal(SourceStatus.Disconnected, h.Status);
        Assert.True(h.State.ConsecutiveFeedFailures >= 5);
    }

    [Fact]
    public async Task SnapshotRetry_BacksOffExponentiallyAndCapsAtTheMaximum()
    {
        var adapter = NewAdapter(("A", 100m));
        adapter.Down = true;
        await using var h = new RunnerHarness(adapter).Start();

        var delays = new List<TimeSpan>();
        for (var attempt = 1; attempt <= 5; attempt++)
        {
            var expectedFailures = attempt;
            SourceRuntimeState? captured = null;
            await h.PumpUntil(
                () =>
                {
                    // 失敗次數同下次到期時間分兩步寫入；等到期時間追上先讀。
                    var state = h.State;
                    if (state.ConsecutiveSnapshotFailures == expectedFailures
                        && state.NextSnapshotDueAtUtc > state.LastSnapshotErrorAtUtc)
                    {
                        captured = state;
                        return true;
                    }

                    return false;
                },
                $"snapshot failure #{expectedFailures}",
                step: HalfSecond);
            delays.Add(captured!.NextSnapshotDueAtUtc!.Value - captured.LastSnapshotErrorAtUtc!.Value);
        }

        Assert.Equal(
            [OneSecond, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(8)],
            delays);
    }

    [Fact]
    public async Task FailedSnapshot_KeepsServingTheOldData_AndRecordsTheError()
    {
        var adapter = NewAdapter(("A", 100m));
        await using var h = new RunnerHarness(adapter).Start();
        await h.WaitUntil(() => h.Status == SourceStatus.Live, "first sync");

        adapter.Down = true;
        var failed = await h.Runner.RefreshSnapshotAsync(SnapshotTrigger.Manual, CancellationToken.None);

        Assert.Equal(SnapshotRefreshOutcome.Failed, failed.Outcome);
        Assert.Contains("is down", failed.Error);
        Assert.Equal(100m, h.Record("A")!.Price);
        Assert.Equal(1, h.State.ConsecutiveSnapshotFailures);
        Assert.NotNull(h.State.LastSnapshotError);
        Assert.False(h.State.SnapshotInProgress);
        Assert.Equal(SourceStatus.Live, h.Status);

        adapter.Down = false;
        var recovered = await h.Runner.RefreshSnapshotAsync(SnapshotTrigger.Manual, CancellationToken.None);

        Assert.Equal(SnapshotRefreshOutcome.Succeeded, recovered.Outcome);
        Assert.Equal(0, h.State.ConsecutiveSnapshotFailures);
        Assert.Null(h.State.LastSnapshotError);
    }

    [Fact]
    public async Task ManualRefresh_WhileASnapshotIsRunning_ReturnsAlreadyRunningWithoutHittingTheSource()
    {
        var adapter = NewAdapter(("A", 100m));
        adapter.HoldSnapshots();
        await using var h = new RunnerHarness(adapter);

        var first = h.Runner.RefreshSnapshotAsync(SnapshotTrigger.Manual, CancellationToken.None);
        await h.WaitUntil(() => adapter.GatedSnapshotCalls == 1, "first refresh reached the source");
        Assert.True(h.State.SnapshotInProgress);

        var second = await h.Runner.RefreshSnapshotAsync(SnapshotTrigger.Manual, CancellationToken.None);

        Assert.Equal(SnapshotRefreshOutcome.AlreadyRunning, second.Outcome);
        Assert.Equal(1, adapter.SnapshotCalls);

        adapter.ReleaseSnapshots();
        var completed = await first;

        Assert.Equal(SnapshotRefreshOutcome.Succeeded, completed.Outcome);
        Assert.False(h.State.SnapshotInProgress);
        Assert.Equal(100m, h.Record("A")!.Price);
    }

    [Fact]
    public async Task ScheduledSnapshots_RunAtTheConfiguredInterval()
    {
        var adapter = NewAdapter(("A", 100m));
        var options = NewOptions(o => o.Snapshot.IntervalHours = 1);
        await using var h = new RunnerHarness(adapter, options: options, schedulerTick: TimeSpan.FromSeconds(30)).Start();
        await h.WaitUntil(() => h.Status == SourceStatus.Live, "first sync");
        Assert.Equal(T0.AddHours(1), h.State.NextSnapshotDueAtUtc);

        h.Time.Advance(TimeSpan.FromMinutes(59));
        await Task.Delay(50);
        Assert.Equal(1, adapter.SnapshotCalls);

        adapter.ChangeSilently("A", 130m);
        await h.PumpUntil(() => adapter.SnapshotCalls == 2, "hourly snapshot", step: TimeSpan.FromSeconds(30));
        await h.WaitUntil(() => h.Record("A")?.Price == 130m, "scheduled snapshot applied");

        Assert.Equal(SourceStatus.Live, h.Status);
    }

    [Fact]
    public async Task ShutdownCancellation_StopsAllLoopsCleanly()
    {
        var adapter = NewAdapter(("A", 100m));
        var h = new RunnerHarness(adapter).Start();
        await h.WaitUntil(() => h.Status == SourceStatus.Live, "first sync");

        await h.DisposeAsync();

        Assert.Equal(0, adapter.ActiveSubscriptions);
    }
}
