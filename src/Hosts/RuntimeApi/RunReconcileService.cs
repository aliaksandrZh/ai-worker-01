using Microsoft.EntityFrameworkCore;
using PiAgent.Infrastructure.Persistence;

namespace PiAgent.RuntimeApi;

/// <summary>
/// Shared reconciliation facts for the ticket-13 timeout/death reconcilers: effective execution limits
/// (§16: a profile version may lower `startup_timeout`/`idle_timeout`/`max_run_duration` but never
/// bypass the system constraints — the effective value is capped at the system knob) and the
/// reconciled-terminal write (the identical terminal facts a delivery would record, plus the Komodo
/// stop). The stop is attempted first and confirmed: a Komodo outage (decision 22) preserves the
/// desired state and the cycle simply retries on the next tick without corrupting anything.
/// </summary>
public static class RunReconcileService
{
    /// <summary>Effective limit for one pinned profile version: `version ?? system`, capped by system.</summary>
    public static TimeSpan EffectiveLimit(TimeSpan? profileValue, TimeSpan systemValue) =>
        profileValue is { } value && value < systemValue ? value : systemValue;

    /// <summary>
    /// One reconciled terminal Run: stop the container via Komodo, then record the Run terminal state
    /// + error code + the terminal facts (log reference, credential revocation, session terminated,
    /// workspace release). Returns false when the stop could not be confirmed — nothing was written
    /// (desired state preserved, retried later).
    /// </summary>
    public static async Task<bool> TerminalAsync(
        AppDbContext db, IKomodoClient komodo, RuntimeStorageService storage,
        Run run, string status, string? errorCode, string message, CancellationToken cancellationToken)
    {
        var container = await db.ContainerInstances
            .Where(c => c.RunId == run.Id)
            .OrderByDescending(c => c.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);
        if (container?.DeploymentName is { } deployment)
        {
            var stopped = await komodo.StopAsync(deployment, cancellationToken);
            if (!stopped)
                return false; // decision 22 posture: desired state preserved, nothing corrupted, retried later
            container.ObservedState = ContainerObservedStates.Stopped;
            container.LastStoppedAt = DateTime.UtcNow;
        }

        await CallbackHandler.FinalizeTerminalRunAsync(db, run, storage, cancellationToken);
        run.Status = status;
        run.ErrorCode = errorCode;
        run.ErrorMessage = message;
        run.CompletedAt = DateTime.UtcNow;
        run.LastProgressAt = run.LastProgressAt ?? run.CompletedAt;

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
