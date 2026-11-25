using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PiAgent.Events;

namespace PiAgent.GithubSync;

/// <summary>
/// The sync adapter worker (docs/github-sync.md): polls the configured repositories,
/// diffs against cursors, appends sync events and derives user_command from parsed
/// comments (author permission resolved at detection). Read-only against GitHub.
/// </summary>
public sealed class GithubSyncWorker(
    SyncConfig config,
    IGithubReadApi github,
    IEventLog log,
    ICursorStore cursors,
    ILogger<GithubSyncWorker> logger) : BackgroundService
{
    private static readonly TimeSpan Overlap = TimeSpan.FromMinutes(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("github sync started for [{repos}] as bot {bot}",
            string.Join(", ", config.Repositories.Select(r => $"{r.Owner}/{r.Name}")), config.BotLogin);

        while (!stoppingToken.IsCancellationRequested)
        {
            foreach (var repo in config.Repositories)
            {
                try
                {
                    await SyncRepoAsync(repo, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "sync cycle failed for {repo}", $"{repo.Owner}/{repo.Name}");
                }
            }

            await Task.Delay(TimeSpan.FromSeconds(Math.Max(config.PollIntervalSeconds, 1)), stoppingToken);
        }
    }

    public async Task SyncRepoAsync(SyncConfig.RepoConfig repo, CancellationToken ct)
    {
        var repoKey = $"{repo.Owner}/{repo.Name}";
        await using var uow = await log.BeginTransactionAsync(ct);

        // 1. comments → comment_changed + derived user_command
        var commentsSince = await PollSinceAsync(repoKey, "comments", uow, ct);
        var comments = (await github.ListCommentsSinceAsync(repo.Owner, repo.Name, commentsSince, ct))
            .Where(c => c.ItemNumber > 0)
            .ToList();
        foreach (var comment in comments)
        {
            await log.AppendAsync(new CommentChanged(
                repo.Owner, repo.Name, comment.ItemNumber, comment.Id, comment.Author, comment.Body,
                Deleted: false), uow, ct);

            if (comment.Author.Equals(config.BotLogin, StringComparison.Ordinal))
                continue; // the bot's own comments are noise

            if (CommandGrammar.Parse(comment.Body, config.BotLogin) is not { } parsed)
                continue;

            var permission = await github.GetPermissionAsync(repo.Owner, repo.Name, comment.Author, ct) ?? "read";
            await log.AppendAsync(new UserCommand(
                parsed.Command, repo.Owner, repo.Name, comment.ItemNumber, comment.Id,
                comment.Author, permission, parsed.TrailingContext), uow, ct);
        }

        if (comments.Count > 0)
            await cursors.SetAsync(repoKey, "comments", Watermark(comments.Max(c => c.CreatedAtUtc)), uow, ct);

        // 2. items → item_changed / labels_changed / item_closed; PRs → pr_updated + reviews
        var itemsSince = await PollSinceAsync(repoKey, "items", uow, ct);
        var reviewsSince = await PollSinceAsync(repoKey, "reviews", uow, ct);
        var items = await github.ListItemsUpdatedSinceAsync(repo.Owner, repo.Name, itemsSince, ct);
        var newestItemUtc = itemsSince;

        foreach (var item in items)
        {
            await log.AppendAsync(new ItemChanged(
                repo.Owner, repo.Name, item.Number, item.IsPullRequest,
                item.Title, item.Body, item.State), uow, ct);
            await log.AppendAsync(new LabelsChanged(
                repo.Owner, repo.Name, item.Number, item.Labels), uow, ct);
            if (item.State == "closed")
                await log.AppendAsync(new ItemClosed(repo.Owner, repo.Name, item.Number), uow, ct);
            newestItemUtc = newestItemUtc > item.UpdatedAtUtc ? newestItemUtc : item.UpdatedAtUtc;

            if (!item.IsPullRequest)
                continue;

            if (await github.GetPullHeadShaAsync(repo.Owner, repo.Name, item.Number, ct) is { } sha)
                await log.AppendAsync(new PrUpdated(repo.Owner, repo.Name, item.Number, sha), uow, ct);

            var reviews = await github.ListReviewsSinceAsync(repo.Owner, repo.Name, item.Number, reviewsSince, ct);
            foreach (var review in reviews)
            {
                await log.AppendAsync(new ReviewAdded(
                    repo.Owner, repo.Name, item.Number, review.Id,
                    review.Author, review.Verdict, review.Body), uow, ct);
            }

            if (reviews.Count > 0)
                await cursors.SetAsync(repoKey, "reviews", Watermark(reviews.Max(r => r.CreatedAtUtc)), uow, ct);
        }

        if (items.Count > 0)
            await cursors.SetAsync(repoKey, "items", Watermark(newestItemUtc), uow, ct);

        await uow.CommitAsync(ct);
    }

    /// <summary>
    /// The since-anchor of a stream: the stored watermark, rewound for clock skew. With no
    /// cursor yet the sync starts from NOW — the backlog is not replayed, only live
    /// activity is reacted to (the orchestrator's history is the repo's, not the log's).
    /// </summary>
    private async Task<DateTime> PollSinceAsync(string repoKey, string stream, IEventTransaction uow, CancellationToken ct)
    {
        var raw = await cursors.GetAsync(repoKey, stream, ct);
        if (raw is null)
            return DateTime.UtcNow;

        var watermark = new DateTime(long.Parse(raw), DateTimeKind.Utc);
        return watermark - Overlap;
    }

    private static string Watermark(DateTime utc) => utc.Ticks.ToString();
}
