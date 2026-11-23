using PiAgent.RuntimeApi.Provisioning;
using PiAgent.RuntimeApi.Runs;
using PiAgent.RuntimeApi.Api;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PiAgent.Infrastructure.Persistence;
using PiAgent.Platform.Workers;

namespace PiAgent.RuntimeApi.Runs;

/// <summary>One idle-check outcome of the sweeper (unit-testable without a database).</summary>
public enum TimeoutVerdict
{
    /// <summary>Still inside every limit (or no limit applies yet).</summary>
    None,
    /// <summary>`startup_timeout`: the preparation never became ready (no agent_started).</summary>
    StartupTimeout,
    /// <summary>`max_run_duration`: the wall-clock limit is up (Run → `timed_out`).</summary>
    RunDuration,
    /// <summary>Silence long enough to be an idle CANDIDATE — reconciliation, never an immediate stop.</summary>
    IdleCandidate,
    /// <summary>Idle candidate + the Host Monitor's process/CPU/session check found no activity — a confirmed hang.</summary>
    IdleConfirmed,
    /// <summary>Idle candidate, but the Host Monitor observed an active child/CPU/session — a busy run is never killed for silence.</summary>
    IdleBusy,
    /// <summary>No heartbeat at all for longer than the idle window — the Bridge is likely dead; the
    /// container-death reconciler (which confirms through the observed container state) owns this case.</summary>
    HeartbeatLost,
}

/// <summary>
/// Pure limit arithmetic shared by <see cref="TimeoutSweeper"/> (unit-testable): which reconciliation
/// verdict applies to a Run's current facts. `now`/`startedAt`/`lastProgressAt`/`lastHeartbeatAt` are
/// UTC instants; null progress falls back to the start instant.
/// `permission_blocked`, thinking deltas and queue updates are never progress and reset no timer (they
/// are not in RunEventTypes.Progress, so the callback route never advances `last_progress_at` for them).
/// </summary>
public static class TimeoutEvaluator
{
    public static TimeoutVerdict Evaluate(
        string runStatus,
        DateTime? startedAt,
        DateTime? lastProgressAt,
        DateTime? lastHeartbeatAt,
        DateTime now,
        TimeSpan startupTimeout,
        TimeSpan idleTimeout,
        TimeSpan maxRunDuration,
        bool? hostMonitorActivity = null)
    {
        if (startedAt is not { } started)
            return TimeoutVerdict.None; // dispatch not yet handed off — the Orchestrator owns `pending`

        if (runStatus == RunStatuses.Starting && now - started >= startupTimeout)
            return TimeoutVerdict.StartupTimeout;

        if (runStatus is not (RunStatuses.Starting or RunStatuses.Running))
            return TimeoutVerdict.None;

        if (now - started >= maxRunDuration)
            return TimeoutVerdict.RunDuration;

        var progress = lastProgressAt ?? started;
        if (now - progress < idleTimeout)
            return TimeoutVerdict.None;

        // The idle definition: NO observable activity at all while the container is
        // alive — heartbeat continues (the Bridge is alive) but nothing else happened. A lost heartbeat
        // means the liveness signal itself is gone: the container-death reconciler owns that case
        // through the observed container state, not this verdict.
        if (lastHeartbeatAt is not { } heartbeat || now - heartbeat > idleTimeout)
            return TimeoutVerdict.HeartbeatLost;

        return hostMonitorActivity switch
        {
            true => TimeoutVerdict.IdleBusy, // a long-running build/test is not killed for silence
            false => TimeoutVerdict.IdleConfirmed,
            // no fresh Host-Monitor observation: fail-safe — reconciliation proceeds on the next tick
            // with evidence; an unevidenced idle kill never runs
            null => TimeoutVerdict.IdleCandidate,
        };
    }
}

