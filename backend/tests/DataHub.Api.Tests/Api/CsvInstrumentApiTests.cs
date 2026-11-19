using System.Net;
using System.Text.Json;
using DataHub.Api.Tests.Support;

namespace DataHub.Api.Tests.Api;

public sealed class CsvInstrumentApiTests
{
    private static Dictionary<string, string?> Settings(string spotPath, string compositionPath) => new()
    {
        ["DataHub:CsvShowcase:Enabled"] = "true",
        ["DataHub:CsvShowcase:SpotSourceId"] = "csv-http",
        ["DataHub:CsvShowcase:SpotDisplayName"] = "Synthetic CSV HTTP test",
        ["DataHub:CsvShowcase:SpotPath"] = spotPath,
        ["DataHub:CsvShowcase:CompositionSourceId"] = "demo-composition-http",
        ["DataHub:CsvShowcase:CompositionPath"] = compositionPath,
        ["DataHub:CsvShowcase:PollIntervalSeconds"] = "0.1",
    };

    [Fact]
    public async Task CsvFilesFlowThroughSourcesStoreAndInstrumentHttpEndpoints()
    {
        using var files = new TempDirectory();
        using var checkpoints = new TempDirectory();
        var spotPath = files.File("spot.csv");
        var compositionPath = files.File("composition.csv");
        await File.WriteAllTextAsync(spotPath,
            "InstrumentId,ProviderSymbol,Price,Currency,SourceUpdatedAtUtc,PriceKind\nhk-0005,0005.HK,123.45000001,HKD,2026-03-01T10:00:00+08:00,Last");
        await File.WriteAllTextAsync(compositionPath,
            "IndexId,InstrumentId,Weight,EffectiveDate\nDEMO-HK-A,hk-0005,0.25,2026-03-01");
        await using var factory = new DataHubFactory(null, checkpoints.FullPath, Settings(spotPath, compositionPath));
        var client = factory.Api;
        await client.WaitForLiveAsync("csv-http");
        var csvSource = (await client.GetSourcesAsync()).Sources.Single(source => source.SourceId == "csv-http");
        Assert.Equal(DataHub.Api.Domain.SourceKind.Csv, csvSource.Kind);
        Assert.True(csvSource.IsSynthetic);
        Assert.NotNull(csvSource.CsvImport?.LastSuccessfulValidationAtUtc);
        Assert.False((await client.GetHealthAsync()).MockMode);
        var composition = await ApiExtensions.WaitForAsync(
            async () => (await client.GetReplyAsync("/api/instruments/hk-0005/memberships")).Body,
            body => body.Contains("\"totalCount\":1", StringComparison.Ordinal),
            "composition CSV to be imported");

        using var prices = JsonDocument.Parse((await client.GetReplyAsync("/api/prices?sourceId=csv-http"))
            .Expect(HttpStatusCode.OK).Body);
        var price = Assert.Single(prices.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal("0005.HK", price.GetProperty("providerSymbol").GetString());
        Assert.Equal(JsonValueKind.Number, price.GetProperty("price").ValueKind);
        Assert.Equal("123.45000001", price.GetProperty("price").GetRawText());
        Assert.True(price.GetProperty("isSynthetic").GetBoolean());
        Assert.Equal(1, prices.RootElement.GetProperty("totalCount").GetInt32());

        using var detail = JsonDocument.Parse((await client.GetReplyAsync("/api/instruments/hk-0005"))
            .Expect(HttpStatusCode.OK).Body);
        Assert.Equal("hk-0005", detail.RootElement.GetProperty("instrumentId").GetString());
        Assert.Equal("csv-http", detail.RootElement.GetProperty("observations")[0].GetProperty("sourceId").GetString());
        using var memberships = JsonDocument.Parse(composition);
        Assert.Equal("Live", memberships.RootElement.GetProperty("sourceStatus").GetString());
        Assert.Equal("DEMO-HK-A", memberships.RootElement.GetProperty("items")[0].GetProperty("indexId").GetString());
        Assert.Equal("0.25", memberships.RootElement.GetProperty("items")[0].GetProperty("weight").GetRawText());

        var missingCompositionPriceSource = await client.GetReplyAsync("/api/prices?sourceId=demo-composition-http");
        Assert.Equal(HttpStatusCode.NotFound, missingCompositionPriceSource.Status);
    }

    [Fact]
    public async Task CheckpointRecoveryRetainsLastKnownCsvStateUntilAValidSnapshotReturns()
    {
        using var files = new TempDirectory();
        using var checkpoints = new TempDirectory();
        var spotPath = files.File("spot.csv");
        var compositionPath = files.File("composition.csv");
        await File.WriteAllTextAsync(spotPath,
            "InstrumentId,ProviderSymbol,Price,Currency,SourceUpdatedAtUtc,PriceKind\nhk-0005,0005.HK,123.45,HKD,2026-03-01T10:00:00+08:00,Last");
        await File.WriteAllTextAsync(compositionPath,
            "IndexId,InstrumentId,Weight,EffectiveDate\nDEMO-HK-A,hk-0005,0.25,2026-03-01");

        await using (var first = new DataHubFactory(null, checkpoints.FullPath, Settings(spotPath, compositionPath)))
        {
            var client = first.Api;
            await client.WaitForLiveAsync("csv-http");
            await ApiExtensions.WaitForAsync(
                async () => await client.GetReplyAsync("/api/instruments/hk-0005/memberships"),
                reply => reply.Body.Contains("\"totalCount\":1", StringComparison.Ordinal),
                "composition membership import");
            (await client.PostReplyAsync("/api/admin/checkpoint")).Expect(HttpStatusCode.OK);
        }

        await File.WriteAllTextAsync(spotPath,
            "InstrumentId,ProviderSymbol,Price,Currency,SourceUpdatedAtUtc,PriceKind\nhk-0005,0005.HK,invalid,HKD,2026-03-01T10:00:00+08:00,Last");
        await File.WriteAllTextAsync(compositionPath, "invalid,header");

        await using var restored = new DataHubFactory(null, checkpoints.FullPath, Settings(spotPath, compositionPath));
        var recoveredClient = restored.Api;
        var source = await ApiExtensions.WaitForAsync(
            async () => (await recoveredClient.GetSourcesAsync()).Sources.Single(item => item.SourceId == "csv-http"),
            item => item.LastError is not null,
            "failed post-recovery CSV validation");
        Assert.True(source.RestoredFromCheckpoint);
        Assert.False(source.SyncedSinceStart);
        Assert.Equal(1, source.RecordCount);

        using var recoveredPrice = JsonDocument.Parse((await recoveredClient.GetReplyAsync("/api/prices?sourceId=csv-http"))
            .Expect(HttpStatusCode.OK).Body);
        Assert.Equal(123.45m, recoveredPrice.RootElement.GetProperty("items")[0].GetProperty("price").GetDecimal());
        Assert.NotEqual("Live", recoveredPrice.RootElement.GetProperty("items")[0].GetProperty("sourceStatus").GetString());

        var memberships = await ApiExtensions.WaitForAsync(
            async () => await recoveredClient.GetReplyAsync("/api/instruments/hk-0005/memberships"),
            reply => reply.Body.Contains("\"sourceStatus\":\"Disconnected\"", StringComparison.Ordinal)
                && reply.Body.Contains("\"totalCount\":1", StringComparison.Ordinal),
            "recovered membership retained after failed validation");
        using var membershipJson = JsonDocument.Parse(memberships.Body);
        Assert.Equal("Disconnected", membershipJson.RootElement.GetProperty("sourceStatus").GetString());
        Assert.Equal("DEMO-HK-A", membershipJson.RootElement.GetProperty("items")[0].GetProperty("indexId").GetString());
    }
}
