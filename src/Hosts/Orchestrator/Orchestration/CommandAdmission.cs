using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PiAgent.Infrastructure.Persistence;
using PiAgent.Orchestrator.Commands;

namespace PiAgent.Orchestrator.Orchestration;

/// <summary>
/// The durable-queue retry policy pinned on a Task at creation: the profile
/// version's retry defaults over the system defaults. (`initial_backoff_seconds` is emitted too, so
/// reading back `Effective` output is idempotent.) The Repository-override level has no representation
/// in the configuration shape.
/// </summary>
public static class RetryPolicies
{
    public const int DefaultMaxAutomaticRetries = 1;
    public static readonly TimeSpan DefaultInitialBackoff = TimeSpan.FromSeconds(30);

    public static string Effective(string? profileRetryPolicyJson)
    {
        var retries = DefaultMaxAutomaticRetries;
        var backoff = DefaultInitialBackoff;
        if (!string.IsNullOrWhiteSpace(profileRetryPolicyJson))
        {
            try
            {
                using var document = JsonDocument.Parse(profileRetryPolicyJson);
                var root = document.RootElement;
                if (root.TryGetProperty("max_automatic_retries", out var max) && max.TryGetInt32(out var maxRetries) && maxRetries >= 0)
                    retries = maxRetries;
                if (root.TryGetProperty("initial_backoff", out var profileBackoff) && profileBackoff.ValueKind == JsonValueKind.String
                    && TimeSpan.TryParse(profileBackoff.GetString(), out var backoffValue))
                    backoff = backoffValue;
                else if (root.TryGetProperty("initial_backoff_seconds", out var backoffSeconds) && backoffSeconds.TryGetInt32(out var seconds))
                    backoff = TimeSpan.FromSeconds(seconds);
            }
            catch (JsonException)
            {
                // the version snapshot is config-plane validated; a data bug degrades to the system defaults
            }
        }
        return JsonSerializer.Serialize(new
        {
            max_automatic_retries = retries,
            initial_backoff_seconds = (int)backoff.TotalSeconds,
        });
    }

    /// <summary>Reads a pinned policy back (inverse of `Effective`, same defaults on missing/invalid).</summary>
    public static RetryPolicyValues Parse(string? json)
    {
        var values = new RetryPolicyValues(DefaultMaxAutomaticRetries, (int)DefaultInitialBackoff.TotalSeconds);
        if (string.IsNullOrWhiteSpace(json))
            return values;
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.TryGetProperty("max_automatic_retries", out var max) && max.TryGetInt32(out var retries) && retries >= 0)
                values = values with { MaxAutomaticRetries = retries };
            if (root.TryGetProperty("initial_backoff_seconds", out var backoffSeconds) && backoffSeconds.TryGetInt32(out var seconds) && seconds >= 0)
                values = values with { InitialBackoffSeconds = seconds };
            return values;
        }
        catch (JsonException)
        {
            return new RetryPolicyValues(DefaultMaxAutomaticRetries, (int)DefaultInitialBackoff.TotalSeconds);
        }
    }
}

/// <summary>Non-null = rejection (reason + short detail) for the processor's RejectAsync path; null =
/// admitted (Task+Job written) or the command stayed untouched (control commands, non-failed /retry).
/// `Silent` rejections are stored on the command row but never answered in the issue (the AI-scope
/// label gate — "we simply ignore it").</summary>
public sealed record AdmissionOutcome(string Reason, string Detail, bool Silent = false);

/// <summary>The pinned effective policy a consumer acts on (repository override not
/// representable in the configuration shape — profile level only).</summary>
public sealed record RetryPolicyValues(int MaxAutomaticRetries, int InitialBackoffSeconds);

/// <summary>
/// The admission stage: gates an authorized task-producing command, then
/// creates the Workflow get-or-create + Task + (for /code) TaskArtifactInput + OrchestrationJob, and
/// moves the command to `queued` — inside the caller's (CommentCommandProcessor's) transaction. The
/// invariant check runs under a `FOR UPDATE` lock on the Workflow row; the partial unique index
/// `ux_task_one_unfinished_per_workflow` is the backstop. Rejections happen before any
/// Task exists; a rejected first command never creates a Workflow.
/// </summary>
public static class CommandAdmission
{
    /// <summary>Commands that create Tasks; /retry, /status, /stop, /help are not
    /// admission inputs here. Public because /help reuses the same command→task-type map.</summary>
    public static readonly Dictionary<string, string> TaskTypeFor = new()
    {
        [AgentCommandNames.Plan] = OrchestrationTaskTypes.Plan,
        [AgentCommandNames.Code] = OrchestrationTaskTypes.Implement,
        [AgentCommandNames.Review] = OrchestrationTaskTypes.Review,
        [AgentCommandNames.TestCases] = OrchestrationTaskTypes.TestDesign,
    };

