using DataHub.Api.Configuration;
using Microsoft.Extensions.Options;

namespace DataHub.Api.News;

public sealed record NewsIdentity(string SourceId, string ArticleId, DateTimeOffset FirstSeenAtUtc);
public sealed record NewsCapture(long Version, IReadOnlyList<NewsRecord> Items, IReadOnlyList<NewsIdentity> Identities);

public sealed class NewsStore(IOptions<DataHubOptions> options, TimeProvider time)
{
    private readonly object _gate = new();
    private readonly Dictionary<(string Source, string Article), NewsRecord> _items = new();
    private readonly Dictionary<(string Source, string Article), DateTimeOffset> _seen = new();
    private readonly NewsOptions _options = options.Value.News;
    private long _version;

    public void Upsert(IEnumerable<NewsRecord> records)
    {
        lock (_gate)
        {
            foreach (var record in records)
            {
                var key = (record.SourceId, record.ArticleId);
                var firstSeen = _items.TryGetValue(key, out var previous)
                    ? previous.FirstSeenAtUtc
                    : _seen.GetValueOrDefault(key, record.FirstSeenAtUtc);
                _seen[key] = firstSeen;
                var updated = record with { FirstSeenAtUtc = firstSeen };
                _items[key] = updated;
                _version++;
            }
            Evict(time.GetUtcNow());
        }
    }

    public void Restore(IEnumerable<NewsRecord> records, IEnumerable<NewsIdentity> identities)
    {
        lock (_gate)
        {
            foreach (var identity in identities)
                _seen[(identity.SourceId, identity.ArticleId)] = identity.FirstSeenAtUtc;
            Upsert(records);
            Evict(time.GetUtcNow());
        }
    }

    public NewsCapture Capture()
    {
        lock (_gate)
        {
            Evict(time.GetUtcNow());
            return new NewsCapture(_version, _items.Values.ToArray(),
                _seen.Select(pair => new NewsIdentity(pair.Key.Source, pair.Key.Article, pair.Value)).ToArray());
        }
    }

    private void Evict(DateTimeOffset now)
    {
        var cutoff = now.AddDays(-_options.RetentionDays);
        var ordered = _items.OrderBy(pair => pair.Value.FirstSeenAtUtc)
            .ThenBy(pair => pair.Key.Source, StringComparer.Ordinal)
            .ThenBy(pair => pair.Key.Article, StringComparer.Ordinal).ToArray();
        var excess = Math.Max(0, ordered.Length - _options.MaxItems);
        for (var i = 0; i < ordered.Length; i++)
        {
            if (i >= excess && ordered[i].Value.FirstSeenAtUtc > cutoff) break;
            _items.Remove(ordered[i].Key);
            _version++;
        }
        var forgotten = _seen.OrderBy(pair => pair.Value)
            .ThenBy(pair => pair.Key.Source, StringComparer.Ordinal)
            .ThenBy(pair => pair.Key.Article, StringComparer.Ordinal)
            .Take(Math.Max(0, _seen.Count - _options.MaxItems)).ToArray();
        foreach (var entry in forgotten)
        {
            _seen.Remove(entry.Key);
            _version++;
        }
    }
}
