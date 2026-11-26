using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Security.Cryptography;
using System.Text;
using PiAgent.Events;

namespace PiAgent.GithubSync;

/// <summary>
/// The sync adapter worker (docs/github-sync.md): polls the configured repositories and
/// appends sync events — the mechanics are the proven main-poller design adapted to the
/// event-log world. No `since` parameter (GitHub's is inclusive and second-precision —
/// a watermark + since pairing re-fetched the same record every cycle forever): each stream
/// fetches a newest-first page, walks it down to the overlap floor, and dedups by
/// CONTENT-FINGERPRINTED EventId — a re-fetched identical change maps to the same eventId
/// and the EventLog's unique (type, eventId) index silently drops it. The cursor POSITION
/// is monotonic: `max(previous, max(updated seen))`, capped at any unresolvable record
/// (never skip past one — the next window re-fetches it). With no cursor yet the stream
/// anchors ≈NOW (a fresh repo's backlog is not replayed; the orchestrator's history is the
/// repo's, not the log's) — except comments, which anchor at the item watermark: an item
/// updated during downtime witnesses a command typed while the plane was down.
/// </summary>
public sealed class GithubSyncWorker(
    SyncConfig config,
    IGithubReadApi github,
    IEventLog log,
    ICursorStore cursors,
    ILogger<GithubSyncWorker> logger) : BackgroundService
{
    private static readonly TimeSpan Overlap = TimeSpan.FromMinutes(5);
    private DateTime _startedUtc = DateTime.UtcNow;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _startedUtc = DateTime.UtcNow;
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

        // ---- items first: their watermark anchors the comments' fresh start, and item_changed
        // precedes comment_changed on the same item stream. ----
        var itemsPosition = await PollPositionAsync(repoKey, "items", ct);
        var itemsFloor = (itemsPosition ?? _startedUtc) - Overlap;
        var items = await github.ListItemsAsync(repo.Owner, repo.Name, ct);
        var itemsPositionNew = itemsPosition ?? _startedUtc;

        foreach (var item in items)
        {
            if (item.UpdatedAtUtc < itemsFloor)
                break; // newest-first: everything below the floor was seen in an earlier pass

            var at = Unix(item.UpdatedAtUtc);
            var itemId = $"item:{item.Number}:{at}";
            await log.AppendAsync(new ItemChanged(
                repo.Owner, repo.Name, item.Number, item.IsPullRequest,
                item.Title, item.Body, item.State, EventId: itemId), uow, ct);
            await log.AppendAsync(new LabelsChanged(
                repo.Owner, repo.Name, item.Number, item.Labels,
                EventId: $"{itemId}:labels:{Sha8(string.Join(',', item.Labels))}"), uow, ct);
            if (item.State == "closed")
                await log.AppendAsync(new ItemClosed(
                    repo.Owner, repo.Name, item.Number, EventId: $"{itemId}:closed"), uow, ct);
            if (itemsPositionNew < item.UpdatedAtUtc)
                itemsPositionNew = item.UpdatedAtUtc;

            if (!item.IsPullRequest)
                continue;

            if (await github.GetPullHeadShaAsync(repo.Owner, repo.Name, item.Number, ct) is { } sha)
                await log.AppendAsync(new PrUpdated(
                    repo.Owner, repo.Name, item.Number, sha,
                    EventId: $"pr:{item.Number}:{at}:{sha[..Math.Min(12, sha.Length)]}"), uow, ct);

            var reviewsPosition = await PollPositionAsync(repoKey, $"reviews:{item.Number}", ct);
            var reviewsFloor = (reviewsPosition ?? _startedUtc) - Overlap;
            var reviews = await github.ListReviewsAsync(repo.Owner, repo.Name, item.Number, ct);
            foreach (var review in reviews)
            {
                if (review.CreatedAtUtc < reviewsFloor)
                    break; // reviews come chronological — below the floor, done
                await log.AppendAsync(new ReviewAdded(
                    repo.Owner, repo.Name, item.Number, review.Id,
                    review.Author, review.Verdict, review.Body,
                    EventId: $"review:{review.Id}"), uow, ct);
                if (reviewsPosition is null || reviewsPosition < review.CreatedAtUtc)
                    reviewsPosition = review.CreatedAtUtc;
            }

            if (reviewsPosition is { } rp && rp > (await PollPositionAsync(repoKey, $"reviews:{item.Number}", ct) ?? DateTime.MinValue))
                await cursors.SetAsync(repoKey, $"reviews:{item.Number}", Watermark(rp), uow, ct);
        }

        if (itemsPosition is null || itemsPositionNew > itemsPosition)
            await cursors.SetAsync(repoKey, "items", Watermark(itemsPositionNew), uow, ct);

        // ---- comments → comment_changed + derived commands/decisions. Fresh start anchors at
        // the item watermark (the downtime witness), everything else at ≈NOW. ----
        var commentsPosition = await PollPositionAsync(repoKey, "comments", ct);
        var commentsAnchor = commentsPosition ?? (await PollPositionAsync(repoKey, "items", ct) ?? _startedUtc);
        var commentsFloor = commentsAnchor - Overlap;
        var comments = await github.ListCommentsAsync(repo.Owner, repo.Name, ct);
        var commentsPositionNew = commentsPosition ?? _startedUtc;
        var minUnresolvable = DateTime.MaxValue;

        foreach (var comment in comments)
        {
            if (comment.UpdatedAtUtc < commentsFloor)
                break; // newest-first: below the floor was seen earlier

            if (comment.ItemNumber <= 0)
            {
                // main's boundary rule: never advance past an unresolvable comment — the next
                // window re-fetches it once it becomes resolvable
                if (minUnresolvable > comment.UpdatedAtUtc)
                    minUnresolvable = comment.UpdatedAtUtc;
                continue;
            }

            var at = Unix(comment.UpdatedAtUtc);
            await log.AppendAsync(new CommentChanged(
                repo.Owner, repo.Name, comment.ItemNumber, comment.Id, comment.Author, comment.Body,
                Deleted: false,
                EventId: $"comment:{comment.Id}:{at}:{Sha8(comment.Body)}"), uow, ct);

            if (comment.Author.Equals(config.BotLogin, StringComparison.Ordinal))
                continue; // the bot's own comments are noise

            foreach (var parsed in CommandGrammar.ParseAll(comment.Body, config.BotLogin))
            {
                switch (parsed.Command)
                {
                    // decisions at the edge (docs/flows events.md "user_decision"): plan approval
                    // / changes requested — the Orchestrator consumes the typed event
                    case "approve":
                        await log.AppendAsync(new UserDecision(DecisionKind.PlanApproved,
                            repo.Owner, repo.Name, comment.ItemNumber, comment.Author,
                            parsed.TrailingContext,
                            EventId: $"decision:{comment.Id}:{at}:{Sha8(comment.Body)}"), uow, ct);
                        break;
                    case "changes":
                        await log.AppendAsync(new UserDecision(DecisionKind.PlanChangesRequested,
                            repo.Owner, repo.Name, comment.ItemNumber, comment.Author,
                            parsed.TrailingContext,
                            EventId: $"decision:{comment.Id}:{at}:{Sha8(comment.Body)}"), uow, ct);
                        break;
                    default:
                        var permission = await github.GetPermissionAsync(repo.Owner, repo.Name, comment.Author, ct) ?? "read";
                        await log.AppendAsync(new UserCommand(
                            parsed.Command, repo.Owner, repo.Name, comment.ItemNumber, comment.Id,
                            comment.Author, permission, parsed.TrailingContext,
                            EventId: $"command:{comment.Id}:{at}:{parsed.Command}:{Sha8(comment.Body)}"), uow, ct);
                        break;
                }
            }

            if (commentsPositionNew < comment.UpdatedAtUtc)
                commentsPositionNew = comment.UpdatedAtUtc;
        }

        if (minUnresolvable < DateTime.MaxValue && minUnresolvable < commentsPositionNew)
            commentsPositionNew = minUnresolvable; // the boundary cap
        if (commentsPosition is null || commentsPositionNew > commentsPosition)
            await cursors.SetAsync(repoKey, "comments", Watermark(commentsPositionNew), uow, ct);

        await uow.CommitAsync(ct);
        logger.LogInformation("sync {repo} cycle: {comments} comment(s), {items} item(s), position {position:O}",
            repoKey, comments.Count, items.Count, commentsPositionNew);
    }

    /// <summary>The stored POSITION (the newest updated_at already detected), or null.</summary>
    private async Task<DateTime?> PollPositionAsync(string repoKey, string stream, CancellationToken ct)
    {
        var raw = await cursors.GetAsync(repoKey, stream, ct);
        return raw is null ? null : new DateTime(long.Parse(raw), DateTimeKind.Utc);
    }

    private static string Watermark(DateTime utc) => utc.Ticks.ToString();
    private static long Unix(DateTime utc) => new DateTimeOffset(utc, TimeSpan.Zero).ToUnixTimeSeconds();

    /// <summary>main's Sha8: a content fingerprint that distinguishes edits within the same second.</summary>
    private static string Sha8(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..8].ToLowerInvariant();
}
