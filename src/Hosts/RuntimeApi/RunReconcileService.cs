using Microsoft.EntityFrameworkCore;
using PiAgent.Infrastructure.Persistence;

namespace PiAgent.RuntimeApi;

/// <summary>
/// Shared reconciliation facts for the ticket-13 timeout/death reconcilers: effective execution limits
/// (§16: a profile version may lower `startup_timeout`/`idle_timeout`/`max_run_duration` but never
/// bypass the system constraints — the effective value is capped at the system knob) and the
/// reconciled-terminal write (the identical terminal facts a delivery would record, plus the Docker
/// stop). The stop is attempted first and confirmed: a Docker outage (decision 22) preserves the
/// desired state and the cycle simply retries on the next tick without corrupting anything.
/// </summary>
public static class RunReconcileService
{
    /// <summary>Effective limit for one pinned profile version: `version ?? system`, capped by system.</summary>
    public static TimeSpan EffectiveLimit(TimeSpan? profileValue, TimeSpan systemValue) =>
        profileValue is { } value && value < systemValue ? value : systemValue;

    /// <summary>
    /// One reconciled terminal Run: stop the container over the Docker socket, then record the Run terminal state
    /// + error code + the terminal facts (log reference, credential revocation, session terminated,
    /// workspace release). Returns false when the stop could not be confirmed — nothing was written
    /// (desired state preserved, retried later). The terminal write itself is a conditional claim from a
    /// non-terminal status (task 16): the terminal callback that lands first wins — a stale reconciler
    /// observation never overwrites a just-delivered `completed`/`failed`/`cancelled`; 0 rows = already
    /// terminal = idempotent skip.
    /// </summary>
    public static async Task<bool> TerminalAsync(
        AppDbContext db, IContainerProvisioner provisioner, RuntimeStorageService storage,
        Run run, string status, string? errorCode, string message, CancellationToken cancellationToken)
    {
        var container = await db.ContainerInstances
            .Where(c => c.RunId == run.Id)
            .OrderByDescending(c => c.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);
        if (container?.ContainerName is { } containerName)
        {
            var stopped = await provisioner.StopAsync(containerName, cancellationToken);
            if (!stopped)
                return false; // decision 22 posture: desired state preserved, nothing corrupted, retried later
            container.ObservedState = ContainerObservedStates.Stopped;
            container.LastStoppedAt = DateTime.UtcNow;
        }

        // conditional terminal claim FIRST (04 §8: exactly one terminal writer): run from a non-terminal
        // status, so a callback delivery racing this reconcile is the survivor and this becomes a no-op —
        // the terminal facts below are only applied by the actual winner
        var now = DateTime.UtcNow;
        var claimed = await db.Runs
            .Where(r => r.Id == run.Id
                && (r.Status == RunStatuses.Pending
                    || r.Status == RunStatuses.Starting
                    || r.Status == RunStatuses.Running
                    || r.Status == RunStatuses.Stopping))
            .ExecuteUpdateAsync(set => set
                .SetProperty(r => r.Status, status)
                .SetProperty(r => r.ErrorCode, errorCode)
                .SetProperty(r => r.ErrorMessage, message)
                .SetProperty(r => r.CompletedAt, now)
                .SetProperty(r => r.LastProgressAt, r => r.LastProgressAt ?? now), cancellationToken);
        if (claimed == 0)
            return true; // already terminal — the delivered callback (or a prior reconcile) owns the state

        await CallbackHandler.FinalizeTerminalRunAsync(db, run, storage, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        await db.Workspaces
            .Where(w => w.TaskId == run.TaskId)
            .ExecuteUpdateAsync(set =>
            {
                set.SetProperty(w => w.Status, WorkspaceStatuses.Ready); // in_use/provisioning → the run is over
                set.SetProperty(w => w.LastUsedAt, DateTime.UtcNow);
            }, cancellationToken);
        return true;
    }
}
