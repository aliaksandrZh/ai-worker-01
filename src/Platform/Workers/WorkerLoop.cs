using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace PiAgent.Platform.Workers;

public sealed class WorkerLoopOptions
{
    /// <summary>Tick cadence in seconds. Heartbeat advances every tick; last-successful-cycle only on clean ticks.</summary>
    public int IntervalSeconds { get; set; } = 5;
}

public sealed class WorkerLoopState
{
    public bool Running { get; set; }
    public DateTime? HeartbeatUtc { get; set; }
    public DateTime? LastSuccessfulCycleUtc { get; set; }
}

/// <summary>
/// §13 worker heartbeat: ticks every IntervalSeconds, runs all IWorkerCycle units.
/// Dependency failures inside cycles never stop the loop (startup-restart tolerance);
/// lastSuccessfulCycleUtc advances only when every cycle succeeds.
/// </summary>
public sealed class WorkerLoop(
    IEnumerable<IWorkerCycle> cycles,
    WorkerLoopState state,
    IOptions<WorkerLoopOptions> options,
    ILogger<WorkerLoop> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        state.Running = true;
        state.HeartbeatUtc = DateTime.UtcNow;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(options.Value.IntervalSeconds));
        try
        {
            do
            {
                state.HeartbeatUtc = DateTime.UtcNow;
                var failedCycles = 0;
                foreach (var cycle in cycles)
                {
                    try
                    {
                        await cycle.RunCycleAsync(stoppingToken);
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception exception)
                    {
                        failedCycles++;
                        logger.LogWarning(exception, "cycle failed {cycle}", cycle.GetType().Name);
                    }
                }
                if (failedCycles == 0)
                    state.LastSuccessfulCycleUtc = DateTime.UtcNow;
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException)
        {
            // normal shutdown
        }
        finally
        {
            state.Running = false;
        }
    }
}

public static class WorkerLoopRegistration
{
    /// <summary>Registers the worker loop state + hosted loop; hosts add their IWorkerCycle/IWorkerMetrics alongside.</summary>
    public static IServiceCollection AddPiWorkerLoop(this IServiceCollection services)
    {
        services.AddSingleton<WorkerLoopState>();
        services.AddHostedService<WorkerLoop>();
        services.AddOptions<WorkerLoopOptions>().BindConfiguration("WorkerLoop");
        return services;
    }
}
