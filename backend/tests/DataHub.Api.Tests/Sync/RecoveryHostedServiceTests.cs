using DataHub.Api.Configuration;
using DataHub.Api.Domain;
using DataHub.Api.Persistence;
using DataHub.Api.Store;
using DataHub.Api.Sync;
using DataHub.Api.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using static DataHub.Api.Tests.Support.TestData;

namespace DataHub.Api.Tests.Sync;

/// <summary>啟動恢復：恢復嘅數據即時可讀，但狀態係 Recovering；checkpoint 有問題都唔可以令啟動失敗。</summary>
public class RecoveryHostedServiceTests
{
    private readonly FakeTimeProvider _time = new(T0);
    private readonly FakeCheckpointStore _files = new();
    private readonly FakeStreamingAdapter _sourceA = new("src-a");
    private readonly FakeStreamingAdapter _sourceB = new("src-b");

    private sealed class Sut(
        LatestStateStore store,
        CheckpointService checkpoints,
        SourceRegistry registry,
        RecoveryHostedService recovery,
        SourceViewService views)
    {
        public LatestStateStore Store { get; } = store;

        public CheckpointService Checkpoints { get; } = checkpoints;

        public SourceRegistry Registry { get; } = registry;

        public RecoveryHostedService Recovery { get; } = recovery;

        public SourceViewService Views { get; } = views;
    }

    private Sut Create(Action<DataHubOptions>? configure = null)
    {
        var options = Options.Create(NewOptions(configure));
        var store = new LatestStateStore(["src-a", "src-b"]);
        var registry = new SourceRegistry(new FakeCatalog(_sourceA, _sourceB), store, options, _time, NullLoggerFactory.Instance);
        var checkpoints = new CheckpointService(store, _files, options, _time, NullLogger<CheckpointService>.Instance);
        var recovery = new RecoveryHostedService(store, _files, checkpoints, registry, options, NullLogger<RecoveryHostedService>.Instance);
        return new Sut(store, checkpoints, registry, recovery, new SourceViewService(registry, store, _time));
    }

    private static CheckpointPayload Payload(params SourceData[] sources) => new(Version: 7, SavedAtUtc: T0, sources);

    private static SourceData Saved(string sourceId, params (string Id, decimal Price, long Revision)[] records)
        => SourceData.FromRecords(
            sourceId,
            records.Select(r => TestData.Record(sourceId, r.Id, r.Price, r.Revision)),
            snapshotRevision: records.Length == 0 ? 0 : records.Max(r => r.Revision),
            lastSnapshotAtUtc: T0,
            lastUpdateReceivedAtUtc: T0);

    [Fact]
    public async Task RestoresSavedSources_AndServesThemAsRecoveringNeverLive()
    {
        _files.LoadResult = new CheckpointLoadResult(
            Payload(Saved("src-a", ("A", 10m, 5), ("B", 20m, 6)), Saved("src-b", ("A", 99m, 3))),
            "primary",
            []);
        var sut = Create();

        await sut.Recovery.StartAsync(CancellationToken.None);

        Assert.Equal(10m, sut.Store.Find("src-a", "A")!.Price);
        Assert.Equal(99m, sut.Store.Find("src-b", "A")!.Price);
        Assert.All(sut.Views.GetAll(), view => Assert.Equal(SourceStatus.Recovering, view.Status));
        Assert.Equal("primary", sut.Checkpoints.Status.LoadedFrom);
        Assert.Equal(3, sut.Checkpoints.Status.LoadedRecordCount);
    }

    [Fact]
    public async Task OnlyTheSourcesInTheCheckpointAreRecovering_TheOthersStayInitializing()
    {
        _files.LoadResult = new CheckpointLoadResult(Payload(Saved("src-a", ("A", 10m, 5))), "primary", []);
        var sut = Create();

        await sut.Recovery.StartAsync(CancellationToken.None);

        Assert.Equal(SourceStatus.Recovering, sut.Views.Find("src-a")!.Status);
        Assert.Equal(SourceStatus.Initializing, sut.Views.Find("src-b")!.Status);
    }

