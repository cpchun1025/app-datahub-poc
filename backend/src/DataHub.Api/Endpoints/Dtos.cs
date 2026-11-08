using DataHub.Api.Domain;

namespace DataHub.Api.Endpoints;

public sealed record PriceDto(
    string SourceId,
    string InstrumentId,
    decimal Price,
    string Currency,
    DateTimeOffset SourceUpdatedAtUtc,
    DateTimeOffset ReceivedAtUtc,
    long SourceRevision,
    SourceStatus SourceStatus)
{
    public static PriceDto From(PriceRecord record, SourceStatus status) => new(
        record.SourceId, record.InstrumentId, record.Price, record.Currency,
        record.SourceUpdatedAtUtc, record.ReceivedAtUtc, record.SourceRevision, status);
}

/// <summary>價格列表回應附帶各來源嘅精簡 freshness，前端唔使為咗顯示狀態而額外 call /api/sources。</summary>
public sealed record SourceFreshnessDto(
    string SourceId,
    SourceStatus Status,
    bool SyncedSinceStart,
    DateTimeOffset? LastUpdateReceivedAtUtc);

public sealed record PriceListResponse(
    DateTimeOffset ServerTimeUtc,
    int Page,
    int PageSize,
    long TotalCount,
    int TotalPages,
    IReadOnlyList<PriceDto> Items,
    IReadOnlyList<SourceFreshnessDto> Sources);

public sealed record SourceDto(
    string SourceId,
    string DisplayName,
    SourceKind Kind,
    bool IsMock,
    string Description,
    int PriceDecimals,
    SourceStatus Status,
    long RecordCount,
    bool RestoredFromCheckpoint,
    bool SyncedSinceStart,
    bool SnapshotInProgress,
    DateTimeOffset? LastSnapshotAtUtc,
    DateTimeOffset? LastUpdateReceivedAtUtc,
    DateTimeOffset? LastFeedActivityAtUtc,
    DateTimeOffset? NextSnapshotDueAtUtc,
    string? LastError,
    DateTimeOffset? LastErrorAtUtc);

public sealed record SourceListResponse(
    DateTimeOffset ServerTimeUtc,
    IReadOnlyList<SourceDto> Sources);

public sealed record SourceSummaryDto(
    int Total,
    int Live,
    int Initializing,
    int Recovering,
    int Stale,
    int Disconnected);

public sealed record CheckpointDto(
    bool Enabled,
    string? LoadedFrom,
    DateTimeOffset? LoadedSavedAtUtc,
    long? LoadedRecordCount,
    IReadOnlyList<string> LoadWarnings,
    bool SaveInProgress,
    DateTimeOffset? LastAttemptAtUtc,
    DateTimeOffset? LastSuccessAtUtc,
    long? LastSuccessRecordCount,
    long? LastSuccessBytes,
    double? LastSuccessDurationMs,
    DateTimeOffset? LastErrorAtUtc,
    string? LastError,
    int ConsecutiveFailures);

public sealed record HealthResponse(
    string Status,
    DateTimeOffset ServerTimeUtc,
    DateTimeOffset StartedAtUtc,
    bool MockMode,
    bool AdminEnabled,
    SourceSummaryDto Sources,
    CheckpointDto Checkpoint);

public sealed record SnapshotRefreshResponse(
    string SourceId,
    int Added,
    int Updated,
    int Removed,
    int KeptNewer,
    int TotalRecords,
    bool Skipped);

public sealed record CheckpointRunResponse(
    string Outcome,
    CheckpointDto Checkpoint);

public sealed record MockOutageResponse(
    string SourceId,
    int Seconds,
    DateTimeOffset? OutageUntilUtc);
