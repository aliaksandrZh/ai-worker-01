using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PiAgent.Infrastructure.Persistence;
using PiAgent.Platform.Workers;

namespace PiAgent.RuntimeApi;

/// <summary>
/// §14 Runtime-owned gauges: `runsActive` counts Runs in pending/starting/running/stopping, refreshed by the
/// reconciler cycle; other baseline counters stay with their tickets (timeouts → ticket 13).
/// </summary>
public sealed class RunPipelineMetrics(WorkerLoopState state, RunReconcilerCycle reconciler) : WorkerMetricsBase(state)
{
    protected override IEnumerable<KeyValuePair<string, object?>> CollectExtra()
    {
        yield return new KeyValuePair<string, object?>("runsActive", reconciler.RunsActive);
    }
}

/// <summary>
/// The Runtime restart reconciler (01 §12): recovers desired/observed runtime state from PostgreSQL by
/// re-observing each non-terminal Run's container through Komodo. It only refreshes observed machine +
/// stop/removed timestamps — failing a container that died without a terminal callback is ticket 13's job.
/// </summary>
public sealed class RunReconcilerCycle(IServiceScopeFactory scopeFactory, ILogger<RunReconcilerCycle> logger) : IWorkerCycle
{
    public long RunsActive { get; private set; }

    public async Task RunCycleAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var komodo = scope.ServiceProvider.GetRequiredService<IKomodoClient>();

        var unfinishedIds = await db.Runs
            .Where(r => r.Status == RunStatuses.Pending
                || r.Status == RunStatuses.Starting
                || r.Status == RunStatuses.Running
                || r.Status == RunStatuses.Stopping)
            .Select(r => r.Id)
            .ToListAsync(cancellationToken);
        RunsActive = unfinishedIds.Count;
        if (unfinishedIds.Count == 0)
            return;

        var containers = await db.ContainerInstances
            .Where(c => unfinishedIds.Contains(c.RunId!.Value) && c.DeploymentName != null)
            .ToListAsync(cancellationToken);
        var changed = 0;
        foreach (var container in containers)
        {
            var observed = await komodo.GetContainerStateAsync(container.DeploymentName!, cancellationToken);
            var mapped = MapState(observed?.State);
            if (mapped is null || container.ObservedState == mapped)
                continue;
            logger.LogInformation(
                "reconciler: container {deployment} observed '{observed}' (komodo '{state}')",
                container.DeploymentName, mapped, observed?.State ?? "unknown-deployment");
            container.ObservedState = mapped;
            changed++;
            if (mapped == ContainerObservedStates.Stopped)
                container.LastStoppedAt = DateTime.UtcNow;
            if (mapped == ContainerObservedStates.Removed)
                container.RemovedAt = DateTime.UtcNow;
        }
        if (changed > 0)
            await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Komodo state → ContainerObservedStates (03 §5); unmapped states leave the row alone.</summary>
    internal static string? MapState(string? komodoState) => komodoState switch
    {
        "running" => ContainerObservedStates.Running,
        "created" => ContainerObservedStates.Created,
        "exited" or "dead" => ContainerObservedStates.Failed,
        "stopping" or "paused" => ContainerObservedStates.Stopping,
        "not_deployed" or "removing" => ContainerObservedStates.Removed,
        _ => null,
    };
}
