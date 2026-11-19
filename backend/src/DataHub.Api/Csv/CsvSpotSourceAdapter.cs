using DataHub.Api.Configuration;
using DataHub.Api.Domain;
using DataHub.Api.Sources;
using DataHub.Api.Sources.Mock;
using Microsoft.Extensions.Options;

namespace DataHub.Api.Csv;

public interface ICsvSnapshotSourceAdapter : ISourceAdapter
{
    TimeSpan PollInterval { get; }
    void ConfirmSnapshot(SourceSnapshot snapshot, DateTimeOffset appliedAtUtc);
}

public sealed class CsvSpotSourceAdapter : ICsvSnapshotSourceAdapter
{
    private readonly CsvShowcaseOptions _options;
    private readonly CsvShowcaseReader _reader;
    private readonly CsvImportMonitor _monitor;
    private readonly TimeProvider _time;
    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _fingerprint;
    private string? _pendingFingerprint;
    private IReadOnlyList<PriceUpdate> _records = [];
    private long _revision;

    public CsvSpotSourceAdapter(
        IOptions<DataHubOptions> options,
        IHostEnvironment environment,
        CsvShowcaseReader reader,
        CsvImportMonitor monitor,
        TimeProvider time)
    {
        _options = options.Value.CsvShowcase;
        _reader = reader;
        _monitor = monitor;
        _time = time;
        _path = ResolvePath(environment.ContentRootPath, _options.SpotPath!);
        PollInterval = TimeSpan.FromSeconds(_options.PollIntervalSeconds);
        Descriptor = new SourceDescriptor(_options.SpotSourceId, _options.SpotDisplayName, SourceKind.Csv,
            IsMock: false, PriceDecimals: 8,
            "Operator-configured CSV snapshot adapter; data is SYNTHETIC DEMO, not an external market feed.",
            IsSynthetic: true);
    }

    public SourceDescriptor Descriptor { get; }
    public TimeSpan PollInterval { get; }

    public async Task<SourceSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var checkedAt = _time.GetUtcNow();
            var previous = _monitor.GetStatus(Descriptor.SourceId)
                ?? new CsvImportStatus(null, null, null, null, 0, 0, null);
            _monitor.SetStatus(Descriptor.SourceId, previous with { LastFileCheckAtUtc = checkedAt });

            SpotCandidate candidate;
            try
            {
                candidate = await _reader.ReadSpotAsync(_path, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (CsvShowcaseException ex)
            {
                var latest = _monitor.GetStatus(Descriptor.SourceId)!;
                _monitor.SetStatus(Descriptor.SourceId, latest with { ErrorCount = 1, LastError = ex.Message });
                throw;
            }

            if (candidate.Fingerprint == _fingerprint)
            {
                _pendingFingerprint = null;
                var latest = _monitor.GetStatus(Descriptor.SourceId)!;
                _monitor.SetStatus(Descriptor.SourceId, latest with
                {
                    LastSuccessfulValidationAtUtc = checkedAt,
                    Fingerprint = candidate.Fingerprint,
                    RowCount = candidate.Records.Count,
                    ErrorCount = 0,
                    LastError = null,
                });
                return new SourceSnapshot(_revision, checkedAt, _records, UnchangedContent: true);
            }

            var nextRevision = _revision + 1;
            var nextRecords = candidate.Records.Select(record => record with { Revision = nextRevision }).ToArray();
            _pendingFingerprint = candidate.Fingerprint;
            return new SourceSnapshot(nextRevision, checkedAt, nextRecords);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void ConfirmSnapshot(SourceSnapshot snapshot, DateTimeOffset appliedAtUtc)
    {
        if (snapshot.UnchangedContent)
            return;
        _revision = snapshot.Revision;
        _fingerprint = _pendingFingerprint;
        _pendingFingerprint = null;
        _records = snapshot.Records;
        var current = _monitor.GetStatus(Descriptor.SourceId)
            ?? new CsvImportStatus(null, null, null, null, 0, 0, null);
        _monitor.SetStatus(Descriptor.SourceId, current with
        {
            LastSuccessfulValidationAtUtc = appliedAtUtc,
            LastDataImportAtUtc = appliedAtUtc,
            Fingerprint = _fingerprint,
            RowCount = snapshot.Records.Count,
            ErrorCount = 0,
            LastError = null,
        });
    }

    internal static string ResolvePath(string contentRoot, string path)
        => Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(contentRoot, path));
}

public sealed class CompositeSourceCatalog : ISourceCatalog
{
    public CompositeSourceCatalog(
        IOptions<DataHubOptions> options,
        IHostEnvironment environment,
        TimeProvider time,
        CsvShowcaseReader csvReader,
        CsvImportMonitor csvMonitor)
    {
        var adapters = MockSourceCatalog.CreateAll(options, time).ToList();
        if (options.Value.CsvShowcase.Enabled)
            adapters.Add(new CsvSpotSourceAdapter(options, environment, csvReader, csvMonitor, time));
        Adapters = adapters;
    }

    public IReadOnlyList<ISourceAdapter> Adapters { get; }
}
