namespace DataHub.Api.News;

public enum PublicationTimePrecision
{
    Unknown,
    Date,
    Instant,
}

public sealed record NewsRecord(
    string SourceId,
    string ArticleId,
    string Title,
    string Url,
    string Publisher,
    string? OriginalPublisher,
    string? Category,
    DateTimeOffset? PublishedAtUtc,
    DateOnly? PublishedDate,
    PublicationTimePrecision PublicationTimePrecision,
    string? OriginalPublicationTime,
    DateTimeOffset FirstSeenAtUtc,
    DateTimeOffset FetchedAtUtc,
    string Language,
    IReadOnlyList<string> MappingWarnings);

public sealed record MockNewsItem(
    string Id,
    string Headline,
    string Link,
    string? Section,
    string? PublicationDate,
    string? OriginalPublisher,
    string Language);
