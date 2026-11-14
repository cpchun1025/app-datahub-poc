using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using DataHub.Api.Configuration;
using DataHub.Api.News;
using DataHub.Api.Persistence;
using DataHub.Api.Store;
using DataHub.Api.Sync;
using DataHub.Api.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace DataHub.Api.Tests.News;

public class NewsTests
{
    private static readonly DateTimeOffset Now = new(2026, 2, 1, 0, 0, 0, TimeSpan.Zero);
    private static MockNewsItem Item(string id, string? date = "2026-01-30T12:00:00+08:00") =>
        new(id, "Headline", "https://example.com/article", null, date, null, "en");
    private static NewsRecord Map(string id, DateTimeOffset? at = null) => NewsMapper.Map(Item(id), "mock-news", at ?? Now);
    private static NewsStore Store(FakeTimeProvider time, int max = 500, int days = 7) =>
        new(Options.Create(new DataHubOptions { News = new NewsOptions { MaxItems = max, RetentionDays = days } }), time);

    [Fact]
    public void MapperDistinguishesInstantsDatesAndUnknown()
    {
        var instant = Map("instant");
        Assert.Equal(PublicationTimePrecision.Instant, instant.PublicationTimePrecision);
        Assert.Equal(new DateTimeOffset(2026, 1, 30, 4, 0, 0, TimeSpan.Zero), instant.PublishedAtUtc);
        var date = NewsMapper.Map(Item("date", "2026-01-30"), "mock-news", Now);
        Assert.Equal(new DateOnly(2026, 1, 30), date.PublishedDate);
        Assert.Null(date.PublishedAtUtc);
        var unknown = NewsMapper.Map(Item("unknown", null), "mock-news", Now);
        Assert.Null(unknown.PublishedAtUtc);
        Assert.Single(unknown.MappingWarnings);
    }

    [Theory]
    [InlineData("javascript:alert(1)", "2026-01-30")]
    [InlineData("https://example.com", "2026-01-30T10:00:00")]
    [InlineData("https://example.com", "bad")]
    public void MapperRejectsInvalidInput(string link, string date) =>
        Assert.Throws<ArgumentException>(() => NewsMapper.Map(Item("id", date) with { Link = link }, "mock-news", Now));

    [Fact]
    public void StoreUpsertsPerSourceAndEvictsByFirstSeenDeterministically()
    {
        var time = new FakeTimeProvider(Now);
        var store = Store(time, max: 2);
        store.Upsert([Map("b"), Map("a"), Map("a") with { SourceId = "other" }]);
        Assert.Equal(["b", "a"], store.Capture().Items.OrderBy(x => x.SourceId).Select(x => x.ArticleId));
        time.Advance(TimeSpan.FromDays(6));
        store.Upsert([Map("b", time.GetUtcNow()) with { Title = "Revised" }]);
        var revised = Assert.Single(store.Capture().Items, x => x.ArticleId == "b");
        Assert.Equal(Now, revised.FirstSeenAtUtc);
        time.Advance(TimeSpan.FromDays(1));
        Assert.Empty(store.Capture().Items);
    }

    [Fact]
    public void RecoveredCacheIsNotConnectedBeforeFetch()
    {
        var time = new FakeTimeProvider(Now);
        var store = Store(time);
        store.Upsert([Map("a")]);
        var service = new MockNewsService(store, time, NullLogger<MockNewsService>.Instance);
        Assert.Equal("Recovering", service.Status().State);
        Assert.Null(service.Status().LastSuccessfulFetchAtUtc);
    }

    [Fact]
    public void RepeatedFetchDoesNotReinsertExpiredIdentityIncludingAfterRestore()
    {
        var time = new FakeTimeProvider(Now);
        var store = Store(time);
        store.Upsert([Map("a")]);
        time.Advance(TimeSpan.FromDays(7));
        Assert.Empty(store.Capture().Items);
        store.Upsert([Map("a", time.GetUtcNow())]);
        var captured = store.Capture();
        Assert.Empty(captured.Items);
        Assert.Equal(Now, Assert.Single(captured.Identities).FirstSeenAtUtc);
        var payload = new CheckpointPayload(1, time.GetUtcNow(), []) { NewsIdentities = captured.Identities };
        var restoredPayload = CheckpointSerializer.Deserialize(CheckpointSerializer.Serialize(payload));
        var restored = Store(time);
        restored.Restore(restoredPayload.News, restoredPayload.NewsIdentities);
        restored.Upsert([Map("a", time.GetUtcNow())]);
        Assert.Empty(restored.Capture().Items);
    }

