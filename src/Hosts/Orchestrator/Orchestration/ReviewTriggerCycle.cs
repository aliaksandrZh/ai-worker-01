using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PiAgent.Infrastructure.Persistence;
using PiAgent.Orchestrator.Commands;
using PiAgent.Platform.Workers;

namespace PiAgent.Orchestrator.Orchestration;

/// <summary>
/// System-owned review triggers. Consumes three Poller-written
/// event kinds for Pull Requests and never reads GitHub directly:
/// - `review_requested` (bot is the requested reviewer) — starts the initial review on a ready PR; on a
///   draft PR the request is only recorded (the draft→ready transition decides later);
/// - `work_item_updated` whose `changed` set contains `draft` and that now reads ready — the deferred
///   initial review under the three conditions (request still active, none created yet, no explicit
///   `/review` ran);
/// - `review_request_removed` (bot) — cancels the not-yet-started automatic review Task.
/// Exactly one automatic initial review per Workflow lifetime: `Workflow.InitialReviewCreatedAt` is set at
/// creation and never cleared, even if the Task is later cancelled.
/// </summary>
public sealed class ReviewTriggerCycle(
    IServiceScopeFactory scopeFactory,
    Config.ConfigurationState configurationState,
    ILogger<ReviewTriggerCycle> logger) : IWorkerCycle
{
    private const int BatchSize = 32;

    private static readonly string[] TriggerEventTypes =
    [
        GitHubEventTypes.ReviewRequested,
        GitHubEventTypes.ReviewRequestRemoved,
        GitHubEventTypes.WorkItemUpdated,
    ];

    public async Task RunCycleAsync(CancellationToken cancellationToken)
    {
        if (!configurationState.Synchronized || string.IsNullOrEmpty(configurationState.BotLogin))
            return; // cold-start gate
        var botLogin = configurationState.BotLogin;

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var events = await db.GitHubEvents
            .Where(e => TriggerEventTypes.Contains(e.EventType)
                && e.ProcessingStatus == GitHubEventStatuses.Pending
                && e.WorkItemId != null)
            .OrderBy(e => e.ReceivedAt).ThenBy(e => e.Id)
            .Take(BatchSize)
            .ToListAsync(cancellationToken);

        foreach (var githubEvent in events)
        {
            // Only events that can matter are processed; the rest (e.g. non-draft `work_item_updated`)
            // are marked processed immediately so they never block the queue.
            githubEvent.ProcessingStatus = GitHubEventStatuses.Processing;
            await db.SaveChangesAsync(cancellationToken);
            try
            {
                await ProcessAsync(db, githubEvent, botLogin, cancellationToken);
                githubEvent.ProcessingStatus = GitHubEventStatuses.Processed;
                githubEvent.ProcessedAt = DateTime.UtcNow;
                githubEvent.ProcessingError = null;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogError(exception, "review trigger event {eventId} failed — releasing for retry", githubEvent.Id);
                githubEvent.ProcessingStatus = GitHubEventStatuses.Pending;
                githubEvent.ProcessingError = exception.Message[..Math.Min(exception.Message.Length, 2000)];
            }
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    private static async Task ProcessAsync(AppDbContext db, GitHubEvent githubEvent, string botLogin, CancellationToken cancellationToken)
    {
        var workItem = await db.WorkItems.SingleOrDefaultAsync(w => w.Id == githubEvent.WorkItemId, cancellationToken);
        if (workItem is null || workItem.Type != WorkItemTypes.PullRequest || workItem.State != "open")
            return; // only open Pull Requests take automatic reviews

        switch (githubEvent.EventType)
        {
            case GitHubEventTypes.ReviewRequested:
                if (!IsBotPayload(githubEvent, botLogin))
                    return;
                if (workItem.Draft == true)
                    return; // recorded only: the draft→ready transition decides
                await TryCreateInitialReviewAsync(db, githubEvent, workItem, botLogin, cancellationToken);
                return;

            case GitHubEventTypes.WorkItemUpdated:
                if (!IsDraftToReady(githubEvent) || workItem.Draft != false)
                    return;
                await TryCreateInitialReviewAsync(db, githubEvent, workItem, botLogin, cancellationToken);
                return;

            case GitHubEventTypes.ReviewRequestRemoved:
                if (!IsBotPayload(githubEvent, botLogin))
                    return;
                await CancelQueuedInitialReviewAsync(db, workItem, cancellationToken);
                return;
        }
    }

    /// <summary>Creates the automatic initial review when the three conditions hold.
    /// Every failing condition is a silent no-op — the event is consumed, nothing is created.</summary>
    private static async Task TryCreateInitialReviewAsync(
        AppDbContext db, GitHubEvent githubEvent, WorkItem workItem, string botLogin, CancellationToken cancellationToken)
    {
        // Condition 1: the bot request is still active (read from the synced snapshot).
        if (!RequestedReviewers(workItem).Contains(botLogin, StringComparer.OrdinalIgnoreCase))
            return;

        var repository = await db.Repositories.SingleAsync(r => r.Id == workItem.RepositoryId, cancellationToken);
        var now = DateTime.UtcNow;

        var workflow = await LockWorkflowAsync(db, workItem.Id, cancellationToken);
        if (workflow is null)
        {
            workflow = new Workflow
            {
                Id = Guid.NewGuid(),
                WorkItemId = workItem.Id,
                Status = WorkflowStatuses.WaitingForHuman,
                CreatedAt = now,
                UpdatedAt = now,
            };
            db.Workflows.Add(workflow);
        }

        // Condition 2: no automatic initial review was created yet (once per Workflow lifetime).
        if (workflow.InitialReviewCreatedAt is not null)
            return;
        if (workflow.Status is WorkflowStatuses.Failed or WorkflowStatuses.Closed)
            return;

        // Condition 3: no explicit `/review` already ran for this PR.
        var explicitReviewExists = await db.OrchestrationTasks.AnyAsync(
            t => t.WorkflowId == workflow.Id && t.Type == OrchestrationTaskTypes.Review && t.TriggerCommandId != null,
            cancellationToken);
        if (explicitReviewExists)
            return;

        // The one-unfinished-Task-per-Workflow invariant — defer if another Task is in flight.
        if (await db.OrchestrationTasks.AnyAsync(
                t => t.WorkflowId == workflow.Id && OrchestrationTaskStatuses.Unfinished.Contains(t.Status), cancellationToken))
            return;

        var version = await ResolveReviewVersionAsync(db, repository, cancellationToken);
        if (version is null)
            return; // no ready reviewer profile: the review is not created (the ready/recheck path reports it)

        var maxSequence = await db.OrchestrationTasks
            .Where(t => t.WorkflowId == workflow.Id)
            .MaxAsync(t => (int?)t.Sequence, cancellationToken) ?? 0;

        var task = new OrchestrationTask
        {
            Id = Guid.NewGuid(),
            WorkflowId = workflow.Id,
            Sequence = maxSequence + 1,
            Type = OrchestrationTaskTypes.Review,
            AgentProfileVersionId = version.Id,
            Status = OrchestrationTaskStatuses.Queued,
            TriggerGithubEventId = githubEvent.Id,
            // the triggering event's GitHub time anchors the no-new-input gate on this Task
            TriggerAtGithub = githubEvent.OccurredAtGithub ?? githubEvent.ReceivedAt,
            TargetHeadSha = workItem.HeadSha,
            TargetBaseSha = workItem.BaseSha,
            CreatedAt = now,
            RetryPolicy = RetryPolicies.Effective(version.RetryPolicy),
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

        workflow.InitialReviewCreatedAt = now;
        workflow.Status = WorkflowStatuses.Active;
        workflow.CurrentTaskId = task.Id;
        workflow.StartedAt ??= now;
        workflow.UpdatedAt = now;
    }

    /// <summary>Withdrawn request: cancel only a QUEUED automatic review (a running review is never
    /// interrupted). Only the Task's `queued` status is touched here; the Job and the notice are
    /// the claim-time path's job (`JobQueueWorker`, reason `review_request_removed_before_execution`), so the
    /// Task/Job state machine has one writer. `TriggerGithubEventId` stays set, so a renewed request never
    /// starts a second automatic review (the Workflow guard is `InitialReviewCreatedAt`, never cleared).</summary>
    private static async Task CancelQueuedInitialReviewAsync(AppDbContext db, WorkItem workItem, CancellationToken cancellationToken)
    {
        var workflow = await db.Workflows.SingleOrDefaultAsync(w => w.WorkItemId == workItem.Id, cancellationToken);
        if (workflow is null)
            return;
        // Mark the queued automatic review so the claim path cancels it before it starts.
        await db.OrchestrationTasks
            .Where(t => t.WorkflowId == workflow.Id
                && t.Type == OrchestrationTaskTypes.Review
                && t.TriggerGithubEventId != null
                && t.Status == OrchestrationTaskStatuses.Queued)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.FailureReason, AgentCommandRejectionReasons.ReviewRequestRemovedBeforeExecution), cancellationToken);
    }

    private static async Task<AgentProfileVersion?> ResolveReviewVersionAsync(
        AppDbContext db, Repository repository, CancellationToken cancellationToken)
    {
        var (resolved, _, _) = await ProfileGate.ResolveAsync(db, repository, OrchestrationTaskTypes.Review, cancellationToken);
        return resolved?.Version;
    }

    private static async Task<Workflow?> LockWorkflowAsync(AppDbContext db, Guid workItemId, CancellationToken cancellationToken)
    {
        var locked = await db.Workflows
            .FromSqlInterpolated($"SELECT * FROM workflow WHERE work_item_id = {workItemId} FOR UPDATE")
            .ToListAsync(cancellationToken);
        return locked.Count > 0 ? locked[0] : null;
    }

    private static IReadOnlyList<string> RequestedReviewers(WorkItem workItem)
    {
        if (string.IsNullOrWhiteSpace(workItem.RequestedReviewers))
            return [];
        return JsonSerializer.Deserialize<List<string>>(workItem.RequestedReviewers) ?? [];
    }

    private static bool IsBotPayload(GitHubEvent githubEvent, string botLogin)
    {
        using var document = JsonDocument.Parse(githubEvent.Payload);
        return document.RootElement.TryGetProperty("reviewer", out var reviewer)
            && string.Equals(reviewer.GetString(), botLogin, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>`work_item_updated` carries the changed-field set; a draft flip is recorded as the `draft` key.</summary>
    private static bool IsDraftToReady(GitHubEvent githubEvent)
    {
        using var document = JsonDocument.Parse(githubEvent.Payload);
        return document.RootElement.TryGetProperty("changed", out var changed)
            && changed.ValueKind == JsonValueKind.Object
            && changed.TryGetProperty("draft", out _);
    }
}
