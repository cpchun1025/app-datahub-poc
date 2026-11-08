using DataHub.Api.Configuration;
using DataHub.Api.Domain;
using DataHub.Api.Sources;
using DataHub.Api.Sources.Mock;
using DataHub.Api.Tests.Support;
using Microsoft.Extensions.Time.Testing;

namespace DataHub.Api.Tests.Sources;

public class MockUpstreamTests
{
    private readonly FakeTimeProvider _time = new(TestData.T0);

    private static MockSourceOptions Options(Action<MockSourceOptions>? configure = null)
    {
        var options = new MockSourceOptions
        {
            SourceId = "mock-test",
            DisplayName = "Mock test",
            InstrumentCount = 50,
            UpdateIntervalMs = 100,
            UpdatesPerTick = 10,
            SnapshotLatencyMs = 0,
            PriceDecimals = 2,
            MaxMovePercent = 1,
            MembershipChangeProbability = 0,
            Seed = 42,
        };
        configure?.Invoke(options);
        return options;
    }

    private MockUpstream Create(Action<MockSourceOptions>? configure = null) => new(Options(configure), _time);

    [Fact]
    public async Task SameSeed_ProducesTheSameSnapshotAndTheSameTicks()
    {
        var first = Create();
        var second = Create();

        var firstSnapshot = await first.GetSnapshotAsync(CancellationToken.None);
        var secondSnapshot = await second.GetSnapshotAsync(CancellationToken.None);
        var firstTicks = Enumerable.Range(0, 5).SelectMany(_ => first.Tick()).ToArray();
        var secondTicks = Enumerable.Range(0, 5).SelectMany(_ => second.Tick()).ToArray();

        Assert.Equal(firstSnapshot.Records, secondSnapshot.Records);
        Assert.Equal(firstTicks, secondTicks);
    }

    [Fact]
    public async Task DifferentSeeds_ProduceDifferentData()
    {
        var first = await Create().GetSnapshotAsync(CancellationToken.None);
        var second = await Create(o => o.Seed = 43).GetSnapshotAsync(CancellationToken.None);

        Assert.NotEqual(first.Records, second.Records);
    }

    [Fact]
    public async Task Snapshot_ContainsEveryInstrumentAtTheCutRevision()
    {
        var upstream = Create();
        upstream.Tick();

        var snapshot = await upstream.GetSnapshotAsync(CancellationToken.None);

        Assert.Equal(50, snapshot.Records.Count);
        Assert.Equal(50, snapshot.Records.Select(r => r.InstrumentId).Distinct().Count());
        Assert.Equal(upstream.Revision, snapshot.Revision);
        Assert.All(snapshot.Records, record => Assert.True(record.Revision <= snapshot.Revision));
        Assert.All(snapshot.Records, record => Assert.Equal("USD", record.Currency));
    }

    [Fact]
    public void Tick_RevisionsIncreaseStrictly_AndPricesStayPositiveAndMove()
    {
        var upstream = Create(o => o.InstrumentCount = 3);
        var previous = upstream.Revision;
        var lastPrice = new Dictionary<string, decimal>();

        for (var tick = 0; tick < 200; tick++)
        {
            foreach (var update in upstream.Tick())
            {
                Assert.True(update.Revision > previous, "revision must be strictly increasing");
                Assert.True(update.Price > 0m, "price must stay positive");
                if (lastPrice.TryGetValue(update.InstrumentId, out var before))
                {
                    Assert.NotEqual(before, update.Price);
                }

                lastPrice[update.InstrumentId] = update.Price;
                previous = update.Revision;
            }
        }

        Assert.Equal(previous, upstream.Revision);
    }

    [Fact]
    public async Task Poll_ReturnsOnlyWhatChangedAfterTheCursor_AtTheCurrentRevision()
    {
        var upstream = Create();
        var snapshot = await upstream.GetSnapshotAsync(CancellationToken.None);

        Assert.Empty(upstream.Poll(snapshot.Revision).Updates);

        var published = upstream.Tick();
        var result = upstream.Poll(snapshot.Revision);

        Assert.Equal(upstream.Revision, result.Revision);
        Assert.Equal(
            published.Select(update => update.InstrumentId).Distinct().Order(StringComparer.Ordinal),
            result.Updates.Select(update => update.InstrumentId).Order(StringComparer.Ordinal));
        Assert.Empty(upstream.Poll(upstream.Revision).Updates);
    }

    [Fact]
    public async Task Subscriber_ReceivesEveryTickInOrder()
    {
        var upstream = Create();
        await using var subscription = upstream.Subscribe();

        var published = new List<IReadOnlyList<PriceUpdate>> { upstream.Tick(), upstream.Tick(), upstream.Tick() };

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var received = new List<IReadOnlyList<PriceUpdate>>();
        await foreach (var batch in subscription.ReadUpdatesAsync(cts.Token))
        {
            received.Add(batch);
            if (received.Count == published.Count)
            {
                break;
            }
        }

        Assert.Equal(published, received);
    }

