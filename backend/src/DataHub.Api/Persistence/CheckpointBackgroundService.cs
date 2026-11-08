using DataHub.Api.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace DataHub.Api.Persistence;

/// <summary>
/// 定時保存 checkpoint（預設每 30 秒，同每星期一次嘅 source snapshot 係兩回事），並喺關機時做最後一次保存。
/// 呢個 service 要註冊喺 source sync 之前：host 以相反次序停止，所以 sync 停咗之後先會做最後保存。
/// </summary>
public sealed class CheckpointBackgroundService(
    CheckpointService checkpoints,
    IOptions<DataHubOptions> options,
    TimeProvider time,
    ILogger<CheckpointBackgroundService> logger) : BackgroundService
{
    private static readonly TimeSpan ShutdownSaveTimeout = TimeSpan.FromSeconds(10);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var checkpoint = options.Value.Checkpoint;
        if (!checkpoint.Enabled)
        {
            logger.LogInformation("Checkpoint is disabled by configuration.");
            return;
        }

        using var timer = new PeriodicTimer(checkpoint.Interval, time);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await checkpoints.SaveAsync(CheckpointTrigger.Periodic, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);

        var checkpoint = options.Value.Checkpoint;
        if (!checkpoint.Enabled || !checkpoint.SaveOnShutdown)
        {
            return;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ShutdownSaveTimeout);
        try
        {
            var result = await checkpoints.SaveAsync(CheckpointTrigger.Shutdown, timeout.Token);
            logger.LogInformation("Final checkpoint on shutdown: {Outcome}.", result.Outcome);
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning("Final checkpoint on shutdown was cancelled before it completed.");
        }
    }
}
