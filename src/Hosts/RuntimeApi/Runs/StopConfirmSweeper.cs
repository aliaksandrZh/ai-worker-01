using PiAgent.RuntimeApi.Provisioning;
using PiAgent.RuntimeApi.Api;
using PiAgent.RuntimeApi.Runs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PiAgent.Infrastructure.Persistence;
using PiAgent.Platform.Workers;

namespace PiAgent.RuntimeApi.Runs;

/// <summary>
/// The stop-confirm sweeper — the reconciliation leg of "Run/Task `cancelled` after runtime
/// confirmation OR reconciliation". A Run left
/// in `stopping` whose stop signal is older than the grace window without a terminal callback gets the
/// hard backstop: docker stop (idempotent) and Run `cancelled` with the identical terminal facts a
/// delivery would record (log reference, credential revocation, session terminated, workspace
/// release). The Bridge's worst-case sequence is ~46 s + delivery retries — the window leaves
/// headroom; a Bridge that delivers `cancelled` later meets "already terminal" and gives up cleanly.
/// Orchestrator-owned Task/Workflow transitions stay with the terminal-Run consumer.
/// </summary>
public sealed class StopConfirmSweeper(
    IServiceScopeFactory scopeFactory,
    ILogger<StopConfirmSweeper> logger) : IWorkerCycle
{
    /// <summary>Grace granted to the Bridge's graceful sequence + terminal delivery before the
    /// reconciled cancellation runs. Constant: only /stop can put a Run into `stopping`.</summary>
    public static readonly TimeSpan GraceWindow = TimeSpan.FromSeconds(90);

    public async Task RunCycleAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var provisioner = scope.ServiceProvider.GetRequiredService<IContainerProvisioner>();
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
            await ReconcileStopAsync(db, provisioner, storage, run, cancellationToken);
        }
    }

    /// <summary>One reconciled cancellation via the shared reconciled-terminal write (identical terminal
    /// facts + the conditional terminal claim so a late Bridge delivery always wins); retried on the
    /// next tick when Docker is unreachable.</summary>
    private async Task ReconcileStopAsync(
        AppDbContext db, IContainerProvisioner provisioner, RuntimeStorageService storage, Run run,
        CancellationToken cancellationToken)
    {
        var reconciled = await RunReconcileService.TerminalAsync(db, provisioner, storage, run, RunStatuses.Cancelled,
            null, "stop unconfirmed: the Bridge never delivered the terminal cancelled callback", cancellationToken);
        if (reconciled)
            logger.LogInformation("run {runId} reconciled to cancelled without terminal confirmation", run.Id);
    }
}
