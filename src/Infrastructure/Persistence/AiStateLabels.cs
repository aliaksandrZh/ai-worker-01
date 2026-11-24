using Microsoft.EntityFrameworkCore;

namespace PiAgent.Infrastructure.Persistence;

/// <summary>
/// The AI-lifecycle issue labels — at most ONE of them on a work item at any time, managed
/// exclusively through the outbox (`ai_label` actions, durable + retried like the system comments):
/// <list type="bullet">
/// <item><see cref="Scheduled"/> — the command passed every validation and was admitted (Task+Job
/// queued), or a system-owned task (automatic plan/review) was queued;</item>
/// <item><see cref="Processing"/> — the run was handed off to the Runtime API;</item>
/// <item><see cref="Finished"/> — the task succeeded;</item>
/// <item><see cref="Failed"/> — the task finally failed (retries exhausted, timeout, startup death).</item>
/// </list>
/// Deliberately NOT rewritten: a silent automatic retry keeps the current label (the item is still
/// "in work"); a command rejected at validation never gets a label. Cleared (all four removed) on
/// /stop cancellation and root closure — neither is a failure. A human editing the labels by hand is
/// not fought; the next transition restores the state.
/// </summary>
public static class AiStateLabels
{
    public const string Scheduled = "AI Scheduled";
    public const string Processing = "AI Processing";
    public const string Finished = "AI Finished";
    public const string Failed = "AI Failed";

    public static readonly string[] All = [Scheduled, Processing, Finished, Failed];

    /// <summary>Enqueues the replace action in the caller's transaction: the Outbox dispatcher
    /// removes the other three labels (404 = already absent, fine) and adds `target`;
    /// `target = null` removes all four (the clear action). A pull request that is linked to an
    /// issue (the review→fix loop's card) marks BOTH items — the PR and the issue carry the same
    /// AI state while review work runs; an issue (or an unlinked PR) is marked alone.</summary>
    public static async Task MarkAsync(
        AppDbContext db, Guid repositoryId, Guid workItemId, long issueNumber, Guid? taskId,
        string? target, CancellationToken cancellationToken)
    {
        var store = new OutboxActionStore(db);
        await store.EnqueueAiLabelAsync(
            new AiLabelEnqueueRequest(repositoryId, workItemId, taskId, issueNumber, target), cancellationToken);
        var linkedIssue = await db.WorkItemLinks.AsNoTracking()
            .Where(l => l.PrWorkItemId == workItemId)
            .Join(db.WorkItems.AsNoTracking(), l => l.IssueWorkItemId, w => w.Id,
                (l, w) => new { w.Id, w.Number })
            .FirstOrDefaultAsync(cancellationToken);
        if (linkedIssue is { } card)
            await store.EnqueueAiLabelAsync(
                new AiLabelEnqueueRequest(repositoryId, card.Id, taskId, card.Number, target), cancellationToken);
    }

    /// <summary>Removes all four AI labels (cancellation / root closure — not a failure); marks the
    /// linked issue too (the same dual-write rule <see cref="MarkAsync"/> follows).</summary>
    public static Task ClearAsync(
        AppDbContext db, Guid repositoryId, Guid workItemId, long issueNumber, Guid? taskId,
        CancellationToken cancellationToken) =>
        MarkAsync(db, repositoryId, workItemId, issueNumber, taskId, null, cancellationToken);
}

/// <summary>`Label = null` is the clear-all action; otherwise it must be one of <see cref="AiStateLabels.All"/>.</summary>
public sealed record AiLabelEnqueueRequest(
    Guid RepositoryId, Guid WorkItemId, Guid? TaskId, long IssueNumber, string? Label);

/// <summary>One named label add/remove (not one of the four AI state labels — those go through
/// <see cref="AiLabelEnqueueRequest"/>).</summary>
public sealed record LabelEnqueueRequest(
    Guid RepositoryId, Guid WorkItemId, Guid? TaskId, long IssueNumber, string Label, bool Add);
