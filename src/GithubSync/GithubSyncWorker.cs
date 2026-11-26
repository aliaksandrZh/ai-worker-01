using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PiAgent.Events;

namespace PiAgent.GithubSync;

/// <summary>
/// The sync adapter (docs/github-sync.md): polls the configured repositories and appends sync
/// events. Detection carries no state of its own — no cursors, no time windows, no overlap
/// floors, no anchoring rules. Each cycle re-reads the whole working set — page 1 of the repo's
/// items, newest update first — and appends content-fingerprinted events: an unchanged item
/// re-detects to the same eventId and the EventLog's unique (type, eventId) index silently drops
/// the repeat, so the fingerprint IS the cursor. Only open items are synced: state=open gets the
/// full derivation (item, PR sha, reviews, comments, commands); a closed item — out of the
/// working set — gets only its ItemClosed terminal beat while it lingers on page 1. One instance
/// only: there is no cursor-claim machinery to distribute repositories across instances.
///
/// The event building itself is Derivation (pure); this worker is the walking half — API reads,
/// the one permission lookup, and the appends. Appends are fire-and-commit, no unit of work
/// around the cycle: a cycle is not atomic and doesn't need to be — a half-done cycle is
/// re-detected next cycle, and the fingerprint dedup drops the repeats. (The transaction was the
/// cursor era's requirement: cursor advance atomic with its events. There is no cursor.) Holding
/// one open across the cycle's HTTP reads would idle a connection in-transaction for its whole
/// duration.
/// </summary>
public sealed class GithubSyncWorker(
    SyncConfig config,
    IGithubReadApi github,
    IEventLog log,
    ILogger<GithubSyncWorker> logger) : BackgroundService
{
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
        var itemsProcessed = 0;
        var commentsProcessed = 0;

        foreach (var item in await github.ListItemsAsync(repo.Owner, repo.Name, ct))
        {
            var (changed, closed) = Derivation.ItemEvents(repo.Owner, repo.Name, item);

            if (closed is { } beat)
            {
                await log.AppendAsync(beat, ct);
                continue;
            }

            itemsProcessed++;
            await log.AppendAsync(changed!, ct);

            if (item.IsPullRequest)
            {
                if (await github.GetPullHeadShaAsync(repo.Owner, repo.Name, item.Number, ct) is { } sha)
                    await log.AppendAsync(
                        Derivation.PrEvent(repo.Owner, repo.Name, item.Number, item.UpdatedAtUtc, sha)!, ct);

                foreach (var review in await github.ListReviewsAsync(repo.Owner, repo.Name, item.Number, ct))
                    await log.AppendAsync(
                        Derivation.ReviewEvent(repo.Owner, repo.Name, item.Number, review)!, ct);
            }

            foreach (var comment in await github.ListCommentsAsync(repo.Owner, repo.Name, item.Number, ct))
            {
                commentsProcessed++;
                var derived = Derivation.CommentEvents(repo.Owner, repo.Name, item.Number, comment, config.BotLogin);
                await log.AppendAsync(derived.Comment, ct);

                foreach (var decision in derived.Decisions)
                    await log.AppendAsync(decision, ct);

                foreach (var draft in derived.Commands)
                {
                    var permission = await github.GetPermissionAsync(repo.Owner, repo.Name, draft.Author, ct) ?? "read";
                    await log.AppendAsync(draft.Build(permission), ct);
                }
            }
        }

        logger.LogInformation("sync {repo} cycle: {comments} comment(s), {items} item(s) processed",
            repoKey, commentsProcessed, itemsProcessed);
    }
}
