using System.Text;
using Microsoft.EntityFrameworkCore;
using PiAgent.Infrastructure.Persistence;
using PiAgent.Orchestrator.Orchestration;

namespace PiAgent.Orchestrator.Commands;

/// <summary>Short display text for every V1 command; rendered as `/{name.Replace('_', '-')}`.</summary>
public static class CommandDescriptions
{
    public static readonly Dictionary<string, string> For = new()
    {
        [AgentCommandNames.Plan] = "analyze the issue and prepare an implementation plan",
        [AgentCommandNames.Code] = "implement the approved plan; commit and push a branch",
        [AgentCommandNames.Review] = "review the pull request and post findings",
        [AgentCommandNames.TestCases] = "design test scenarios and cases for the issue",
        [AgentCommandNames.Status] = "report the current orchestration state for this work item",
        [AgentCommandNames.Stop] = "cancel the active task and any queued tasks for this work item",
        [AgentCommandNames.Help] = "show this list of commands",
        [AgentCommandNames.Retry] = "restart the last failed task with a new attempt",
    };

    public static string Token(string name) => "/" + name.Replace('_', '-');
}

/// <summary>
/// Control-command replies (06 §7, §9, decision 29): `/status` renders the draft status format from
/// current rows only (Workflow / current Task incl. `Run attempt: #N` per decision 79, execution
/// heartbeat, queue position vs system-wide pending jobs, last completed Task, last error/reason);
/// `/help` lists commands that would be accepted right now (target type, permitted commands, pause,
/// routing/profile gate, Workflow state all filtered — owner ruling: omitted when the image is not
/// `ready`, and task-producing commands omitted while the repository is paused). No section, Workflow,
/// Task, Run or Container row is written here; the reply itself is one Outbox enqueue.
/// </summary>
public static class ControlReplies
{
    public static async Task<string> StatusReplyAsync(
        AppDbContext db, WorkItem workItem, CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var workflow = await db.Workflows.AsNoTracking()
            .SingleOrDefaultAsync(w => w.WorkItemId == workItem.Id, cancellationToken);
        if (workflow is null)
            return "No agent workflow started.";

        var sb = new StringBuilder();
        sb.AppendLine("Workflow Status");
        sb.AppendLine($"WorkItem: {(workItem.Type == WorkItemTypes.PullRequest ? "Pull Request" : "Issue")} #{workItem.Number}");
        sb.Append($"Workflow: {workflow.Status}");
        if (workflow.Status == WorkflowStatuses.Failed && workflow.FailureReason is { } failure)
            sb.Append($" ({failure})");
        sb.AppendLine();

        var task = workflow.CurrentTaskId is { } currentId
            ? await db.OrchestrationTasks.AsNoTracking().SingleOrDefaultAsync(t => t.Id == currentId, cancellationToken)
            : null;
        task ??= await db.OrchestrationTasks.AsNoTracking()
            .Where(t => t.WorkflowId == workflow.Id)
            .OrderByDescending(t => t.Sequence)
            .FirstOrDefaultAsync(cancellationToken);

        if (OrchestrationTaskStatuses.Unfinished.Contains(task?.Status ?? ""))
        {
            sb.AppendLine();
            sb.AppendLine("Current Task:");
            var unfinished = task!;
            var profileName = await db.AgentProfileVersions.AsNoTracking()
                .Where(v => v.Id == unfinished.AgentProfileVersionId)
                .Join(db.AgentProfiles, v => v.AgentProfileId, p => p.Id, (v, p) => p.Name)
                .SingleOrDefaultAsync(cancellationToken);
            var run = await db.Runs.AsNoTracking()
                .Where(r => r.TaskId == unfinished.Id)
                .OrderByDescending(r => r.AttemptNumber)
                .FirstOrDefaultAsync(cancellationToken);

            sb.Append($"- Type: {unfinished.Type} | Status: {unfinished.Status} | Agent: {profileName}");
            if (run is not null)
                sb.Append($" | Run attempt: #{run.AttemptNumber}");
            sb.AppendLine();

            var started = unfinished.StartedAt ?? unfinished.ClaimedAt;
            if (started is { } at)
                sb.AppendLine($"- Started: {Clock(at)} UTC | Duration: {Duration(now - at)}");
            var latest = run;
            if (latest is not null)
            {
                if (latest.ErrorCode is { } code)
                    sb.AppendLine($"- Error: {code} — {latest.ErrorMessage ?? "no message"}");
                else if (latest.ResultSummary is { } summary)
                    sb.AppendLine($"- Result: {summary}");
            }
            if (unfinished.FailureReason is { } reason && run?.ErrorCode is null)
                sb.AppendLine($"- Reason: {reason}");

            var execution = await ExecutionLineAsync(db, latest, now, cancellationToken);
            if (execution is { } line)
            {
                sb.AppendLine();
                sb.AppendLine("Execution:");
                sb.AppendLine(line);
            }
        }

        if (task is not null && !OrchestrationTaskStatuses.Unfinished.Contains(task.Status))
        {
            sb.AppendLine();
            sb.AppendLine("Last completed Task:");
            sb.AppendLine($"- {task.Type} — {task.Status}" + (task.FailureReason is { } failureReason ? $" ({failureReason})" : ""));
        }
        else if (task is { } unfinishedTask)
        {
            var finished = await db.OrchestrationTasks.AsNoTracking()
                .Where(t => t.WorkflowId == workflow.Id
                    && !OrchestrationTaskStatuses.Unfinished.Contains(t.Status)
                    && t.Id != unfinishedTask.Id)
                .OrderByDescending(t => t.Sequence)
                .FirstOrDefaultAsync(cancellationToken);
            if (finished is not null)
            {
                sb.AppendLine();
                sb.AppendLine("Last completed Task:");
                sb.AppendLine($"- {finished.Type} — {finished.Status}" + (finished.FailureReason is { } finishedFailure ? $" ({finishedFailure})" : ""));
            }
        }

        sb.AppendLine();
        sb.AppendLine("Queue:");
        sb.AppendLine($"- Position: {await QueuePositionAsync(db, task, cancellationToken)} | Pending jobs: {await db.OrchestrationJobs.CountAsync(j => j.Status == OrchestrationJobStatuses.Pending, cancellationToken)}");

        return sb.ToString().TrimEnd();
    }

