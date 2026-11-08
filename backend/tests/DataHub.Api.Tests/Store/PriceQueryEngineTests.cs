using DataHub.Api.Domain;
using DataHub.Api.Endpoints;
using DataHub.Api.Configuration;
using DataHub.Api.Store;
using DataHub.Api.Sync;
using DataHub.Api.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DataHub.Api.Tests.Store;

public class PriceQueryEngineTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-01-02T00:00:00Z");

    private static PriceRecord Row(string source, string instrument, decimal price, string currency = "USD", int seconds = 0)
        => new(source, instrument, price, currency, Now.AddSeconds(seconds), Now.AddSeconds(-seconds), 1);

    private static SourceData Data(string source, params PriceRecord[] rows)
        => SourceData.FromRecords(source, rows, 1, Now, Now);

    private static readonly SourceData[] Sources =
    [
        Data("beta", Row("beta", "B", 10, "EUR", 2), Row("beta", "SHARED", 2, "USD", 4)),
        Data("alpha", Row("alpha", "A", -5, "HKD", 1), Row("alpha", "SHARED", 2, "USD", 3)),
    ];

    [Theory]
    [InlineData(false, "alpha/A", "alpha/SHARED", "beta/SHARED", "beta/B")]
    [InlineData(true, "beta/B", "alpha/SHARED", "beta/SHARED", "alpha/A")]
    public void SortsEntireDatasetBeforePaging_WithAscendingIdentityTieBreakers(bool descending, params string[] expected)
    {
        var query = new PriceQuery(null, null, 1, 2, PriceSortField.Price, descending);
        var first = PriceQueryEngine.Execute(Sources, query);
        var second = PriceQueryEngine.Execute(Sources, query with { Page = 2 });
        Assert.Equal(4, first.TotalCount);
        Assert.Equal(expected, first.Items.Concat(second.Items).Select(r => $"{r.SourceId}/{r.InstrumentId}"));
    }

    [Theory]
    [InlineData(PriceSortField.InstrumentId, "alpha/A", "beta/B", "alpha/SHARED", "beta/SHARED")]
    [InlineData(PriceSortField.Currency, "beta/B", "alpha/A", "alpha/SHARED", "beta/SHARED")]
    [InlineData(PriceSortField.SourceUpdatedAtUtc, "alpha/A", "beta/B", "alpha/SHARED", "beta/SHARED")]
    [InlineData(PriceSortField.ReceivedAtUtc, "beta/SHARED", "alpha/SHARED", "beta/B", "alpha/A")]
    public void SortFieldsUseTheirRealTypes(PriceSortField field, params string[] expected)
    {
        var result = PriceQueryEngine.Execute(Sources, new PriceQuery(null, null, 1, 10, field));
        Assert.Equal(expected, result.Items.Select(r => $"{r.SourceId}/{r.InstrumentId}"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NullTextIsLastInEitherDirection_WithoutTreatingEmptyAsNull(bool descending)
    {
        Assert.Equal(0, PriceQueryEngine.CompareText(null, null, descending));
        Assert.True(PriceQueryEngine.CompareText(null, "", descending) > 0);
        Assert.True(PriceQueryEngine.CompareText("USD", null, descending) < 0);
        Assert.Equal(descending ? 1 : -1, Math.Sign(PriceQueryEngine.CompareText("", "USD", descending)));
    }

    [Fact]
    public void CombinesAllFilters_AndCountsBeforePagination()
    {
        var query = new PriceQuery("ALPHA", "hare", 1, 1, Currency: "usd", MinPrice: 2, MaxPrice: 2);
        var result = PriceQueryEngine.Execute(Sources, query);
        Assert.Equal(1, result.TotalCount);
        Assert.Equal("SHARED", Assert.Single(result.Items).InstrumentId);
        Assert.Empty(PriceQueryEngine.Execute(Sources, query with { MinPrice = 3 }).Items);
        Assert.Empty(PriceQueryEngine.Execute(Sources, query with { Page = int.MaxValue }).Items);
    }

    [Fact]
    public void ExportLimitsApplyToSelectedRows_WithoutTruncation()
    {
        var query = new PriceQuery(null, null, 2, 2, PriceSortField.Price);
        Assert.Throws<PriceExportLimitException>(() => PriceQueryEngine.Execute(Sources, query, allMatching: true, exportLimit: 3));
        Assert.Throws<PriceExportLimitException>(() => PriceQueryEngine.Execute(Sources, query, exportLimit: 1));
        var page = PriceQueryEngine.Execute(Sources, query, exportLimit: 2);
        var all = PriceQueryEngine.Execute(Sources, query, allMatching: true, exportLimit: 4);
        Assert.Equal(all.Items.Skip(2), page.Items);
    }

    [Fact]
    public void CaptureRemainsUnchangedAfterLiveStoreUpdates_AndSupportsCancellation()
    {
        var store = new LatestStateStore(["alpha"]);
        store.Restore(Sources[1]);
        var capture = store.GetAllSources();
        store.ApplyUpdates("alpha", [new PriceUpdate("A", 999, "USD", Now, 2)], Now);
        Assert.Equal(-5, PriceQueryEngine.Execute(capture, new(null, null, 1, 10)).Items[0].Price);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => PriceQueryEngine.Execute(capture, new(null, null, 1, 10), cancellation.Token));
    }

    [Fact]
    public void ReadUsesOneEvaluationTimeForStatusesRowsAndResponse()
    {
        var time = new CountingTime();
        var store = new LatestStateStore(["alpha", "beta"]);
        foreach (var source in Sources) store.Restore(source);
        var catalog = new FakeCatalog(new FakeStreamingAdapter("alpha"), new FakeStreamingAdapter("beta"));
        var options = Options.Create(new DataHubOptions());
        var registry = new SourceRegistry(catalog, store, options, time, NullLoggerFactory.Instance);
        foreach (var runner in registry.Runners)
            runner.Runtime.Update(s => s with { SyncedSinceStart = true, FeedConnected = true, LastFeedActivityAtUtc = Now });
        var reader = new PriceReadService(store, new SourceViewService(registry, store, time), time);
        time.Calls = 0;
        var response = reader.Read(new(null, null, 1, 10), SourceStatus.Live, CancellationToken.None);
        Assert.Equal(1, time.Calls);
        Assert.Equal(Now, response.ServerTimeUtc);
        Assert.Equal(4, response.TotalCount);
        Assert.All(response.Items, row => Assert.Equal(SourceStatus.Live, row.SourceStatus));
        Assert.All(response.Sources, source => Assert.Equal(SourceStatus.Live, source.Status));
    }

    private sealed class CountingTime : TimeProvider
    {
        public int Calls { get; set; }
        public override DateTimeOffset GetUtcNow() => Now.AddMinutes(Calls++);
    }
}
