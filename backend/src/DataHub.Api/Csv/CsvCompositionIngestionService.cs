using DataHub.Api.Configuration;
using Microsoft.Extensions.Options;

namespace DataHub.Api.Csv;

public sealed class CsvCompositionIngestionService(
    IOptions<DataHubOptions> options,
    IHostEnvironment environment,
    CsvShowcaseReader reader,
    CompositionStore store,
    TimeProvider time,
    ILogger<CsvCompositionIngestionService> logger) : BackgroundService
{
    private readonly CsvShowcaseOptions _config = options.Value.CsvShowcase;
    private readonly SemaphoreSlim _gate = new(1, 1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_config.Enabled)
            return;

        var path = CsvSpotSourceAdapter.ResolvePath(environment.ContentRootPath, _config.CompositionPath!);
        var failures = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            var imported = await TryImportOnceAsync(path, stoppingToken);
            failures = imported ? 0 : Math.Min(failures + 1, 16);
            var multiplier = imported ? 1 : Math.Pow(2, failures - 1);
            var seconds = Math.Min(60, _config.PollIntervalSeconds * multiplier);
            await Task.Delay(TimeSpan.FromSeconds(seconds), time, stoppingToken);
        }
    }

    public async Task ImportOnceAsync(string path, CancellationToken cancellationToken)
        => _ = await TryImportOnceAsync(path, cancellationToken);

    private async Task<bool> TryImportOnceAsync(string path, CancellationToken cancellationToken)
    {
        if (!await _gate.WaitAsync(0, cancellationToken))
            return false;
        try
        {
            var before = store.Capture().Status;
            var checkedAt = time.GetUtcNow();
            store.SetStatus(before with { LastFileCheckAtUtc = checkedAt });

            CompositionCandidate candidate;
            try
            {
                candidate = await reader.ReadCompositionAsync(path, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (CsvShowcaseException ex)
            {
                var latest = store.Capture().Status;
                store.SetStatus(latest with { ErrorCount = 1, LastError = ex.Message });
                logger.LogWarning("CSV composition import failed for source {SourceId}: {Error}",
                    _config.CompositionSourceId, ex.Message);
                return false;
            }

            var current = store.Capture();
            var unchanged = string.Equals(candidate.Fingerprint, current.Status.Fingerprint, StringComparison.Ordinal);
            var importedAt = unchanged ? current.Status.LastDataImportAtUtc : checkedAt;
            var status = current.Status with
            {
                LastSuccessfulValidationAtUtc = checkedAt,
                LastDataImportAtUtc = importedAt,
                Fingerprint = candidate.Fingerprint,
                RowCount = candidate.Memberships.Count,
                ErrorCount = 0,
                LastError = null,
            };
            if (unchanged)
                store.SetStatus(status, validated: true);
            else
                store.Replace(candidate.Memberships, status);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }
}
