using System.Text;
using DataHub.Api.Configuration;
using DataHub.Api.Csv;
using DataHub.Api.Tests.Support;

namespace DataHub.Api.Tests.Csv;

public sealed class CsvShowcaseReaderTests
{
    [Fact]
    public async Task ReadSpot_AcceptsBomQuotedFieldsAndPreservesSymbolAndDecimal()
    {
        using var directory = new TempDirectory();
        var path = directory.File("spot.csv");
        var content = "\uFEFFInstrumentId,ProviderSymbol,Price,Currency,SourceUpdatedAtUtc,PriceKind\r\n"
            + "hk-0005,\"0005.HK, primary\",123.45000001,HKD,2026-03-01T10:00:00+08:00,Last\r\n";
        await File.WriteAllTextAsync(path, content, new UTF8Encoding(false));
        var reader = new CsvShowcaseReader(new CsvShowcaseOptions());

        var candidate = await reader.ReadSpotAsync(path, CancellationToken.None);

        var record = Assert.Single(candidate.Records);
        Assert.Equal("hk-0005", record.InstrumentId);
        Assert.Equal("0005.HK, primary", record.ProviderSymbol);
        Assert.Equal(123.45000001m, record.Price);
        Assert.Equal(TimeSpan.Zero, record.SourceUpdatedAtUtc.Offset);
        Assert.Equal(new DateTimeOffset(2026, 3, 1, 2, 0, 0, TimeSpan.Zero), record.SourceUpdatedAtUtc);
    }

    [Theory]
    [InlineData("NaN")]
    [InlineData("1,000")]
    [InlineData("")]
    public async Task ReadSpot_RejectsInvalidPrice(string price)
    {
        using var directory = new TempDirectory();
        var path = directory.File("spot.csv");
        await File.WriteAllTextAsync(path,
            $"InstrumentId,ProviderSymbol,Price,Currency,SourceUpdatedAtUtc,PriceKind\nA,0005.HK,{price},HKD,2026-03-01T10:00:00Z,Last");
        var reader = new CsvShowcaseReader(new CsvShowcaseOptions());

        await Assert.ThrowsAsync<CsvShowcaseException>(() => reader.ReadSpotAsync(path, CancellationToken.None));
    }

    [Fact]
    public async Task ReadComposition_UsesFractionWeightsAndDateOnlyAcrossOverlappingIndices()
    {
        using var directory = new TempDirectory();
        var path = directory.File("composition.csv");
        await File.WriteAllTextAsync(path,
            "IndexId,InstrumentId,Weight,EffectiveDate\nDEMO-HK-A,hk-0005,0.25,2026-03-01\nDEMO-HK-B,hk-0005,1,2026-03-02");
        var reader = new CsvShowcaseReader(new CsvShowcaseOptions { CompositionSourceId = "demo-composition" });

        var candidate = await reader.ReadCompositionAsync(path, CancellationToken.None);

        Assert.Equal(2, candidate.Memberships.Count);
        Assert.All(candidate.Memberships, item => Assert.Equal("hk-0005", item.InstrumentId));
        Assert.Equal(0.25m, candidate.Memberships[0].Weight);
        Assert.Equal(new DateOnly(2026, 3, 1), candidate.Memberships[0].EffectiveDate);
    }

    [Fact]
    public async Task ReadSpot_RejectsDuplicateInstrumentAndRetainsNoPartialCandidate()
    {
        using var directory = new TempDirectory();
        var path = directory.File("spot.csv");
        await File.WriteAllTextAsync(path,
            "InstrumentId,ProviderSymbol,Price,Currency,SourceUpdatedAtUtc,PriceKind\nA,0005.HK,1,HKD,2026-03-01T10:00:00Z,Last\nA,0005.HK,2,HKD,2026-03-01T10:00:00Z,Last");
        var reader = new CsvShowcaseReader(new CsvShowcaseOptions());

        await Assert.ThrowsAsync<CsvShowcaseException>(() => reader.ReadSpotAsync(path, CancellationToken.None));
    }
}
