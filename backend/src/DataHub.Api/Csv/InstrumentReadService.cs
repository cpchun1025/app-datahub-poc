using DataHub.Api.Configuration;
using DataHub.Api.Domain;
using DataHub.Api.Endpoints;
using DataHub.Api.Sync;
using DataHub.Api.Store;
using Microsoft.Extensions.Options;

namespace DataHub.Api.Csv;

public sealed class InstrumentReadService(
    ILatestStateStore prices,
    SourceRegistry sources,
    CompositionStore composition,
    IOptions<DataHubOptions> options,
    TimeProvider time)
{
    public InstrumentDetailResponse ReadObservations(string instrumentId, int page, int pageSize)
    {
        var all = prices.GetAllSources()
            .SelectMany(data => data.Records.Values)
            .Where(record => string.Equals(record.InstrumentId, instrumentId, StringComparison.Ordinal))
            .Select(record =>
            {
                var runner = sources.Find(record.SourceId);
                var descriptor = runner?.Descriptor;
                var status = runner?.GetStatus(time.GetUtcNow()) ?? SourceStatus.Disconnected;
                return new InstrumentObservationDto(record.SourceId, descriptor?.DisplayName ?? record.SourceId,
                    record.InstrumentId, record.ProviderSymbol, record.Price, record.Currency,
                    record.SourceUpdatedAtUtc, record.ReceivedAtUtc, record.PriceKind, status,
                    descriptor?.IsSynthetic ?? false);
            })
            .OrderBy(item => item.SourceId, StringComparer.Ordinal).ToArray();
        var items = all.Skip((int)Math.Min((long)(page - 1) * pageSize, int.MaxValue)).Take(pageSize).ToArray();
        return new InstrumentDetailResponse(instrumentId, time.GetUtcNow(), page, pageSize, all.Length,
            TotalPages(all.Length, pageSize), items);
    }

    public InstrumentMembershipResponse ReadMemberships(string instrumentId, int page, int pageSize)
    {
        var capture = composition.Capture();
        var items = capture.Memberships.Where(item => string.Equals(item.InstrumentId, instrumentId, StringComparison.Ordinal))
            .OrderBy(item => item.IndexId, StringComparer.Ordinal)
            .ThenBy(item => item.EffectiveDate)
            .ToArray();
        var status = !options.Value.CsvShowcase.Enabled ? "Disabled"
            : capture.Status.LastError is not null ? "Disconnected"
            : capture.RequiresValidation ? "Recovering"
            : capture.Status.LastSuccessfulValidationAtUtc is not { } validatedAt ? "Initializing"
            : time.GetUtcNow() - validatedAt > TimeSpan.FromSeconds(options.Value.CsvShowcase.PollIntervalSeconds * 3)
                ? "Stale"
                : "Live";
        return new InstrumentMembershipResponse(instrumentId, time.GetUtcNow(),
            options.Value.CsvShowcase.CompositionSourceId, status,
            capture.Status.LastSuccessfulValidationAtUtc, capture.Status.LastError,
            page, pageSize, items.Length, TotalPages(items.Length, pageSize),
            items.Skip((int)Math.Min((long)(page - 1) * pageSize, int.MaxValue)).Take(pageSize)
                .Select(item => new IndexMembershipDto(item.SourceId, item.IndexId, item.InstrumentId,
                    item.Weight, item.EffectiveDate)).ToArray());
    }

    private static int TotalPages(int count, int size) => count == 0 ? 0 : (int)(((long)count + size - 1) / size);
}
