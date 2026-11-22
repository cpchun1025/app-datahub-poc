using System.Net;
using System.Text.Json;
using DataHub.Api.Domain;
using DataHub.Api.Endpoints;
using DataHub.Api.Tests.Support;

namespace DataHub.Api.Tests.Api;

/// <summary>
/// 三個內容固定嘅 fake 來源，同一個 InstrumentId "SHARED" 喺全部來源出現（價錢同貨幣各不相同）。
/// 只讀測試共用一個 host，所以每個測試之間唔可以改動來源內容。
/// </summary>
public sealed class LiveApiFixture : IAsyncLifetime
{
    private readonly TempDirectory _checkpoints = new();
    private DataHubFactory? _factory;

    internal HttpClient Client { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        var alpha = new FakeStreamingAdapter("alpha", new FakeUpstream("USD"), priceDecimals: 2);
        alpha.Upstream.Set("AAA", 10.00m);
        alpha.Upstream.Set("SHARED", 100.00m);
        alpha.Upstream.Set("ZZZ", 30.00m);

        var beta = new FakePollingAdapter("beta", TimeSpan.FromMilliseconds(200), new FakeUpstream("EUR"), priceDecimals: 3);
        beta.Upstream.Set("BBB", 20.5m);
        beta.Upstream.Set("SHARED", 200.5m);

        var gamma = new FakeStreamingAdapter("gamma", new FakeUpstream("HKD"), priceDecimals: 4);
        gamma.Upstream.Set("SHARED", 300.25m);

        _factory = new DataHubFactory(new FakeCatalog(alpha, beta, gamma), _checkpoints.FullPath);
        Client = _factory.Api;
        await Client.WaitForLiveAsync("alpha", "beta", "gamma");
    }

    public async Task DisposeAsync()
    {
        if (_factory is not null)
        {
            await _factory.DisposeAsync();
        }

        _checkpoints.Dispose();
    }
}

public class ApiReadTests(LiveApiFixture api) : IClassFixture<LiveApiFixture>
{
    private HttpClient Client => api.Client;

    [Fact]
    public async Task Root_PointsToTheOtherEndpoints()
    {
        var reply = (await Client.GetReplyAsync("/")).Expect(HttpStatusCode.OK);

        using var document = JsonDocument.Parse(reply.Body);
        Assert.Equal("/api/health", document.RootElement.GetProperty("health").GetString());
        Assert.Equal("/openapi/v1.json", document.RootElement.GetProperty("openapi").GetString());
    }

    [Fact]
    public async Task Health_ReportsOkAndTheSourceSummary()
    {
        var health = await Client.GetHealthAsync();

        Assert.Equal("ok", health.Status);
        Assert.True(health.AdminEnabled);
        Assert.True(health.StartedAtUtc <= health.ServerTimeUtc);
        Assert.Equal(3, health.Sources.Total);
        Assert.Equal(3, health.Sources.Live);
        Assert.Equal(0, health.Sources.Initializing + health.Sources.Recovering + health.Sources.Stale + health.Sources.Disconnected);
        Assert.True(health.Checkpoint.Enabled);
    }

    [Fact]
    public async Task Sources_ListsEachSourceWithItsKindDecimalsAndRecordCount()
    {
        var list = await Client.GetSourcesAsync();

        Assert.Equal(["alpha", "beta", "gamma"], list.Sources.Select(source => source.SourceId));
        Assert.All(list.Sources, source =>
        {
            Assert.Equal(SourceStatus.Live, source.Status);
            Assert.True(source.SyncedSinceStart);
            Assert.False(source.RestoredFromCheckpoint);
            Assert.False(source.IsMock);
            Assert.Null(source.LastError);
            Assert.NotNull(source.LastSnapshotAtUtc);
        });

        var alpha = list.Sources.Single(source => source.SourceId == "alpha");
        Assert.Equal(SourceKind.Streaming, alpha.Kind);
        Assert.Equal(2, alpha.PriceDecimals);
        Assert.Equal(3, alpha.RecordCount);

        var beta = list.Sources.Single(source => source.SourceId == "beta");
        Assert.Equal(SourceKind.Polling, beta.Kind);
        Assert.Equal(3, beta.PriceDecimals);
        Assert.Equal(2, beta.RecordCount);
    }

