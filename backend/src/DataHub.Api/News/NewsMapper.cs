using System.Globalization;
using System.Text.RegularExpressions;

namespace DataHub.Api.News;

public static partial class NewsMapper
{
    [GeneratedRegex(@"(?:Z|[+-]\d{2}:\d{2})$", RegexOptions.IgnoreCase)]
    private static partial Regex OffsetSuffix();

    public static NewsRecord Map(MockNewsItem item, string sourceId, DateTimeOffset fetchedAtUtc)
    {
        if (string.IsNullOrWhiteSpace(sourceId) || string.IsNullOrWhiteSpace(item.Id)
            || string.IsNullOrWhiteSpace(item.Headline) || string.IsNullOrWhiteSpace(item.Language))
            throw new ArgumentException("Source, article ID, headline and language are required.");
        if (!Uri.TryCreate(item.Link, UriKind.Absolute, out var url)
            || (url.Scheme != Uri.UriSchemeHttp && url.Scheme != Uri.UriSchemeHttps))
            throw new ArgumentException("Article URL must be an absolute HTTP(S) URL.");

        DateTimeOffset? instant = null;
        DateOnly? date = null;
        var precision = PublicationTimePrecision.Unknown;
        var warnings = new List<string>();
        if (string.IsNullOrWhiteSpace(item.PublicationDate))
        {
            warnings.Add("Publication time unavailable.");
        }
        else if (DateOnly.TryParseExact(item.PublicationDate, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                     DateTimeStyles.None, out var parsedDate))
        {
            date = parsedDate;
            precision = PublicationTimePrecision.Date;
        }
        else if (OffsetSuffix().IsMatch(item.PublicationDate)
                 && DateTimeOffset.TryParse(item.PublicationDate, CultureInfo.InvariantCulture,
                     DateTimeStyles.None, out var parsedInstant))
        {
            instant = parsedInstant.ToUniversalTime();
            precision = PublicationTimePrecision.Instant;
        }
        else
        {
            throw new ArgumentException("Publication time must be a date or timestamp with an explicit UTC offset.");
        }

        return new NewsRecord(sourceId, item.Id.Trim(), item.Headline.Trim(), url.AbsoluteUri,
            "Mock News", item.OriginalPublisher, item.Section, instant, date, precision,
            item.PublicationDate, fetchedAtUtc, fetchedAtUtc, item.Language, warnings);
    }
}