    [Fact]
    public void CheckpointSupportsLegacyAndVerifiesNewsChecksum()
    {
        var payload = new CheckpointPayload(1, Now, []) { News = [Map("a")] };
        var bytes = CheckpointSerializer.Serialize(payload);
        Assert.Equal("a", Assert.Single(CheckpointSerializer.Deserialize(bytes).News).ArticleId);
        var tampered = Encoding.UTF8.GetString(bytes).Replace("Headline", "Changed", StringComparison.Ordinal);
        Assert.Throws<CheckpointFormatException>(() => CheckpointSerializer.Deserialize(Encoding.UTF8.GetBytes(tampered)));
        var sources = "[]";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sources))).ToLowerInvariant();
        var legacy = $$"""{"schemaVersion":1,"checkpointVersion":1,"savedAtUtc":"2026-02-01T00:00:00Z","recordCount":0,"payloadSha256":"{{hash}}","sources":[]}""";
        Assert.Empty(CheckpointSerializer.Deserialize(Encoding.UTF8.GetBytes(legacy)).News);
    }

    [Fact]
    public async Task FutureCheckpointNeverOverwrittenEvenWhenBackupIsFuture()
    {
        using var directory = new TempDirectory();
        var options = Options.Create(new DataHubOptions { Checkpoint = new CheckpointOptions { Directory = directory.FullPath } });
        var files = new FileCheckpointStore(options, new TestHostEnvironment(directory.FullPath), NullLogger<FileCheckpointStore>.Instance);
        await files.SaveAsync(new CheckpointPayload(1, Now, []), CancellationToken.None);
        var backup = directory.File("checkpoint.json.bak");
        await File.WriteAllTextAsync(backup, """{"schemaVersion":999}""");
        await Assert.ThrowsAsync<IOException>(() => files.SaveAsync(new CheckpointPayload(2, Now, []), CancellationToken.None));
        Assert.Equal("""{"schemaVersion":999}""", await File.ReadAllTextAsync(backup));
    }

    [Fact]
    public async Task RecoveryAppliesRetentionAndLeavesNewsRecovering()
    {
        var time = new FakeTimeProvider(Now);
        var options = Options.Create(new DataHubOptions());
        var news = Store(time);
        var prices = new LatestStateStore(["price"]);
        var registry = new SourceRegistry(new FakeCatalog(new FakeStreamingAdapter("price")), prices, options, time, NullLoggerFactory.Instance);
        var files = new FakeCheckpointStore
        {
            LoadResult = new CheckpointLoadResult(
                new CheckpointPayload(1, Now, []) { News = [Map("kept"), Map("expired", Now.AddDays(-8))] },
                "backup", ["Primary corrupt."]),
        };
        var checkpoints = new CheckpointService(prices, files, options, time, NullLogger<CheckpointService>.Instance, news);
        var recovery = new RecoveryHostedService(prices, files, checkpoints, registry, options,
            NullLogger<RecoveryHostedService>.Instance, news);
        await recovery.StartAsync(CancellationToken.None);
        Assert.Equal("kept", Assert.Single(news.Capture().Items).ArticleId);
        Assert.Equal("Recovering", new MockNewsService(news, time, NullLogger<MockNewsService>.Instance).Status().State);
        Assert.Contains("Primary corrupt.", checkpoints.Status.LoadWarnings);
        Assert.Equal(CheckpointOutcome.Saved, (await checkpoints.SaveAsync(CheckpointTrigger.Periodic, CancellationToken.None)).Outcome);
        Assert.Equal("kept", Assert.Single(Assert.Single(files.Saved).News).ArticleId);
    }

    [Fact]
    public async Task ApiFiltersPaginatesAndRejectsBadQueries()
    {
        using var directory = new TempDirectory();
        await using var factory = new DataHubFactory(null, directory.FullPath);
        var client = factory.Api;
        NewsListResponse? data = null;
        for (var i = 0; i < 50; i++)
        {
            data = await client.GetFromJsonAsync<NewsListResponse>("/api/news?pageSize=1", DataHubJson.CreateOptions());
            if (data!.TotalCount == 3) break;
            await Task.Delay(20);
        }
        Assert.NotNull(data);
        Assert.Equal(3, data.TotalCount);
        Assert.Single(data.Items);
        Assert.Equal("news-1", data.Items[0].ArticleId);
        var unknown = await client.GetFromJsonAsync<NewsListResponse>("/api/news?page=3&pageSize=1", DataHubJson.CreateOptions());
        Assert.Equal(PublicationTimePrecision.Unknown, Assert.Single(unknown!.Items).PublicationTimePrecision);
        var filtered = await client.GetFromJsonAsync<NewsListResponse>("/api/news?category=Markets&publishedFrom=2026-01-02&publishedTo=2026-01-02", DataHubJson.CreateOptions());
        Assert.Equal("news-1", Assert.Single(filtered!.Items).ArticleId);
        var ascending = await client.GetFromJsonAsync<NewsListResponse>("/api/news?sortDirection=asc", DataHubJson.CreateOptions());
        Assert.Equal(["news-2", "news-1", "news-3"], ascending!.Items.Select(x => x.ArticleId));
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/news?publishedFrom=bad")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/news?page=0")).StatusCode);
    }

    [Fact]
    public async Task MockOutageRetainsCachedNewsAndDoesNotAffectPrices()
    {
        using var directory = new TempDirectory();
        await using var factory = new DataHubFactory(null, directory.FullPath);
        var client = factory.Api;
        NewsListResponse? data = null;
        for (var i = 0; i < 100; i++)
        {
            data = await client.GetFromJsonAsync<NewsListResponse>("/api/news", DataHubJson.CreateOptions());
            if (data!.Source.State == "Connected") break;
            await Task.Delay(20);
        }
        Assert.Equal("Connected", data!.Source.State);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/api/admin/mock-news/outage?seconds=20", null)).StatusCode);
        for (var i = 0; i < 150; i++)
        {
            data = await client.GetFromJsonAsync<NewsListResponse>("/api/news", DataHubJson.CreateOptions());
            if (data!.Source.State == "Disconnected") break;
            await Task.Delay(50);
        }
        Assert.Equal("Disconnected", data!.Source.State);
        Assert.Equal(3, data.Source.CachedItemCount);
        Assert.NotNull(data.Source.LastSuccessfulFetchAtUtc);
        Assert.NotNull(data.Source.LastError);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/prices")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/api/admin/mock-news/outage?seconds=0", null)).StatusCode);
        for (var i = 0; i < 150; i++)
        {
            data = await client.GetFromJsonAsync<NewsListResponse>("/api/news", DataHubJson.CreateOptions());
            if (data!.Source.State == "Connected") break;
            await Task.Delay(50);
        }
        Assert.Equal("Connected", data!.Source.State);
        Assert.Null(data.Source.LastError);
        Assert.Equal(3, data.Source.CachedItemCount);
    }
}