    /// <summary>`- Phase: tool_started | Container: running | Last heartbeat: 20s ago | …`; omitted when
    /// nothing observed yet. The Phase mirrors the Run's LATEST persisted RunEvent TYPE (task 12 owner
    /// ruling: no extra enum — the event type vocabulary itself, 02 §18) and deliberately skips
    /// `heartbeat` rows: heartbeat proves liveness and never advances progress or the phase (decision 58).</summary>
    private static async Task<string?> ExecutionLineAsync(
        AppDbContext db, Run? run, DateTime now, CancellationToken cancellationToken)
    {
        if (run is null)
            return null;
        var container = await db.ContainerInstances.AsNoTracking()
            .SingleOrDefaultAsync(c => c.RunId == run.Id, cancellationToken);

        var pieces = new List<string>();
        var latestEventType = await db.RunEvents.AsNoTracking()
            .Where(e => e.RunId == run.Id && e.Type != RunEventTypes.Heartbeat)
            .OrderByDescending(e => e.ReceivedAt)
            .Select(e => e.Type)
            .FirstOrDefaultAsync(cancellationToken);
        if (latestEventType is not null)
            pieces.Add($"Phase: {latestEventType}");
        if (container is { ObservedState: not ContainerObservedStates.Unknown })
            pieces.Add($"Container: {container.ObservedState}");
        if (run.LastHeartbeatAt is { } heartbeat)
            pieces.Add($"Last heartbeat: {Relative(now - heartbeat)} ago");
        if (run.LastProgressAt is { } progress)
            pieces.Add($"Last progress: {Relative(now - progress)} ago");
        return pieces.Count == 0 ? null : "- " + string.Join(" | ", pieces);
    }

