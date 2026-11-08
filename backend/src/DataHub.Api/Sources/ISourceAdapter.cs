using DataHub.Api.Domain;

namespace DataHub.Api.Sources;

/// <summary>
/// 來源 adapter 嘅共同合約：每個來源都要提供完整 snapshot。
/// 真實 Solace / REST / MSSQL adapter 只需要實作呢組 interface，唔需要改 store 或 API。
/// </summary>
public interface ISourceAdapter
{
    SourceDescriptor Descriptor { get; }

    /// <summary>取得完整 snapshot。<see cref="SourceSnapshot.Revision"/> 必須係切 snapshot 嗰刻嘅 source revision。</summary>
    Task<SourceSnapshot> GetSnapshotAsync(CancellationToken cancellationToken);
}

/// <summary>Snapshot + streaming 來源。</summary>
public interface IStreamingSourceAdapter : ISourceAdapter
{
    /// <summary>建立 update 訂閱。Hub 會喺攞 snapshot 之前先訂閱，避免 snapshot 同 stream 之間出現空檔。</summary>
    ValueTask<IUpdateSubscription> SubscribeAsync(CancellationToken cancellationToken);
}

public interface IUpdateSubscription : IAsyncDisposable
{
    /// <summary>
    /// 持續回傳 update batch。空 batch 視為 heartbeat（只更新 freshness，唔改價格）。
    /// 連線中斷時要拋出例外或者正常完成，Hub 會重新訂閱並觸發 resync。
    /// </summary>
    IAsyncEnumerable<IReadOnlyList<PriceUpdate>> ReadUpdatesAsync(CancellationToken cancellationToken);
}

/// <summary>Snapshot + 定時 polling 來源。</summary>
public interface IPollingSourceAdapter : ISourceAdapter
{
    TimeSpan PollInterval { get; }

    /// <summary>取得 <paramref name="afterRevision"/> 之後變更過嘅 record（delta）。Polling 睇唔到刪除，刪除要靠下一次完整 snapshot。</summary>
    Task<PollResult> PollAsync(long afterRevision, CancellationToken cancellationToken);
}
