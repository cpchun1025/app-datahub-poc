using System.Collections.Immutable;

namespace DataHub.Api.Csv;

public sealed record IndexMembership(
    string SourceId,
    string IndexId,
    string InstrumentId,
    decimal Weight,
    DateOnly EffectiveDate);

public sealed record CompositionImportStatus(
    DateTimeOffset? LastFileCheckAtUtc,
    DateTimeOffset? LastSuccessfulValidationAtUtc,
    DateTimeOffset? LastDataImportAtUtc,
    string? Fingerprint,
    int RowCount,
    int ErrorCount,
    string? LastError);

public sealed record CsvImportStatus(
    DateTimeOffset? LastFileCheckAtUtc,
    DateTimeOffset? LastSuccessfulValidationAtUtc,
    DateTimeOffset? LastDataImportAtUtc,
    string? Fingerprint,
    int RowCount,
    int ErrorCount,
    string? LastError);

public sealed record CompositionCapture(
    long Version,
    IReadOnlyList<IndexMembership> Memberships,
    CompositionImportStatus Status,
    bool RequiresValidation = false);

public sealed class CompositionStore
{
    private readonly object _gate = new();
    private ImmutableDictionary<(string SourceId, string IndexId, string InstrumentId), IndexMembership> _items
        = ImmutableDictionary<(string, string, string), IndexMembership>.Empty;
    private CompositionImportStatus _status = new(null, null, null, null, 0, 0, null);
    private bool _requiresValidation;
    private long _version;

    public CompositionCapture Capture()
    {
        lock (_gate)
            return new CompositionCapture(_version, _items.Values.OrderBy(item => item.IndexId, StringComparer.Ordinal)
                .ThenBy(item => item.InstrumentId, StringComparer.Ordinal).ToArray(), _status, _requiresValidation);
    }

    public void Replace(IReadOnlyList<IndexMembership> memberships, CompositionImportStatus status)
    {
        var next = memberships.ToImmutableDictionary(
            item => (item.SourceId, item.IndexId, item.InstrumentId),
            item => item);
        lock (_gate)
        {
            if (_items.Count != next.Count || _items.Any(pair => !next.TryGetValue(pair.Key, out var item) || item != pair.Value)
                || _status != status)
                _version++;
            _items = next;
            _status = status;
            _requiresValidation = false;
        }
    }

    public void SetStatus(CompositionImportStatus status, bool validated = false)
    {
        lock (_gate)
        {
            if (_status != status)
                _version++;
            _status = status;
            if (validated)
                _requiresValidation = false;
        }
    }

    public void Restore(IReadOnlyList<IndexMembership> memberships, CompositionImportStatus? status)
    {
        var next = memberships.ToImmutableDictionary(
            item => (item.SourceId, item.IndexId, item.InstrumentId),
            item => item);
        lock (_gate)
        {
            _items = next;
            _status = status ?? new CompositionImportStatus(null, null, null, null, next.Count, 0, null);
            _requiresValidation = _status.Fingerprint is not null;
            _version++;
        }
    }
}

public sealed class CsvImportMonitor
{
    private readonly object _gate = new();
    private readonly Dictionary<string, CsvImportStatus> _statuses = new(StringComparer.OrdinalIgnoreCase);

    public CsvImportStatus? GetStatus(string sourceId)
    {
        lock (_gate)
            return _statuses.GetValueOrDefault(sourceId);
    }

    public void SetStatus(string sourceId, CsvImportStatus status)
    {
        lock (_gate)
            _statuses[sourceId] = status;
    }
}
