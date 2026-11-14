using System.Globalization;
using DataHub.Api.Configuration;
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

public static class NewsEndpoints
{
    public static IEndpointRouteBuilder MapNewsEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/news", Read).WithTags("News").WithName("GetNews");
        return app;
    }

    private static IResult Read(HttpRequest request, NewsStore store, MockNewsService source,
        IOptions<DataHubOptions> options, TimeProvider time)
    {
        var q = request.Query;
        var errors = new Dictionary<string, string[]>();
        var page = ParseInt("page", 1, 1, int.MaxValue);
        var size = ParseInt("pageSize", options.Value.Api.DefaultPageSize, 1, options.Value.Api.MaxPageSize);
        var from = ParseDate("publishedFrom");
        var to = ParseDate("publishedTo");
        var direction = q["sortDirection"].ToString();
        if (direction.Length == 0) direction = "desc";
        if (direction != "asc" && direction != "desc") errors["sortDirection"] = ["Use asc or desc."];
        if (from > to) errors["publishedTo"] = ["Must not precede publishedFrom."];
        if (errors.Count > 0) return Results.ValidationProblem(errors);
        var keyword = q["keyword"].ToString().Trim();
        var category = q["category"].ToString().Trim();
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
        return Results.Ok(new NewsListResponse(time.GetUtcNow(), page, size, items.Length,
            (int)(((long)items.Length + size - 1) / size), result, source.Status()));

        int ParseInt(string name, int fallback, int min, int max)
        {
            if (!q.ContainsKey(name)) return fallback;
            if (!int.TryParse(q[name], NumberStyles.None, CultureInfo.InvariantCulture, out var value) || value < min || value > max)
            {
                errors[name] = [$"Must be between {min} and {max}."];
                return fallback;
            }
            return value;
        }

        DateOnly? ParseDate(string name)
        {
            if (!q.ContainsKey(name)) return null;
            if (!DateOnly.TryParseExact(q[name], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var value))
            {
                errors[name] = ["Use yyyy-MM-dd."];
                return null;
            }
            return value;
        }
    }
}
