using DataHub.Api.Domain;
using DataHub.Api.Store;
using DataHub.Api.Tests.Support;
using static DataHub.Api.Tests.Support.TestData;

namespace DataHub.Api.Tests.Sync;

/// <summary>
/// 由 checkpoint 恢復嘅資料：重新同步之前只可以係 Recovering（或者 Disconnected），絕對唔可以係 Live；
/// 同埋多個來源之間要互相隔離。
/// </summary>
public class SourceRunnerRecoveryTests
{
    private static readonly TimeSpan OneSecond = TimeSpan.FromSeconds(1);

    private static LatestStateStore StoreRestoredWithStaleData(string sourceId)
    {
        var store = new LatestStateStore([sourceId]);
        store.Restore(SourceData.FromRecords(
            sourceId,
            [
                TestData.Record(sourceId, "A", 500m, revision: 9_000),
                TestData.Record(sourceId, "GONE", 1m, revision: 8_000),
            ],
            snapshotRevision: 9_000,
            lastSnapshotAtUtc: T0.AddMinutes(-5),
            lastUpdateReceivedAtUtc: T0.AddMinutes(-5)));
        return store;
    }

    [Fact]
    public async Task RestoredData_IsServedAsRecoveringAndNeverLive_UntilTheFirstSnapshotReplacesIt()
    {
        var adapter = new FakeStreamingAdapter("stream-a");
        adapter.Upstream.Set("A", 100m);
        adapter.HoldSnapshots();
        await using var h = new RunnerHarness(adapter, StoreRestoredWithStaleData("stream-a"));
        h.Runner.MarkRestoredFromCheckpoint();

        Assert.Equal(SourceStatus.Recovering, h.Status);

        h.Start();
        await h.WaitUntil(() => adapter.GatedSnapshotCalls == 1, "snapshot in flight");

        Assert.Equal(SourceStatus.Recovering, h.Status);
        Assert.Equal(500m, h.Record("A")!.Price);
        Assert.NotNull(h.Record("GONE"));

        adapter.ReleaseSnapshots();
        await h.WaitUntil(() => h.Status == SourceStatus.Live, "first snapshot applied");

        // 舊 session 嘅 revision（9000）唔可以同新 session（1）比較：Replace 之後以來源為準。
        Assert.Equal(100m, h.Record("A")!.Price);
        Assert.Null(h.Record("GONE"));
    }

    [Fact]
    public async Task RestoredData_WhenTheSourceIsDown_IsDisconnectedButStillServed_ThenReplacedOnRecovery()
    {
        var adapter = new FakeStreamingAdapter("stream-a");
        adapter.Upstream.Set("A", 100m);
        adapter.Down = true;
        await using var h = new RunnerHarness(adapter, StoreRestoredWithStaleData("stream-a"));
        h.Runner.MarkRestoredFromCheckpoint();
        h.Start();

        await h.PumpUntil(() => h.Status == SourceStatus.Disconnected, "source unreachable", step: OneSecond);
        Assert.Equal(500m, h.Record("A")!.Price);

        adapter.Down = false;
        await h.PumpUntil(() => h.Status == SourceStatus.Live, "source recovered", step: OneSecond);

        Assert.Equal(100m, h.Record("A")!.Price);
        Assert.Null(h.Record("GONE"));
    }

    [Fact]
    public async Task OneFailingSource_DoesNotAffectTheOther_AndSameInstrumentIdsNeverOverwriteEachOther()
    {
        var good = new FakeStreamingAdapter("good");
        good.Upstream.Set("AAPL", 100m);
        var bad = new FakeStreamingAdapter("bad");
        bad.Upstream.Set("AAPL", 7m);
        bad.Down = true;

        var store = new LatestStateStore(["good", "bad"]);
        var time = new RecordingTimeProvider(T0);
        await using var goodRunner = new RunnerHarness(good, store, time: time).Start();
        await using var badRunner = new RunnerHarness(bad, store, time: time).Start();

        await goodRunner.WaitUntil(() => goodRunner.Status == SourceStatus.Live, "good source synced");
        await badRunner.PumpUntil(() => badRunner.Status == SourceStatus.Disconnected, "bad source unreachable", step: OneSecond);

        good.Emit("AAPL", 101m);
        await goodRunner.WaitUntil(() => store.Find("good", "AAPL")?.Price == 101m, "update on the healthy source");

        Assert.Equal(SourceStatus.Live, goodRunner.Status);
        Assert.Null(store.Find("bad", "AAPL"));

        bad.Down = false;
        await badRunner.PumpUntil(() => badRunner.Status == SourceStatus.Live, "bad source recovered", step: OneSecond);

        Assert.Equal(7m, store.Find("bad", "AAPL")!.Price);
        Assert.Equal(101m, store.Find("good", "AAPL")!.Price);
    }
}
