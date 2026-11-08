using DataHub.Api.Configuration;
using DataHub.Api.Domain;

namespace DataHub.Api.Tests.Support;

internal static class TestData
{
    public static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public static PriceUpdate Update(string instrumentId, decimal price, long revision, string currency = "USD")
        => new(instrumentId, price, currency, T0.AddSeconds(revision), revision);

    public static SourceSnapshot Snapshot(long revision, params PriceUpdate[] records)
        => new(revision, T0.AddSeconds(revision), records);

    public static PriceRecord Record(string sourceId, string instrumentId, decimal price, long revision, string currency = "USD")
        => new(
            sourceId,
            instrumentId,
            price,
            currency,
            T0.AddSeconds(revision),
            T0.AddSeconds(revision).AddMilliseconds(5),
            revision);

    /// <summary>測試用 options：停用 stale 偵測、退避上限 8 秒（配合 FakeTimeProvider，唔使真係等）。</summary>
    public static DataHubOptions NewOptions(Action<DataHubOptions>? configure = null)
    {
        var options = new DataHubOptions();
        options.Freshness.StaleAfterSeconds = 0;
        options.Snapshot.RetryInitialDelaySeconds = 1;
        options.Snapshot.RetryMaxDelaySeconds = 8;
        configure?.Invoke(options);
        return options;
    }
}
