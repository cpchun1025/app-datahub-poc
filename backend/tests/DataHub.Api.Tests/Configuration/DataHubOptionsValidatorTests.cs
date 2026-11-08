using DataHub.Api.Configuration;
using DataHub.Api.Domain;
using Microsoft.Extensions.Configuration;

namespace DataHub.Api.Tests.Configuration;

public class DataHubOptionsValidatorTests
{
    private static readonly DataHubOptionsValidator Validator = new();

    private static DataHubOptions Valid(Action<DataHubOptions>? configure = null)
    {
        var options = new DataHubOptions();
        options.Mock.Sources.Add(new MockSourceOptions { SourceId = "mock-a", DisplayName = "Mock A", Currency = "USD" });
        configure?.Invoke(options);
        return options;
    }

    private static string[] FailuresOf(DataHubOptions options)
    {
        var result = Validator.Validate(null, options);
        return result.Failed ? [.. result.Failures!] : [];
    }

    private static Action<DataHubOptions> Source(Action<MockSourceOptions> change) => o => change(o.Mock.Sources[0]);

    private static IEnumerable<(string Name, Action<DataHubOptions> Mutate, string Expected)> InvalidCases()
    {
        yield return ("mock sources switched off", o => o.UseMockSources = false, "UseMockSources");

        yield return ("snapshot interval zero", o => o.Snapshot.IntervalHours = 0, "Snapshot:IntervalHours");
        yield return ("snapshot interval NaN", o => o.Snapshot.IntervalHours = double.NaN, "Snapshot:IntervalHours");
        yield return ("snapshot interval infinite", o => o.Snapshot.IntervalHours = double.PositiveInfinity, "Snapshot:IntervalHours");
        yield return ("snapshot interval would overflow TimeSpan", o => o.Snapshot.IntervalHours = 1e12, "Snapshot:IntervalHours");
        yield return ("retry initial zero", o => o.Snapshot.RetryInitialDelaySeconds = 0, "RetryInitialDelaySeconds");
        yield return ("retry initial NaN", o => o.Snapshot.RetryInitialDelaySeconds = double.NaN, "RetryInitialDelaySeconds");
        yield return (
            "retry max below initial",
            o =>
            {
                o.Snapshot.RetryInitialDelaySeconds = 5;
                o.Snapshot.RetryMaxDelaySeconds = 2;
            },
            "RetryMaxDelaySeconds");
        yield return ("retry max NaN", o => o.Snapshot.RetryMaxDelaySeconds = double.NaN, "RetryMaxDelaySeconds");

        yield return ("checkpoint directory blank", o => o.Checkpoint.Directory = "  ", "Checkpoint:Directory");
        yield return ("checkpoint interval below one second", o => o.Checkpoint.IntervalSeconds = 0.5, "Checkpoint:IntervalSeconds");
        yield return ("checkpoint interval NaN", o => o.Checkpoint.IntervalSeconds = double.NaN, "Checkpoint:IntervalSeconds");
        yield return ("checkpoint file name with a path", o => o.Checkpoint.FileName = "sub/checkpoint.json", "Checkpoint:FileName");
        yield return ("checkpoint file name blank", o => o.Checkpoint.FileName = "", "Checkpoint:FileName");

        yield return ("stale threshold negative", o => o.Freshness.StaleAfterSeconds = -1, "StaleAfterSeconds");
        yield return ("stale threshold NaN", o => o.Freshness.StaleAfterSeconds = double.NaN, "StaleAfterSeconds");

        yield return ("max page size zero", o => o.Api.MaxPageSize = 0, "MaxPageSize");
        yield return ("max page size too large", o => o.Api.MaxPageSize = 5_001, "MaxPageSize");
        yield return ("default page size zero", o => o.Api.DefaultPageSize = 0, "DefaultPageSize");
        yield return ("default page size above max", o => o.Api.DefaultPageSize = o.Api.MaxPageSize + 1, "DefaultPageSize");
        yield return ("export limit zero", o => o.Api.ExportMaxRows = 0, "ExportMaxRows");
        yield return ("export limit too large", o => o.Api.ExportMaxRows = 100_001, "ExportMaxRows");

        yield return ("no mock sources", o => o.Mock.Sources.Clear(), "Mock:Sources");
        yield return ("source id with a space", Source(s => s.SourceId = "bad id"), "Sources:0:SourceId");
        yield return ("source id upper case", Source(s => s.SourceId = "Mock-A"), "Sources:0:SourceId");
        yield return ("source id blank", Source(s => s.SourceId = ""), "Sources:0:SourceId");
        yield return ("source id too long", Source(s => s.SourceId = new string('a', 65)), "Sources:0:SourceId");
        yield return ("display name blank", Source(s => s.DisplayName = ""), "DisplayName");
        yield return ("instrument count negative", Source(s => s.InstrumentCount = -1), "InstrumentCount");
        yield return ("instrument count too large", Source(s => s.InstrumentCount = 2_000_001), "InstrumentCount");
        yield return ("update interval below 10ms", Source(s => s.UpdateIntervalMs = 5), "UpdateIntervalMs");
        yield return ("updates per tick negative", Source(s => s.UpdatesPerTick = -1), "UpdatesPerTick");
        yield return ("poll interval zero", Source(s => s.PollIntervalSeconds = 0), "PollIntervalSeconds");
        yield return ("poll interval NaN", Source(s => s.PollIntervalSeconds = double.NaN), "PollIntervalSeconds");
        yield return ("snapshot latency negative", Source(s => s.SnapshotLatencyMs = -1), "SnapshotLatencyMs");
        yield return ("currency too short", Source(s => s.Currency = "US"), "Currency");
        yield return ("currency too long", Source(s => s.Currency = "USDX"), "Currency");
        yield return ("price decimals negative", Source(s => s.PriceDecimals = -1), "PriceDecimals");
        yield return ("price decimals too large", Source(s => s.PriceDecimals = 9), "PriceDecimals");
        yield return ("max move zero", Source(s => s.MaxMovePercent = 0), "MaxMovePercent");
        yield return ("max move above 50 percent", Source(s => s.MaxMovePercent = 51), "MaxMovePercent");
        yield return ("max move NaN", Source(s => s.MaxMovePercent = double.NaN), "MaxMovePercent");
        yield return ("membership probability negative", Source(s => s.MembershipChangeProbability = -0.1), "MembershipChangeProbability");
        yield return ("membership probability above one", Source(s => s.MembershipChangeProbability = 1.1), "MembershipChangeProbability");
        yield return ("membership probability NaN", Source(s => s.MembershipChangeProbability = double.NaN), "MembershipChangeProbability");
        yield return (
            "duplicate source id (case-insensitive)",
            o => o.Mock.Sources.Add(new MockSourceOptions { SourceId = "mock-a", DisplayName = "Again", Currency = "HKD" }),
            "重複");
    }

