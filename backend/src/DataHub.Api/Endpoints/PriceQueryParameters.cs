using System.Globalization;
using DataHub.Api.Configuration;
using DataHub.Api.Domain;
using DataHub.Api.Store;
using Microsoft.AspNetCore.Mvc;

namespace DataHub.Api.Endpoints;

public sealed class PriceQueryParameters
{
    [FromQuery(Name = "sourceId")] public string? SourceId { get; set; }
    [FromQuery(Name = "instrumentId")] public string? InstrumentId { get; set; }
    [FromQuery(Name = "page")] public string? Page { get; set; }
    [FromQuery(Name = "pageSize")] public string? PageSize { get; set; }
    [FromQuery(Name = "sortBy")] public string? SortBy { get; set; }
    [FromQuery(Name = "sortDirection")] public string? SortDirection { get; set; }
    [FromQuery(Name = "currency")] public string? Currency { get; set; }
    [FromQuery(Name = "sourceStatus")] public string? SourceStatus { get; set; }
    [FromQuery(Name = "minPrice")] public string? MinPrice { get; set; }
    [FromQuery(Name = "maxPrice")] public string? MaxPrice { get; set; }

    public bool TryParse(ApiOptions options, out PriceQuery query, out SourceStatus? status,
        out Dictionary<string, string[]> errors, bool ignorePagination = false)
    {
        var failures = new Dictionary<string, string[]>();
        var page = ignorePagination ? 1 : ParsePage(Page, "page", 1, int.MaxValue);
        var size = ignorePagination ? options.DefaultPageSize : ParsePage(PageSize, "pageSize", options.DefaultPageSize, options.MaxPageSize);
        var instrument = Clean(InstrumentId);
        if (InstrumentId?.Length > 64) failures["instrumentId"] = ["instrumentId must be at most 64 characters."];
        var currency = Clean(Currency);
        if (currency is not null && (currency.Length != 3 || !currency.All(char.IsAsciiLetter)))
            failures["currency"] = ["currency must be a three-letter code."];

        var sort = PriceSortField.SourceId;
        if (Clean(SortBy) is { } sortName && !TryNamedEnum(sortName, out sort))
            failures["sortBy"] = ["Use sourceId, instrumentId, price, currency, sourceUpdatedAtUtc or receivedAtUtc."];
        var direction = Clean(SortDirection) ?? "asc";
        if (!direction.Equals("asc", StringComparison.OrdinalIgnoreCase) && !direction.Equals("desc", StringComparison.OrdinalIgnoreCase))
            failures["sortDirection"] = ["Use asc or desc."];

        status = null;
        if (Clean(SourceStatus) is { } statusName)
        {
            if (TryNamedEnum<Domain.SourceStatus>(statusName, out var parsed)) status = parsed;
            else failures["sourceStatus"] = ["Use Initializing, Live, Recovering, Stale or Disconnected."];
        }

        var min = ParsePrice(MinPrice, "minPrice");
        var max = ParsePrice(MaxPrice, "maxPrice");
        if (min > max) failures["maxPrice"] = ["maxPrice must be greater than or equal to minPrice."];
        query = new PriceQuery(Clean(SourceId), instrument, page, size, sort,
            direction.Equals("desc", StringComparison.OrdinalIgnoreCase), currency, min, max);
        errors = failures;
        return failures.Count == 0;

        int ParsePage(string? value, string field, int fallback, int maximum)
        {
            if (value is null) return fallback;
            if (int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) && parsed >= 1 && parsed <= maximum)
                return parsed;
            failures[field] = [$"{field} must be an integer between 1 and {maximum}."];
            return fallback;
        }

        decimal? ParsePrice(string? value, string field)
        {
            if (Clean(value) is not { } cleaned) return null;
            if (decimal.TryParse(cleaned, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture, out var parsed)) return parsed;
            failures[field] = [$"{field} must be a finite decimal using '.' without grouping or an exponent."];
            return null;
        }
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static bool TryNamedEnum<T>(string value, out T result) where T : struct, Enum
    {
        result = default;
        return Enum.GetNames<T>().Any(name => name.Equals(value, StringComparison.OrdinalIgnoreCase))
            && Enum.TryParse(value, true, out result);
    }
}