    /// <summary>Pending jobs ahead of this Workflow's pending job in the FIFO (claim order =
    /// available_at, then id); 0 when nothing is waiting in the queue.</summary>
    private static async Task<int> QueuePositionAsync(AppDbContext db, OrchestrationTask? task, CancellationToken cancellationToken)
    {
        if (task is null)
            return 0;
        var job = await db.OrchestrationJobs.AsNoTracking()
            .Where(j => j.TaskId == task.Id)
            .OrderByDescending(j => j.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);
        if (job is null || job.Status != OrchestrationJobStatuses.Pending)
            return 0;
        return await db.Database.SqlQuery<int>($"""
            SELECT COUNT(*)::int AS "Value" FROM orchestration_job j
            WHERE j.status = {OrchestrationJobStatuses.Pending}
              AND (j.available_at, j.id) < ({job.AvailableAt}, {job.Id})
            """).SingleAsync(cancellationToken);
    }

    private static string Clock(DateTime utc) => utc.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture);

    private static string Duration(TimeSpan span) => span.TotalHours >= 1
        ? $"{(int)span.TotalHours}h {span.Minutes}m"
        : $"{(int)span.TotalMinutes}m";

    private static string Relative(TimeSpan span) => span.TotalHours >= 1
        ? $"{(int)span.TotalHours}h"
        : span.TotalMinutes >= 1
            ? $"{(int)span.TotalMinutes}m"
            : $"{(int)span.TotalSeconds}s";
}

/// <summary>
/// The /help availability filter (06 §9 plus owner rulings): a command is listed only when the
/// pipeline would accept it right now — target type, repository permitted set, not paused (task
/// producing commands), the routing/profile gate (missing/disabled/or version not `ready` omitted),
/// and the Workflow state (closed WorkItem, failed/closed Workflow, unfinished Task, missing plan
/// Artifact for /code, no last-failed Task for /retry). /status, /help and /stop need no Workflow.
/// </summary>
public static class HelpAvailability
{
    public static async Task<string> HelpCommentAsync(
        AppDbContext db, WorkItem workItem, Repository repository, CancellationToken cancellationToken)
    {
        var available = new List<string>();
        var config = RepoCommandConfigParser.Parse(repository);
        var permitted = config.PermittedCommands.ToHashSet(StringComparer.Ordinal);
        var validTargets = CommandIngestion.ValidTargetTypes;

        var workflow = await db.Workflows.AsNoTracking()
            .SingleOrDefaultAsync(w => w.WorkItemId == workItem.Id, cancellationToken);
        var unfinished = workflow is not null && await db.OrchestrationTasks.AnyAsync(
            t => t.WorkflowId == workflow.Id && OrchestrationTaskStatuses.Unfinished.Contains(t.Status), cancellationToken);
        var blocked = unfinished
            || workflow is { Status: WorkflowStatuses.Failed }
            || workflow is { Status: WorkflowStatuses.Closed }
            || workItem.State != "open";

        foreach (var name in AgentCommandNames.All)
        {
            if (!permitted.Contains(name) || !validTargets[name].Contains(workItem.Type))
                continue;
            if (name == AgentCommandNames.Retry)
            {
                if (repository.IsEnabled &&
                    await RetryAvailableAsync(db, workflow, workItem, repository, cancellationToken))
                    available.Add(name); // owner ruling: omitted while paused (parse path rejects /retry)
                continue;
            }
            if (CommandAdmission.TaskTypeFor.ContainsKey(name))
            {
                if (blocked || !repository.IsEnabled)
                    continue; // owner ruling: task-producing commands hidden while paused or state-blocked
                var (resolved, _, _) = await ProfileGate.ResolveAsync(
                    db, repository, CommandAdmission.TaskTypeFor[name], cancellationToken);
                if (resolved is null)
                    continue; // phase-/profile-gated (ruling: not-ready versions omitted too)
                if (name == AgentCommandNames.Code &&
                    await CommandAdmission.LatestPlanArtifactIdAsync(db, workflow?.Id, cancellationToken) is null)
                    continue; // decision 80 — also covers a missing Workflow (no plan without one)
                available.Add(name);
                continue;
            }
            available.Add(name); // status / stop / help
        }

        var sb = new StringBuilder();
        sb.AppendLine($"Available commands for this {(workItem.Type == WorkItemTypes.PullRequest ? "pull request" : "issue")}:");
        sb.AppendLine();
        foreach (var name in available)
            sb.AppendLine($"- `{CommandDescriptions.Token(name)}` — {CommandDescriptions.For[name]}.");
        return sb.ToString().TrimEnd();
    }

