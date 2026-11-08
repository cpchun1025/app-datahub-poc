using System.Net;
using DataHub.Api.Domain;
using DataHub.Api.Endpoints;
using DataHub.Api.Tests.Support;

namespace DataHub.Api.Tests.Api;

public class ApiSourceIsolationTests
{
    [Fact]
    public async Task LiveUpdates_ReachTheApi_WithoutTouchingTheSameInstrumentOfAnotherSource()
    {
        await using var scenario = new ApiScenario();
        var client = await scenario.StartAsync();

        scenario.Alpha.Emit("SHARED", 101m);
        scenario.Beta.Upstream.Set("BBB", 21m);

        await ApiExtensions.WaitForAsync(
            () => client.GetPricesAsync(),
            prices => PriceOf(prices, "alpha", "SHARED") == 101m && PriceOf(prices, "beta", "BBB") == 21m,
            "the streamed update (alpha) and the polled update (beta) to be visible");

        var after = await client.GetPricesAsync();
        Assert.Equal(200m, PriceOf(after, "beta", "SHARED"));
        Assert.Equal(10m, PriceOf(after, "alpha", "AAA"));
    }

    [Fact]
    public async Task LateAndDuplicateMessages_NeverRegressTheLatestState()
    {
        await using var scenario = new ApiScenario();
        var client = await scenario.StartAsync();

        scenario.Alpha.Emit("SHARED", 101m);
        await ApiExtensions.WaitForAsync(
            () => client.GetPricesAsync("sourceId=alpha"),
            prices => PriceOf(prices, "alpha", "SHARED") == 101m,
            "the first update to be applied");

        // 遲到（revision 1）同重複（revision 3）嘅訊息都要被忽略；
        // 之後嗰個 AAA update 一定喺佢哋之後先被處理（同一條 stream，有次序）。
        scenario.Alpha.PushRaw(TestData.Update("SHARED", 1m, revision: 1), TestData.Update("SHARED", 999m, revision: 3));
        scenario.Alpha.Emit("AAA", 12m);
        await ApiExtensions.WaitForAsync(
            () => client.GetPricesAsync("sourceId=alpha"),
            prices => PriceOf(prices, "alpha", "AAA") == 12m,
            "the marker update to be applied");

        Assert.Equal(101m, PriceOf(await client.GetPricesAsync("sourceId=alpha"), "alpha", "SHARED"));
    }

    [Fact]
    public async Task OneFailingSource_DoesNotAffectTheOthers_AndRecoversOnItsOwn()
    {
        await using var scenario = new ApiScenario();
        scenario.Beta.Down = true;
        var client = scenario.Client;

        await client.WaitForLiveAsync("alpha");
        var beta = await client.WaitForSourceAsync(
            "beta",
            source => source.Status == SourceStatus.Disconnected && source.LastError is not null,
            "beta to be Disconnected with an error");
        Assert.False(beta.SyncedSinceStart);
        Assert.Equal(0, beta.RecordCount);

        var prices = await client.GetPricesAsync();
        Assert.Equal(2, prices.TotalCount);
        Assert.All(prices.Items, item => Assert.Equal("alpha", item.SourceId));

        var health = await client.GetHealthAsync();
        Assert.Equal((1, 1), (health.Sources.Live, health.Sources.Disconnected));

        scenario.Beta.Down = false;
        await client.WaitForLiveAsync("alpha", "beta");
        Assert.Equal(4, (await client.GetPricesAsync()).TotalCount);
    }

    private static decimal? PriceOf(PriceListResponse prices, string sourceId, string instrumentId)
        => prices.Items.SingleOrDefault(item => item.SourceId == sourceId && item.InstrumentId == instrumentId)?.Price;
}

public class ApiMockSourceTests
{
    // 縮細 mock 數量、停用上市／下市令筆數確定；retry 固定 1 秒令復原更快。
    private static Dictionary<string, string?> QuietMocks() => new()
    {
        ["DataHub:Mock:Sources:0:InstrumentCount"] = "50",
        ["DataHub:Mock:Sources:1:InstrumentCount"] = "40",
        ["DataHub:Mock:Sources:2:InstrumentCount"] = "30",
        ["DataHub:Mock:Sources:0:MembershipChangeProbability"] = "0",
        ["DataHub:Mock:Sources:1:MembershipChangeProbability"] = "0",
        ["DataHub:Mock:Sources:2:MembershipChangeProbability"] = "0",
        ["DataHub:Snapshot:RetryMaxDelaySeconds"] = "1",
    };

    [Fact]
    public async Task ShippedMockSources_BecomeLive_AndKeepUpdating()
    {
        using var checkpoints = new TempDirectory();
        await using var factory = new DataHubFactory(catalog: null, checkpoints.FullPath, QuietMocks());
        var client = factory.Api;

        var list = await client.WaitForLiveAsync("mock-stream-a", "mock-stream-b", "mock-api-c");
        Assert.All(list.Sources, source => Assert.True(source.IsMock));
        Assert.Equal(
            [("mock-stream-a", SourceKind.Streaming, 50L), ("mock-stream-b", SourceKind.Streaming, 40L), ("mock-api-c", SourceKind.Polling, 30L)],
            list.Sources.Select(source => (source.SourceId, source.Kind, source.RecordCount)));
        Assert.True((await client.GetHealthAsync()).MockMode);

        // 價格持續變動：stream 來源嘅最高 revision 會上升。
        var before = await client.GetPricesAsync("sourceId=mock-stream-a&pageSize=500");
        var startRevision = before.Items.Max(item => item.SourceRevision);
        var after = await ApiExtensions.WaitForAsync(
            () => client.GetPricesAsync("sourceId=mock-stream-a&pageSize=500"),
            prices => prices.Items.Max(item => item.SourceRevision) > startRevision,
            "mock-stream-a to publish new ticks");
        Assert.Equal(50, after.TotalCount);
    }

