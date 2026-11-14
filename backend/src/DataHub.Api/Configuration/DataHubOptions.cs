using DataHub.Api.Domain;

namespace DataHub.Api.Configuration;

/// <summary>
/// 所有設定集中喺 "DataHub" section。環境變數用 DataHub__Checkpoint__Directory 呢類寫法覆蓋。
/// </summary>
public sealed class DataHubOptions
{
    public const string SectionName = "DataHub";

    /// <summary>PoC 只實作 mock sources；設為 false 會喺啟動時報錯，真實 adapter 要自行註冊（見 README 嘅 extension points）。</summary>
    public bool UseMockSources { get; set; } = true;

    public SnapshotOptions Snapshot { get; set; } = new();
    public CheckpointOptions Checkpoint { get; set; } = new();
    public FreshnessOptions Freshness { get; set; } = new();
    public ApiOptions Api { get; set; } = new();
    public MockOptions Mock { get; set; } = new();
    public NewsOptions News { get; set; } = new();
}

public sealed class NewsOptions
{
    public int MaxItems { get; set; } = 500;
    public int RetentionDays { get; set; } = 7;
}

public sealed class SnapshotOptions
{
    /// <summary>完整 source snapshot 刷新間隔，預設 168 小時（七日）。</summary>
    public double IntervalHours { get; set; } = 168;

    /// <summary>snapshot 或 feed 連線失敗後嘅初始重試間隔（之後指數退避）。</summary>
    public double RetryInitialDelaySeconds { get; set; } = 1;

    public double RetryMaxDelaySeconds { get; set; } = 15;

    public TimeSpan Interval => TimeSpan.FromHours(IntervalHours);
    public TimeSpan RetryInitialDelay => TimeSpan.FromSeconds(RetryInitialDelaySeconds);
    public TimeSpan RetryMaxDelay => TimeSpan.FromSeconds(RetryMaxDelaySeconds);
}

public sealed class CheckpointOptions
{
    public bool Enabled { get; set; } = true;

    /// <summary>相對路徑以 ContentRoot 為基準；亦可以係絕對路徑或 UNC path（例如 \\server\share\datahub）。</summary>
    public string Directory { get; set; } = "data/checkpoints";

    public string FileName { get; set; } = "checkpoint.json";

    /// <summary>本地 checkpoint 間隔，同每星期一次嘅 source snapshot 係兩回事。</summary>
    public double IntervalSeconds { get; set; } = 30;

    public bool SaveOnShutdown { get; set; } = true;

    public TimeSpan Interval => TimeSpan.FromSeconds(IntervalSeconds);
}

public sealed class FreshnessOptions
{
    /// <summary>已經 Live 嘅來源，超過呢段時間冇收到任何更新／成功 poll 就標示為 Stale。0 = 停用。</summary>
    public double StaleAfterSeconds { get; set; } = 30;

    public TimeSpan StaleAfter => TimeSpan.FromSeconds(StaleAfterSeconds);
}

public sealed class ApiOptions
{
    public int DefaultPageSize { get; set; } = 100;
    public int MaxPageSize { get; set; } = 500;
    public int ExportMaxRows { get; set; } = 10_000;

    /// <summary>PoC admin endpoints（手動 snapshot / checkpoint / mock outage）。冇認證，唔好暴露到 production。</summary>
    public bool AdminEnabled { get; set; } = true;

    public bool SwaggerUiEnabled { get; set; }

    /// <summary>只喺前端直接跨 origin 呼叫 backend 時先需要；預設經 Vite proxy / nginx 同源存取。</summary>
    public string[] CorsAllowedOrigins { get; set; } = [];
}

public sealed class MockOptions
{
    public List<MockSourceOptions> Sources { get; set; } = [];
}

public sealed class MockSourceOptions
{
    public string SourceId { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public SourceKind Kind { get; set; } = SourceKind.Streaming;

    public int InstrumentCount { get; set; } = 1000;

    /// <summary>模擬 upstream 每隔幾耐產生一批變更。</summary>
    public int UpdateIntervalMs { get; set; } = 250;

    public int UpdatesPerTick { get; set; } = 20;

    /// <summary>Polling source 每隔幾耐 poll 一次（Streaming 忽略）。</summary>
    public double PollIntervalSeconds { get; set; } = 5;

    /// <summary>模擬 snapshot 傳輸時間；期間 upstream 繼續更新，用嚟重現 snapshot 同 update 嘅競爭情況。</summary>
    public int SnapshotLatencyMs { get; set; } = 300;

    public string Currency { get; set; } = "USD";
    public int PriceDecimals { get; set; } = 4;
    public double MaxMovePercent { get; set; } = 0.5;

    /// <summary>每個更新名額（每次 tick 有 UpdatesPerTick 個）變成「新上市／下市」而唔係價格變動嘅機率。下市只會喺下一次完整 snapshot 先反映。</summary>
    public double MembershipChangeProbability { get; set; } = 0.001;

    public int Seed { get; set; } = 1;
}
