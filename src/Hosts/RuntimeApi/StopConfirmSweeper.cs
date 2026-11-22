using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PiAgent.Infrastructure.Persistence;
using PiAgent.Platform.Workers;

namespace PiAgent.RuntimeApi;

/// <summary>
/// The stop-confirm sweeper — ticket 14's reconciliation leg of "Run/Task `cancelled` after runtime
/// confirmation OR reconciliation" (06 §8; ticket 13's general reconciler does not exist). A Run left
/// in `stopping` whose stop signal is older than the grace window without a terminal callback gets the
/// hard backstop: Komodo stop (idempotent) and Run `cancelled` with the identical terminal facts a
/// delivery would record (log reference, credential revocation, session terminated, workspace
/// release). The Bridge's worst-case sequence is ~46 s + delivery retries — the window leaves
/// headroom; a Bridge that delivers `cancelled` later meets "already terminal" and gives up cleanly.
/// Orchestrator-owned Task/Workflow transitions stay with the terminal-Run consumer (03 §2).
/// </summary>
public sealed class StopConfirmSweeper(
    IServiceScopeFactory scopeFactory,
    ILogger<StopConfirmSweeper> logger) : IWorkerCycle
{
    /// <summary>Grace granted to the Bridge's graceful sequence + terminal delivery before the
    /// reconciled cancellation runs. Constant in V1: only /stop can put a Run into `stopping`.</summary>
    public static readonly TimeSpan GraceWindow = TimeSpan.FromSeconds(90);

    public async Task RunCycleAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var komodo = scope.ServiceProvider.GetRequiredService<IKomodoClient>();
        var storage = scope.ServiceProvider.GetRequiredService<RuntimeStorageService>();

        var deadline = DateTime.UtcNow - GraceWindow;
        var stale = await db.Runs
            .Where(r => r.Status == RunStatuses.Stopping
                && r.StopRequestedAt != null
                && r.StopRequestedAt < deadline)
            .ToListAsync(cancellationToken);
        if (stale.Count == 0)
            return;

        foreach (var run in stale)
        {
            if (cancellationToken.IsCancellationRequested)
                return;
            logger.LogWarning("run {runId} never confirmed its stop (signalled {signalledAt}) — reconciling cancellation",
                run.Id, run.StopRequestedAt);
            await ReconcileStopAsync(db, komodo, storage, run, cancellationToken);
        }
    }

    /// <summary>One reconciled cancellation; retried on the next tick when Komodo is unreachable.</summary>
    private async Task ReconcileStopAsync(
        AppDbContext db, IKomodoClient komodo, RuntimeStorageService storage, Run run,
        CancellationToken cancellationToken)
    {
        var container = await db.ContainerInstances
            .Where(c => c.RunId == run.Id)
            .OrderByDescending(c => c.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);
        if (container?.DeploymentName is { } deployment)
        {
            var stopped = await komodo.StopAsync(deployment, cancellationToken);
            if (!stopped)
                return; // decision 22 posture: desired state preserved, nothing corrupted, retried later
            container.ObservedState = ContainerObservedStates.Stopped;
            container.LastStoppedAt = DateTime.UtcNow;
        }

        await CallbackHandler.FinalizeTerminalRunAsync(db, run, storage, cancellationToken);
        run.Status = RunStatuses.Cancelled;
        run.CompletedAt = DateTime.UtcNow;
        run.ErrorMessage = "stop unconfirmed: the Bridge never delivered the terminal cancelled callback";

        await db.SaveChangesAsync(cancellationToken);
        await ReleaseWorkspaceAsync(db, run.TaskId, cancellationToken);
        logger.LogInformation("run {runId} reconciled to cancelled without terminal confirmation", run.Id);
    }

    /// <summary>Mirrors the terminal-callback release (decision 75): the workspace leaves `in_use` when
    /// the container is a factually stopped machine.</summary>
    private static async Task ReleaseWorkspaceAsync(AppDbContext db, Guid taskId, CancellationToken cancellationToken)
    {
        await db.Workspaces
            .Where(w => w.TaskId == taskId && w.Status == WorkspaceStatuses.InUse)
            .ExecuteUpdateAsync(set =>
            {
                set.SetProperty(w => w.Status, WorkspaceStatuses.Ready);
                set.SetProperty(w => w.LastUsedAt, DateTime.UtcNow);
            }, cancellationToken);
    }
}
