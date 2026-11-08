using DataHub.Api.Domain;
using DataHub.Api.Sync;
using static DataHub.Api.Tests.Support.TestData;

namespace DataHub.Api.Tests.Sync;

public class SourceStatusEvaluatorTests
{
    private static readonly DateTimeOffset Now = T0.AddMinutes(5);
    private static readonly TimeSpan Threshold = TimeSpan.FromSeconds(30);
    private static readonly SourceRuntimeState Fresh = new();

    private static SourceRuntimeState Synced(Func<SourceRuntimeState, SourceRuntimeState>? tweak = null)
    {
        var state = Fresh with { SyncedSinceStart = true, FeedConnected = true, LastFeedActivityAtUtc = Now };
        return tweak is null ? state : tweak(state);
    }

    private static readonly (string Name, SourceRuntimeState State, TimeSpan StaleAfter, SourceStatus Expected)[] Cases =
    [
        ("fresh start", Fresh, Threshold, SourceStatus.Initializing),
        ("fresh start, feed already connected", Fresh with { FeedConnected = true, ResyncPending = true }, Threshold, SourceStatus.Initializing),
        ("restored from checkpoint, not yet synced", Fresh with { RestoredFromCheckpoint = true }, Threshold, SourceStatus.Recovering),
        ("restored, feed connected but still not synced", Fresh with { RestoredFromCheckpoint = true, FeedConnected = true }, Threshold, SourceStatus.Recovering),
        ("restored, recent activity, still not synced is never Live", Fresh with { RestoredFromCheckpoint = true, FeedConnected = true, LastFeedActivityAtUtc = Now }, Threshold, SourceStatus.Recovering),
        ("restored, source unreachable (snapshot failing)", Fresh with { RestoredFromCheckpoint = true, ConsecutiveSnapshotFailures = 1 }, Threshold, SourceStatus.Disconnected),
        ("restored, source unreachable (feed failing)", Fresh with { RestoredFromCheckpoint = true, ConsecutiveFeedFailures = 3 }, Threshold, SourceStatus.Disconnected),
        ("no checkpoint, source unreachable at start", Fresh with { ConsecutiveSnapshotFailures = 2 }, Threshold, SourceStatus.Disconnected),
        ("synced and feed connected", Synced(), Threshold, SourceStatus.Live),
        ("synced, restored earlier, now Live", Synced(s => s with { RestoredFromCheckpoint = true }), Threshold, SourceStatus.Live),
        ("synced but feed down", Synced(s => s with { FeedConnected = false }), Threshold, SourceStatus.Disconnected),
        ("synced, resync pending after reconnect", Synced(s => s with { ResyncPending = true }), Threshold, SourceStatus.Recovering),
        ("feed down wins over resync pending", Synced(s => s with { FeedConnected = false, ResyncPending = true }), Threshold, SourceStatus.Disconnected),
        ("silent for one second more than the threshold", Synced(s => s with { LastFeedActivityAtUtc = Now - Threshold - TimeSpan.FromSeconds(1) }), Threshold, SourceStatus.Stale),
        ("silent for exactly the threshold is still Live", Synced(s => s with { LastFeedActivityAtUtc = Now - Threshold }), Threshold, SourceStatus.Live),
        ("stale detection disabled", Synced(s => s with { LastFeedActivityAtUtc = Now - TimeSpan.FromDays(3) }), TimeSpan.Zero, SourceStatus.Live),
        ("resync pending wins over stale", Synced(s => s with { ResyncPending = true, LastFeedActivityAtUtc = Now - TimeSpan.FromHours(1) }), Threshold, SourceStatus.Recovering),
        ("disconnected wins over stale", Synced(s => s with { FeedConnected = false, LastFeedActivityAtUtc = Now - TimeSpan.FromHours(1) }), Threshold, SourceStatus.Disconnected),
        ("synced without any activity timestamp yet", Synced(s => s with { LastFeedActivityAtUtc = null }), Threshold, SourceStatus.Live),
        ("a failing scheduled snapshot alone does not hide a healthy feed", Synced(s => s with { ConsecutiveSnapshotFailures = 4, LastSnapshotError = "boom" }), Threshold, SourceStatus.Live),
    ];

    [Fact]
    public void EveryTransitionCase_ProducesTheExpectedStatus()
    {
        var mismatches = Cases
            .Select(c => (c.Name, c.Expected, Actual: SourceStatusEvaluator.Evaluate(c.State, Now, c.StaleAfter)))
            .Where(r => r.Actual != r.Expected)
            .Select(r => $"{r.Name}: expected {r.Expected} but was {r.Actual}")
            .ToArray();

        Assert.Empty(mismatches);
    }

    [Fact]
    public void StaleIsEvaluatedAtReadTime_SoItAppearsWithoutAnyNewEvent()
    {
        var state = Synced();

        Assert.Equal(SourceStatus.Live, SourceStatusEvaluator.Evaluate(state, Now.AddSeconds(30), Threshold));
        Assert.Equal(SourceStatus.Stale, SourceStatusEvaluator.Evaluate(state, Now.AddSeconds(31), Threshold));
    }
}