    [Fact]
    public async Task Prices_AreSortedBySourceThenInstrument_AndTheSameInstrumentFromEverySourceIsKept()
    {
        var prices = await Client.GetPricesAsync();

        Assert.Equal(6, prices.TotalCount);
        Assert.Equal(
            ["alpha/AAA", "alpha/SHARED", "alpha/ZZZ", "beta/BBB", "beta/SHARED", "gamma/SHARED"],
            prices.Items.Select(item => $"{item.SourceId}/{item.InstrumentId}"));

        var shared = prices.Items.Where(item => item.InstrumentId == "SHARED").ToDictionary(item => item.SourceId);
        Assert.Equal((100.00m, "USD"), (shared["alpha"].Price, shared["alpha"].Currency));
        Assert.Equal((200.5m, "EUR"), (shared["beta"].Price, shared["beta"].Currency));
        Assert.Equal((300.25m, "HKD"), (shared["gamma"].Price, shared["gamma"].Currency));

        Assert.All(prices.Items, item => Assert.Equal(SourceStatus.Live, item.SourceStatus));
        Assert.Equal(["alpha", "beta", "gamma"], prices.Sources.Select(source => source.SourceId));
    }

    [Theory]
    [InlineData("beta")]
    [InlineData("BETA")]
    public async Task Prices_CanBeFilteredBySource_IgnoringCase(string sourceId)
    {
        var prices = await Client.GetPricesAsync($"sourceId={sourceId}");

        Assert.Equal(2, prices.TotalCount);
        Assert.All(prices.Items, item => Assert.Equal("beta", item.SourceId));
        Assert.Equal("beta", Assert.Single(prices.Sources).SourceId);
    }

    [Fact]
    public async Task Prices_InstrumentSearch_IsACaseInsensitiveContains()
    {
        var contains = await Client.GetPricesAsync("instrumentId=hare");
        Assert.Equal(3, contains.TotalCount);
        Assert.All(contains.Items, item => Assert.Equal("SHARED", item.InstrumentId));

        var combined = await Client.GetPricesAsync("sourceId=gamma&instrumentId=SHA");
        Assert.Equal("gamma", Assert.Single(combined.Items).SourceId);

        var none = await Client.GetPricesAsync("instrumentId=NOPE");
        Assert.Empty(none.Items);
        Assert.Equal(0, none.TotalCount);
        Assert.Equal(0, none.TotalPages);
    }

    [Fact]
    public async Task Prices_ArePaginated()
    {
        var first = await Client.GetPricesAsync("page=1&pageSize=4");
        Assert.Equal((1, 4, 6L, 2), (first.Page, first.PageSize, first.TotalCount, first.TotalPages));
        Assert.Equal(["alpha/AAA", "alpha/SHARED", "alpha/ZZZ", "beta/BBB"], first.Items.Select(item => $"{item.SourceId}/{item.InstrumentId}"));

        var second = await Client.GetPricesAsync("page=2&pageSize=4");
        Assert.Equal(["beta/SHARED", "gamma/SHARED"], second.Items.Select(item => $"{item.SourceId}/{item.InstrumentId}"));

        var beyond = await Client.GetPricesAsync("page=9&pageSize=4");
        Assert.Empty(beyond.Items);
        Assert.Equal(6, beyond.TotalCount);
    }

    [Theory]
    [InlineData("page=0", "page")]
    [InlineData("page=-3", "page")]
    [InlineData("pageSize=0", "pageSize")]
    [InlineData("pageSize=501", "pageSize")]
    public async Task Prices_RejectsOutOfRangePaging_WithProblemDetails(string query, string field)
    {
        var problem = (await Client.GetReplyAsync("/api/prices?" + query)).ExpectProblem(HttpStatusCode.BadRequest);

        Assert.Contains(field, problem.Errors!.Keys);
    }

    [Theory]
    [InlineData("page=abc")]
    [InlineData("pageSize=1.5")]
    public async Task Prices_NonNumericPaging_IsAClientErrorNotAServerError(string query)
    {
        var reply = await Client.GetReplyAsync("/api/prices?" + query);

        Assert.Equal(HttpStatusCode.BadRequest, reply.Status);
    }

    [Fact]
    public async Task Prices_InstrumentSearchLength_IsBoundedAt64Characters()
    {
        var tooLong = await Client.GetReplyAsync("/api/prices?instrumentId=" + new string('x', 65));
        var problem = tooLong.ExpectProblem(HttpStatusCode.BadRequest);
        Assert.Contains("instrumentId", problem.Errors!.Keys);

        var atLimit = await Client.GetPricesAsync("instrumentId=" + new string('x', 64));
        Assert.Empty(atLimit.Items);
    }

    [Fact]
    public async Task Prices_UnknownSource_Returns404()
    {
        var problem = (await Client.GetReplyAsync("/api/prices?sourceId=nope")).ExpectProblem(HttpStatusCode.NotFound);

        Assert.Equal("Unknown source", problem.Title);
    }

