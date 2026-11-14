using DataHub.Api.Configuration;
using DataHub.Api.Domain;
using DataHub.Api.Persistence;
using DataHub.Api.Sources;
using DataHub.Api.Store;
using DataHub.Api.News;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace DataHub.Api.Sync;

/// <summary>每個已註冊來源一個 <see cref="SourceRunner"/>。</summary>
public sealed class SourceRegistry
{
    private readonly Dictionary<string, SourceRunner> _byId;

    public SourceRegistry(
        ISourceCatalog catalog,
        ILatestStateStore store,
        IOptions<DataHubOptions> options,
        TimeProvider time,
        ILoggerFactory loggerFactory)
    {
        Runners = catalog.Adapters
            .Select(adapter => new SourceRunner(
                adapter,
                store,
                options.Value,
                time,
                loggerFactory.CreateLogger($"DataHub.Source.{adapter.Descriptor.SourceId}")))
            .ToArray();
        _byId = Runners.ToDictionary(runner => runner.SourceId, StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyList<SourceRunner> Runners { get; }

    public SourceRunner? Find(string sourceId) => _byId.GetValueOrDefault(sourceId);
}

/// <summary>啟動所有 source runner。每個 runner 自己處理錯誤同重試，所以一個來源失敗唔會停止其他來源。</summary>
public sealed class SourceSupervisor(SourceRegistry registry, ILogger<SourceSupervisor> logger) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Starting {Count} source runner(s).", registry.Runners.Count);
        return Task.WhenAll(registry.Runners.Select(runner => runner.RunAsync(stoppingToken)));
    }
}

/// <summary>
/// 啟動時由 checkpoint 恢復資料。必須喺 Kestrel 開始接受請求之前完成（hosted services 比 web server 先啟動），
/// 所以第一個 API 回應已經可以讀到恢復嘅數據，而且各來源會標示為 Recovering（唔係 Live），直到重新同步完成。
/// Checkpoint 缺失或損壞唔會令啟動失敗：會 fallback 去 .bak，再唔得就由來源 snapshot 重建。
/// </summary>
public sealed class RecoveryHostedService(
    ILatestStateStore store,
    ICheckpointStore checkpointStore,
    CheckpointService checkpoints,
    SourceRegistry registry,
    IOptions<DataHubOptions> options,
    ILogger<RecoveryHostedService> logger,
    NewsStore? news = null) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!options.Value.Checkpoint.Enabled)
        {
            logger.LogInformation("Checkpoint disabled: starting empty, all sources will sync from their snapshots.");
            return;
        }

        CheckpointLoadResult load;
        try
        {
            load = await checkpointStore.LoadAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Checkpoint could not be read; falling back to source snapshots.");
            load = new CheckpointLoadResult(null, null, [$"Checkpoint could not be read: {ex.Message}"]);
        }

        var warnings = new List<string>(load.Warnings);
        var restoredRecords = 0L;
        if (load.Payload is not null)
        {
            news?.Restore(load.Payload.News, load.Payload.NewsIdentities);
            foreach (var data in load.Payload.Sources)
            {
                var runner = registry.Find(data.SourceId);
                if (runner is null)
                {
                    var message = $"Checkpoint contains source '{data.SourceId}' which is not registered; ignored.";
                    warnings.Add(message);
                    logger.LogWarning("{Message}", message);
                    continue;
                }

                store.Restore(data with { SourceId = runner.SourceId });
                runner.MarkRestoredFromCheckpoint();
                restoredRecords += data.RecordCount;
            }

            logger.LogInformation(
                "Restored {Records} record(s) from {From} checkpoint saved at {SavedAt:O}. Sources stay Recovering until re-synced.",
                restoredRecords, load.LoadedFrom, load.Payload.SavedAtUtc);
        }
        else
        {
            logger.LogInformation("No usable checkpoint found: sources will sync from their snapshots.");
        }

        checkpoints.OnLoaded(load with { Warnings = warnings }, store.Version);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

public sealed record SourceView(
    SourceDescriptor Descriptor,
    SourceStatus Status,
    SourceData Data,
    SourceRuntimeState Runtime);

/// <summary>將 registry、store 同 runtime 狀態組合成 API 用嘅唯讀視圖；status 喺讀取時按當刻時間計算。</summary>
public sealed class SourceViewService(SourceRegistry registry, ILatestStateStore store, TimeProvider time)
{
    public IReadOnlyList<SourceView> GetAll() => GetAll(time.GetUtcNow());

    public IReadOnlyList<SourceView> GetAll(DateTimeOffset now)
        => registry.Runners.Select(runner => Build(runner, now)).ToArray();

    public SourceView? Find(string sourceId)
    {
        var runner = registry.Find(sourceId);
        return runner is null ? null : Build(runner, time.GetUtcNow());
    }

    private SourceView Build(SourceRunner runner, DateTimeOffset now)
    {
        var runtime = runner.Runtime.State;
        var data = store.GetSource(runner.SourceId) ?? SourceData.Empty(runner.SourceId);
        return new SourceView(runner.Descriptor, SourceStatusEvaluator.Evaluate(runtime, now, runner.StaleAfter), data, runtime);
    }
}