    [Fact]
    public void AValidConfiguration_PassesWithNoFailures()
    {
        var result = Validator.Validate(null, Valid());

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void EveryInvalidSetting_IsRejected_WithAMessageNamingTheSetting()
    {
        var mismatches = new List<string>();
        foreach (var (name, mutate, expected) in InvalidCases())
        {
            var failures = FailuresOf(Valid(mutate));
            if (!failures.Any(message => message.Contains(expected, StringComparison.Ordinal)))
            {
                mismatches.Add($"{name}: expected a failure mentioning '{expected}', got [{string.Join(" | ", failures)}]");
            }
        }

        Assert.Empty(mismatches);
    }

    [Fact]
    public void AllProblemsAreReportedTogether_NotJustTheFirstOne()
    {
        var failures = FailuresOf(Valid(o =>
        {
            o.Snapshot.IntervalHours = 0;
            o.Api.MaxPageSize = 0;
            o.Mock.Sources[0].Currency = "X";
        }));

        Assert.Contains(failures, message => message.Contains("IntervalHours", StringComparison.Ordinal));
        Assert.Contains(failures, message => message.Contains("MaxPageSize", StringComparison.Ordinal));
        Assert.Contains(failures, message => message.Contains("Currency", StringComparison.Ordinal));
    }

    [Fact]
    public void TheFailureForASecondSource_SaysWhichSourceItIs()
    {
        var failures = FailuresOf(Valid(o => o.Mock.Sources.Add(
            new MockSourceOptions { SourceId = "mock-b", DisplayName = "B", Currency = "EURO" })));

        var failure = Assert.Single(failures);
        Assert.Contains("Sources:1:Currency", failure, StringComparison.Ordinal);
    }

    [Fact]
    public void SettingsOfADisabledCheckpoint_AreNotValidated()
    {
        var failures = FailuresOf(Valid(o =>
        {
            o.Checkpoint.Enabled = false;
            o.Checkpoint.Directory = "";
            o.Checkpoint.IntervalSeconds = 0;
            o.Checkpoint.FileName = "";
        }));

        Assert.Empty(failures);
    }

    [Fact]
    public void ZeroStaleThreshold_IsAllowed_AndMeansDisabled()
    {
        Assert.Empty(FailuresOf(Valid(o => o.Freshness.StaleAfterSeconds = 0)));
    }

    [Fact]
    public void TheShippedAppsettings_AreValid()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        Assert.True(File.Exists(path), $"appsettings.json was not copied next to the test assembly: {path}");

        var options = new ConfigurationBuilder()
            .AddJsonFile(path, optional: false)
            .Build()
            .GetSection(DataHubOptions.SectionName)
            .Get<DataHubOptions>();

        Assert.NotNull(options);
        Assert.Empty(FailuresOf(options));
        Assert.True(options.Mock.Sources.Count >= 2);
        Assert.Contains(options.Mock.Sources, source => source.Kind == SourceKind.Streaming);
        Assert.Contains(options.Mock.Sources, source => source.Kind == SourceKind.Polling);
        Assert.Equal(168, options.Snapshot.IntervalHours);
    }
}
