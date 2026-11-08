using DataHub.Api.Domain;

namespace DataHub.Api.Store;

public sealed class PriceExportLimitException(int limit, long requested)
    : Exception($"Export contains {requested} rows; the limit is {limit}. Narrow the filters or export a smaller page.");

public static class PriceQueryEngine
{
    public static PriceQueryResult Execute(
        IReadOnlyList<SourceData> sources, PriceQuery query, CancellationToken cancellationToken = default,
        bool allMatching = false, int? exportLimit = null)
    {
        IEnumerable<PriceRecord> Matching()
        {
            foreach (var source in sources)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (query.SourceId is not null && !string.Equals(source.SourceId, query.SourceId, StringComparison.OrdinalIgnoreCase))
                    continue;

                foreach (var row in source.Records.Values)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!string.IsNullOrEmpty(query.InstrumentContains)
                        && !row.InstrumentId.Contains(query.InstrumentContains, StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (query.Currency is not null && !string.Equals(row.Currency, query.Currency, StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (query.MinPrice is { } min && row.Price < min || query.MaxPrice is { } max && row.Price > max)
                        continue;
                    yield return row;
                }
            }
        }

        // Both passes read the same immutable per-source references, never the live store again.
        var total = Matching().LongCount();
        var pageSize = Math.Max(1, query.PageSize);
        var skip = allMatching ? 0L : (long)(Math.Max(1, query.Page) - 1) * pageSize;
        var take = allMatching ? total : Math.Min(pageSize, Math.Max(0, total - skip));
        if (exportLimit is { } limit && take > limit)
            throw new PriceExportLimitException(limit, take);
        if (take == 0)
            return new PriceQueryResult([], total);

        var comparer = Comparer<PriceRecord>.Create((left, right) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var comparison = query.SortBy switch
            {
                PriceSortField.SourceId => CompareText(left.SourceId, right.SourceId, query.Descending),
                PriceSortField.InstrumentId => CompareText(left.InstrumentId, right.InstrumentId, query.Descending),
                PriceSortField.Currency => CompareText(left.Currency, right.Currency, query.Descending),
                PriceSortField.Price => Direction(left.Price.CompareTo(right.Price), query.Descending),
                PriceSortField.SourceUpdatedAtUtc => Direction(left.SourceUpdatedAtUtc.CompareTo(right.SourceUpdatedAtUtc), query.Descending),
                PriceSortField.ReceivedAtUtc => Direction(left.ReceivedAtUtc.CompareTo(right.ReceivedAtUtc), query.Descending),
                _ => throw new ArgumentOutOfRangeException(nameof(query)),
            };
            if (comparison != 0) return comparison;
            var source = StringComparer.Ordinal.Compare(left.SourceId, right.SourceId);
            return source != 0 ? source : StringComparer.Ordinal.Compare(left.InstrumentId, right.InstrumentId);
        });

        var items = new List<PriceRecord>();
        long index = 0;
        try
        {
            foreach (var row in Matching().Order(comparer))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (index++ < skip) continue;
                items.Add(row);
                if (items.Count == take) break;
            }
        }
        catch (InvalidOperationException ex) when (ex.InnerException is OperationCanceledException)
        {
            // LINQ's sort wraps comparer exceptions; preserve request cancellation.
            throw new OperationCanceledException(cancellationToken);
        }

        return new PriceQueryResult(items.ToArray(), total);
    }

    // Null is always last, independently of direction. Existing numeric/time fields are non-nullable.
    public static int CompareText(string? left, string? right, bool descending)
        => left is null ? right is null ? 0 : 1
            : right is null ? -1 : Direction(StringComparer.Ordinal.Compare(left, right), descending);

    private static int Direction(int comparison, bool descending) => descending ? -Math.Sign(comparison) : comparison;
}
