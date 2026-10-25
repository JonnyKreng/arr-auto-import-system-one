using ArrImportSystemOne.Hub;
using ArrImportSystemOne.Options;
using ArrImportSystemOne.Queue;
using Microsoft.Extensions.Options;

namespace ArrImportSystemOne.Solver;

/// <summary>
/// Probes the configured System One decision provider and opens/closes <see cref="SystemOneAvailability"/>.
/// The worker waits while the gate is closed, so an unavailable model pauses the pipeline instead of
/// producing leave-to-human decisions. Changes are pushed to the UI via <c>queue.updated</c>.
/// </summary>
public sealed class SystemOneHealthMonitor(
    ISystemOneDecisionClient client,
    SystemOneAvailability availability,
    WorkerProgress progress,
    IDecisionBroadcaster broadcaster,
    IOptionsMonitor<LidarrOptions> options,
    ILogger<SystemOneHealthMonitor> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation(
            "System One decision provider configured at {Url}",
            options.CurrentValue.SystemOne.Url);

        var firstCheck = true;

        while (!stoppingToken.IsCancellationRequested)
        {
            var wasAvailable = availability.Snapshot().Available;

            SystemOneHealthResult health;
            try
            {
                health = await client.CheckHealthAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                // A provider implementation must not take the host down with it.
                health = new SystemOneHealthResult(false, ex.Message);
            }

            if (health.Healthy)
                availability.ReportAvailable();
            else
                availability.ReportUnavailable(health.Detail);

            if (firstCheck || wasAvailable != health.Healthy)
            {
                if (health.Healthy)
                    logger.LogInformation(
                        "System One decision provider is available at {Url}",
                        options.CurrentValue.SystemOne.Url);
                else
                    logger.LogWarning(
                        "System One decision provider is unavailable at {Url}: {Reason}. Decision processing paused",
                        options.CurrentValue.SystemOne.Url,
                        health.Detail ?? "health check failed");

                firstCheck = false;
                await BroadcastAsync(stoppingToken).ConfigureAwait(false);
            }

            try
            {
                var seconds = Math.Max(2, options.CurrentValue.SystemOne.HealthCheckSeconds);
                await Task.Delay(TimeSpan.FromSeconds(seconds), stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task BroadcastAsync(CancellationToken ct)
    {
        try
        {
            await broadcaster
                .QueueUpdatedAsync(progress.BuildQueueUpdated(), ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutting down.
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "queue.updated broadcast failed");
        }
    }
}
