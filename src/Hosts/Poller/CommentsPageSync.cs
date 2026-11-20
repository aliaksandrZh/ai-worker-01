using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PiAgent.Infrastructure.Persistence;
using PiAgent.Platform.Http;

namespace PiAgent.Poller;

/// <summary>
/// Shared sync for the two comment streams (issue_comments, pr_review_comments — same resource family
/// per 02 §7/decision 53): repo-wide `sort=updated&direction=desc` listings, page size 100 via `Link`
/// headers, one DB transaction per page (cursor advances only after durable save).
///
/// Work-item resolution: comments reference their issue/PR by number; a comment whose WorkItem is not
/// yet in the store is skipped this pass and recorded as a BOUNDARY — the cursor position is capped at
/// the boundary (the pass continues, all resolvable items are always processed), so the next overlap
/// window re-fetches every boundary and never loses a comment (Review Focus 1). Position rule:
/// boundaries exist → min(previous, oldest boundary); otherwise max(previous, page max) — monotonic,
/// crash-safe per committed page.
/// </summary>
public static class CommentsPageSync
{
    public static async Task<StreamResult> SyncAsync(StreamRun run, string streamName, string firstPath,
        Func<System.Text.Json.JsonElement, CommentSnapshot> map, CancellationToken cancellationToken)
    {
        var cursor = await SyncCursors.GetOrCreateAsync(run.Db, run.Repository.Id, streamName, cancellationToken);
        var cursorData = TimestampCursor.TryParse(cursor.CursorValue);
        try
        {
            var position = cursorData?.Position;
            var stopBelow = position is { } at ? at - run.Options.Overlap : (DateTime?)null;
            bool boundarySeen = false;
            var minBoundary = DateTime.MaxValue;
            var requestPath = firstPath;

            while (true)
            {
                var page = await run.Client.GetPageAsync(requestPath, null, cancellationToken);
                if (page.NotModified)
                    return await IssuesStream.FinishNotModifiedAsync(run, cursor, cancellationToken);

                var snapshots = page.Items.Select(map).ToList();
                if (stopBelow is { } stop && snapshots.Count > 0 && snapshots.All(s => s.UpdatedAt < stop))
                    break; // page (and later ones) entirely below the overlap floor

                (position, boundarySeen, minBoundary) = await PersistPageAsync(
                    run, cursor, snapshots, position, stopBelow, boundarySeen, minBoundary, cancellationToken);

                if (page.NextPageUri is not { } nextPage)
                    break;
                requestPath = nextPage.PathAndQuery;
            }
            return new StreamResult(StreamResultKind.Completed);
        }
        catch (GitHubRateLimitedException)
        {
            throw; // the cycle must stop for the whole run — nothing advances (decision 53)
        }
        catch (Exception exception)
        {
            return await SyncCursors.RecordFailureAsync(run.Db, run.Repository.Id, streamName, exception, cancellationToken);
        }
    }

