namespace DataHub.Api.Hosting;

/// <summary>進程啟動時間，供 /api/health 顯示（驗證重啟時好用）。</summary>
public sealed class ServiceInfo(TimeProvider time)
{
    public DateTimeOffset StartedAtUtc { get; } = time.GetUtcNow();
}