    /// <summary>/retry (06 §9a): the Workflow's most recent non-cancelled task-producing Task is
    /// `failed`, nothing unfinished, and the Task's pinned profile/version is still usable; original
    /// preconditions still hold (for implement, the plan Artifact still exists — decision 80).</summary>
    private static async Task<bool> RetryAvailableAsync(
        AppDbContext db, Workflow? workflow, WorkItem workItem, Repository repository, CancellationToken cancellationToken)
    {
        if (workflow is null || workItem.State != "open")
            return false;
        if (workflow.Status is WorkflowStatuses.Failed or WorkflowStatuses.Closed)
            return false; // decision 71: only /status, /help and root closure remain available
        var unfinished = await db.OrchestrationTasks.AnyAsync(
            t => t.WorkflowId == workflow.Id && OrchestrationTaskStatuses.Unfinished.Contains(t.Status), cancellationToken);
        if (unfinished)
            return false;
        var last = await db.OrchestrationTasks.AsNoTracking()
            .Where(t => t.WorkflowId == workflow.Id && t.Status != OrchestrationTaskStatuses.Cancelled)
            .OrderByDescending(t => t.Sequence)
            .FirstOrDefaultAsync(cancellationToken);
        if (last is null || last.Status != OrchestrationTaskStatuses.Failed)
            return false;
        var version = await db.AgentProfileVersions.AsNoTracking()
            .SingleAsync(v => v.Id == last.AgentProfileVersionId, cancellationToken);
        var profile = await db.AgentProfiles.AsNoTracking()
            .SingleAsync(p => p.Id == version.AgentProfileId, cancellationToken);
        if (!profile.IsEnabled || version.State != ProfileVersionStates.Ready)
            return false; // agent_profile_not_ready on the retry path (06 §9a)
        if (last.Type == OrchestrationTaskTypes.Implement &&
            await CommandAdmission.LatestPlanArtifactIdAsync(db, workflow.Id, cancellationToken) is null)
            return false;
        return true;
    }
}

/// <summary>
/// Executes an authorized control command inside the caller's (processor's) transaction: renders the
/// reply from current rows and enqueues exactly one Outbox comment (decision 29), then marks the
/// command `processed`. Creates no Workflow, Task, Run, Session or Container row — /stop and /retry
/// are handled by tickets 14/15 and stay untouched here.
/// </summary>
public static class ControlCommandHandler
{
    public static async Task HandleAsync(
        AppDbContext db, AgentCommand command, WorkItem workItem, Repository repository,
        CancellationToken cancellationToken)
    {
        var text = command.CommandName == AgentCommandNames.Status
            ? await ControlReplies.StatusReplyAsync(db, workItem, cancellationToken)
            : await HelpAvailability.HelpCommentAsync(db, workItem, repository, cancellationToken);

        await new OutboxActionStore(db).EnqueueAsync(
            new OutboxEnqueueRequest(repository.Id, workItem.Id, null, workItem.Number, text, Guid.NewGuid()),
            cancellationToken);
        command.Status = AgentCommandStatuses.Processed;
        command.ProcessedAt = DateTime.UtcNow;
    }
}
