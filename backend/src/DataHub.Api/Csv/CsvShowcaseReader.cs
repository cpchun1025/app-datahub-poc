using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using DataHub.Api.Configuration;
using DataHub.Api.Domain;
using Microsoft.VisualBasic.FileIO;

namespace DataHub.Api.Csv;

public sealed record SpotCandidate(string Fingerprint, IReadOnlyList<PriceUpdate> Records);
public sealed record CompositionCandidate(string Fingerprint, IReadOnlyList<IndexMembership> Memberships);

public sealed class CsvShowcaseReader(CsvShowcaseOptions options)
{
    private static readonly string[] TimestampFormats =
        ["yyyy-MM-dd'T'HH:mm:ssK", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFK"];
    private static readonly string[] SpotHeaders =
        ["InstrumentId", "ProviderSymbol", "Price", "Currency", "SourceUpdatedAtUtc", "PriceKind"];
    private static readonly string[] CompositionHeaders =
        ["IndexId", "InstrumentId", "Weight", "EffectiveDate"];

    public async Task<SpotCandidate> ReadSpotAsync(string path, CancellationToken cancellationToken)
    {
        var bytes = await ReadBytesAsync(path, cancellationToken);
        var fingerprint = Fingerprint(bytes);
        using var parser = CreateParser(bytes);
        var header = ReadHeader(parser, SpotHeaders);
        var records = new List<PriceUpdate>();
        var keys = new HashSet<string>(StringComparer.Ordinal);
        var rowNumber = 1;
        while (!parser.EndOfData)
        {
            cancellationToken.ThrowIfCancellationRequested();
            rowNumber++;
            var fields = ReadFields(parser, rowNumber);
            if (fields.Length != SpotHeaders.Length)
                throw RowError(rowNumber, "column count does not match the header");
            ValidateFieldLengths(fields, rowNumber);
            var id = Required(fields[header["InstrumentId"]], rowNumber, "InstrumentId");
            ValidateInstrumentId(id, rowNumber);
            var symbol = Required(fields[header["ProviderSymbol"]], rowNumber, "ProviderSymbol");
            var priceText = Required(fields[header["Price"]], rowNumber, "Price");
            var currency = Required(fields[header["Currency"]], rowNumber, "Currency");
            var timeText = Required(fields[header["SourceUpdatedAtUtc"]], rowNumber, "SourceUpdatedAtUtc");
            var kind = Required(fields[header["PriceKind"]], rowNumber, "PriceKind");

            if (!keys.Add(id)) throw RowError(rowNumber, $"duplicate InstrumentId '{id}'");
            if (currency.Length != 3 || !currency.All(char.IsAsciiLetter))
                throw RowError(rowNumber, "Currency must be a three-letter ASCII code");
            if (!decimal.TryParse(priceText, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                    CultureInfo.InvariantCulture, out var price))
                throw RowError(rowNumber, "Price must be an invariant finite decimal without exponent or grouping");
            if (!Regex.IsMatch(timeText, @"(?:Z|[+-]\d{2}:\d{2})$", RegexOptions.IgnoreCase)
                || !DateTimeOffset.TryParseExact(timeText, TimestampFormats, CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var updated))
                throw RowError(rowNumber, "SourceUpdatedAtUtc must be an ISO-8601 timestamp with an explicit UTC offset");
            if (!string.Equals(kind, nameof(PriceKind.Last), StringComparison.Ordinal))
                throw RowError(rowNumber, "PriceKind must be Last");

            records.Add(new PriceUpdate(id, price, currency.ToUpperInvariant(), updated.ToUniversalTime(), 0,
                symbol, PriceKind.Last));
            if (records.Count > options.MaxRows) throw RowError(rowNumber, "row limit exceeded");
        }

        ValidateNotEmpty(records.Count);
        return new SpotCandidate(fingerprint, records);
    }

    public async Task<CompositionCandidate> ReadCompositionAsync(string path, CancellationToken cancellationToken)
    {
        var bytes = await ReadBytesAsync(path, cancellationToken);
        var fingerprint = Fingerprint(bytes);
        using var parser = CreateParser(bytes);
        var header = ReadHeader(parser, CompositionHeaders);
        var memberships = new List<IndexMembership>();
        var keys = new HashSet<(string Index, string Instrument)>();
        var rowNumber = 1;
        while (!parser.EndOfData)
        {
            cancellationToken.ThrowIfCancellationRequested();
            rowNumber++;
            var fields = ReadFields(parser, rowNumber);
            if (fields.Length != CompositionHeaders.Length)
                throw RowError(rowNumber, "column count does not match the header");
            ValidateFieldLengths(fields, rowNumber);
            var index = Required(fields[header["IndexId"]], rowNumber, "IndexId");
            var instrument = Required(fields[header["InstrumentId"]], rowNumber, "InstrumentId");
            ValidateInstrumentId(instrument, rowNumber);
            var weightText = Required(fields[header["Weight"]], rowNumber, "Weight");
            var dateText = Required(fields[header["EffectiveDate"]], rowNumber, "EffectiveDate");
            if (!index.StartsWith("DEMO", StringComparison.Ordinal))
                throw RowError(rowNumber, "IndexId must use the DEMO prefix");
            if (!keys.Add((index, instrument)))
                throw RowError(rowNumber, $"duplicate membership '{index}/{instrument}'");
            if (!decimal.TryParse(weightText, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                    CultureInfo.InvariantCulture, out var weight) || weight is < 0 or > 1)
                throw RowError(rowNumber, "Weight must be an invariant fraction between 0 and 1");
            if (!DateOnly.TryParseExact(dateText, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var effectiveDate))
                throw RowError(rowNumber, "EffectiveDate must use yyyy-MM-dd");

            memberships.Add(new IndexMembership(options.CompositionSourceId, index, instrument, weight, effectiveDate));
            if (memberships.Count > options.MaxRows) throw RowError(rowNumber, "row limit exceeded");
        }

        ValidateNotEmpty(memberships.Count);
        return new CompositionCandidate(fingerprint, memberships);
    }

    private async Task<byte[]> ReadBytesAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.Read | FileShare.Delete, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (stream.Length > options.MaxFileBytes) throw new CsvShowcaseException("CSV file exceeds the configured byte limit.");
            using var output = new MemoryStream((int)stream.Length);
            var buffer = new byte[64 * 1024];
            while (true)
            {
                var read = await stream.ReadAsync(buffer, cancellationToken);
                if (read == 0) break;
                if (output.Length + read > options.MaxFileBytes)
                    throw new CsvShowcaseException("CSV file exceeds the configured byte limit.");
                output.Write(buffer, 0, read);
            }
            return output.ToArray();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (CsvShowcaseException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            throw new CsvShowcaseException("CSV file is unavailable or cannot be read.");
        }
    }

