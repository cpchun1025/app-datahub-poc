using Microsoft.Extensions.Hosting;

namespace DataHub.Api.Sources.Mock;

/// <summary>驅動所有 mock upstream 嘅模擬 tick。只處理 catalog 內嘅 mock adapter，換咗真實 adapter 就自動唔做嘢。</summary>
public sealed class MockFeedService(
    ISourceCatalog catalog,
    TimeProvider time,
    ILogger<MockFeedService> logger) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var loops = catalog.Adapters
            .OfType<IMockSourceAdapter>()
            .Select(adapter => RunTickerAsync(adapter.Upstream, stoppingToken))
            .ToArray();

        return Task.WhenAll(loops);
    }

    private async Task RunTickerAsync(MockUpstream upstream, CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(upstream.TickInterval, time);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                upstream.Tick();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Mock ticker for {SourceId} stopped unexpectedly.", upstream.SourceId);
        }
    }
}
