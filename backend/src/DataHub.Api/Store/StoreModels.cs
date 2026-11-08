using System.Collections.Immutable;
using DataHub.Api.Domain;

namespace DataHub.Api.Store;

/// <summary>
/// 一個來源嘅不可變狀態：records 同 metadata 一齊原子替換，所以 reader（API / checkpoint）攞到嘅永遠係該來源某個時間點嘅一致切面。
/// </summary>
public sealed record SourceData
{
    public required string SourceId { get; init; }

    /// <summary>Key = InstrumentId（ordinal 排序），所以分頁次序穩定。</summary>
    public ImmutableSortedDictionary<string, PriceRecord> Records { get; init; } = EmptyRecords;

    /// <summary>
    /// 最近一次套用 snapshot 嘅 cut revision（0 = 未有）。喺呢個 revision 或以前、但 store 冇嘅 key，
    /// 之後遲到嘅舊 update 唔可以將佢「復活」。
    /// </summary>
    public long SnapshotRevision { get; init; }

    public DateTimeOffset? LastSnapshotAtUtc { get; init; }
    public DateTimeOffset? LastUpdateReceivedAtUtc { get; init; }

    public int RecordCount => Records.Count;

    public static ImmutableSortedDictionary<string, PriceRecord> EmptyRecords { get; }
        = ImmutableSortedDictionary.Create<string, PriceRecord>(StringComparer.Ordinal);

    public static SourceData Empty(string sourceId) => new() { SourceId = sourceId };

    public static SourceData FromRecords(
        string sourceId,
        IEnumerable<PriceRecord> records,
        long snapshotRevision,
        DateTimeOffset? lastSnapshotAtUtc,
        DateTimeOffset? lastUpdateReceivedAtUtc)
    {
        var builder = EmptyRecords.ToBuilder();
        foreach (var record in records)
        {
            builder[record.InstrumentId] = record with { SourceId = sourceId };
        }

        return new SourceData
        {
            SourceId = sourceId,
            Records = builder.ToImmutable(),
            SnapshotRevision = snapshotRevision,
            LastSnapshotAtUtc = lastSnapshotAtUtc,
            LastUpdateReceivedAtUtc = lastUpdateReceivedAtUtc,
        };
    }
}

public enum SnapshotApplyMode
{
    /// <summary>以 revision 比較合併：只接受較新嘅 record；cut 之前已經唔喺 snapshot 嘅 record 會被移除。</summary>
    Merge,

    /// <summary>以 snapshot 完整取代該來源嘅現有資料。用於 checkpoint 恢復後嘅第一個 snapshot（舊 revision 同新 session 唔可比較）。</summary>
    Replace,
}

public readonly record struct UpdateApplyResult(int Applied, int Ignored);

public readonly record struct SnapshotApplyResult(
    int Added,
    int Updated,
    int Removed,
    int KeptNewer,
    int TotalRecords,
    bool Skipped);

public enum PriceSortField { SourceId, InstrumentId, Price, Currency, SourceUpdatedAtUtc, ReceivedAtUtc }

public sealed record PriceQuery(
    string? SourceId, string? InstrumentContains, int Page, int PageSize,
    PriceSortField SortBy = PriceSortField.SourceId, bool Descending = false,
    string? Currency = null, decimal? MinPrice = null, decimal? MaxPrice = null);

public sealed record PriceQueryResult(IReadOnlyList<PriceRecord> Items, long TotalCount);

public sealed record StoreCapture(long Version, IReadOnlyList<SourceData> Sources);
