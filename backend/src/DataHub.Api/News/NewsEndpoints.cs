using System.Globalization;
using DataHub.Api.Configuration;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace DataHub.Api.News;

public sealed record NewsDto(
    string SourceId, string ArticleId, string Title, string Url, string Publisher,
    string? OriginalPublisher, string? Category, DateTimeOffset? PublishedAtUtc,
    DateOnly? PublishedDate, PublicationTimePrecision PublicationTimePrecision,
    string? OriginalPublicationTime, DateTimeOffset FirstSeenAtUtc, DateTimeOffset FetchedAtUtc,
    string Language, IReadOnlyList<string> MappingWarnings)
{
    public static NewsDto From(NewsRecord item) => new(item.SourceId, item.ArticleId, item.Title, item.Url,
        item.Publisher, item.OriginalPublisher, item.Category, item.PublishedAtUtc, item.PublishedDate,
        item.PublicationTimePrecision, item.OriginalPublicationTime, item.FirstSeenAtUtc, item.FetchedAtUtc,
        item.Language, item.MappingWarnings);
}

public sealed record NewsListResponse(DateTimeOffset ServerTimeUtc, int Page, int PageSize,
    int TotalCount, int TotalPages, IReadOnlyList<NewsDto> Items, NewsStatus Source);

public sealed class NewsQueryParameters
{
    [FromQuery(Name = "page")] public string? Page { get; set; }
    [FromQuery(Name = "pageSize")] public string? PageSize { get; set; }
    [FromQuery(Name = "keyword")] public string? Keyword { get; set; }
    [FromQuery(Name = "category")] public string? Category { get; set; }
    [FromQuery(Name = "publishedFrom")] public string? PublishedFrom { get; set; }
    [FromQuery(Name = "publishedTo")] public string? PublishedTo { get; set; }
    [FromQuery(Name = "sortDirection")] public string? SortDirection { get; set; }
}

public static class NewsEndpoints
{
    public static IEndpointRouteBuilder MapNewsEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/news", Read).WithTags("News").WithName("GetNews")
            .WithSummary("Filtered and paginated bounded news metadata and source fetch status.")
            .WithDescription("page defaults to 1; pageSize defaults to Api:DefaultPageSize and is limited by Api:MaxPageSize. "
                + "keyword is a case-insensitive title substring; category is a case-insensitive exact match. "
                + "publishedFrom and publishedTo are inclusive yyyy-MM-dd dates; unknown dates do not match a date filter. "
                + "sortDirection is case-sensitive asc or desc (default desc). Unknown publication times sort last; "
                + "sourceId and articleId are ascending tie-breakers. Separate requests are not a shared snapshot.");
        return app;
    }

    private static Results<Ok<NewsListResponse>, ValidationProblem> Read(
        [AsParameters] NewsQueryParameters parameters, NewsStore store, MockNewsService source,
        IOptions<DataHubOptions> options, TimeProvider time)
    {
        var errors = new Dictionary<string, string[]>();
        var page = ParseInt(parameters.Page, "page", 1, 1, int.MaxValue);
        var size = ParseInt(parameters.PageSize, "pageSize", options.Value.Api.DefaultPageSize, 1, options.Value.Api.MaxPageSize);
        var from = ParseDate(parameters.PublishedFrom, "publishedFrom");
        var to = ParseDate(parameters.PublishedTo, "publishedTo");
        var direction = string.IsNullOrEmpty(parameters.SortDirection) ? "desc" : parameters.SortDirection;
        if (direction != "asc" && direction != "desc") errors["sortDirection"] = ["Use asc or desc."];
        if (from > to) errors["publishedTo"] = ["Must not precede publishedFrom."];
        if (errors.Count > 0) return TypedResults.ValidationProblem(errors);
        var keyword = parameters.Keyword?.Trim() ?? "";
        var category = parameters.Category?.Trim() ?? "";
        var items = store.Capture().Items.Where(item =>
        {
            var date = item.PublishedDate ?? (item.PublishedAtUtc is { } instant ? DateOnly.FromDateTime(instant.UtcDateTime) : (DateOnly?)null);
            return (keyword.Length == 0 || item.Title.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                && (category.Length == 0 || string.Equals(item.Category, category, StringComparison.OrdinalIgnoreCase))
                && (from is null || date >= from) && (to is null || date <= to);
        }).ToArray();
        DateOnly? Date(NewsRecord item) => item.PublishedDate ?? (item.PublishedAtUtc is { } instant ? DateOnly.FromDateTime(instant.UtcDateTime) : null);
        var ordered = direction == "asc"
            ? items.OrderBy(item => Date(item) is null).ThenBy(Date).ThenBy(item => item.PublishedAtUtc)
            : items.OrderBy(item => Date(item) is null).ThenByDescending(Date).ThenByDescending(item => item.PublishedAtUtc);
        var result = ordered.ThenBy(item => item.SourceId, StringComparer.Ordinal)
            .ThenBy(item => item.ArticleId, StringComparer.Ordinal)
            .Skip((int)Math.Min((long)(page - 1) * size, int.MaxValue)).Take(size).Select(NewsDto.From).ToArray();
        return TypedResults.Ok(new NewsListResponse(time.GetUtcNow(), page, size, items.Length,
            (int)(((long)items.Length + size - 1) / size), result, source.Status()));

        int ParseInt(string? text, string name, int fallback, int min, int max)
        {
            if (text is null) return fallback;
            if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value) || value < min || value > max)
            {
                errors[name] = [$"Must be between {min} and {max}."];
                return fallback;
            }
            return value;
        }

        DateOnly? ParseDate(string? text, string name)
        {
            if (text is null) return null;
            if (!DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var value))
            {
                errors[name] = ["Use yyyy-MM-dd."];
                return null;
            }
            return value;
        }
    }
}
