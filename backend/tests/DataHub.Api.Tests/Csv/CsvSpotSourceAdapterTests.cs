using DataHub.Api.Configuration;
using DataHub.Api.Csv;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using DataHub.Api.Tests.Support;

namespace DataHub.Api.Tests.Csv;

public sealed class CsvSpotSourceAdapterTests
{
    [Fact]
    public async Task UnchangedContentDoesNotAdvanceRevisionButSameTimestampEditDoes()
    {
        using var directory = new TempDirectory();
        var path = directory.File("spot.csv");
        var firstContent = "InstrumentId,ProviderSymbol,Price,Currency,SourceUpdatedAtUtc,PriceKind\nhk-0005,0005.HK,10.00,HKD,2026-03-01T10:00:00Z,Last";
        await File.WriteAllTextAsync(path, firstContent);
        var config = new CsvShowcaseOptions { Enabled = true, SpotPath = path };
        var monitor = new CsvImportMonitor();
        var adapter = new CsvSpotSourceAdapter(
            Options.Create(new DataHubOptions { CsvShowcase = config }),
            new TestEnvironment(directory.FullPath),
            new CsvShowcaseReader(config),
            monitor,
            TimeProvider.System);

        var first = await adapter.GetSnapshotAsync(CancellationToken.None);
        adapter.ConfirmSnapshot(first, first.TakenAtUtc);
        var firstStatus = monitor.GetStatus("demo-csv")!;
        Assert.Equal(1, first.Revision);
        Assert.Equal(10.00m, Assert.Single(first.Records).Price);

        var unchanged = await adapter.GetSnapshotAsync(CancellationToken.None);
        adapter.ConfirmSnapshot(unchanged, unchanged.TakenAtUtc);
        Assert.True(unchanged.UnchangedContent);
        Assert.Equal(first.Revision, unchanged.Revision);
        Assert.Equal(firstStatus.LastDataImportAtUtc, monitor.GetStatus("demo-csv")!.LastDataImportAtUtc);

        await File.WriteAllTextAsync(path, firstContent.Replace("10.00", "11.00", StringComparison.Ordinal));
        var revised = await adapter.GetSnapshotAsync(CancellationToken.None);
        Assert.False(revised.UnchangedContent);
        Assert.Equal(2, revised.Revision);
        Assert.Equal(first.Records[0].SourceUpdatedAtUtc, revised.Records[0].SourceUpdatedAtUtc);
        Assert.Equal(11.00m, revised.Records[0].Price);
    }

    private sealed class TestEnvironment(string contentRoot) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "CsvTests";
        public string ContentRootPath { get; set; } = contentRoot;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