/// <summary>
/// The execution-limit reconciler. One cycle:
/// <list type="bullet">
/// <item>`startup_timeout` — a Run still `starting` past the effective limit (its container/agent never
/// became ready) is stopped and failed with `startup_timeout`.</item>
/// <item>`idle_timeout` — silence of ALL observable activity while alive (heartbeat continues) makes the
/// Run an idle CANDIDATE: the Host Monitor's latest telemetry decides — active child processes/CPU or a
/// touched session file keep the run alive (a long-running build/test is not killed for silence); a
/// confirmed hang is stopped and failed with `idle_timeout`.</item>
/// <item>`max_run_duration` — the wall-clock limit ends the Run `timed_out` independently of heartbeat
/// or progress; no automatic retry exists for it (`/retry` is the only recovery).</item>
/// </list>
/// `startup_timeout`/`idle_timeout` verdicts are suppressed while the Run's Bridge callback
/// spool still holds undelivered entries (spool redelivery may be in flight — e.g. right
/// after a Runtime API restart mid-run); the facts they would adjudicate on may still be in the spool.
/// Every reconciled terminal goes through <see cref="RunReconcileService"/> (Docker stop confirmed →
/// identical terminal facts), so a Docker outage simply retries the cycle. The Run
/// `starting → running` transition itself is event-driven (`agent_started`, Callbacks.cs); business
/// Task/Workflow transitions stay with the Orchestrator's terminal consumer.
/// </summary>
public sealed class TimeoutSweeper(
    IServiceScopeFactory scopeFactory,
    RuntimeLimits limits,
    ILogger<TimeoutSweeper> logger) : IWorkerCycle
{
    /// <summary>A Host-Monitor observation older than this is treated as absent (fail-safe: no idle kill).</summary>
    public static readonly TimeSpan TelemetryFreshness = TimeSpan.FromSeconds(30);

    public async Task RunCycleAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var provisioner = scope.ServiceProvider.GetRequiredService<IContainerProvisioner>();
        var storage = scope.ServiceProvider.GetRequiredService<RuntimeStorageService>();

        var now = DateTime.UtcNow;
        var candidates = await db.Runs
            .Where(r => r.Status == RunStatuses.Starting || r.Status == RunStatuses.Running)
            .Select(r => new { r.Id, r.Status, r.StartedAt, r.LastProgressAt, r.LastHeartbeatAt, r.TaskId })
            .ToListAsync(cancellationToken);
        if (candidates.Count == 0)
            return;

        var versions = await db.OrchestrationTasks
            .Where(t => candidates.Select(c => c.TaskId).Contains(t.Id))
            .Select(t => new { t.Id, t.AgentProfileVersionId, t.WorkflowId })
            .ToDictionaryAsync(t => t.Id, t => (Version: t.AgentProfileVersionId, Workflow: t.WorkflowId), cancellationToken);
        var limitsByTask = await db.AgentProfileVersions
            .Where(v => versions.Values.Select(x => x.Version).Contains(v.Id))
            .Select(v => new { v.Id, v.StartupTimeout, v.IdleTimeout, v.MaxRunDuration })
            .ToDictionaryAsync(v => v.Id, v => (Startup: v.StartupTimeout, Idle: v.IdleTimeout, Duration: v.MaxRunDuration), cancellationToken);

        // the latest observation (the route upserts a single row, but history rows must not break it)
        var observation = await db.MonitorObservations.AsNoTracking()
            .OrderByDescending(o => o.UpdatedAt)
            .FirstOrDefaultAsync(cancellationToken);
        var (observationAge, activityByRun) = ParseObservation(observation, now);

        foreach (var run in candidates)
        {
            if (cancellationToken.IsCancellationRequested)
                return;
            var pinned = limitsByTask.GetValueOrDefault(versions.GetValueOrDefault(run.TaskId).Version);
            var startup = RunReconcileService.EffectiveLimit(pinned.Startup, limits.StartupTimeout);
            var idle = RunReconcileService.EffectiveLimit(pinned.Idle, limits.IdleTimeout);
            var duration = RunReconcileService.EffectiveLimit(pinned.Duration, limits.MaxRunDuration);

            // Host-Monitor activity evidence only counts while the observation is fresh; a stale
            // observation is treated as absent (the null branch below — no unevidenced idle kill).
            bool? activity = observationAge is { } age && age <= TelemetryFreshness
                ? activityByRun.GetValueOrDefault(run.Id)
                : null;
            var verdict = TimeoutEvaluator.Evaluate(
                run.Status, run.StartedAt, run.LastProgressAt, run.LastHeartbeatAt, now, startup, idle, duration, activity);

            // posture: investigate before stopping: `startup_timeout` and
            // `idle_timeout` failures adjudicate the ABSENCE of lifecycle facts — while the Run's Bridge
            // spool still holds undelivered entries (an outage / Runtime restart recovering via spool
            // redelivery), those facts may simply be in flight; skipping the tick lets the
            // redelivery resolve the Run before any kill. `max_run_duration` stays unguarded: the wall
            // clock is independent of delivery.
            if (verdict is TimeoutVerdict.StartupTimeout or TimeoutVerdict.IdleConfirmed)
            {
                var workflowId = versions.GetValueOrDefault(run.TaskId).Workflow;
                if (workflowId != Guid.Empty && storage.SpoolHasUndeliveredEntries(workflowId, run.Id))
                {
                    logger.LogInformation(
                        "run {runId} '{verdict}' suppressed: the callback spool still holds undelivered entries (spool redelivery in flight)",
                        run.Id, verdict);
                    continue;
                }
            }

            var ok = verdict switch
            {
                TimeoutVerdict.StartupTimeout => await FailAsync(db, provisioner, storage, run.Id,
                    RunStatuses.Failed, RunErrorCodes.StartupTimeout,
                    $"reconciled: the run never became ready within the effective startup_timeout ({startup})",
                    cancellationToken),
                TimeoutVerdict.RunDuration => await FailAsync(db, provisioner, storage, run.Id,
                    RunStatuses.TimedOut, null,
                    $"reconciled: the run exceeded the effective max_run_duration ({duration}); /retry is the only recovery",
                    cancellationToken),
                TimeoutVerdict.IdleConfirmed => await FailAsync(db, provisioner, storage, run.Id,
                    RunStatuses.Failed, RunErrorCodes.IdleTimeout,
                    $"reconciled: no observable activity within the effective idle_timeout ({idle}) and the host monitor confirmed no active processes/CPU/session activity",
                    cancellationToken),
                _ => true,
            };
            switch (verdict)
            {
                case TimeoutVerdict.None:
                    break;
                case TimeoutVerdict.IdleCandidate:
                    logger.LogInformation("run {runId} is an idle candidate (no progress within {idle}); awaiting host-monitor confirmation", run.Id, idle);
                    break;
                case TimeoutVerdict.IdleBusy:
                    logger.LogInformation("run {runId} silent for {idle} but the host monitor reports an active process tree — not killed", run.Id, idle);
                    break;
                case TimeoutVerdict.HeartbeatLost:
                    logger.LogWarning("run {runId} lost its heartbeat — left to the container-death reconciler", run.Id);
                    break;
                default:
                    if (ok)
                        logger.LogWarning("run {runId} reconciled: '{verdict}'", run.Id, verdict);
                    break;
            }
        }
    }

    private async Task<bool> FailAsync(
        AppDbContext db, IContainerProvisioner provisioner, RuntimeStorageService storage, Guid runId,
        string status, string? errorCode, string message, CancellationToken cancellationToken)
    {
        var run = await db.Runs.SingleAsync(r => r.Id == runId, cancellationToken);
        return await RunReconcileService.TerminalAsync(db, provisioner, storage, run, status, errorCode, message, cancellationToken);
    }

    /// <summary>Extracts (observation age, per-run activity) from the persisted latest observation.
    /// `activityByRun[runId] == true` means the monitor saw an active child process tree, notable CPU,
    /// or a session file touched after the idle window opened (the busy-run protection).</summary>
    public static (TimeSpan? Age, Dictionary<Guid, bool> ActivityByRun) ParseObservation(
        MonitorObservation? observation, DateTime now)
    {
        if (observation is null)
            return (null, []);
        var age = now - observation.ObservedAt;
        var activity = new Dictionary<Guid, bool>();

        if (observation.Containers is { } containers)
        {
            try
            {
                using var document = JsonDocument.Parse(containers);
                foreach (var container in document.RootElement.EnumerateArray())
                {
                    if (!Guid.TryParse(container.GetProperty("runId").GetString(), out var runId))
                        continue;
                    var busy = container.GetProperty("busy").GetBoolean();
                    activity[runId] = activity.GetValueOrDefault(runId) || busy;
                }
            }
            catch (JsonException)
            {
                // a corrupted observation never authorizes a kill; the next cycle replaces it
            }
        }
        return (age >= TimeSpan.Zero ? age : TimeSpan.Zero, activity);
    }
}
