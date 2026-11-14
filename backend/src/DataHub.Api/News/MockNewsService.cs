namespace DataHub.Api.News;

public sealed record NewsStatus(
    string SourceId,
    string State,
    DateTimeOffset? LastAttemptAtUtc,
    DateTimeOffset? LastSuccessfulFetchAtUtc,
    DateTimeOffset? LatestArticlePublishedAtUtc,
    string? LastError,
    int CachedItemCount);

public sealed class MockNewsService(NewsStore store, TimeProvider time, ILogger<MockNewsService> logger) : BackgroundService
{
    public const string SourceId = "mock-news";
    private readonly object _gate = new();
    private DateTimeOffset? _attempt;
    private DateTimeOffset? _success;
    private DateTimeOffset? _outageUntil;
    private string? _error;
    private int _fetchCount;

    public NewsStatus Status()
    {
        var records = store.Capture().Items.Where(item => item.SourceId == SourceId).ToArray();
        lock (_gate)
            return new NewsStatus(SourceId, _error is not null ? "Disconnected"
                : _success is null ? (records.Length > 0 ? "Recovering" : "Initializing") : "Connected", _attempt, _success,
                records.Max(item => item.PublishedAtUtc), _error, records.Length);
    }

    public DateTimeOffset? SetOutage(int seconds)
    {
        lock (_gate)
        {
            _outageUntil = seconds == 0 ? null : time.GetUtcNow().AddSeconds(seconds);
            return _outageUntil;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var now = time.GetUtcNow();
            try
            {
                lock (_gate)
                {
                    _attempt = now;
                    if (_outageUntil > now) throw new InvalidOperationException("Simulated mock news outage.");
                }
                var first = new MockNewsItem("news-1", _fetchCount == 0 ? "Synthetic markets update" : "Synthetic markets update revised",
                    "https://example.com/news/1", "Markets", "2026-01-02T12:00:00Z", null, "en");
                var items = new[]
                {
                    first,
                    first,
                    new MockNewsItem("news-2", "模擬新聞：市場資訊", "https://example.com/news/2", null, "2026-01-01", "Synthetic publisher", "zh"),
                    new MockNewsItem("news-3", "Synthetic briefing", "https://example.com/news/3", "Briefing", null, null, "en"),
                };
                store.Upsert(items.Select(item => NewsMapper.Map(item, SourceId, now)).ToArray());
                lock (_gate) { _fetchCount++; _success = now; _error = null; }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                lock (_gate) _error = ex.Message;
                logger.LogWarning(ex, "Mock news fetch failed; cached news retained.");
            }
            await Task.Delay(TimeSpan.FromSeconds(5), time, stoppingToken);
        }
    }
}