    [Fact]
    public async Task ASourceThatIsNoLongerRegistered_IsIgnoredWithAWarning()
    {
        _files.LoadResult = new CheckpointLoadResult(
            Payload(Saved("src-a", ("A", 10m, 5)), Saved("retired-source", ("Z", 1m, 1))),
            "primary",
            []);
        var sut = Create();

        await sut.Recovery.StartAsync(CancellationToken.None);

        Assert.Equal(10m, sut.Store.Find("src-a", "A")!.Price);
        Assert.Single(sut.Checkpoints.Status.LoadWarnings);
        Assert.Contains("retired-source", sut.Checkpoints.Status.LoadWarnings[0]);
        Assert.Equal(1, sut.Store.Query(new PriceQuery(null, null, 1, 100)).TotalCount);
    }

    [Fact]
    public async Task NoCheckpoint_StartsEmpty_WithEverySourceInitializing()
    {
        var sut = Create();

        await sut.Recovery.StartAsync(CancellationToken.None);

        Assert.All(sut.Views.GetAll(), view => Assert.Equal(SourceStatus.Initializing, view.Status));
        Assert.Equal(0, sut.Store.Query(new PriceQuery(null, null, 1, 100)).TotalCount);
        Assert.Null(sut.Checkpoints.Status.LoadedFrom);
    }

    [Fact]
    public async Task UnreadableCheckpoint_DoesNotFailStartup_AndIsReportedAsAWarning()
    {
        _files.LoadFailure = new IOException("disk unavailable");
        var sut = Create();

        await sut.Recovery.StartAsync(CancellationToken.None);

        Assert.All(sut.Views.GetAll(), view => Assert.Equal(SourceStatus.Initializing, view.Status));
        Assert.Contains("disk unavailable", Assert.Single(sut.Checkpoints.Status.LoadWarnings));
    }

    [Fact]
    public async Task LoadWarningsFromTheStoreAreKept()
    {
        _files.LoadResult = new CheckpointLoadResult(
            Payload(Saved("src-a", ("A", 10m, 5))),
            "backup",
            ["primary checkpoint 'checkpoint.json' is unusable: truncated"]);
        var sut = Create();

        await sut.Recovery.StartAsync(CancellationToken.None);

        Assert.Equal("backup", sut.Checkpoints.Status.LoadedFrom);
        Assert.Contains("truncated", Assert.Single(sut.Checkpoints.Status.LoadWarnings));
        Assert.Equal(SourceStatus.Recovering, sut.Views.Find("src-a")!.Status);
    }

    [Fact]
    public async Task DisabledCheckpoint_NeverReadsTheStore()
    {
        _files.LoadResult = new CheckpointLoadResult(Payload(Saved("src-a", ("A", 10m, 5))), "primary", []);
        var sut = Create(o => o.Checkpoint.Enabled = false);

        await sut.Recovery.StartAsync(CancellationToken.None);

        Assert.Equal(0, _files.LoadCalls);
        Assert.Null(sut.Store.Find("src-a", "A"));
        Assert.Equal(SourceStatus.Initializing, sut.Views.Find("src-a")!.Status);
    }

    [Fact]
    public async Task RecoveredSource_BecomesLiveOnlyAfterItsFirstSnapshotHasBeenApplied()
    {
        _files.LoadResult = new CheckpointLoadResult(Payload(Saved("src-a", ("A", 500m, 9_000))), "primary", []);
        _sourceA.Upstream.Set("A", 100m);
        _sourceA.HoldSnapshots();
        var sut = Create();
        await sut.Recovery.StartAsync(CancellationToken.None);

        var runner = sut.Registry.Find("src-a")!;
        using var cts = new CancellationTokenSource();
        var run = runner.RunAsync(cts.Token);
        try
        {
            await WaitFor(() => _sourceA.GatedSnapshotCalls == 1);

            var recovering = sut.Views.Find("src-a")!;
            Assert.Equal(SourceStatus.Recovering, recovering.Status);
            Assert.Equal(500m, recovering.Data.Records["A"].Price);

            _sourceA.ReleaseSnapshots();
            await WaitFor(() => sut.Views.Find("src-a")!.Status == SourceStatus.Live);

            Assert.Equal(100m, sut.Store.Find("src-a", "A")!.Price);
        }
        finally
        {
            await cts.CancelAsync();
            await run;
        }
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        var deadline = Environment.TickCount64 + 5_000;
        while (!condition())
        {
            if (Environment.TickCount64 > deadline)
            {
                Assert.Fail("Condition not reached within 5 seconds.");
            }

            await Task.Delay(1);
        }
    }
}
