using Microsoft.EntityFrameworkCore;
using PiAgent.Infrastructure.Persistence;
using PiAgent.Orchestrator.Config;

namespace PiAgent.Orchestrator.Orchestration;

/// <summary>
/// Shared review-loop helpers: the loop card resolution, the STATUS+LABEL launch gate's status half
/// (the label half is the scope-label gate every path already enforces) and the round cap. The loop
/// card is the ISSUE the review's pull request serves (the board card the human moves); a PR off a
/// link (or an issue itself) loops on itself. The status half reads the card's current column
/// against projects.yaml `ai_statuses.review_statuses` — not on a configured board, or a board
/// without the key, fails OPEN (the same stance the manual-/code gate takes). The cap comes from
/// projects.yaml `review_loop.max_rounds` of the card's board; default <see cref="ReviewLoopConfig.DefaultMaxRounds"/>.
/// </summary>
public static class ReviewFlow
{
    /// <summary>The diagnostic code the exhaustion comment names (special error code, human text around it).</summary>
    public const string ExhaustedCode = "review_loop_exhausted";

    /// <summary>The board card the review loop runs on: the linked issue of a pull request, else the
    /// item itself. Null only when a PR's linked issue row vanished (referential integrity bug).</summary>
    public static async Task<WorkItem?> LoopCardAsync(
        AppDbContext db, WorkItem workItem, CancellationToken cancellationToken)
    {
        if (workItem.Type != WorkItemTypes.PullRequest)
            return workItem;
        var issueId = await db.WorkItemLinks.AsNoTracking()
            .Where(l => l.PrWorkItemId == workItem.Id)
            .Select(l => (Guid?)l.IssueWorkItemId)
            .FirstOrDefaultAsync(cancellationToken);
        if (issueId is null)
            return workItem; // a PR nobody linked to an issue loops on itself
        return await db.WorkItems.AsNoTracking().SingleOrDefaultAsync(w => w.Id == issueId, cancellationToken) ?? workItem;
    }

    /// <summary>The card's linked open pull request (WorkItemLink; newest created first). Null when
    /// none is linked/open — the caller consumes the trigger with a log line.</summary>
    public static async Task<WorkItem?> LinkedOpenPullRequestAsync(
        AppDbContext db, Guid issueWorkItemId, CancellationToken cancellationToken) =>
        await db.WorkItemLinks.AsNoTracking()
            .Where(l => l.IssueWorkItemId == issueWorkItemId)
            .Join(db.WorkItems.AsNoTracking(), l => l.PrWorkItemId, p => p.Id, (l, p) => p)
            .Where(p => p.State == "open" && p.Type == WorkItemTypes.PullRequest)
            .OrderByDescending(p => p.CreatedAtGithub).ThenByDescending(p => p.Number)
            .FirstOrDefaultAsync(cancellationToken);

    /// <summary>The status half of the review-launch gate (STATUS + LABEL): `true` when the loop
    /// card's current column is one of `review_statuses` — or when the status cannot be verified
    /// (the card is not on a configured board / the key is absent / the row is gone): fail open.</summary>
    public static async Task<bool> StatusAllowedAsync(
        AppDbContext db, Config.ConfigurationState configurationState,
        Guid repositoryId, WorkItem workItem, CancellationToken cancellationToken)
    {
        var card = await LoopCardAsync(db, workItem, cancellationToken);
        if (card is null)
            return true; // dangling link: cannot verify — fail open
        if (await BoardStatusOutbox.TryGetBoardStatusAsync(db, configurationState,
                repositoryId, card.Number, cancellationToken) is not { } board)
            return true;
        if (board.Config.ReviewStatuses is not { Count: > 0 } allowed)
            return true; // the board does not constrain review launches
        return allowed.Contains(board.Status, StringComparer.Ordinal);
    }

    /// <summary>The review→fix round cap for the item's board; the default when the card is not on
    /// a configured board or its board declares no `review_loop`.</summary>
    public static async Task<int> MaxRoundsAsync(
        AppDbContext db, Config.ConfigurationState configurationState,
        Guid repositoryId, long issueNumber, CancellationToken cancellationToken)
    {
        if (configurationState.ReviewLoopConfigs.Count == 0)
            return ReviewLoopConfig.DefaultMaxRounds;
        var boards = await db.ProjectItems.AsNoTracking()
            .Where(i => i.RepositoryId == repositoryId && i.Number == (int)issueNumber)
            .Join(db.ProjectSyncs.AsNoTracking(),
                i => i.ProjectSyncId, s => s.Id,
                (i, s) => new { s.Owner, s.ProjectNumber })
            .ToListAsync(cancellationToken);
        foreach (var config in configurationState.ReviewLoopConfigs)
            if (boards.Any(b => b.Owner == config.Owner && b.ProjectNumber == config.Number))
                return config.MaxRounds;
        return ReviewLoopConfig.DefaultMaxRounds;
    }
}