    private TextFieldParser CreateParser(byte[] bytes)
    {
        string text;
        try
        {
            var start = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
            text = new UTF8Encoding(false, true).GetString(bytes, start, bytes.Length - start);
        }
        catch (DecoderFallbackException ex)
        {
            throw new CsvShowcaseException("CSV must be valid UTF-8.", ex);
        }
        return new TextFieldParser(new StringReader(text))
        {
            TextFieldType = FieldType.Delimited,
            HasFieldsEnclosedInQuotes = true,
            TrimWhiteSpace = false,
            Delimiters = [","],
        };
    }

    private static Dictionary<string, int> ReadHeader(TextFieldParser parser, string[] expected)
    {
        if (parser.EndOfData) throw new CsvShowcaseException("CSV is empty; refusing to clear the current dataset.");
        var fields = ReadFields(parser, 1);
        if (!fields.SequenceEqual(expected, StringComparer.Ordinal))
            throw new CsvShowcaseException($"CSV header must exactly match: {string.Join(",", expected)}.");
        return expected.Select((name, index) => (name, index)).ToDictionary(item => item.name, item => item.index,
            StringComparer.Ordinal);
    }

    private static string[] ReadFields(TextFieldParser parser, int row)
    {
        try
        {
            return parser.ReadFields() ?? throw RowError(row, "unexpected empty row");
        }
        catch (MalformedLineException ex)
        {
            throw RowError(row, "malformed quoted CSV row", ex);
        }
    }

    private void ValidateFieldLengths(IEnumerable<string> fields, int row)
    {
        if (fields.Any(field => field.Length > options.MaxFieldLength))
            throw RowError(row, "field exceeds the configured length limit");
    }

    private void ValidateNotEmpty(int rows)
    {
        if (rows == 0 && !options.AllowEmptySnapshot)
            throw new CsvShowcaseException("Header-only CSV is refused; enable AllowEmptySnapshot explicitly to clear this dataset.");
    }

    private static string Required(string value, int row, string name)
        => string.IsNullOrWhiteSpace(value) ? throw RowError(row, $"{name} is required") : value;

    private static void ValidateInstrumentId(string value, int row)
    {
        if (value.Length > 256 || value.Contains('/') || value.Contains('\\') || value.Any(char.IsControl))
            throw RowError(row, "InstrumentId must be at most 256 characters and cannot contain path separators or control characters");
    }

    private static CsvShowcaseException RowError(int row, string message, Exception? inner = null)
        => new($"CSV row {row}: {message}.", inner);

    private static string Fingerprint(byte[] bytes)
        => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}

public sealed class CsvShowcaseException(string message, Exception? inner = null) : Exception(message, inner);