    [Fact]
    public async Task Delisting_IsSilentOnTheStream_AndOnlyVisibleInTheNextSnapshot()
    {
        var upstream = Create(o =>
        {
            o.UpdatesPerTick = 1;
            o.MembershipChangeProbability = 1;
        });
        var before = (await upstream.GetSnapshotAsync(CancellationToken.None)).Records.Select(r => r.InstrumentId).ToHashSet();
        await using var subscription = upstream.Subscribe();

        for (var i = 0; i < 60; i++)
        {
            upstream.Tick();
        }

        // 中斷一下令 subscription 完結，先可以讀晒已排隊嘅 batch；之後即刻解除中斷，先攞得到 snapshot。
        upstream.SimulateOutage(TimeSpan.FromSeconds(1));
        upstream.SimulateOutage(TimeSpan.Zero);
        var notified = new HashSet<string>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAsync<MockSourceUnavailableException>(async () =>
        {
            await foreach (var batch in subscription.ReadUpdatesAsync(cts.Token))
            {
                foreach (var update in batch)
                {
                    notified.Add(update.InstrumentId);
                }
            }
        });

        var after = (await upstream.GetSnapshotAsync(CancellationToken.None)).Records.Select(r => r.InstrumentId).ToHashSet();
        var delisted = before.Except(after).ToArray();
        var listed = after.Except(before).ToArray();

        Assert.NotEmpty(delisted);
        Assert.NotEmpty(listed);
        Assert.DoesNotContain(delisted, notified.Contains);
        Assert.All(listed, id => Assert.Contains(id, notified));
    }

    [Fact]
    public async Task Outage_FailsEveryOperation_FaultsSubscribers_AndEndsOnTime()
    {
        var upstream = Create();
        await using var subscription = upstream.Subscribe();

        var until = upstream.SimulateOutage(TimeSpan.FromSeconds(10));

        Assert.Equal(TestData.T0.AddSeconds(10), until);
        Assert.True(upstream.IsInOutage);
        await Assert.ThrowsAsync<MockSourceUnavailableException>(() => upstream.GetSnapshotAsync(CancellationToken.None));
        Assert.Throws<MockSourceUnavailableException>(() => upstream.Poll(0));
        Assert.Throws<MockSourceUnavailableException>(() => upstream.Subscribe());
        await Assert.ThrowsAsync<MockSourceUnavailableException>(async () =>
        {
            await foreach (var _ in subscription.ReadUpdatesAsync(CancellationToken.None))
            {
            }
        });

        // 中斷期間 upstream 自己照常變動，所以恢復後 Hub 一定要 resync 先補到返。
        var revisionDuringOutage = upstream.Revision;
        upstream.Tick();
        Assert.True(upstream.Revision > revisionDuringOutage);

        _time.Advance(TimeSpan.FromSeconds(10));

        Assert.False(upstream.IsInOutage);
        Assert.Equal(50, (await upstream.GetSnapshotAsync(CancellationToken.None)).Records.Count);
    }

    [Fact]
    public async Task Outage_CanBeClearedEarly()
    {
        var upstream = Create();
        upstream.SimulateOutage(TimeSpan.FromMinutes(5));

        Assert.Null(upstream.SimulateOutage(TimeSpan.Zero));

        Assert.False(upstream.IsInOutage);
        await using var subscription = upstream.Subscribe();
    }

    [Fact]
    public async Task SnapshotLatency_ModelsTransportTime_AndTheUpstreamKeepsMovingMeanwhile()
    {
        var upstream = Create(o => o.SnapshotLatencyMs = 400);

        var pending = upstream.GetSnapshotAsync(CancellationToken.None);
        Assert.False(pending.IsCompleted);

        var revisionAtCut = upstream.Revision;
        upstream.Tick();
        _time.Advance(TimeSpan.FromMilliseconds(400));
        var snapshot = await pending;

        Assert.Equal(revisionAtCut, snapshot.Revision);
        Assert.True(upstream.Revision > snapshot.Revision);
    }

    [Fact]
    public async Task SlowSubscriber_IsDroppedInsteadOfBlockingTheUpstream()
    {
        var upstream = Create(o => o.UpdatesPerTick = 1);
        await using var subscription = upstream.Subscribe();

        for (var i = 0; i < 1_100; i++)
        {
            upstream.Tick();
        }

        var drained = 0;
        await Assert.ThrowsAsync<MockSourceUnavailableException>(async () =>
        {
            await foreach (var _ in subscription.ReadUpdatesAsync(CancellationToken.None))
            {
                drained++;
            }
        });
        Assert.Equal(1_024, drained);
    }

    [Theory]
    [InlineData(SourceKind.Streaming, typeof(IStreamingSourceAdapter))]
    [InlineData(SourceKind.Polling, typeof(IPollingSourceAdapter))]
    public void Catalog_BuildsAnAdapterOfTheConfiguredKind_AndMarksItAsMock(SourceKind kind, Type expectedInterface)
    {
        var adapter = MockSourceCatalog.Create(Options(o => o.Kind = kind), _time);

        Assert.IsAssignableFrom(expectedInterface, adapter);
        Assert.True(adapter.Descriptor.IsMock);
        Assert.Equal(kind, adapter.Descriptor.Kind);
        Assert.Equal("mock-test", adapter.Descriptor.SourceId);
        Assert.Equal(2, adapter.Descriptor.PriceDecimals);
    }
}
