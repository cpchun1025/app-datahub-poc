using DataHub.Api.Configuration;
using DataHub.Api.Store;
using Microsoft.Extensions.Options;

namespace DataHub.Api.Persistence;

public enum CheckpointTrigger
{
    Periodic,
    Manual,
    Shutdown,
}

public enum CheckpointOutcome
{
    Saved,
    SkippedUnchanged,
    SkippedEmpty,
    Busy,
    Disabled,
    Failed,
}

public sealed record CheckpointStatus
{
    public bool Enabled { get; init; }
    public string? LoadedFrom { get; init; }
    public DateTimeOffset? LoadedSavedAtUtc { get; init; }
    public long? LoadedRecordCount { get; init; }
    public IReadOnlyList<string> LoadWarnings { get; init; } = [];
    public bool SaveInProgress { get; init; }
    public DateTimeOffset? LastAttemptAtUtc { get; init; }
    public DateTimeOffset? LastSuccessAtUtc { get; init; }
    public long? LastSuccessVersion { get; init; }
    public long? LastSuccessRecordCount { get; init; }
    public long? LastSuccessBytes { get; init; }
    public double? LastSuccessDurationMs { get; init; }
    public DateTimeOffset? LastErrorAtUtc { get; init; }
    public string? LastError { get; init; }
    public int ConsecutiveFailures { get; init; }
}

public sealed record CheckpointRunResult(CheckpointOutcome Outcome, CheckpointStatus Status, string? Error = null);

/// <summary>
/// 協調 checkpoint 保存：同一時間只會有一個保存（唔重疊）、無變動就跳過、失敗只記錄唔影響 API 服務。
/// 保存時只係攞 store 嘅不可變切面，全部 I/O 喺 store 鎖之外進行。
/// </summary>
public sealed class CheckpointService
{
    private readonly ILatestStateStore _store;
    private readonly ICheckpointStore _fileStore;
    private readonly TimeProvider _time;
    private readonly ILogger<CheckpointService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _statusLock = new();
    private CheckpointStatus _status;
    private long _lastSavedVersion = -1;

    public CheckpointService(
        ILatestStateStore store,
        ICheckpointStore fileStore,
        IOptions<DataHubOptions> options,
        TimeProvider time,
        ILogger<CheckpointService> logger)
    {
        _store = store;
        _fileStore = fileStore;
        _time = time;
        _logger = logger;
        Enabled = options.Value.Checkpoint.Enabled;
        _status = new CheckpointStatus { Enabled = Enabled };
    }

    public bool Enabled { get; }

    public CheckpointStatus Status => Volatile.Read(ref _status);

    /// <summary>啟動恢復完成後呼叫。由 backup 恢復時刻意唔記低 version，令下一次定時保存修復 primary。</summary>
    public void OnLoaded(CheckpointLoadResult load, long storeVersion)
    {
        if (load.Payload is not null && load.LoadedFrom == "primary")
        {
            Volatile.Write(ref _lastSavedVersion, storeVersion);
        }

        UpdateStatus(status => status with
        {
            LoadedFrom = load.LoadedFrom,
            LoadedSavedAtUtc = load.Payload?.SavedAtUtc,
            LoadedRecordCount = load.Payload?.RecordCount,
            LoadWarnings = load.Warnings,
        });
    }

    public async Task<CheckpointRunResult> SaveAsync(CheckpointTrigger trigger, CancellationToken cancellationToken)
    {
        if (!Enabled)
        {
            return new CheckpointRunResult(CheckpointOutcome.Disabled, Status);
        }

        if (trigger == CheckpointTrigger.Shutdown)
        {
            await _gate.WaitAsync(cancellationToken);
        }
        else if (!await _gate.WaitAsync(0, cancellationToken))
        {
            return new CheckpointRunResult(CheckpointOutcome.Busy, Status);
        }

        try
        {
            var capture = _store.Capture();

            // 完全未有任何資料（未恢復、未 sync）就唔寫，避免用空檔案蓋走上一次嘅有效 checkpoint。
            if (capture.Sources.All(source => source.RecordCount == 0 && source.LastSnapshotAtUtc is null))
            {
                return new CheckpointRunResult(CheckpointOutcome.SkippedEmpty, Status);
            }

            if (trigger != CheckpointTrigger.Manual && capture.Version == Volatile.Read(ref _lastSavedVersion))
            {
                return new CheckpointRunResult(CheckpointOutcome.SkippedUnchanged, Status);
            }

            var attemptedAt = _time.GetUtcNow();
            UpdateStatus(status => status with { SaveInProgress = true, LastAttemptAtUtc = attemptedAt });

            try
            {
                var payload = new CheckpointPayload(capture.Version, attemptedAt, capture.Sources);
                var result = await _fileStore.SaveAsync(payload, cancellationToken);
                Volatile.Write(ref _lastSavedVersion, capture.Version);

                var savedAt = _time.GetUtcNow();
                UpdateStatus(status => status with
                {
                    SaveInProgress = false,
                    LastSuccessAtUtc = savedAt,
                    LastSuccessVersion = capture.Version,
                    LastSuccessRecordCount = result.RecordCount,
                    LastSuccessBytes = result.Bytes,
                    LastSuccessDurationMs = result.Duration.TotalMilliseconds,
                    LastError = null,
                    LastErrorAtUtc = null,
                    ConsecutiveFailures = 0,
                });

                _logger.LogDebug(
                    "Checkpoint saved: version {Version}, {Records} records, {Bytes} bytes in {Duration:F0} ms ({Trigger}).",
                    capture.Version, result.RecordCount, result.Bytes, result.Duration.TotalMilliseconds, trigger);
                return new CheckpointRunResult(CheckpointOutcome.Saved, Status);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                UpdateStatus(status => status with { SaveInProgress = false });
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Checkpoint save failed ({Trigger}); the Data Hub keeps serving from memory.", trigger);
                var failedAt = _time.GetUtcNow();
                UpdateStatus(status => status with
                {
                    SaveInProgress = false,
                    LastError = ex.Message,
                    LastErrorAtUtc = failedAt,
                    ConsecutiveFailures = status.ConsecutiveFailures + 1,
                });
                return new CheckpointRunResult(CheckpointOutcome.Failed, Status, ex.Message);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private void UpdateStatus(Func<CheckpointStatus, CheckpointStatus> mutate)
    {
        lock (_statusLock)
        {
            Volatile.Write(ref _status, mutate(_status));
        }
    }
}
