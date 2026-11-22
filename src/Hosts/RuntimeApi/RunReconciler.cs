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
/// re-observing each non-terminal Run's container through Komodo. Beyond refreshing the observed
/// machine (ticket 09), ticket 13 adds the death reconciliation: a Run still `starting`/`running`
/// whose container is observed `stopped`/`failed`/`removed` — or whose deployment Komodo no longer
/// knows — died without emitting a terminal callback; it is finalized `failed` with
/// `runtime_failure` ("reconciled: …") so the notification path fires (01 §12: never silent
/// recovery). Runs in `stopping` belong to the stop-confirm sweeper (ticket 14). A Komodo outage
/// (decision 22) makes the observation fail — desired state is preserved and the cycle retries.
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

        var storage = scope.ServiceProvider.GetRequiredService<RuntimeStorageService>();
        var containers = await db.ContainerInstances
            .Where(c => unfinishedIds.Contains(c.RunId!.Value) && c.DeploymentName != null)
            .ToListAsync(cancellationToken);
        var runsById = await db.Runs
            .Where(r => unfinishedIds.Contains(r.Id))
            .ToDictionaryAsync(r => r.Id, cancellationToken);
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

        // Death reconciliation (01 §12): a container observed dead while its Run never became terminal.
        var dead = containers
            .Where(c => runsById.TryGetValue(c.RunId!.Value, out var run)
                && run.Status is RunStatuses.Starting or RunStatuses.Running
                && (c.ObservedState is ContainerObservedStates.Stopped or ContainerObservedStates.Failed or ContainerObservedStates.Removed))
            .ToList();
        foreach (var container in dead)
        {
            var run = runsById[container.RunId!.Value];
            logger.LogWarning("run {runId} container {deployment} died without a terminal callback — reconciling failure",
                run.Id, container.DeploymentName);
            await RunReconcileService.TerminalAsync(db, komodo, storage, run, RunStatuses.Failed,
                RunErrorCodes.RuntimeFailure,
                "reconciled: the container died without emitting a terminal callback (observed by the Runtime reconciler)",
                cancellationToken);
        }
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
