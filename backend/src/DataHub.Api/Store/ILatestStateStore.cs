using DataHub.Api.Domain;

namespace DataHub.Api.Store;

/// <summary>
/// In-memory latest-state store。API 主要讀取路徑；寫入只會喺記憶體內完成（唔做 network / disk I/O）。
/// </summary>
public interface ILatestStateStore
{
    /// <summary>每次資料變更後遞增，供 checkpoint 判斷「自上次保存後有冇變動」。</summary>
    long Version { get; }

    IReadOnlyList<string> SourceIds { get; }

    /// <summary>套用 streaming / polling updates。以 source-local revision 比較，舊值或重複值會被忽略。</summary>
    UpdateApplyResult ApplyUpdates(string sourceId, IReadOnlyList<PriceUpdate> updates, DateTimeOffset receivedAtUtc);

    SnapshotApplyResult ApplySnapshot(string sourceId, SourceSnapshot snapshot, DateTimeOffset receivedAtUtc, SnapshotApplyMode mode);

    /// <summary>由 checkpoint 恢復一個來源嘅資料（取代現有內容）。</summary>
    void Restore(SourceData data);

    SourceData? GetSource(string sourceId);

    IReadOnlyList<SourceData> GetAllSources();

    PriceRecord? Find(string sourceId, string instrumentId);

    PriceQueryResult Query(PriceQuery query);

    /// <summary>
    /// 攞各來源嘅不可變切面。每個來源內部一致；跨來源只係逐個來源順序讀取（fuzzy），並非全域 transactional。
    /// </summary>
    StoreCapture Capture();
}
