using System.Net;
using System.Text;
using DataHub.Api.Endpoints;
using DataHub.Api.Tests.Support;

namespace DataHub.Api.Tests.Api;

public class PriceGridApiTests(LiveApiFixture fixture) : IClassFixture<LiveApiFixture>
{
    [Theory]
    [InlineData("sortBy=anything", "sortBy")]
    [InlineData("sortBy=2", "sortBy")]
    [InlineData("sortDirection=sideways", "sortDirection")]
    [InlineData("sourceStatus=1", "sourceStatus")]
    [InlineData("sourceStatus=Live,Stale", "sourceStatus")]
    [InlineData("currency=US", "currency")]
    [InlineData("currency=123", "currency")]
    [InlineData("minPrice=NaN", "minPrice")]
    [InlineData("maxPrice=1,000", "maxPrice")]
    [InlineData("minPrice=5&maxPrice=4", "maxPrice")]
    [InlineData("page=abc", "page")]
    public async Task InvalidQueriesUseProblemDetailsForTableAndExport(string query, string field)
    {
        foreach (var path in new[] { "/api/prices?", "/api/prices/export.csv?" })
        {
            var problem = (await fixture.Client.GetReplyAsync(path + query)).ExpectProblem(HttpStatusCode.BadRequest);
            Assert.Contains(field, problem.Errors!.Keys);
        }
    }

    [Fact]
    public async Task TableAndBothExportsUseIdenticalFilteringAndOrdering()
    {
        const string query = "sourceStatus=live&minPrice=20&maxPrice=301&sortBy=price&sortDirection=desc&page=2&pageSize=2";
        var table = await fixture.Client.GetPricesAsync(query);
        Assert.Equal(5, table.TotalCount);
        Assert.Equal([100m, 30m], table.Items.Select(r => r.Price));
        var page = await fixture.Client.GetAsync("/api/prices/export.csv?scope=page&" + query);
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Equal("text/csv", page.Content.Headers.ContentType?.MediaType);
        Assert.True(page.Headers.CacheControl?.NoStore);
        Assert.Matches("^prices-page-[0-9]{8}T[0-9]{9}Z\\.csv$", page.Content.Headers.ContentDisposition!.FileNameStar!);
        var bytes = await page.Content.ReadAsByteArrayAsync();
        Assert.Equal(Encoding.UTF8.GetPreamble(), bytes.Take(3));
        await using var expected = new MemoryStream();
        await PricesCsv.WriteAsync(expected, table.Items, CancellationToken.None);
        Assert.Equal(expected.ToArray(), bytes);

        var allTable = await fixture.Client.GetPricesAsync(query.Replace("page=2&pageSize=2", "page=1&pageSize=500"));
        var all = await fixture.Client.GetByteArrayAsync("/api/prices/export.csv?scope=all&" + query);
        await using var expectedAll = new MemoryStream();
        await PricesCsv.WriteAsync(expectedAll, allTable.Items, CancellationToken.None);
        Assert.Equal(expectedAll.ToArray(), all);
    }

    [Fact]
    public async Task AllExportIgnoresPagination_AndUnknownSourceAndScopeAreExplicit()
    {
        Assert.Equal(HttpStatusCode.OK, (await fixture.Client.GetAsync("/api/prices/export.csv?scope=all&page=bad&pageSize=0")).StatusCode);
        (await fixture.Client.GetReplyAsync("/api/prices/export.csv?sourceId=missing")).ExpectProblem(HttpStatusCode.NotFound);
        (await fixture.Client.GetReplyAsync("/api/prices/export.csv?scope=missing")).ExpectProblem(HttpStatusCode.BadRequest);
        var filtered = await fixture.Client.GetPricesAsync("currency=eur&sourceStatus=Live&minPrice=20.5&maxPrice=20.5");
        Assert.Equal("BBB", Assert.Single(filtered.Items).InstrumentId);
        Assert.Empty((await fixture.Client.GetPricesAsync("sourceStatus=Recovering")).Items);
    }

    [Fact]
    public async Task ConfiguredExportLimitRejectsWithoutCsvBody()
    {
        using var directory = new TempDirectory();
        var source = new FakeStreamingAdapter("alpha");
        source.Upstream.Set("A", 1);
        source.Upstream.Set("B", 2);
        await using var factory = new DataHubFactory(new FakeCatalog(source), directory.FullPath,
            new Dictionary<string, string?> { ["DataHub:Api:ExportMaxRows"] = "1" });
        await factory.Api.WaitForLiveAsync("alpha");
        foreach (var scope in new[] { "page", "all" })
        {
            var reply = await factory.Api.GetReplyAsync("/api/prices/export.csv?scope=" + scope);
            var problem = reply.ExpectProblem(HttpStatusCode.UnprocessableEntity);
            Assert.Equal("Export row limit exceeded", problem.Title);
        }
        Assert.Equal(HttpStatusCode.OK, (await factory.Api.GetAsync("/api/prices/export.csv?pageSize=1")).StatusCode);
    }
}