    [Fact]
    public async Task PriceDetail_ReturnsTheExactSourceAndInstrument()
    {
        var price = await Client.GetJsonAsync<PriceDto>("/api/prices/beta/SHARED");

        Assert.Equal("beta", price.SourceId);
        Assert.Equal("SHARED", price.InstrumentId);
        Assert.Equal(200.5m, price.Price);
        Assert.Equal("EUR", price.Currency);
        Assert.Equal(SourceStatus.Live, price.SourceStatus);
        Assert.True(price.SourceRevision > 0);

        var canonical = await Client.GetJsonAsync<PriceDto>("/api/prices/BETA/SHARED");
        Assert.Equal("beta", canonical.SourceId);
    }

    [Fact]
    public async Task InstrumentDetail_ReturnsPagedSourceObservationsWithoutConsolidating()
    {
        var reply = (await Client.GetReplyAsync("/api/instruments/SHARED?page=1&pageSize=2"))
            .Expect(HttpStatusCode.OK);
        using var document = JsonDocument.Parse(reply.Body);
        var root = document.RootElement;
        Assert.Equal("SHARED", root.GetProperty("instrumentId").GetString());
        Assert.Equal(3, root.GetProperty("totalCount").GetInt32());
        Assert.Equal(2, root.GetProperty("observations").GetArrayLength());
        Assert.Equal(2, root.GetProperty("totalPages").GetInt32());
        Assert.Equal(["alpha", "beta"], root.GetProperty("observations").EnumerateArray()
            .Select(item => item.GetProperty("sourceId").GetString()));

        var second = (await Client.GetReplyAsync("/api/instruments/SHARED?page=2&pageSize=2"))
            .Expect(HttpStatusCode.OK);
        using var secondDocument = JsonDocument.Parse(second.Body);
        Assert.Equal("gamma", secondDocument.RootElement.GetProperty("observations")[0].GetProperty("sourceId").GetString());
    }

    [Fact]
    public async Task InstrumentMemberships_ReturnsBoundedEmptyStateAndValidationProblems()
    {
        var reply = (await Client.GetReplyAsync("/api/instruments/SHARED/memberships?page=1&pageSize=2"))
            .Expect(HttpStatusCode.OK);
        using var document = JsonDocument.Parse(reply.Body);
        var root = document.RootElement;
        Assert.Equal(0, root.GetProperty("totalCount").GetInt32());
        Assert.Empty(root.GetProperty("items").EnumerateArray());
        Assert.Equal("Disabled", root.GetProperty("sourceStatus").GetString());

        var invalidPage = await Client.GetReplyAsync("/api/instruments/SHARED?page=0");
        var problem = invalidPage.ExpectProblem(HttpStatusCode.BadRequest);
        Assert.Contains("page", problem.Errors!.Keys);

        var invalidIdentity = (await Client.GetReplyAsync("/api/instruments/%20")).ExpectProblem(HttpStatusCode.BadRequest);
        Assert.Contains("instrumentId", invalidIdentity.Errors!.Keys);
    }

    [Theory]
    [InlineData("/api/prices/nope/AAA", "Unknown source")]
    [InlineData("/api/prices/alpha/NOPE", "Instrument not found")]
    [InlineData("/api/prices/beta/AAA", "Instrument not found")]
    public async Task PriceDetail_MissingSourceOrInstrument_Returns404(string url, string title)
    {
        var problem = (await Client.GetReplyAsync(url)).ExpectProblem(HttpStatusCode.NotFound);

        Assert.Equal(title, problem.Title);
    }

    [Fact]
    public async Task Json_UsesCamelCase_StringEnums_AndUtcTimestamps()
    {
        var reply = (await Client.GetReplyAsync("/api/prices/alpha/AAA")).Expect(HttpStatusCode.OK);

        using var document = JsonDocument.Parse(reply.Body);
        var root = document.RootElement;
        Assert.Equal("alpha", root.GetProperty("sourceId").GetString());
        Assert.Equal("AAA", root.GetProperty("instrumentId").GetString());
        Assert.Equal(JsonValueKind.Number, root.GetProperty("price").ValueKind);
        Assert.Equal(JsonValueKind.Number, root.GetProperty("sourceRevision").ValueKind);
        Assert.Equal("Live", root.GetProperty("sourceStatus").GetString());
        Assert.EndsWith("Z", root.GetProperty("sourceUpdatedAtUtc").GetString());
        Assert.EndsWith("Z", root.GetProperty("receivedAtUtc").GetString());

        var sources = (await Client.GetReplyAsync("/api/sources")).Expect(HttpStatusCode.OK);
        using var sourcesDocument = JsonDocument.Parse(sources.Body);
        var first = sourcesDocument.RootElement.GetProperty("sources")[0];
        Assert.Equal("Streaming", first.GetProperty("kind").GetString());
        Assert.Equal("Live", first.GetProperty("status").GetString());
        Assert.EndsWith("Z", first.GetProperty("lastSnapshotAtUtc").GetString());
    }

