namespace DataHub.Api.Domain;

/// <summary>
/// 來源提供嘅一筆價格觀察值。Snapshot 內每筆 record 同 streaming / polling update 共用呢個型別。
/// <see cref="Revision"/> 係 source-local 單調遞增序號，只喺同一個 SourceId（同一個 source session）內有排序意義，
/// 唔可以拎去同其他來源比較。
/// </summary>
public sealed record PriceUpdate(
    string InstrumentId,
    decimal Price,
    string Currency,
    DateTimeOffset SourceUpdatedAtUtc,
    long Revision);

/// <summary>
/// Hub 內保存嘅最新狀態。Key = (SourceId, InstrumentId)，所以不同來源嘅相同 InstrumentId 唔會互相覆蓋。
/// </summary>
public sealed record PriceRecord(
    string SourceId,
    string InstrumentId,
    decimal Price,
    string Currency,
    DateTimeOffset SourceUpdatedAtUtc,
    DateTimeOffset ReceivedAtUtc,
    long SourceRevision);

public enum SourceKind
{
    Streaming,
    Polling,
}

public sealed record SourceDescriptor(
    string SourceId,
    string DisplayName,
    SourceKind Kind,
    bool IsMock,
    int PriceDecimals,
    string Description);

/// <summary>
/// 完整 snapshot。<see cref="Revision"/> 係切 snapshot 嗰刻嘅 source revision：
/// snapshot 包含 Revision 及以前嘅所有變更；唔喺 snapshot 內、而且 revision 不大於 Revision 嘅 record 視為已經唔係成員。
/// </summary>
public sealed record SourceSnapshot(
    long Revision,
    DateTimeOffset TakenAtUtc,
    IReadOnlyList<PriceUpdate> Records);

/// <summary>Polling 結果：自 afterRevision 之後變更過嘅 record（每個 instrument 只有最新值），以及 poll 當刻嘅 source revision。</summary>
public sealed record PollResult(
    long Revision,
    IReadOnlyList<PriceUpdate> Updates);

public enum SourceStatus
{
    Initializing,
    Live,
    Recovering,
    Stale,
    Disconnected,
}