    [Fact]
    public async Task Outage_DisconnectsOnlyThatSource_ThenItResyncsAndGoesLiveAgain()
    {
        using var checkpoints = new TempDirectory();
        await using var factory = new DataHubFactory(catalog: null, checkpoints.FullPath, QuietMocks());
        var client = factory.Api;
        await client.WaitForLiveAsync("mock-stream-a", "mock-stream-b", "mock-api-c");

        var outage = (await client.PostReplyAsync("/api/admin/mock/mock-stream-a/outage?seconds=60"))
            .Expect(HttpStatusCode.OK)
            .Json<MockOutageResponse>();
        Assert.Equal(("mock-stream-a", 60), (outage.SourceId, outage.Seconds));
        Assert.NotNull(outage.OutageUntilUtc);

        var during = await ApiExtensions.WaitForAsync(
            client.GetSourcesAsync,
            list => list.Sources.Single(source => source.SourceId == "mock-stream-a").Status == SourceStatus.Disconnected,
            "mock-stream-a to be Disconnected during its outage");
        Assert.All(
            during.Sources.Where(source => source.SourceId != "mock-stream-a"),
            source => Assert.Equal(SourceStatus.Live, source.Status));

        // 斷線期間最後已知嘅資料照常服務，但每行都清楚標示 Disconnected，唔會扮 Live。
        var served = await client.GetPricesAsync("sourceId=mock-stream-a&pageSize=500");
        Assert.Equal(50, served.TotalCount);
        Assert.All(served.Items, item => Assert.Equal(SourceStatus.Disconnected, item.SourceStatus));

        // seconds=0 提前結束 outage：source 自動重連、重新 snapshot，然後返回 Live。
        (await client.PostReplyAsync("/api/admin/mock/mock-stream-a/outage?seconds=0")).Expect(HttpStatusCode.OK);
        await client.WaitForLiveAsync("mock-stream-a", "mock-stream-b", "mock-api-c");
        Assert.Equal(50, (await client.GetPricesAsync("sourceId=mock-stream-a&pageSize=500")).TotalCount);
    }

    [Fact]
    public async Task Outage_RejectsOutOfRangeSeconds()
    {
        using var checkpoints = new TempDirectory();
        await using var factory = new DataHubFactory(catalog: null, checkpoints.FullPath, QuietMocks());
        var client = factory.Api;

        foreach (var seconds in new[] { -1, 3601 })
        {
            var problem = (await client.PostReplyAsync($"/api/admin/mock/mock-stream-a/outage?seconds={seconds}"))
                .ExpectProblem(HttpStatusCode.BadRequest);
            Assert.Contains("seconds", problem.Errors!.Keys);
        }

        await client.WaitForLiveAsync("mock-stream-a");
    }
}

public class ApiHostingTests
{
    [Fact]
    public async Task Development_ExposesSwaggerUi()
    {
        await using var scenario = new ApiScenario(environment: "Development");

        var reply = (await scenario.Client.GetReplyAsync("/swagger/index.html")).Expect(HttpStatusCode.OK);

        Assert.Equal("text/html", reply.ContentType);
        Assert.Contains("swagger", reply.Body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SwaggerUi_CanBeEnabledExplicitlyOutsideDevelopment()
    {
        var settings = new Dictionary<string, string?> { ["DataHub:Api:SwaggerUiEnabled"] = "true" };
        await using var scenario = new ApiScenario(settings);

        (await scenario.Client.GetReplyAsync("/swagger/index.html")).Expect(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Cors_AllowsOnlyTheConfiguredOrigins()
    {
        var settings = new Dictionary<string, string?> { ["DataHub:Api:CorsAllowedOrigins:0"] = "http://localhost:5173" };
        await using var scenario = new ApiScenario(settings);

        var allowed = await scenario.Client.GetReplyAsync("/api/health", ("Origin", "http://localhost:5173"));
        Assert.Equal("http://localhost:5173", allowed.Headers["Access-Control-Allow-Origin"]);

        var other = await scenario.Client.GetReplyAsync("/api/health", ("Origin", "http://evil.example"));
        Assert.False(other.Headers.ContainsKey("Access-Control-Allow-Origin"));
    }

    [Fact]
    public async Task Cors_IsOffWhenNoOriginIsConfigured()
    {
        await using var scenario = new ApiScenario();

        var reply = await scenario.Client.GetReplyAsync("/api/health", ("Origin", "http://localhost:5173"));

        Assert.False(reply.Headers.ContainsKey("Access-Control-Allow-Origin"));
    }

    [Fact]
    public async Task DevelopmentSettings_AllowTheViteDevServerOrigin()
    {
        await using var scenario = new ApiScenario(environment: "Development");

        var reply = await scenario.Client.GetReplyAsync("/api/health", ("Origin", "http://localhost:5173"));

        Assert.Equal("http://localhost:5173", reply.Headers["Access-Control-Allow-Origin"]);
    }

    [Fact]
    public async Task InvalidConfiguration_FailsFastAtStartup()
    {
        var settings = new Dictionary<string, string?> { ["DataHub:Api:MaxPageSize"] = "0" };
        await using var scenario = new ApiScenario(settings);

        var failure = Assert.ThrowsAny<Exception>(() => scenario.Client);

        Assert.Contains("DataHub:Api:MaxPageSize", failure.ToString());
    }
}
