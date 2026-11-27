using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PiAgent.Events;
using PiAgent.RunWorker.Docker;

namespace PiAgent.RunWorker;

/// <summary>
/// The run watchdog (docs/run-worker.md invariant 3): liveness via docker inspect — a Run
/// exceeding the timeout, or exiting without a terminal callback after a grace period,
/// is settled as failed. Recovery after worker restarts is the registry + container labels.
/// </summary>
public sealed class RunWatchdog(
    RunRegistry registry,
    IContainerProvisioner docker,
    RunWorkerConsumer consumer,
    IOptions<RunWorkerOptions> options,
    ILogger<RunWatchdog> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SweepAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "watchdog sweep failed");
            }

            try
            {
                await timer.WaitForNextTickAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    internal async Task SweepAsync(CancellationToken ct)
    {
        var timeout = TimeSpan.FromSeconds(options.Value.RunTimeoutSeconds);
        var grace = TimeSpan.FromSeconds(options.Value.StopGraceSeconds);

        foreach (var entry in registry.All())
        {
            var snapshot = await docker.GetContainerStateAsync(entry.ContainerName, ct);

            if (snapshot.State == "removed" || snapshot.State is "exited" or "dead")
            {
                // the Bridge may still be delivering the terminal callback — grace runs from the
                // container's actual exit, not the run's start (a run longer than the grace period
                // would otherwise be failed on the first sweep after exit, run 52's race)
                var reference = snapshot.FinishedAtUtc ?? entry.StartedUtc;
                if (DateTime.UtcNow - reference > grace)
                {
                    await consumer.SettleOnWatchdogAsync(entry,
                        $"container {snapshot.State} without a terminal callback", ct);
                }

                continue;
            }

            if (DateTime.UtcNow - entry.StartedUtc > timeout)
            {
                logger.LogWarning("run {run} exceeded the watchdog timeout — stopping", entry.RunId);
                await docker.StopAsync(entry.ContainerName, ct);
                await consumer.SettleOnWatchdogAsync(entry, "watchdog timeout", ct);
            }
        }
    }
}
