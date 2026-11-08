using DataHub.Api.Domain;
using DataHub.Api.Sync;
using DataHub.Api.Tests.Support;
using static DataHub.Api.Tests.Support.TestData;

namespace DataHub.Api.Tests.Sync;

public class SourceRunnerPollingTests
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan OneSecond = TimeSpan.FromSeconds(1);

    private static FakePollingAdapter NewAdapter(params (string Id, decimal Price)[] seed)
    {
        var adapter = new FakePollingAdapter("api-c", PollInterval);
        foreach (var (id, price) in seed)
        {
            adapter.Upstream.Set(id, price);
        }

        return adapter;
    }

    [Fact]
    public async Task InitialSnapshot_ThenPollsOnlyTheDeltaUsingTheRevisionCursor()
    {
        var adapter = NewAdapter(("A", 10m), ("B", 20m));
        await using var h = new RunnerHarness(adapter).Start();
        await h.WaitUntil(() => h.Status == SourceStatus.Live, "initial snapshot");

        Assert.Empty(adapter.PollCursors);

        adapter.Upstream.Set("A", 11m);
        await h.PumpUntil(() => h.Record("A")?.Price == 11m, "first poll applied", step: OneSecond);
        await h.PumpUntil(() => adapter.PollCursors.Count >= 2, "second poll", step: OneSecond);

        Assert.Equal(2, adapter.PollCursors[0]);
        Assert.Equal(3, adapter.PollCursors[1]);
        Assert.Equal(1, adapter.SnapshotCalls);
        Assert.Equal(20m, h.Record("B")!.Price);
    }

    [Fact]
    public async Task PollFailure_IsDisconnected_ThenRecoversWithTheDeltaItMissed()
    {
        var adapter = NewAdapter(("A", 10m));
        await using var h = new RunnerHarness(adapter).Start();
        await h.WaitUntil(() => h.Status == SourceStatus.Live, "initial snapshot");

        adapter.Down = true;
        await h.PumpUntil(() => h.Status == SourceStatus.Disconnected, "poll failure", step: OneSecond);
        Assert.Equal(10m, h.Record("A")!.Price);

        adapter.Upstream.Set("A", 12m);
        adapter.Down = false;
        await h.PumpUntil(() => h.Status == SourceStatus.Live && h.Record("A")?.Price == 12m, "recovered", step: OneSecond);

        Assert.Equal(0, h.State.ConsecutiveFeedFailures);
    }

    [Fact]
    public async Task PollingCannotSeeDeletions_SoTheNextFullSnapshotRemovesThem()
    {
        var adapter = NewAdapter(("A", 1m), ("B", 2m));
        await using var h = new RunnerHarness(adapter).Start();
        await h.WaitUntil(() => h.Status == SourceStatus.Live, "initial snapshot");

        adapter.Upstream.Remove("B");
        await h.PumpUntil(() => adapter.PollCursors.Count >= 1, "a poll that sees no change", step: OneSecond);
        Assert.NotNull(h.Record("B"));

        var refreshed = await h.Runner.RefreshSnapshotAsync(SnapshotTrigger.Manual, CancellationToken.None);

        Assert.Equal(SnapshotRefreshOutcome.Succeeded, refreshed.Outcome);
        Assert.Equal(1, refreshed.Applied!.Value.Removed);
        Assert.Null(h.Record("B"));
        Assert.NotNull(h.Record("A"));
    }

    [Theory]
    [InlineData(2, 10, 10)]
    [InlineData(5, 10, 15)]
    [InlineData(5, 0, 0)]
    public void StaleThreshold_IsAtLeastThreePollIntervals_AndZeroDisablesIt(int pollSeconds, int configuredSeconds, int expectedSeconds)
    {
        var adapter = new FakePollingAdapter("api-c", TimeSpan.FromSeconds(pollSeconds));
        var runner = new RunnerHarness(adapter, options: NewOptions(o => o.Freshness.StaleAfterSeconds = configuredSeconds)).Runner;

        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), runner.StaleAfter);
    }

    [Fact]
    public void StaleThreshold_ForStreamingSourcesIsTheConfiguredValue()
    {
        var adapter = new FakeStreamingAdapter("stream-a");
        var runner = new RunnerHarness(adapter, options: NewOptions(o => o.Freshness.StaleAfterSeconds = 10)).Runner;

        Assert.Equal(TimeSpan.FromSeconds(10), runner.StaleAfter);
    }
}
