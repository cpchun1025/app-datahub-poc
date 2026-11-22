using DataHub.Api.Domain;
using DataHub.Api.Store;
using DataHub.Api.Sync;

namespace DataHub.Api.Endpoints;

public sealed class PriceReadService(ILatestStateStore store, SourceViewService sources, TimeProvider time)
{
    public bool HasSource(string? sourceId)
        => sourceId is null || store.SourceIds.Contains(sourceId, StringComparer.OrdinalIgnoreCase);

    public PriceListResponse Read(
        PriceQuery query, SourceStatus? status, CancellationToken cancellationToken,
        bool allMatching = false, int? exportLimit = null)
    {
        var now = time.GetUtcNow();
        var views = sources.GetAll(now)
            .Where(v => (query.SourceId is null || v.Descriptor.SourceId.Equals(query.SourceId, StringComparison.OrdinalIgnoreCase))
                && (status is null || v.Status == status))
            .ToArray();
        var sourceViews = views.ToDictionary(v => v.Descriptor.SourceId, StringComparer.Ordinal);
        var result = PriceQueryEngine.Execute(views.Select(v => v.Data).ToArray(), query, cancellationToken, allMatching, exportLimit);
        var items = result.Items.Select(row => PriceDto.From(row, sourceViews[row.SourceId].Status,
            sourceViews[row.SourceId].Descriptor.IsSynthetic)).ToArray();
        var freshness = views.Select(v => new SourceFreshnessDto(v.Descriptor.SourceId, v.Status,
            v.Runtime.SyncedSinceStart, v.Data.LastUpdateReceivedAtUtc)).ToArray();
        var totalPages = (int)Math.Min(int.MaxValue, (result.TotalCount + query.PageSize - 1) / query.PageSize);
        return new PriceListResponse(now, query.Page, query.PageSize, result.TotalCount, totalPages, items, freshness);
    }
}
