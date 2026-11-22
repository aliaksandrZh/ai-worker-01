using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PiAgent.Infrastructure.Persistence;
using PiAgent.Orchestrator.Commands;

namespace PiAgent.Orchestrator.Orchestration;

/// <summary>
/// The durable-queue retry policy pinned on a Task at creation (02 §10, decision 28): the profile
/// version's retry defaults over the system defaults. (`initial_backoff_seconds` is emitted too, so
/// reading back `Effective` output is idempotent.) The Repository-override level has no representation
/// in the V1 configuration shape and arrives with ticket 15 if needed (design doc).
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
/// admitted (Task+Job written) or the command stayed untouched (control commands, non-failed /retry).</summary>
public sealed record AdmissionOutcome(string Reason, string Detail);

/// <summary>The pinned effective policy a consumer acts on (decision 28: repository override not
/// representable in the V1 configuration shape — profile level only).</summary>
public sealed record RetryPolicyValues(int MaxAutomaticRetries, int InitialBackoffSeconds);

/// <summary>
/// The admission stage (04 §2, 06 §12, task 07): gates an authorized task-producing command, then
/// creates the Workflow get-or-create + Task + (for /code) TaskArtifactInput + OrchestrationJob, and
/// moves the command to `queued` — inside the caller's (CommentCommandProcessor's) transaction. The
/// invariant check runs under a `FOR UPDATE` lock on the Workflow row; the partial unique index
/// `ux_task_one_unfinished_per_workflow` is the backstop (decision 79). Rejections happen before any
/// Task exists (decisions 35/80); a rejected first command never creates a Workflow.
/// </summary>
public static class CommandAdmission
{
    /// <summary>Commands that create Tasks (06 §2 table); /retry, /status, /stop, /help are not
    /// admission inputs here. Public because /help (task 08) reuses the same command→task-type map.</summary>
    public static readonly Dictionary<string, string> TaskTypeFor = new()
    {
        [AgentCommandNames.Plan] = OrchestrationTaskTypes.Plan,
        [AgentCommandNames.Code] = OrchestrationTaskTypes.Implement,
        [AgentCommandNames.Review] = OrchestrationTaskTypes.Review,
        [AgentCommandNames.TestCases] = OrchestrationTaskTypes.TestDesign,
    };

    public static async Task<AdmissionOutcome?> AdmitAsync(
        AppDbContext db, AgentCommand command, WorkItem workItem, Repository repository, CancellationToken cancellationToken)
    {
        var name = command.CommandName;

        if (name == AgentCommandNames.Retry)
        {
            // /retry flows through ticket 15; until then it is untouched except on a failed Workflow
            // (decision 71: only /status, /help and root closure remain available).
            var retryWorkflow = await db.Workflows.AsNoTracking()
                .SingleOrDefaultAsync(w => w.WorkItemId == workItem.Id, cancellationToken);
            return retryWorkflow is { Status: WorkflowStatuses.Failed }
                ? new AdmissionOutcome(AgentCommandRejectionReasons.WorkflowFailed,
                    "the workflow has failed unrecoverably (decision 71)")
                : null;
        }

        if (name is null || !TaskTypeFor.ContainsKey(name))
            return null; // /status, /help, /stop: consumed by tickets 08/14 — untouched here

        var now = DateTime.UtcNow;
        if (workItem.State != "open")
            return new AdmissionOutcome(AgentCommandRejectionReasons.WorkItemClosed,
                "the work item is closed");

        var taskType = TaskTypeFor[name];

        // Routing/profile gates (02 §12a, decisions 35/36) — the same resolution /help lists with.
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
            // decision 80: /code approves the latest successful plan artifact — no implement-without-plan mode
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
                Status = WorkflowStatuses.WaitingForHuman, // 03 §1: first accepted command → task queued → active below
                CreatedAt = now,
                UpdatedAt = now,
            };
            db.Workflows.Add(workflow);
        }
        else if (workflow.Status == WorkflowStatuses.Failed)
            return new AdmissionOutcome(AgentCommandRejectionReasons.WorkflowFailed,
                "the workflow has failed unrecoverably (decision 71)");
        else if (workflow.Status == WorkflowStatuses.Closed)
            return new AdmissionOutcome(AgentCommandRejectionReasons.WorkItemClosed,
                "the workflow is closed (root WorkItem closure)");
        else if (await db.OrchestrationTasks.AnyAsync(
                t => t.WorkflowId == workflow.Id && OrchestrationTaskStatuses.Unfinished.Contains(t.Status), cancellationToken))
            return new AdmissionOutcome(AgentCommandRejectionReasons.UnfinishedTaskExists,
                "the workflow already holds an unfinished Task (04 §2)");

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

        db.OrchestrationJobs.Add(new OrchestrationJob
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
        });

        // the Task occupies the Workflow: waiting_for_human → active (03 §1)
        workflow.Status = WorkflowStatuses.Active;
        workflow.CurrentTaskId = task.Id;
        workflow.StartedAt ??= now;
        workflow.UpdatedAt = now;

        command.Status = AgentCommandStatuses.Queued;
        command.ProcessedAt = now;
        return null;
    }

    /// <summary>`SELECT … FOR UPDATE` on the Workflow row serializes same-Workflow admissions
    /// (decision 79); returns the tracked row or null when none exists.</summary>
    private static async Task<Workflow?> LockWorkflowAsync(AppDbContext db, Guid workItemId, CancellationToken cancellationToken)
    {
        var locked = await db.Workflows
            .FromSqlInterpolated($"SELECT * FROM workflow WHERE work_item_id = {workItemId} FOR UPDATE")
            .ToListAsync(cancellationToken);
        return locked.Count > 0 ? locked[0] : null;
    }

    /// <summary>The Artifact of the latest successful plan Task (decision 80) — shared by admission
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
