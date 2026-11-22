using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PiAgent.Orchestrator.Commands;
using PiAgent.Orchestrator.Config;
using PiAgent.Orchestrator.Context;
using PiAgent.Platform.Workers;
using PiAgent.Infrastructure.Persistence;

namespace PiAgent.Orchestrator.Orchestration;

/// <summary>
/// The durable job queue consumer (02 §19, 03 §8, task 07). Per tick: sweep expired claim leases (job
/// `pending`, Task `claimed → queued`, never-dispatched `pending` Run rows deleted — but a claim whose
/// Run was already handed off is restored to the hand-off state instead: never a rewind that would
/// re-execute the work, task 16), then claim a FIFO
/// batch (`available_at`, then `priority`, higher priority first) up to the global
/// `max_concurrent_runs` cap — the claim transaction locks job rows `FOR UPDATE SKIP LOCKED` so
/// concurrent workers never double-claim. Queued Tasks and `pending` Jobs occupy no slot; the slot
/// math counts Claims (`task.claimed`) plus Runs in `starting|running|stopping` (decision 75; the
/// terminal-Run-with-unconfirmed-stop release half activates with tickets 09/14). Outside the claim
/// transaction each claim revalidates its preconditions (04 §2, 06 §12): Workflow not failed/closed,
/// repository not paused (skip, not cancel), WorkItem open, pinned version still ready, plan input
/// present for implement Tasks, pinned review SHA unchanged, permission recheck — an invalid claim is
/// cancelled with a recorded reason (TaskLifecycle), a transient check releases it; a valid claim is
/// prepared (RunPrepare) and handed to the dispatch stub (ticket 09 replaces the no-op).
/// </summary>
public sealed class JobQueueWorker(
    IServiceScopeFactory scopeFactory,
    ConfigurationState configurationState,
    CollaboratorPermissionGate permissionGate,
    IOptions<JobQueueOptions> options,
    IRunDispatcher dispatcher,
    JobQueueMetrics metrics,
    ILogger<JobQueueWorker> logger) : IWorkerCycle
{
    /// <summary>Worker identity for `locked_by`; per-process (single-instance V1, lease-recovered).</summary>
    private readonly string _workerId =
        $"{Environment.MachineName}:{Environment.ProcessId}:{Guid.NewGuid():N}";
    private readonly JobQueueOptions _options = options.Value;

    public async Task RunCycleAsync(CancellationToken cancellationToken)
    {
        // cold-start gate (01 §13): no claim, no sweep, until configuration synchronization succeeded
        if (!configurationState.Synchronized)
            return;

        var skipped = new HashSet<Guid>();
        await SweepExpiredLeasesAsync(skipped, cancellationToken);
        var claimed = await ClaimBatchAsync(skipped, cancellationToken);
        foreach (var claim in claimed)
            await processClaimAsync(claim, skipped, cancellationToken);
        await RefreshGaugesAsync(cancellationToken);
    }

    /// <summary>Sweeps claim leases left by a terminated Orchestrator (03 §8). An expired claim whose
    /// Run was already handed off (the Runtime API flipped it out of `pending` inside /start) never
    /// rewinds the Task — rewinding would re-execute the same work (task 16); it restores the hand-off
    /// state and lets the terminal-Run consumer own the transition.
    /// Per tick: expired `processing` leases — job `pending` (or hand-off restored), Task `claimed →
    /// queued` only when nothing was dispatched, never-dispatched `pending` Run rows deleted.</summary>
    private async Task SweepExpiredLeasesAsync(HashSet<Guid> skipped, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var now = DateTime.UtcNow;
        var expired = await db.OrchestrationJobs
            .Where(j => j.Status == OrchestrationJobStatuses.Processing
                && (j.LockedUntil == null || j.LockedUntil < now))
            .ToListAsync(cancellationToken);
        if (expired.Count == 0)
            return;

        foreach (var job in expired)
        {
            var task = await db.OrchestrationTasks.SingleAsync(t => t.Id == job.TaskId, cancellationToken);
            await db.Runs
                .Where(r => r.TaskId == task.Id && r.Status == RunStatuses.Pending)
                .ExecuteDeleteAsync(cancellationToken); // DB cascades session + container rows
            var lastRunStatus = await db.Runs
                .Where(r => r.TaskId == task.Id)
                .OrderByDescending(r => r.AttemptNumber)
                .Select(r => (string?)r.Status)
                .FirstOrDefaultAsync(cancellationToken);
            if (lastRunStatus is not null && lastRunStatus != RunStatuses.Pending)
            {
                // hand-off happened before the crash (RunDispatch: the Runtime API flips the Run out of
                // `pending` inside /start, in its own request): rewinding to pending/queued would
                // re-execute the same task — restore what the dispatcher's write-back would have
                // persisted and let the terminal-Run consumer adopt the Run (no duplicate execution)
                logger.LogWarning("job {jobId} lease expired after hand-off (run '{status}') — restoring hand-off state", job.Id, lastRunStatus);
                task.Status = OrchestrationTaskStatuses.Starting;
                task.StartedAt = task.StartedAt ?? now;
                job.Status = OrchestrationJobStatuses.Completed;
                job.LockedUntil = null;
                job.LockedBy = null;
                job.UpdatedAt = now;
                job.LastError = null;
                skipped.Add(job.Id);
                metrics.RecordLeaseExpiration();
                continue;
            }
            if (task.Status == OrchestrationTaskStatuses.Cancelled)
            {
                // /stop cancelled the claimed work while a hung claim still held the lease — never
                // resurrect cancelled work into the queue (the consumer no longer owns this task)
                logger.LogWarning("job {jobId} lease expired after /stop cancelled its task — job cancelled", job.Id);
                job.Status = OrchestrationJobStatuses.Cancelled;
                job.LockedUntil = null;
                job.LockedBy = null;
                job.UpdatedAt = now;
                skipped.Add(job.Id);
                metrics.RecordLeaseExpiration();
                continue;
            }
            if (task.Status == OrchestrationTaskStatuses.Claimed)
            {
                // the only spec edge back from `claimed` (03 §2: claim lease expires) — the claim never
                // handed off, so returning the work to the queue cannot duplicate execution
                task.Status = OrchestrationTaskStatuses.Queued;
                task.ClaimedAt = null;
            }
            job.Status = OrchestrationJobStatuses.Pending;
            job.LockedUntil = null;
            job.LockedBy = null;
            job.UpdatedAt = now;
            job.LastError = "claim lease expired";
            skipped.Add(job.Id); // no same-tick re-claim of a swept job
            metrics.RecordLeaseExpiration();
        }
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Claims up to `min(free, BatchLimit)` jobs atomically. Returns the claimed (job, task) ids;
    /// the actual rechecks/prepare/dispatch run outside this transaction (lease guarantees recovery).</summary>
    private async Task<List<(Guid JobId, Guid TaskId)>> ClaimBatchAsync(IReadOnlySet<Guid> skipped, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var free = configurationState.MaxConcurrentRuns - await CountOccupiedSlotsAsync(db, cancellationToken);
        if (free <= 0)
        {
            await transaction.RollbackAsync(cancellationToken);
            return [];
        }

        var now = DateTime.UtcNow;
        var limit = Math.Min(free, _options.BatchLimit);
        var skip = skipped.ToArray();
        // Paused repositories are skipped in the claim query itself (decision 75: queued Tasks stay
        // unclaimed, never claimed-and-released); `skipped` keeps jobs released/swept earlier this tick
        // from being instantly re-claimed; future `available_at` (retry backoff, ticket 15) waits.
        var candidates = await db.OrchestrationJobs
            .FromSqlInterpolated($"SELECT j.* FROM orchestration_job j JOIN task t ON t.id = j.task_id JOIN workflow w ON w.id = t.workflow_id JOIN work_item wi ON wi.id = w.work_item_id JOIN repository r ON r.id = wi.repository_id WHERE j.status = {OrchestrationJobStatuses.Pending} AND j.available_at <= {now} AND r.is_enabled AND NOT (j.id = ANY({skip})) ORDER BY j.available_at, j.priority DESC LIMIT {limit} FOR UPDATE SKIP LOCKED")
            .ToListAsync(cancellationToken);

        var claimed = new List<(Guid, Guid)>();
        foreach (var job in candidates)
        {
            var task = await db.OrchestrationTasks.SingleAsync(t => t.Id == job.TaskId, cancellationToken);
            if (task.Status != OrchestrationTaskStatuses.Queued)
            {
                // never execute inconsistent work; /retry (ticket 15) creates the fresh job instead
                job.Status = OrchestrationJobStatuses.Failed;
                job.LastError = $"task not in queued state ('{task.Status}')";
                job.UpdatedAt = now;
                continue;
            }
            job.Status = OrchestrationJobStatuses.Processing;
            job.Attempts++;
            job.LockedUntil = now + TimeSpan.FromSeconds(_options.LeaseSeconds);
            job.LockedBy = _workerId;
            job.LastError = null;
            job.UpdatedAt = now;
            task.Status = OrchestrationTaskStatuses.Claimed;
            task.ClaimedAt = now;
            metrics.RecordClaim();
            claimed.Add((job.Id, task.Id));
        }
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return claimed;
    }

    private async Task<int> CountOccupiedSlotsAsync(AppDbContext db, CancellationToken cancellationToken)
    {
        var claimedTasks = await db.OrchestrationTasks
            .CountAsync(t => t.Status == OrchestrationTaskStatuses.Claimed, cancellationToken);
        var activeRuns = await db.Runs
            .CountAsync(r => r.Status == RunStatuses.Starting
                || r.Status == RunStatuses.Running
                || r.Status == RunStatuses.Stopping, cancellationToken);
        return claimedTasks + activeRuns;
    }

    /// <summary>Whether the bot is still among the synced requested reviewers (ticket 19, 06 §11).</summary>
    private static bool RequestedReviewerPresent(WorkItem workItem, string? botLogin)
    {
        if (string.IsNullOrEmpty(botLogin) || string.IsNullOrWhiteSpace(workItem.RequestedReviewers))
            return false;
        var reviewers = System.Text.Json.JsonSerializer.Deserialize<List<string>>(workItem.RequestedReviewers) ?? [];
        return reviewers.Contains(botLogin, StringComparer.OrdinalIgnoreCase);
    }

    private async Task processClaimAsync((Guid JobId, Guid TaskId) claim, ISet<Guid> skipped, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var job = await db.OrchestrationJobs
            .SingleOrDefaultAsync(j => j.Id == claim.JobId && j.Status == OrchestrationJobStatuses.Processing, cancellationToken);
        if (job is null)
            return; // swept/terminal between claim and processing (single instance: recovery only)
        var task = await db.OrchestrationTasks.SingleAsync(t => t.Id == claim.TaskId, cancellationToken);
        var workflow = await db.Workflows.SingleAsync(w => w.Id == task.WorkflowId, cancellationToken);
        var workItem = await db.WorkItems.AsNoTracking().SingleAsync(w => w.Id == workflow.WorkItemId, cancellationToken);
        var repository = await db.Repositories.AsNoTracking().SingleAsync(r => r.Id == workItem.RepositoryId, cancellationToken);
        var version = await db.AgentProfileVersions.SingleAsync(v => v.Id == task.AgentProfileVersionId, cancellationToken);
        var profile = await db.AgentProfiles.AsNoTracking().SingleAsync(p => p.Id == version.AgentProfileId, cancellationToken);
        var command = task.TriggerCommandId is { } commandId
            ? await db.AgentCommands.SingleAsync(c => c.Id == commandId, cancellationToken)
            : null;

        async Task CancelAsync(string reason) =>
            await TaskLifecycle.CancelTaskAtClaimAsync(db, task, job, repository, workItem, reason, metrics, cancellationToken);
        async Task ReleaseAsync()
        {
            var now = DateTime.UtcNow;
            await db.Runs
                .Where(r => r.TaskId == task.Id && r.Status == RunStatuses.Pending)
                .ExecuteDeleteAsync(cancellationToken);
            job.Status = OrchestrationJobStatuses.Pending;
            job.LockedUntil = null;
            job.LockedBy = null;
            job.UpdatedAt = now;
            task.Status = OrchestrationTaskStatuses.Queued;
            await db.SaveChangesAsync(cancellationToken);
            skipped.Add(job.Id); // released this tick: never reclaimed within the same cycle
        }

        // ---- claim-time rechecks (order per 04 §2/06 §12 + design doc) ----
        if (workflow.Status == WorkflowStatuses.Failed)
        {
            await CancelAsync(AgentCommandRejectionReasons.WorkflowFailed);
            return; // decision 71
        }
        if (workflow.Status == WorkflowStatuses.Closed)
        {
            await CancelAsync(AgentCommandRejectionReasons.WorkItemClosed);
            return; // root closed while queued
        }
        if (!repository.IsEnabled)
        {
            // decision 75: pause — queued work stays unclaimed (not cancelled), resumes on re-enable
            await ReleaseAsync();
            return;
        }
        if (workItem.State != "open")
        {
            await CancelAsync(AgentCommandRejectionReasons.WorkItemClosed);
            return;
        }
        if (!profile.IsEnabled || version.State == ProfileVersionStates.Disabled)
        {
            await CancelAsync(AgentCommandRejectionReasons.AgentProfileNotConfigured);
            return;
        }
        if (version.State != ProfileVersionStates.Ready)
        {
            await CancelAsync(AgentCommandRejectionReasons.AgentImageNotReady); // decision 35
            return;
        }
        if (task.Type == OrchestrationTaskTypes.Implement
            && !await db.TaskArtifactInputs.AnyAsync(i => i.TaskId == task.Id, cancellationToken))
        {
            await CancelAsync(AgentCommandRejectionReasons.PlanNotAvailable); // decision 80
            return;
        }
        if (task.Type == OrchestrationTaskTypes.Review && task.TriggerGithubEventId is not null
            && (task.FailureReason == AgentCommandRejectionReasons.ReviewRequestRemovedBeforeExecution
                || !RequestedReviewerPresent(workItem, configurationState.BotLogin)))
        {
            // ticket 19 (06 §11): the automatic initial review re-checks the bot's request before it starts
            await CancelAsync(AgentCommandRejectionReasons.ReviewRequestRemovedBeforeExecution);
            return;
        }
        if (task.Type == OrchestrationTaskTypes.Review
            && !string.Equals(workItem.HeadSha, task.TargetHeadSha, StringComparison.Ordinal))
        {
            await CancelAsync(AgentCommandRejectionReasons.ReviewHeadChanged); // decision 31
            return;
        }
        var permission = await ClaimPermissionCheck.RecheckAsync(db, permissionGate, repository, command, cancellationToken);
        if (permission is { Transient: true })
        {
            logger.LogInformation("claim authorization recheck deferred for task {taskId} (transient GitHub state) — claim released", task.Id);
            await ReleaseAsync();
            return;
        }
        if (permission is { Denied: true, RejectionReason: { } denialReason })
        {
            await CancelAsync(denialReason);
            return;
        }

        var (preparation, contextTooLarge) = await AssembleAndPrepareAsync(
            db, task, job, version, workflow, workItem, repository, command, cancellationToken);
        if (contextTooLarge)
        {
            // ticket 11: the Task already failed terminal with `context_too_large` (no Run rows, job
            // failed, command failed) — nothing to release
            return;
        }
        if (preparation is null)
        {
            // serialization hook refused (a previous Run of this Workflow is still non-terminal)
            await ReleaseAsync();
            return;
        }
        var outcome = await dispatcher.DispatchAsync(db, preparation, cancellationToken);
        switch (outcome)
        {
            case DispatchOutcome.HandedOff:
                break; // run started; the terminal-Run consumer takes over at Run terminal state
            case DispatchOutcome.Transient:
                await ReleaseAsync(); // the dispatcher contract: nothing was started, retry next tick
                break;
            case DispatchOutcome.Permanent:
                await CancelAsync(AgentCommandRejectionReasons.RuntimeDispatchFailed);
                break;
            case DispatchOutcome.Superseded:
                break; // ticket 14: the claim lost to /stop mid-flight — prepared rows removed, nothing to release
        }
    }

    /// <summary>
    /// Ticket 11 (decision 63): the Context Assembler runs BEFORE RunPrepare so that — per the spec —
    /// mandatory context that cannot fit fails the Task with `context_too_large` while the Run is
    /// NEVER created (no Run/AgentSession/Container/Workspace rows, no snapshot, no dispatch). When the
    /// context fits, the Run is prepared carrying the assembled package for the dispatcher to freeze.
    /// `contextTooLarge = true` means the Task is already terminal — no release must happen; a null
    /// preparation (with `contextTooLarge = false`) means the Workflow was busy → release the claim.
    /// </summary>
    private async Task<(RunPreparation? Preparation, bool ContextTooLarge)> AssembleAndPrepareAsync(
        AppDbContext db,
        OrchestrationTask task,
        OrchestrationJob job,
        AgentProfileVersion version,
        Workflow workflow,
        WorkItem workItem,
        Repository repository,
        AgentCommand? command,
        CancellationToken cancellationToken)
    {
        var assembly = await ContextAssembler.AssembleAsync(
            db, task, version, workflow, workItem, repository, command, cancellationToken);
        if (assembly.TooLarge)
        {
            await TaskLifecycle.FailTaskContextTooLargeAsync(
                db, task, job, repository, workItem, assembly.TooLargeDetail ?? "context_too_large", metrics, cancellationToken);
            return (null, true);
        }
        return (await RunPrepare.PrepareAsync(db, task, version, cancellationToken, assembly.Assembled), false);
    }

    private async Task RefreshGaugesAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var now = DateTime.UtcNow;
        var pendingJobs = await db.OrchestrationJobs
            .CountAsync(j => j.Status == OrchestrationJobStatuses.Pending, cancellationToken);
        var oldest = await db.OrchestrationJobs
            .Where(j => j.Status == OrchestrationJobStatuses.Pending)
            .OrderBy(j => j.AvailableAt)
            .Select(j => (DateTime?)j.AvailableAt)
            .FirstOrDefaultAsync(cancellationToken);
        var oldestAge = oldest is { } availableAt ? (now - availableAt).TotalSeconds : 0;
        var slots = await CountOccupiedSlotsAsync(db, cancellationToken);
        metrics.UpdateGauges(pendingJobs, oldestAge, oldestAge, slots);
    }
}