    /// <summary>`botLogin` anchors the no-new-input gate's bot-author filtering (the agent's own
    /// comments and reviews never count as new signal); null disables the filtering — test convenience
    /// only, the production path (CommentCommandProcessor) always passes it.</summary>
    public static async Task<AdmissionOutcome?> AdmitAsync(
        AppDbContext db, AgentCommand command, WorkItem workItem, Repository repository,
        string? botLogin, CancellationToken cancellationToken, ILogger? logger = null)
    {
        var name = command.CommandName;

        if (name == AgentCommandNames.Retry)
        {
            // /retry only touches a failed Workflow (only /status, /help and root closure remain available).
            var retryWorkflow = await db.Workflows.AsNoTracking()
                .SingleOrDefaultAsync(w => w.WorkItemId == workItem.Id, cancellationToken);
            return retryWorkflow is { Status: WorkflowStatuses.Failed }
                ? new AdmissionOutcome(AgentCommandRejectionReasons.WorkflowFailed,
                    "the workflow has failed unrecoverably")
                : null;
        }

        if (name is null || !TaskTypeFor.ContainsKey(name))
            return null; // /status, /help, /stop: consumed elsewhere — untouched here

        var now = DateTime.UtcNow;
        if (workItem.State != "open")
            return new AdmissionOutcome(AgentCommandRejectionReasons.WorkItemClosed,
                "the work item is closed");

        var taskType = TaskTypeFor[name];

        // The AI-scope gate: a task-producing command runs only on an item that carries the type's
        // exact label (AI:Plan/AI:Code/AI:Review/AI:Test); everything else is silently ignored —
        // the owner adds the label and the scope-label scanner starts the run when conditions converge.
        if (!AiScopeLabels.HasScope(workItem, taskType))
            return new AdmissionOutcome(AgentCommandRejectionReasons.LabelScopeMissing,
                $"'{AiScopeLabels.LabelForTaskType[taskType]}' is required on this item", Silent: true);

        // Routing/profile gates — the same resolution /help lists with.
        var (resolved, reason, detail) = await ProfileGate.ResolveAsync(db, repository, taskType, cancellationToken);
        if (resolved is null || reason is not null || detail is not null)
            return new AdmissionOutcome(reason!, detail!);
        var version = resolved.Version;

        Guid? workflowId = await db.Workflows
            .Where(w => w.WorkItemId == workItem.Id)
            .Select(w => (Guid?)w.Id)
            .SingleOrDefaultAsync(cancellationToken);

        Guid? planArtifactId = null;
        if (taskType == OrchestrationTaskTypes.Implement)
        {
            // /code approves the latest successful plan artifact — no implement-without-plan mode
            planArtifactId = await LatestPlanArtifactIdAsync(db, workflowId, cancellationToken);
            if (planArtifactId is null)
                return new AdmissionOutcome(AgentCommandRejectionReasons.PlanNotAvailable,
                    "no successful plan artifact to approve — run /plan first");
        }

        var workflow = await LockWorkflowAsync(db, workItem.Id, cancellationToken);
        if (workflow is null)
        {
            workflow = new Workflow
            {
                Id = Guid.NewGuid(),
                WorkItemId = workItem.Id,
                Status = WorkflowStatuses.WaitingForHuman, // first accepted command → task queued → active below
                CreatedAt = now,
                UpdatedAt = now,
            };
            db.Workflows.Add(workflow);
        }
        else if (workflow.Status == WorkflowStatuses.Failed)
            return new AdmissionOutcome(AgentCommandRejectionReasons.WorkflowFailed,
                "the workflow has failed unrecoverably");
        else if (workflow.Status == WorkflowStatuses.Closed)
            return new AdmissionOutcome(AgentCommandRejectionReasons.WorkItemClosed,
                "the workflow is closed (root WorkItem closure)");
        else if (await db.OrchestrationTasks.AnyAsync(
                t => t.WorkflowId == workflow.Id && OrchestrationTaskStatuses.Unfinished.Contains(t.Status), cancellationToken))
            return new AdmissionOutcome(AgentCommandRejectionReasons.UnfinishedTaskExists,
                "the workflow already holds an unfinished Task");
        else if (await IsNoNewInputRepeatAsync(db, workflow, taskType, workItem, botLogin, cancellationToken))
            return new AdmissionOutcome(AgentCommandRejectionReasons.NoNewInput,
                "the previous same command succeeded and nothing new happened since it — comment what to change, then post again");

        var maxSequence = await db.OrchestrationTasks
            .Where(t => t.WorkflowId == workflow.Id)
            .MaxAsync(t => (int?)t.Sequence, cancellationToken) ?? 0;

        var task = new OrchestrationTask
        {
            Id = Guid.NewGuid(),
            WorkflowId = workflow.Id,
            Sequence = maxSequence + 1,
            Type = taskType,
            AgentProfileVersionId = version.Id,
            Status = OrchestrationTaskStatuses.Queued,
            TriggerCommandId = command.Id,
            // the trigger comment's GitHub time anchors the no-new-input gate on this Task
            TriggerAtGithub = await db.GitHubComments.AsNoTracking()
                .Where(c => c.GithubCommentId == command.GithubCommentId && c.Kind == GitHubCommentKinds.IssueComment)
                .Select(c => (DateTime?)c.CreatedAtGithub)
                .SingleOrDefaultAsync(cancellationToken) ?? DateTime.UtcNow,
            TargetHeadSha = taskType == OrchestrationTaskTypes.Review ? workItem.HeadSha : null,
            TargetBaseSha = taskType == OrchestrationTaskTypes.Review ? workItem.BaseSha : null,
            CreatedAt = now,
            RetryPolicy = RetryPolicies.Effective(version.RetryPolicy),
        };
        db.OrchestrationTasks.Add(task);

        if (planArtifactId is { } artifactId)
        {
            db.TaskArtifactInputs.Add(new TaskArtifactInput
            {
                Id = Guid.NewGuid(),
                TaskId = task.Id,
                ArtifactId = artifactId,
                Purpose = ArtifactInputPurposes.ApprovedPlan,
                CreatedAt = now,
            });
        }

        var orchestrationJob = new OrchestrationJob
        {
            Id = Guid.NewGuid(),
            TaskId = task.Id,
            JobType = OrchestrationJobTypes.RunTask,
            Status = OrchestrationJobStatuses.Pending,
            Priority = 0,
            Attempts = 0,
            AvailableAt = now,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.OrchestrationJobs.Add(orchestrationJob);

        // the Task occupies the Workflow: waiting_for_human → active
        workflow.Status = WorkflowStatuses.Active;
        workflow.CurrentTaskId = task.Id;
        workflow.StartedAt ??= now;
        workflow.UpdatedAt = now;

        command.Status = AgentCommandStatuses.Queued;
        command.ProcessedAt = now;
        logger?.LogInformation(
            "command {commandId} admitted: /{command} on work item {workItemId} → task {taskId} (job {jobId}, workflow {workflowId}, profile version {versionId})",
            command.Id, name, workItem.Id, task.Id, orchestrationJob.Id, workflow.Id, version.Id);
        return null;
    }

    /// <summary>
    /// The no-new-input gate: the same task-producing command repeated after
    /// its previous same-type Task succeeded, with no new human signal since that Task's trigger, is a
    /// duplicate — rejected `no_new_input` instead of burning another Run. Human signals: a non-bot
    /// comment (conversation or inline), a non-bot submitted review, a work item title/body edit, or a
    /// new PR commit not pushed while this Workflow's own Runs were executing (those pushes are the
    /// agent's own work, not user input). All lookups are fail-open: detection gaps can only allow a
    /// repeat, never deny one; a pre-watermark previous Task (TriggerAtGithub null) never rejects.
    /// </summary>
    private static async Task<bool> IsNoNewInputRepeatAsync(
        AppDbContext db, Workflow workflow, string taskType, WorkItem workItem,
        string? botLogin, CancellationToken cancellationToken)
    {
        var previous = await db.OrchestrationTasks.AsNoTracking()
            .Where(t => t.WorkflowId == workflow.Id && t.Type == taskType && t.Status == OrchestrationTaskStatuses.Succeeded)
            .OrderByDescending(t => t.Sequence)
            .Select(t => new { t.Id, t.TriggerAtGithub })
            .FirstOrDefaultAsync(cancellationToken);
        if (previous?.TriggerAtGithub is not { } anchor)
            return false;

        if (await db.GitHubComments.AnyAsync(
                c => c.WorkItemId == workItem.Id && !c.IsDeleted && c.CreatedAtGithub > anchor
                    && (botLogin == null || c.AuthorLogin != botLogin), cancellationToken))
            return false; // a human spoke after the previous command

        if (await db.GitHubReviews.AnyAsync(
                r => r.WorkItemId == workItem.Id && r.SubmittedAtGithub > anchor
                    && (botLogin == null || r.AuthorLogin != botLogin), cancellationToken))
            return false; // a human-submitted review after the previous command

        // title/body edits (content-detected by the poller's snapshot diff; labels/assignees stay noise)
        var editPayloads = await db.GitHubEvents
            .Where(e => e.WorkItemId == workItem.Id
                && e.EventType == GitHubEventTypes.WorkItemUpdated
                && (e.OccurredAtGithub ?? e.ReceivedAt) > anchor)
            .Select(e => e.Payload)
            .ToListAsync(cancellationToken);
        if (editPayloads.Any(IsHumanEditPayload))
            return false;

        if (workItem.Type != WorkItemTypes.PullRequest)
            return true; // commit signals cannot apply to an Issue: no human signal was found above

        // new commits outside this Workflow's own run windows (in-window pushes are the agent's work);
        // the rare human push made mid-run is counted as the agent's — the user recovers by commenting
        var windows = await (from t in db.OrchestrationTasks
                       where t.WorkflowId == workflow.Id
                       join r in db.Runs on t.Id equals r.TaskId
                       select new { r.StartedAt, r.CompletedAt }).ToListAsync(cancellationToken);
        var pushes = await db.GitHubEvents
            .Where(e => e.WorkItemId == workItem.Id && e.EventType == GitHubEventTypes.PullRequestSynchronized)
            .Select(e => new { e.OccurredAtGithub, e.ReceivedAt })
            .ToListAsync(cancellationToken);
        return !pushes.Any(p => (p.OccurredAtGithub ?? p.ReceivedAt) > anchor
            && !windows.Any(w => p.ReceivedAt >= w.StartedAt
                && (w.CompletedAt is not { } completed || p.ReceivedAt <= completed)));
    }

    /// <summary>`work_item_updated` payload ⇒ a human title/body edit (camelCase, Web defaults).</summary>
    private static bool IsHumanEditPayload(string payload)
    {
        try
        {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;
            if (root.TryGetProperty("body_changed", out var bodyChanged) && bodyChanged.ValueKind == JsonValueKind.True)
                return true;
            return root.TryGetProperty("changed", out var changed)
                && changed.ValueKind == JsonValueKind.Object
                && changed.TryGetProperty("title", out _);
        }
        catch (JsonException)
        {
            return false; // unreadable payload: fail open
        }
    }

    /// <summary>`SELECT … FOR UPDATE` on the Workflow row serializes same-Workflow admissions;
    /// returns the tracked row or null when none exists.</summary>
    private static async Task<Workflow?> LockWorkflowAsync(AppDbContext db, Guid workItemId, CancellationToken cancellationToken)
    {
        var locked = await db.Workflows
            .FromSqlInterpolated($"SELECT * FROM workflow WHERE work_item_id = {workItemId} FOR UPDATE")
            .ToListAsync(cancellationToken);
        return locked.Count > 0 ? locked[0] : null;
    }

    /// <summary>The Artifact of the latest successful plan Task — shared by admission
    /// and the /help availability filter; null when no eligible artifact exists.</summary>
    public static async Task<Guid?> LatestPlanArtifactIdAsync(
        AppDbContext db, Guid? workflowId, CancellationToken cancellationToken)
    {
        var latestPlanTaskId = await db.OrchestrationTasks.AsNoTracking()
            .Where(t => t.WorkflowId == workflowId && t.Type == OrchestrationTaskTypes.Plan && t.Status == OrchestrationTaskStatuses.Succeeded)
            .OrderByDescending(t => t.Sequence)
            .Select(t => (Guid?)t.Id)
            .FirstOrDefaultAsync(cancellationToken);
        return latestPlanTaskId is null
            ? null
            : await db.Artifacts
                .Where(a => a.TaskId == latestPlanTaskId && a.Type == ArtifactTypes.Plan)
                .Select(a => (Guid?)a.Id)
                .FirstOrDefaultAsync(cancellationToken);
    }
}
