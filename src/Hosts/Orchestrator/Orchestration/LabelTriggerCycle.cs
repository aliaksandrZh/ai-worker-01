using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PiAgent.Infrastructure.Persistence;
using PiAgent.Platform.Workers;

namespace PiAgent.Orchestrator.Orchestration;

/// <summary>
/// The AI-scope-label scanner — the standing "run it when everything converges" trigger, derived
/// from the synced state on every tick (no GitHub events to claim; a label appearing shows up
/// within one poll interval via `work_items.labels`). For every open work item carrying an
/// `AI:*` scope label (AiScopeLabels: AI:Plan / AI:Code / AI:Review / AI:Test) the corresponding
/// task type starts when ALL of the following hold:
/// - the label is still on the item (exact match, current snapshot);
/// - the item is open and its workflow is not failed/closed;
/// - the type's own conditions: Plan — the item's current board column IS the Clarifying state
///   ("everything must converge": Clarifying without the label and the label outside Clarifying
///   both do nothing); Implement — a successful plan artifact to approve AND the card in the
///   auto_code target column (STATUS + LABEL — the same invariant the trigger and the manual
///   /code gate enforce); Review — an open
///   non-draft PR with no initial review yet and no explicit `/review` (the label IS the request);
///   TestDesign — not implemented (no routing rule), so it silently never fires;
/// - the profile version is `ready` (else the tick silently skips — retried next tick);
/// - the run-decision rule (owner ruling): the scanner auto-starts a type ONLY if it never ran
///   before on this item — a standing label plus a new human comment does NOT re-run anything
///   (that needs an explicit `/plan`); a failed/cancelled previous run stays with `/retry`.
/// Created Tasks are scanner-created (no command, no event — the relaxed `ck_task_has_trigger`),
/// carry the same Workflow/Task/Job shape as every other creator, and mark `AI Scheduled`.
/// </summary>
public sealed class LabelTriggerCycle(
    IServiceScopeFactory scopeFactory,
    Config.ConfigurationState configurationState,
    ILogger<LabelTriggerCycle> logger) : IWorkerCycle
{
    private const int MaxItemsPerTick = 64;
    private const string BoardClarifyingStatus = "Clarifying";
    private const string BoardStatusField = "Status";

    public async Task RunCycleAsync(CancellationToken cancellationToken)
    {
        if (!configurationState.Synchronized || string.IsNullOrEmpty(configurationState.BotLogin))
            return; // cold-start gate

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var rows = await db.WorkItems.AsNoTracking()
            .Where(w => w.State == "open")
            .OrderBy(w => w.Id)
            .Take(MaxItemsPerTick)
            .Select(w => new { w.Id, w.RepositoryId, w.Number, w.Type, w.Labels, w.HeadSha, w.BaseSha, w.Draft })
            .ToListAsync(cancellationToken);
        var candidates = rows
            .Select(w => new Candidate(w.Id, w.RepositoryId, w.Number, w.Type, w.Labels, w.HeadSha, w.BaseSha, w.Draft))
            .ToList();

        foreach (var item in candidates)
        {
            foreach (var taskType in AiScopeLabels.LabelForTaskType.Keys)
            {
                if (!AiScopeLabels.HasScope(item.Labels, taskType))
                    continue;
                try
                {
                    await TryStartAsync(db, item, taskType, cancellationToken);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    logger.LogError(exception, "label trigger scan failed for work item {workItemId} ({taskType}) — retried next tick",
                        item.Id, taskType);
                }
            }
        }
    }

    private sealed record Candidate(
        Guid Id, Guid RepositoryId, long Number, string Type,
        string Labels, string? HeadSha, string? BaseSha, bool? Draft);

    /// <summary>All failing conditions are silent per-item skips — the scanner re-derives them next tick.</summary>
    private async Task TryStartAsync(AppDbContext db, Candidate item, string taskType,
        CancellationToken cancellationToken)
    {
        var repository = await db.Repositories.AsNoTracking()
            .SingleOrDefaultAsync(r => r.Id == item.RepositoryId, cancellationToken);
        if (repository is null)
            return; // untracked repository rows carry no synced work items in practice
        var workItem = await db.WorkItems.AsNoTracking()
            .SingleOrDefaultAsync(w => w.Id == item.Id, cancellationToken);
        if (workItem is null)
            return;

        var workflow = await db.Workflows.AsNoTracking()
            .SingleOrDefaultAsync(w => w.WorkItemId == item.Id, cancellationToken);
        if (workflow is { Status: WorkflowStatuses.Failed or WorkflowStatuses.Closed })
            return;
        if (workflow is not null && await db.OrchestrationTasks.AnyAsync(
                t => t.WorkflowId == workflow.Id && OrchestrationTaskStatuses.Unfinished.Contains(t.Status), cancellationToken))
            return; // the single slot is occupied — the scanner fires when it frees

        // The owner's rule: the scanner auto-starts a type only if it never ran before on this
        // item. A standing label plus a new human comment does NOT re-run anything (that needs an
        // explicit /plan); a failed/cancelled previous run stays with /retry.
        if (workflow is not null && await db.OrchestrationTasks.AsNoTracking()
                .AnyAsync(t => t.WorkflowId == workflow.Id && t.Type == taskType, cancellationToken))
            return;

        var (resolved, reason, detail) = await ProfileGate.ResolveAsync(db, repository, taskType, cancellationToken);
        if (resolved is null)
        {
            logger.LogInformation("label trigger: {taskType} on {repo}#{number} skipped — no ready profile ({reason}: {detail})",
                taskType, repository.FullName, item.Number, reason, detail);
            return;
        }

        // Type-specific convergence conditions.
        Guid? planArtifactId = null;
        switch (taskType)
        {
            case OrchestrationTaskTypes.Plan:
                var projectItem = await db.ProjectItems.AsNoTracking()
                    .SingleOrDefaultAsync(i => i.RepositoryId == item.RepositoryId && i.Number == (int)item.Number, cancellationToken);
                var currentStatus = projectItem is null ? null : CurrentBoardStatus(projectItem.FieldValues);
                if (!string.Equals(currentStatus, BoardClarifyingStatus, StringComparison.Ordinal))
                {
                    logger.LogInformation("label trigger: plan on {repo}#{number} skipped — the item's current board column is '{status}', not '{clarifying}'",
                        repository.FullName, item.Number, currentStatus ?? "(not on a board)", BoardClarifyingStatus);
                    return;
                }
                break;
            case OrchestrationTaskTypes.Implement:
                if (workflow is null)
                    return; // no workflow → no plan task has ever run → nothing to approve
                planArtifactId = await CommandAdmission.LatestPlanArtifactIdAsync(db, workflow.Id, cancellationToken);
                if (planArtifactId is null)
                {
                    logger.LogInformation("label trigger: implement on {repo}#{number} skipped — no successful plan artifact to approve (run the plan first)",
                        repository.FullName, item.Number);
                    return;
                }
                // The same invariant as the auto_code trigger and the manual /code gate —
                // STATUS + LABEL: the automation runs only when the card sits in the trigger's
                // target column (Ready). A label without the column is nothing.
                var codeTrigger = configurationState.ProjectBoardTriggers
                    .FirstOrDefault(t => t.TaskType == OrchestrationTaskTypes.Implement);
                if (codeTrigger is not null)
                {
                    var codeColumn = await db.ProjectItems.AsNoTracking()
                        .Join(db.ProjectSyncs.AsNoTracking(),
                            i => i.ProjectSyncId, s => s.Id,
                            (i, s) => new { i.RepositoryId, i.Number, i.FieldValues, s.Owner, s.ProjectNumber })
                        .Where(x => x.RepositoryId == item.RepositoryId && x.Number == (int)item.Number
                            && x.Owner == codeTrigger.Owner && x.ProjectNumber == codeTrigger.Number)
                        .Select(x => x.FieldValues)
                        .SingleOrDefaultAsync(cancellationToken);
                    var codeStatus = codeColumn is null ? null : CurrentFieldValue(codeColumn, codeTrigger.StatusField);
                    if (!string.Equals(codeStatus, codeTrigger.To, StringComparison.Ordinal))
                    {
                        logger.LogInformation(
                            "label trigger: implement on {repo}#{number} skipped — the item's current board column is '{status}', not the auto_code target '{to}' (status + label must converge)",
                            repository.FullName, item.Number, codeStatus ?? "(not on a board)", codeTrigger.To);
                        return;
                    }
                }
                break;
            case OrchestrationTaskTypes.Review:
                if (item.Type != WorkItemTypes.PullRequest || item.Draft == true)
                    return; // the label is a standing review request: open non-draft PRs only
                if (workflow is { } existing
                    && (existing.InitialReviewCreatedAt is not null
                        || await db.OrchestrationTasks.AnyAsync(
                            t => t.WorkflowId == existing.Id && t.Type == OrchestrationTaskTypes.Review && t.TriggerCommandId != null, cancellationToken)))
                    return; // once per Workflow lifetime; an explicit /review already covered it
                break;
        }

        var now = DateTime.UtcNow;
        var locked = await LockWorkflowAsync(db, item.Id, cancellationToken);
        if (locked is null)
        {
            locked = new Workflow
            {
                Id = Guid.NewGuid(),
                WorkItemId = item.Id,
                Status = WorkflowStatuses.WaitingForHuman,
                CreatedAt = now,
                UpdatedAt = now,
            };
            db.Workflows.Add(locked);
        }
        else if (locked.Status is WorkflowStatuses.Failed or WorkflowStatuses.Closed)
            return; // re-check under the lock: the state may have moved while we resolved the profile
        else if (await db.OrchestrationTasks.AnyAsync(
                t => t.WorkflowId == locked.Id && OrchestrationTaskStatuses.Unfinished.Contains(t.Status), cancellationToken))
            return; // re-check under the lock: a claim/admission may have taken the slot meanwhile

        var maxSequence = await db.OrchestrationTasks
            .Where(t => t.WorkflowId == locked.Id)
            .MaxAsync(t => (int?)t.Sequence, cancellationToken) ?? 0;

        var task = new OrchestrationTask
        {
            Id = Guid.NewGuid(),
            WorkflowId = locked.Id,
            Sequence = maxSequence + 1,
            Type = taskType,
            AgentProfileVersionId = resolved.Version.Id,
            Status = OrchestrationTaskStatuses.Queued,
            // scanner-created: no command row, no producing event (the relaxed trigger check)
            TriggerAtGithub = now, // the no-new-input anchor for everything that follows
            TargetHeadSha = taskType == OrchestrationTaskTypes.Review ? item.HeadSha : null,
            TargetBaseSha = taskType == OrchestrationTaskTypes.Review ? item.BaseSha : null,
            CreatedAt = now,
            RetryPolicy = RetryPolicies.Effective(resolved.Version.RetryPolicy),
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

        locked.Status = WorkflowStatuses.Active;
        locked.CurrentTaskId = task.Id;
        locked.StartedAt ??= now;
        locked.UpdatedAt = now;
        if (taskType == OrchestrationTaskTypes.Review)
            locked.InitialReviewCreatedAt ??= now; // the same once-per-lifetime marker the review trigger uses
        await db.SaveChangesAsync(cancellationToken);
        await AiStateLabels.MarkAsync(db, repository.Id, workItem.Id, workItem.Number,
            task.Id, AiStateLabels.Scheduled, cancellationToken);
        logger.LogInformation("label trigger: {taskType} task {taskId} started for {repo}#{number} (label {label}, profile version {versionId})",
            taskType, task.Id, repository.FullName, item.Number, AiScopeLabels.LabelForTaskType[taskType], resolved.Version.Id);
    }

    /// <summary>`SELECT … FOR UPDATE` — the HTTP /internal/commands hook can admit concurrently.</summary>
    private static async Task<Workflow?> LockWorkflowAsync(AppDbContext db, Guid workItemId,
        CancellationToken cancellationToken)
    {
        var locked = await db.Workflows
            .FromSqlInterpolated($"SELECT * FROM workflow WHERE work_item_id = {workItemId} FOR UPDATE")
            .ToListAsync(cancellationToken);
        return locked.Count > 0 ? locked[0] : null;
    }

    /// <summary>The item's current single-select board status from the synced field snapshot.</summary>
    private static string? CurrentBoardStatus(string fieldValuesJson) =>
        CurrentFieldValue(fieldValuesJson, BoardStatusField);

    private static string? CurrentFieldValue(string fieldValuesJson, string fieldName)
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
                    && field.TryGetProperty("value", out var value)
                    && value.ValueKind == JsonValueKind.String)
                    return value.GetString();
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
