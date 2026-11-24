using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PiAgent.Infrastructure.Persistence;
using PiAgent.Orchestrator.Config;
using PiAgent.Platform.Workers;

namespace PiAgent.Orchestrator.Orchestration;

/// <summary>
/// Consumes the Poller's `project_item_*` events. Two behaviors:
/// (1) observational logging of every event (the "sync + logs" scope — the Poller may emit content
/// that maps to no tracked work item, and events without a trigger still settle `processed`);
/// (2) board triggers (projects.yaml `auto_plan` / `auto_code`): a `project_item_updated` whose
/// status field moved `from → to` on a configured board admits a system-owned Task of the trigger's
/// task type (plan for auto_plan, implement for auto_code) on the item's work item — the board move
/// IS the human signal, no comment needed. Same shape as the automatic initial
/// review (ReviewTriggerCycle): get-or-create Workflow, one Task per triggering event
/// (`TriggerGithubEventId` dedupes crash replays), durable OrchestrationJob, Workflow → active,
/// profile pinned via ProfileGate. The scope label (AI:Plan / AI:Code) must be on the item, and the
/// implement trigger additionally requires a successful plan artifact (the coder implements an
/// approved plan). Shared invariants over the comment path: no second unfinished
/// Task per Workflow and closed Workflows/work items never admit. Deferral: while the Workflow
/// holds an unfinished Task the event is released back to `pending` and re-tried after it settles;
/// event silently (logged). Single instance: claim +
/// orphan recovery rely on the Orchestrator being one process. Cold-start gate: nothing is claimed
/// until configuration synchronization completes (the triggers travel with it).
/// </summary>
public sealed class ProjectEventLogCycle(
    IServiceScopeFactory scopeFactory,
    ConfigurationState configurationState,
    ILogger<ProjectEventLogCycle> logger) : IWorkerCycle
{
    private const int BatchSize = 32;

    private static readonly string[] Handled =
    [
        GitHubEventTypes.ProjectItemAdded,
        GitHubEventTypes.ProjectItemUpdated,
        GitHubEventTypes.ProjectItemRemoved,
    ];

    public async Task RunCycleAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // Crash recovery: a `processing` event cannot survive a tick (single instance), so leftovers
        // are released before the claim below (saved first, same discipline as the comment processor).
        await db.GitHubEvents
            .Where(e => Handled.Contains(e.EventType) && e.ProcessingStatus == GitHubEventStatuses.Processing)
            .ExecuteUpdateAsync(u => u.SetProperty(e => e.ProcessingStatus, GitHubEventStatuses.Pending),
                cancellationToken);

        if (!configurationState.Synchronized)
            return; // cold-start gate: triggers unknown — claim nothing, settle nothing

        var events = await db.GitHubEvents
            .Where(e => Handled.Contains(e.EventType) && e.ProcessingStatus == GitHubEventStatuses.Pending)
            .OrderBy(e => e.ReceivedAt).ThenBy(e => e.Id)
            .Take(BatchSize)
            .ToListAsync(cancellationToken);

        foreach (var githubEvent in events)
        {
            githubEvent.ProcessingStatus = GitHubEventStatuses.Processing;
            await db.SaveChangesAsync(cancellationToken);
            try
            {
                var deferred = await ProcessAsync(db, githubEvent, cancellationToken);
                if (deferred)
                {
                    githubEvent.ProcessingStatus = GitHubEventStatuses.Pending;
                    githubEvent.ProcessingError = "board trigger deferred: the workflow holds an unfinished task";
                }
                else
                {
                    githubEvent.ProcessingStatus = GitHubEventStatuses.Processed;
                    githubEvent.ProcessedAt = DateTime.UtcNow;
                    githubEvent.ProcessingError = null;
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogError(exception, "project event {eventId} failed — releasing for retry", githubEvent.Id);
                githubEvent.ProcessingStatus = GitHubEventStatuses.Pending;
                githubEvent.ProcessingError = exception.ToString()[..Math.Min(exception.ToString().Length, 2000)];
            }
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    /// <summary>Logs the event, then applies the board trigger when it matches. Returns `true` when
    /// the event must be retried later (trigger deferred — the Workflow is busy).</summary>
    private async Task<bool> ProcessAsync(AppDbContext db, GitHubEvent githubEvent,
        CancellationToken cancellationToken)
    {
        logger.LogInformation("processing {eventType} ({source}, dedupe {dedupe}): {payload}",
            githubEvent.EventType, githubEvent.Source, githubEvent.DedupeKey, githubEvent.Payload);

        if (githubEvent.EventType != GitHubEventTypes.ProjectItemUpdated)
            return false; // added/removed stay observational
        if (githubEvent.WorkItemId is not { } workItemId)
            return false; // unmapped content or no tracked work item yet: nothing to trigger on
        var (board, itemId, changes) = ReadPayload(githubEvent.Payload);
        if (board is null || changes is null)
            return false; // unreadable payload: logging-only, never retries on a data bug

        foreach (var trigger in configurationState.ProjectBoardTriggers)
        {
            if (!string.Equals(trigger.Board, board, StringComparison.Ordinal)
                || !changes.TryGetValue(trigger.StatusField, out var change))
                continue; // board or field not configured for this trigger

            // The Poller formats field changes as "old -> new" (ProjectItemsSync.FieldChanges).
            var separator = change.IndexOf(" -> ", StringComparison.Ordinal);
            if (separator < 0)
                continue;
            var from = change[..separator].Trim();
            var to = change[(separator + 4)..].Trim();
            if (!string.Equals(from, trigger.From, StringComparison.Ordinal)
                || !string.Equals(to, trigger.To, StringComparison.Ordinal))
                continue; // not this trigger's transition

            return await TriggerTaskAsync(db, githubEvent, workItemId, itemId, board, trigger, from, to, cancellationToken);
        }
        return false; // no configured trigger matches this event's transition
    }

    private async Task<bool> TriggerTaskAsync(
        AppDbContext db, GitHubEvent githubEvent, Guid workItemId, long? itemId, string board,
        ProjectBoardTrigger trigger, string from, string to, CancellationToken cancellationToken)
    {
        var taskType = trigger.TaskType;
        var workItem = await db.WorkItems.AsNoTracking()
            .SingleOrDefaultAsync(w => w.Id == workItemId, cancellationToken);
        if (workItem is null)
        {
            logger.LogInformation("{taskType} trigger: event {eventId} consumed — work item row missing (referential integrity bug)",
                taskType, githubEvent.Id);
            return false;
        }
        if (!AiScopeLabels.HasScope(workItem, taskType))
        {
            // scope gate: everything must converge — the transition without the scope label is nothing
            logger.LogInformation("{taskType} trigger: event {eventId} consumed — {label} is not on {workItem} (scope gate)",
                taskType, githubEvent.Id, AiScopeLabels.LabelForTaskType[taskType], Label(workItem, await db.Repositories.AsNoTracking()
                    .SingleAsync(r => r.Id == workItem.RepositoryId, cancellationToken)));
            return false;
        }

        // Currency check: the trigger reacts to the recorded transition, but a plan for an item that
        // has since left `to` (a replayed/deferred event, or the human moved the card back) is noise —
        // the card's CURRENT column state must still be the target one.
        var projectItem = await db.ProjectItems.AsNoTracking()
            .SingleOrDefaultAsync(i => i.ItemId == itemId, cancellationToken);
        if (projectItem is { } row
            && ReadField(row.FieldValues, trigger.StatusField) is { } current
            && !string.Equals(current, trigger.To, StringComparison.Ordinal))
        {
            logger.LogInformation("{taskType} trigger: event {eventId} consumed — the item's current {field} is '{current}', not '{to}' (stale transition)",
                taskType, githubEvent.Id, trigger.StatusField, current, trigger.To);
            return false;
        }
        var repository = await db.Repositories.AsNoTracking()
            .SingleAsync(r => r.Id == workItem.RepositoryId, cancellationToken);
        if (workItem.State != "open")
        {
            logger.LogInformation("{taskType} trigger: event {eventId} consumed — work item {label} is closed",
                taskType, githubEvent.Id, Label(workItem, repository));
            return false;
        }
        var (resolved, reason, detail) = await ProfileGate.ResolveAsync(
            db, repository, taskType, cancellationToken);
        if (resolved is null)
        {
            logger.LogInformation("{taskType} trigger: event {eventId} consumed — no ready profile for work item {label} ({reason}: {detail})",
                taskType, githubEvent.Id, Label(workItem, repository), reason, detail);
            return false;
        }
        if (taskType == OrchestrationTaskTypes.Implement)
        {
            // the coder implements an APPROVED plan: a successful plan artifact must exist
            var workflowForArtifact = await db.Workflows.AsNoTracking()
                .SingleOrDefaultAsync(w => w.WorkItemId == workItem.Id, cancellationToken);
            if (workflowForArtifact is null
                || await CommandAdmission.LatestPlanArtifactIdAsync(db, workflowForArtifact.Id, cancellationToken) is null)
            {
                logger.LogInformation("implement trigger: event {eventId} consumed — no successful plan artifact to approve (run the plan first) for work item {label}",
                    githubEvent.Id, Label(workItem, repository));
                return false;
            }
        }

        var now = DateTime.UtcNow;
        var occurredAt = githubEvent.OccurredAtGithub ?? githubEvent.ReceivedAt;
        var workflow = await LockWorkflowAsync(db, workItem.Id, cancellationToken);
        if (workflow is null)
        {
            workflow = new Workflow
            {
                Id = Guid.NewGuid(),
                WorkItemId = workItem.Id,
                Status = WorkflowStatuses.WaitingForHuman, // accepted trigger → task queued → active below
                CreatedAt = now,
                UpdatedAt = now,
            };
            db.Workflows.Add(workflow);
        }
        else
        {
            if (workflow.Status is WorkflowStatuses.Failed or WorkflowStatuses.Closed)
            {
                logger.LogInformation("{taskType} trigger: event {eventId} consumed — workflow is {status} for work item {label}",
                    taskType, githubEvent.Id, workflow.Status, Label(workItem, repository));
                return false;
            }
            if (await db.OrchestrationTasks.AnyAsync(
                    t => t.WorkflowId == workflow.Id && t.TriggerGithubEventId == githubEvent.Id, cancellationToken))
                return false; // crash replay: this event already produced its Task
            if (await db.OrchestrationTasks.AnyAsync(
                    t => t.WorkflowId == workflow.Id && t.Type == taskType
                        && t.TriggerAtGithub > occurredAt, cancellationToken))
            {
                // An explicit command (or a later trigger) of this type already covered this transition.
                logger.LogInformation("{taskType} trigger: event {eventId} consumed — a {taskType} task already ran after the transition for work item {label}",
                    taskType, githubEvent.Id, taskType, Label(workItem, repository));
                return false;
            }
            if (await db.OrchestrationTasks.AnyAsync(
                    t => t.WorkflowId == workflow.Id && OrchestrationTaskStatuses.Unfinished.Contains(t.Status), cancellationToken))
                return true; // deferred: re-tried after the running Task settles
        }

        var maxSequence = await db.OrchestrationTasks
            .Where(t => t.WorkflowId == workflow.Id)
            .MaxAsync(t => (int?)t.Sequence, cancellationToken) ?? 0;

        var task = new OrchestrationTask
        {
            Id = Guid.NewGuid(),
            WorkflowId = workflow.Id,
            Sequence = maxSequence + 1,
            Type = taskType,
            AgentProfileVersionId = resolved.Version.Id,
            Status = OrchestrationTaskStatuses.Queued,
            TriggerGithubEventId = githubEvent.Id,
            // the triggering event's GitHub time anchors the no-new-input gate on this Task
            TriggerAtGithub = occurredAt,
            CreatedAt = now,
            RetryPolicy = RetryPolicies.Effective(resolved.Version.RetryPolicy),
        };
        db.OrchestrationTasks.Add(task);
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

        workflow.Status = WorkflowStatuses.Active;
        workflow.CurrentTaskId = task.Id;
        workflow.StartedAt ??= now;
        workflow.UpdatedAt = now;
        await AiStateLabels.MarkAsync(db, repository.Id, workItem.Id, workItem.Number,
            task.Id, AiStateLabels.Scheduled, cancellationToken);
        logger.LogInformation(
            "{taskType} trigger: automatic {taskType} task {taskId} created for work item {label} (event {eventId}, board {board}, status {from} -> {to}, profile version {versionId})",
            taskType, taskType, task.Id, Label(workItem, repository), githubEvent.Id, board, from, to, resolved.Version.Id);
        return false;
    }

    /// <summary>Work item display label for trigger logs (the synced full_name is lowercased by the
    /// Poller — good enough for a log line).</summary>
    private static string Label(WorkItem workItem, Repository repository) =>
        $"{repository.FullName}#{workItem.Number}";

    /// <summary>`SELECT … FOR UPDATE` on the Workflow row serializes same-Workflow triggers;
    /// returns the tracked row or null when none exists.</summary>
    private static async Task<Workflow?> LockWorkflowAsync(AppDbContext db, Guid workItemId,
        CancellationToken cancellationToken)
    {
        var locked = await db.Workflows
            .FromSqlInterpolated($"SELECT * FROM workflow WHERE work_item_id = {workItemId} FOR UPDATE")
            .ToListAsync(cancellationToken);
        return locked.Count > 0 ? locked[0] : null;
    }

    /// <summary>ProjectItemsSync's ProjectItemEventPayload (Web defaults): `project` is the
    /// `owner/number` board string, `item_id` the item's stable databaseId, `changes` maps field
    /// name → "old -> new".</summary>
    private static (string? Board, long? ItemId, Dictionary<string, string>? Changes) ReadPayload(string payload)
    {
        try
        {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;
            var board = root.TryGetProperty("project", out var project) ? project.GetString() : null;
            long? itemId = root.TryGetProperty("item_id", out var idElement) && idElement.ValueKind == JsonValueKind.Number
                && idElement.TryGetInt64(out var idValue) ? idValue : null;
            if (string.IsNullOrEmpty(board)
                || !root.TryGetProperty("changes", out var changes)
                || changes.ValueKind != JsonValueKind.Object)
                return (null, null, null);
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var property in changes.EnumerateObject())
                map[property.Name] = property.Value.ValueKind == JsonValueKind.String
                    ? property.Value.GetString() ?? ""
                    : property.Value.ToString();
            return (board, itemId, map);
        }
        catch (JsonException)
        {
            return (null, null, null);
        }
    }

    /// <summary>The current value of one field from the stored `project_item.field_values` jsonb
    /// (ProjectItemsSync's canonical shape); null when the field is absent or unreadable.</summary>
    private static string? ReadField(string fieldValuesJson, string fieldName)
    {
        try
        {
            using var document = JsonDocument.Parse(fieldValuesJson);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                return null;
            foreach (var field in document.RootElement.EnumerateArray())
                if (field.ValueKind == JsonValueKind.Object
                    && field.TryGetProperty("name", out var name)
                    && string.Equals(name.GetString(), fieldName, StringComparison.Ordinal)
                    && field.TryGetProperty("value", out var value))
                    return value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetRawText();
            return null;
        }
        catch (JsonException)
        {
            return null; // unreadable snapshot: fail open, the trigger proceeds
        }
    }
}
