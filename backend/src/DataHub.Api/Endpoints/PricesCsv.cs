using System.Globalization;
using System.Text;

namespace DataHub.Api.Endpoints;

public static class PricesCsv
{
    public static async Task WriteAsync(Stream stream, IReadOnlyList<PriceDto> rows, CancellationToken cancellationToken)
    {
        await stream.WriteAsync(Encoding.UTF8.GetPreamble(), cancellationToken);
        await using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true) { NewLine = "\r\n" };
        await writer.WriteLineAsync("SourceId,InstrumentId,Price,Currency,SourceUpdatedAtUtc,ReceivedAtUtc,SourceRevision,SourceStatus".AsMemory(), cancellationToken);
        foreach (var row in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var cells = new[]
            {
                Text(row.SourceId), Text(row.InstrumentId), row.Price.ToString(CultureInfo.InvariantCulture),
                Text(row.Currency), row.SourceUpdatedAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
                row.ReceivedAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
                row.SourceRevision.ToString(CultureInfo.InvariantCulture), Text(row.SourceStatus.ToString()),
            };
            await writer.WriteLineAsync(string.Join(',', cells).AsMemory(), cancellationToken);
        }
        await writer.FlushAsync(cancellationToken);
    }

    public static string Text(string? value)
    {
        value ??= string.Empty;
        // Prefix dangerous text, including formulas hidden behind whitespace/control characters.
        var first = value.AsSpan().TrimStart();
        if (first.Length > 0 && "=+-@".Contains(first[0])
            || value.Any(c => char.IsControl(c) || char.GetUnicodeCategory(c) == UnicodeCategory.Format))
            value = "'" + value;
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }
}