    [Fact]
    public async Task PriceJson_PreservesRepresentativeDecimalValuesAndScaleOnTheWire()
    {
        using var checkpoints = new TempDirectory();
        var source = new FakeStreamingAdapter("precision");
        source.Upstream.Set("SCALE", 117.60m);
        source.Upstream.Set("SMALL", 0.0000000000000000000000000001m);
        source.Upstream.Set("LARGE", 12345678901234567890.12345678m);
        source.Upstream.Set("NEGATIVE", -12.50m);
        await using var factory = new DataHubFactory(new FakeCatalog(source), checkpoints.FullPath);
        var client = factory.Api;
        await client.WaitForLiveAsync("precision");

        var expected = new Dictionary<string, string>
        {
            ["SCALE"] = "117.60",
            ["SMALL"] = "0.0000000000000000000000000001",
            ["LARGE"] = "12345678901234567890.12345678",
            ["NEGATIVE"] = "-12.50",
        };
        using var list = JsonDocument.Parse((await client.GetReplyAsync("/api/prices?sourceId=precision"))
            .Expect(HttpStatusCode.OK).Body);
        foreach (var row in list.RootElement.GetProperty("items").EnumerateArray())
        {
            var id = row.GetProperty("instrumentId").GetString()!;
            var price = row.GetProperty("price");
            Assert.Equal(JsonValueKind.Number, price.ValueKind);
            Assert.Equal(expected[id], price.GetRawText());
            using var detail = JsonDocument.Parse((await client.GetReplyAsync($"/api/prices/precision/{id}"))
                .Expect(HttpStatusCode.OK).Body);
            Assert.Equal(expected[id], detail.RootElement.GetProperty("price").GetRawText());
        }
        Assert.Equal(expected.Count, list.RootElement.GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task OpenApiDocument_DescribesTheEndpoints()
    {
        var reply = (await Client.GetReplyAsync("/openapi/v1.json")).Expect(HttpStatusCode.OK);

        using var document = JsonDocument.Parse(reply.Body);
        Assert.Equal("Data Hub PoC API", document.RootElement.GetProperty("info").GetProperty("title").GetString());

        var paths = document.RootElement.GetProperty("paths").EnumerateObject().Select(path => path.Name).ToArray();
        string[] expected =
        [
            "/api/health",
            "/api/sources",
            "/api/instruments/{instrumentId}",
            "/api/instruments/{instrumentId}/memberships",
            "/api/prices",
            "/api/prices/{sourceId}/{instrumentId}",
            "/api/admin/snapshot/{sourceId}",
            "/api/admin/checkpoint",
            "/api/admin/mock/{sourceId}/outage",
        ];
        Assert.Empty(expected.Except(paths));
    }

    [Fact]
    public async Task OpenApi_DescribesNewsAndPriceExportResponsesAndErrors()
    {
        using var document = JsonDocument.Parse((await Client.GetReplyAsync("/openapi/v1.json"))
            .Expect(HttpStatusCode.OK).Body);
        var root = document.RootElement;
        var paths = root.GetProperty("paths");
        var instrumentDetail = paths.GetProperty("/api/instruments/{instrumentId}").GetProperty("get");
        Assert.Equal("#/components/schemas/InstrumentDetailResponse",
            ResponseSchema(instrumentDetail, "200", "application/json").GetProperty("$ref").GetString());
        var memberships = paths.GetProperty("/api/instruments/{instrumentId}/memberships").GetProperty("get");
        Assert.Equal("#/components/schemas/InstrumentMembershipResponse",
            ResponseSchema(memberships, "200", "application/json").GetProperty("$ref").GetString());
        AssertValidationResponse(instrumentDetail);
        AssertValidationResponse(memberships);
        var news = paths.GetProperty("/api/news").GetProperty("get");
        var parameters = news.GetProperty("parameters").EnumerateArray().ToArray();
        Assert.Equal(
            ["category", "keyword", "page", "pageSize", "publishedFrom", "publishedTo", "sortDirection"],
            parameters.Select(parameter => parameter.GetProperty("name").GetString()).Order(StringComparer.Ordinal));
        Assert.All(parameters, parameter =>
        {
            Assert.Equal("query", parameter.GetProperty("in").GetString());
            Assert.False(parameter.TryGetProperty("required", out var required) && required.GetBoolean());
            Assert.Equal("string", parameter.GetProperty("schema").GetProperty("type").GetString());
        });

        var newsSchema = ResponseSchema(news, "200", "application/json");
        Assert.Equal("#/components/schemas/NewsListResponse", newsSchema.GetProperty("$ref").GetString());
        var schemas = root.GetProperty("components").GetProperty("schemas");
        var newsProperties = schemas.GetProperty("NewsListResponse").GetProperty("properties");
        Assert.Equal("#/components/schemas/NewsDto",
            newsProperties.GetProperty("items").GetProperty("items").GetProperty("$ref").GetString());
        Assert.Equal("#/components/schemas/NewsStatus", newsProperties.GetProperty("source").GetProperty("$ref").GetString());
        var itemProperties = schemas.GetProperty("NewsDto").GetProperty("properties");
        foreach (var name in new[] { "sourceId", "articleId", "publishedAtUtc", "publishedDate", "publicationTimePrecision",
                     "firstSeenAtUtc", "fetchedAtUtc", "mappingWarnings" })
            Assert.True(itemProperties.TryGetProperty(name, out _), $"Missing news property {name}");
        Assert.Equal("string", itemProperties.GetProperty("firstSeenAtUtc").GetProperty("type").GetString());
        Assert.Equal("date-time", itemProperties.GetProperty("firstSeenAtUtc").GetProperty("format").GetString());
        Assert.Equal(["null", "string"], itemProperties.GetProperty("publishedAtUtc").GetProperty("type")
            .EnumerateArray().Select(value => value.GetString()).Order(StringComparer.Ordinal));
        Assert.Equal("date-time", itemProperties.GetProperty("publishedAtUtc").GetProperty("format").GetString());
        Assert.Equal("#/components/schemas/PublicationTimePrecision",
            itemProperties.GetProperty("publicationTimePrecision").GetProperty("$ref").GetString());
        Assert.Equal("array", itemProperties.GetProperty("mappingWarnings").GetProperty("type").GetString());
        var precisionSchema = schemas.GetProperty("PublicationTimePrecision");
        Assert.Equal(["Date", "Instant", "Unknown"], precisionSchema.GetProperty("enum").EnumerateArray()
            .Select(value => value.GetString()).Order(StringComparer.Ordinal));
        Assert.True(schemas.GetProperty("NewsStatus").GetProperty("properties").TryGetProperty("state", out _));
        foreach (var name in new[] { "type", "title", "status", "detail", "instance" })
            Assert.True(schemas.GetProperty("ProblemDetails").GetProperty("properties").TryGetProperty(name, out _));
        Assert.True(schemas.GetProperty("HttpValidationProblemDetails").GetProperty("properties")
            .TryGetProperty("errors", out _));
        AssertValidationResponse(news);

        var prices = paths.GetProperty("/api/prices").GetProperty("get");
        AssertValidationResponse(prices);
        AssertProblemResponse(prices, "404");
        AssertProblemResponse(paths.GetProperty("/api/prices/{sourceId}/{instrumentId}").GetProperty("get"), "404");

        var csv = paths.GetProperty("/api/prices/export.csv").GetProperty("get");
        Assert.Equal("string", ResponseSchema(csv, "200", "text/csv").GetProperty("type").GetString());
        AssertValidationResponse(csv);
        AssertProblemResponse(csv, "404");
        AssertProblemResponse(csv, "422");
        Assert.Contains("ExportMaxRows", csv.GetProperty("description").GetString());

        static JsonElement ResponseSchema(JsonElement operation, string status, string mediaType)
            => operation.GetProperty("responses").GetProperty(status).GetProperty("content")
                .GetProperty(mediaType).GetProperty("schema");

        static void AssertValidationResponse(JsonElement operation)
            => Assert.Equal("#/components/schemas/HttpValidationProblemDetails",
                ResponseSchema(operation, "400", "application/problem+json").GetProperty("$ref").GetString());

        static void AssertProblemResponse(JsonElement operation, string status)
            => Assert.Equal("#/components/schemas/ProblemDetails",
                ResponseSchema(operation, status, "application/problem+json").GetProperty("$ref").GetString());
    }

    [Fact]
    public async Task SwaggerUi_IsNotExposedOutsideDevelopment()
    {
        var reply = await Client.GetReplyAsync("/swagger/index.html");

        Assert.Equal(HttpStatusCode.NotFound, reply.Status);
    }
}
