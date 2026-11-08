using System.Net;
using System.Text.Json;
using DataHub.Api.Domain;
using DataHub.Api.Endpoints;
using DataHub.Api.Tests.Support;

namespace DataHub.Api.Tests.Api;

public class ApiAdminTests
{
    [Fact]
    public async Task RefreshSnapshot_MergesTheCurrentUpstreamState()
    {
        await using var scenario = new ApiScenario();
        var client = await scenario.StartAsync();

        scenario.Alpha.ChangeSilently("AAA", 11m);
        scenario.Alpha.ChangeSilently("NEW", 1m);

        var result = (await client.PostReplyAsync("/api/admin/snapshot/alpha"))
            .Expect(HttpStatusCode.OK)
            .Json<SnapshotRefreshResponse>();

        Assert.Equal("alpha", result.SourceId);
        Assert.Equal((1, 1, 0, 3), (result.Added, result.Updated, result.Removed, result.TotalRecords));
        Assert.False(result.Skipped);

        Assert.Equal(11m, (await client.GetJsonAsync<PriceDto>("/api/prices/alpha/AAA")).Price);
        Assert.Equal(1m, (await client.GetJsonAsync<PriceDto>("/api/prices/alpha/NEW")).Price);
        Assert.Equal(2, (await client.GetPricesAsync("sourceId=beta")).TotalCount);
    }

    [Fact]
    public async Task RefreshSnapshot_RemovesInstrumentsThatNoLongerExistUpstream()
    {
        await using var scenario = new ApiScenario();
        var client = await scenario.StartAsync();

        scenario.Alpha.Upstream.Remove("AAA");

        var result = (await client.PostReplyAsync("/api/admin/snapshot/alpha"))
            .Expect(HttpStatusCode.OK)
            .Json<SnapshotRefreshResponse>();

        Assert.Equal(1, result.Removed);
        Assert.Equal(1, result.TotalRecords);
        (await client.GetReplyAsync("/api/prices/alpha/AAA")).ExpectProblem(HttpStatusCode.NotFound);
        Assert.Equal(2, (await client.GetPricesAsync("sourceId=beta")).TotalCount);
    }

    [Fact]
    public async Task RefreshSnapshot_UnknownSource_Returns404()
    {
        await using var scenario = new ApiScenario();
        var client = await scenario.StartAsync();

        var problem = (await client.PostReplyAsync("/api/admin/snapshot/nope")).ExpectProblem(HttpStatusCode.NotFound);

        Assert.Equal("Unknown source", problem.Title);
    }

    [Fact]
    public async Task RefreshSnapshot_WhileAnotherRefreshIsRunning_Returns409()
    {
        await using var scenario = new ApiScenario();
        var client = await scenario.StartAsync();

        scenario.Alpha.HoldSnapshots();
        var first = client.PostReplyAsync("/api/admin/snapshot/alpha");
        try
        {
            await ApiExtensions.WaitForAsync(
                () => Task.FromResult(scenario.Alpha.GatedSnapshotCalls),
                gated => gated >= 1,
                "the first snapshot to reach the gate");

            var second = await client.PostReplyAsync("/api/admin/snapshot/alpha");
            var problem = second.ExpectProblem(HttpStatusCode.Conflict);
            Assert.Equal("Snapshot already running", problem.Title);

            var source = (await client.GetSourcesAsync()).Sources.Single(item => item.SourceId == "alpha");
            Assert.True(source.SnapshotInProgress);
        }
        finally
        {
            scenario.Alpha.ReleaseSnapshots();
        }

        (await first).Expect(HttpStatusCode.OK);
        var after = (await client.GetSourcesAsync()).Sources.Single(item => item.SourceId == "alpha");
        Assert.False(after.SnapshotInProgress);
    }

    [Fact]
    public async Task RefreshSnapshot_WhenTheSourceIsDown_Returns502_KeepsServingStoredData_AndHealsOnItsOwn()
    {
        await using var scenario = new ApiScenario();
        var client = await scenario.StartAsync();

        scenario.Alpha.Down = true;
        var problem = (await client.PostReplyAsync("/api/admin/snapshot/alpha")).ExpectProblem(HttpStatusCode.BadGateway);
        Assert.Equal("Source snapshot failed", problem.Title);
        Assert.Contains("alpha", problem.Detail);

        // 資料照常服務；feed 未斷，所以狀態仍然係 Live，但錯誤會喺 source 資訊度睇到。
        Assert.Equal(2, (await client.GetPricesAsync("sourceId=alpha")).TotalCount);
        var failing = await client.WaitForSourceAsync("alpha", source => source.LastError is not null, "the snapshot error to be reported");
        Assert.Equal(SourceStatus.Live, failing.Status);
        Assert.NotNull(failing.LastErrorAtUtc);

        // 來源恢復後，排程嘅 retry（backoff）會自己補返。
        scenario.Alpha.Down = false;
        var healed = await client.WaitForSourceAsync("alpha", source => source.LastError is null, "the scheduled retry to succeed");
        Assert.Equal(SourceStatus.Live, healed.Status);
    }

    [Fact]
    public async Task MockOutage_IsRejectedForNonMockSources()
    {
        await using var scenario = new ApiScenario();
        var client = await scenario.StartAsync();

        var notMock = (await client.PostReplyAsync("/api/admin/mock/alpha/outage?seconds=5")).ExpectProblem(HttpStatusCode.Conflict);
        Assert.Equal("Not a mock source", notMock.Title);

        var unknown = (await client.PostReplyAsync("/api/admin/mock/nope/outage?seconds=5")).ExpectProblem(HttpStatusCode.NotFound);
        Assert.Equal("Unknown source", unknown.Title);

        Assert.Equal(SourceStatus.Live, (await client.GetSourcesAsync()).Sources.Single(item => item.SourceId == "alpha").Status);
    }

    [Fact]
    public async Task AdminRoutes_AreNotMappedWhenAdminIsDisabled()
    {
        var settings = new Dictionary<string, string?> { ["DataHub:Api:AdminEnabled"] = "false" };
        await using var scenario = new ApiScenario(settings);
        var client = await scenario.StartAsync();

        (await client.PostReplyAsync("/api/admin/snapshot/alpha")).ExpectProblem(HttpStatusCode.NotFound);
        (await client.PostReplyAsync("/api/admin/checkpoint")).ExpectProblem(HttpStatusCode.NotFound);
        (await client.PostReplyAsync("/api/admin/mock/alpha/outage")).ExpectProblem(HttpStatusCode.NotFound);

        Assert.False((await client.GetHealthAsync()).AdminEnabled);

        var openApi = (await client.GetReplyAsync("/openapi/v1.json")).Expect(HttpStatusCode.OK);
        using var document = JsonDocument.Parse(openApi.Body);
        var paths = document.RootElement.GetProperty("paths").EnumerateObject().Select(path => path.Name);
        Assert.DoesNotContain(paths, path => path.StartsWith("/api/admin/", StringComparison.Ordinal));
        Assert.Contains("/api/prices", paths);
    }
}