    /// <summary>One page in one transaction: resolve, upsert + events for resolvable items, boundary
    /// bookkeeping for unresolvable ones, cursor advance, commit.</summary>
    private static async Task<(DateTime? Position, bool BoundarySeen, DateTime MinBoundary)> PersistPageAsync(
        StreamRun run, SyncCursor cursor, IReadOnlyList<CommentSnapshot> snapshots, DateTime? position,
        DateTime? stopBelow, bool boundarySeen, DateTime minBoundary, CancellationToken cancellationToken)
    {
        await using var transaction = await run.Db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            var now = DateTime.UtcNow;
            var numbers = snapshots.Select(s => s.IssueNumber).Where(n => n > 0).Distinct().ToList();
            var workItems = await run.Db.WorkItems
                .Where(w => w.RepositoryId == run.Repository.Id && numbers.Contains(w.Number))
                .ToDictionaryAsync(w => w.Number, w => w.Id, cancellationToken);
            var commentIds = snapshots.Select(s => s.CommentId).ToList();
            var existing = commentIds.Count == 0
                ? new Dictionary<long, GitHubComment>()
                : await run.Db.GitHubComments
                    .Where(c => c.Kind == snapshots[0].Kind && commentIds.Contains(c.GithubCommentId))
                    .ToDictionaryAsync(c => c.GithubCommentId, cancellationToken);

            var pageMax = DateTime.MinValue;
            bool pageBoundary = false;
            var pageMinBoundary = DateTime.MaxValue;

            foreach (var snapshot in snapshots)
            {
                if (stopBelow is { } stop && snapshot.UpdatedAt < stop)
                    continue; // below the overlap floor — processed in an earlier pass
                if (pageMax < snapshot.UpdatedAt)
                    pageMax = snapshot.UpdatedAt;

                if (!workItems.TryGetValue(snapshot.IssueNumber, out var workItemId))
                {
                    // Review Focus 1: never skip past an unresolved comment — record a boundary;
                    // the position caps there and the next window re-fetches it
                    pageBoundary = true;
                    if (pageMinBoundary > snapshot.UpdatedAt)
                        pageMinBoundary = snapshot.UpdatedAt;
                    continue;
                }

                if (existing.TryGetValue(snapshot.CommentId, out var row))
                {
                    if (snapshot.UpdatedAt <= row.UpdatedAtGithub)
                        continue; // same or older than stored — nothing to sync
                    row.Body = snapshot.Body;
                    row.Path = snapshot.Path;
                    row.CommitSha = snapshot.CommitSha;
                    row.DiffPosition = snapshot.DiffPosition;
                    row.InReplyToCommentId = snapshot.InReplyToId;
                    row.GithubReviewId = snapshot.GithubReviewId;
                    row.UpdatedAtGithub = snapshot.UpdatedAt;
                    row.LastSyncedAt = now;
                    await EventWriter.WriteAsync(run.Db, run.Repository.Id, row.WorkItemId,
                        [new Detection(CommentUpdatedOf(row.Kind), GitHubEventSources.RestResourceSync,
                            CommentKey(row.Kind, row.GithubCommentId, "upd", row.UpdatedAtGithub, row.Body),
                            new CommentUpdatedEventPayload(row.GithubCommentId, snapshot.IssueNumber, row.AuthorLogin),
                            snapshot.UpdatedAt)], cancellationToken);
                }
                else
                {
                    row = new GitHubComment
                    {
                        Id = Guid.NewGuid(),
                        WorkItemId = workItemId,
                        GithubCommentId = snapshot.CommentId,
                        Kind = snapshot.Kind,
                        AuthorLogin = snapshot.AuthorLogin,
                        Body = snapshot.Body,
                        CreatedAtGithub = snapshot.CreatedAt,
                        UpdatedAtGithub = snapshot.UpdatedAt,
                        InReplyToCommentId = snapshot.InReplyToId,
                        GithubReviewId = snapshot.GithubReviewId,
                        Path = snapshot.Path,
                        CommitSha = snapshot.CommitSha,
                        DiffPosition = snapshot.DiffPosition,
                        LastSyncedAt = now,
                    };
                    run.Db.GitHubComments.Add(row);
                    await EventWriter.WriteAsync(run.Db, run.Repository.Id, workItemId,
                        [new Detection(CommentCreatedOf(row.Kind), GitHubEventSources.RestResourceSync,
                            CommentKey(row.Kind, row.GithubCommentId, "created", null, null),
                            new CommentCreatedEventPayload(row.GithubCommentId, snapshot.IssueNumber, row.AuthorLogin),
                            snapshot.CreatedAt)], cancellationToken);
                }
            }

            boundarySeen |= pageBoundary;
            if (pageBoundary && pageMinBoundary < minBoundary)
                minBoundary = pageMinBoundary;

            var newPosition = boundarySeen
                ? position is { } previous ? Min(previous, minBoundary) : minBoundary
                : stopBelow is null
                    ? pageMax > DateTime.MinValue ? pageMax : position
                    : position is { } current ? Max(current, pageMax) : position;

            SyncCursors.RecordSuccess(cursor, newPosition is null ? cursor.CursorValue : new TimestampCursor(newPosition).Serialize());
            await run.Db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return (newPosition, boundarySeen, minBoundary);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    /// <summary>`ic:{id}:created` | `rc:{id}:created` | `ic:{id}:upd:{unix}:{sha8(body)}` — deterministic
    /// 02 §7 fingerprints for comment events (edits within the same second still differ by content).</summary>
    public static string CommentKey(string kind, long commentId, string change, DateTime? at, string? body) =>
        at is null
            ? $"{KeyPrefix(kind)}:{commentId}:{change}"
            : $"{KeyPrefix(kind)}:{commentId}:upd:{new DateTimeOffset(at.Value, TimeSpan.Zero).ToUnixTimeSeconds()}:{Sha8(body ?? "")}";

    public static string KeyPrefix(string kind) =>
        kind == GitHubCommentKinds.IssueComment ? "ic" : "rc";

    private static string CommentCreatedOf(string kind) =>
        kind == GitHubCommentKinds.IssueComment ? GitHubEventTypes.IssueCommentCreated : GitHubEventTypes.ReviewCommentCreated;

    private static string CommentUpdatedOf(string kind) =>
        kind == GitHubCommentKinds.IssueComment ? GitHubEventTypes.IssueCommentUpdated : GitHubEventTypes.ReviewCommentUpdated;

    private static string Sha8(string value)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes)[..8].ToLowerInvariant();
    }

    private static DateTime Min(DateTime left, DateTime right) => left < right ? left : right;
    private static DateTime Max(DateTime left, DateTime right) => left > right ? left : right;
}
