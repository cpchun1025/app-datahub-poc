using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace DataHub.Api.Configuration;

public sealed partial class DataHubOptionsValidator : IValidateOptions<DataHubOptions>
{
    // 上限同時避免 TimeSpan.FromSeconds／FromHours 喺執行期溢位；NaN 同 Infinity 一律拒絕。
    private const double MaxDurationSeconds = 86_400;
    private const double MaxSnapshotIntervalHours = 24 * 365;

    [GeneratedRegex("^[a-z0-9][a-z0-9._-]{0,63}$")]
    private static partial Regex SourceIdPattern();

    private static bool Positive(double value, double max) => double.IsFinite(value) && value > 0 && value <= max;

    private static bool Between(double value, double min, double max) => double.IsFinite(value) && value >= min && value <= max;

    public ValidateOptionsResult Validate(string? name, DataHubOptions options)
    {
        var failures = new List<string>();

        if (!options.UseMockSources)
        {
            failures.Add("DataHub:UseMockSources=false：呢個 PoC 只實作 mock sources，真實 adapter 需要自行註冊（見 README 嘅 extension points）。");
        }

        if (!Positive(options.Snapshot.IntervalHours, MaxSnapshotIntervalHours))
            failures.Add($"DataHub:Snapshot:IntervalHours 必須大於 0 並且不超過 {MaxSnapshotIntervalHours:0}（一年）。");
        if (!Positive(options.Snapshot.RetryInitialDelaySeconds, MaxDurationSeconds))
            failures.Add($"DataHub:Snapshot:RetryInitialDelaySeconds 必須大於 0 並且不超過 {MaxDurationSeconds:0}。");
        if (!Between(options.Snapshot.RetryMaxDelaySeconds, options.Snapshot.RetryInitialDelaySeconds, MaxDurationSeconds))
            failures.Add($"DataHub:Snapshot:RetryMaxDelaySeconds 不可以細過 RetryInitialDelaySeconds，亦不可以超過 {MaxDurationSeconds:0}。");

        if (options.Checkpoint.Enabled)
        {
            if (string.IsNullOrWhiteSpace(options.Checkpoint.Directory))
                failures.Add("DataHub:Checkpoint:Directory 不可以留空。");
            if (!Between(options.Checkpoint.IntervalSeconds, 1, MaxDurationSeconds))
                failures.Add($"DataHub:Checkpoint:IntervalSeconds 必須介乎 1 至 {MaxDurationSeconds:0} 秒。");
            if (string.IsNullOrWhiteSpace(options.Checkpoint.FileName)
                || options.Checkpoint.FileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                failures.Add("DataHub:Checkpoint:FileName 必須係有效檔案名稱（唔可以包含路徑）。");
        }

        if (!Between(options.Freshness.StaleAfterSeconds, 0, MaxDurationSeconds))
            failures.Add($"DataHub:Freshness:StaleAfterSeconds 必須介乎 0 至 {MaxDurationSeconds:0}（0 代表停用 stale 偵測）。");

        if (options.Api.MaxPageSize is < 1 or > 5000)
            failures.Add("DataHub:Api:MaxPageSize 必須介乎 1 至 5000。");
        if (options.Api.DefaultPageSize < 1 || options.Api.DefaultPageSize > options.Api.MaxPageSize)
            failures.Add("DataHub:Api:DefaultPageSize 必須介乎 1 同 MaxPageSize 之間。");
        if (options.Api.ExportMaxRows is < 1 or > 100_000)
            failures.Add("DataHub:Api:ExportMaxRows 必須介乎 1 至 100000。");

        ValidateMockSources(options.Mock, failures);

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    private static void ValidateMockSources(MockOptions mock, List<string> failures)
    {
        if (mock.Sources.Count == 0)
        {
            failures.Add("DataHub:Mock:Sources 至少要有一個 mock source。");
            return;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < mock.Sources.Count; i++)
        {
            var source = mock.Sources[i];
            var prefix = $"DataHub:Mock:Sources:{i}";

            var sourceId = source.SourceId ?? string.Empty;
            if (!SourceIdPattern().IsMatch(sourceId))
                failures.Add($"{prefix}:SourceId 必須係細階英文字母、數字、'.'、'_' 或 '-'（最多 64 字元），而且要以字母或數字開頭。");
            else if (!seen.Add(sourceId))
                failures.Add($"{prefix}:SourceId '{sourceId}' 重複。");

            if (string.IsNullOrWhiteSpace(source.DisplayName))
                failures.Add($"{prefix}:DisplayName 不可以留空。");
            if (source.InstrumentCount is < 0 or > 2_000_000)
                failures.Add($"{prefix}:InstrumentCount 必須介乎 0 至 2000000。");
            if (source.UpdateIntervalMs < 10)
                failures.Add($"{prefix}:UpdateIntervalMs 必須至少 10。");
            if (source.UpdatesPerTick < 0)
                failures.Add($"{prefix}:UpdatesPerTick 不可以為負數。");
            if (!Positive(source.PollIntervalSeconds, MaxDurationSeconds))
                failures.Add($"{prefix}:PollIntervalSeconds 必須大於 0 並且不超過 {MaxDurationSeconds:0}。");
            if (source.SnapshotLatencyMs < 0)
                failures.Add($"{prefix}:SnapshotLatencyMs 不可以為負數。");
            if (string.IsNullOrWhiteSpace(source.Currency) || source.Currency.Length != 3)
                failures.Add($"{prefix}:Currency 必須係 3 個字母嘅貨幣代碼（例如 USD）。");
            if (source.PriceDecimals is < 0 or > 8)
                failures.Add($"{prefix}:PriceDecimals 必須介乎 0 至 8。");
            if (!double.IsFinite(source.MaxMovePercent) || source.MaxMovePercent is <= 0 or > 50)
                failures.Add($"{prefix}:MaxMovePercent 必須大於 0 並且不超過 50。");
            if (!Between(source.MembershipChangeProbability, 0, 1))
                failures.Add($"{prefix}:MembershipChangeProbability 必須介乎 0 至 1。");
        }
    }
}
