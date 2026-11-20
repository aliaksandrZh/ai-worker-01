using Microsoft.EntityFrameworkCore;
using PiAgent.Infrastructure.Persistence;
using PiAgent.Platform.Workers;
using PiAgent.Platform.Http;

namespace PiAgent.Poller;

/// <summary>
/// Deleted-comment daily sweep (decision 77, task 05): a separate full listing compared against stored
/// IDs — all conversation comments via `issues/comments`, inline comments per OPEN PR via
/// `pulls/{n}/comments`. A stored ID missing from a listing is re-checked by direct request; ONLY a
/// confirmed 404 creates a tombstone, 403/rate-limit/network errors never mean deletion (a confirmed
/// deletion and an ambiguous outcome are separate facts). Tombstones and their
/// `issue_comment_deleted`/`review_comment_deleted` events (`source = full_reconciliation`) apply in ONE
/// transaction, together with the run-state bookkeeping — so a failed sweep persists nothing and is
/// retried whole on the next due cycle.
///
/// Own run state (decision 77): the `deleted_comment_sweep` sync_cursor row gates the daily cadence
/// via LastSuccessAt; its `cursor_value` stays NULL. It never reads or advances the six incremental
/// stream cursors. Listings use no ETags: an append-mostly listing can leave a page-1 etag stale after
/// deletions/deep-page changes, and a wrongly-304'd sweep would silently miss deletions.
/// </summary>
public sealed class DeletedCommentSweepStream : IRepositoryStream
{
    /// <summary>Daily cadence per decision 77 (hardcoded default, approved D4 — test-overridable
    /// via cursor timestamps; no system.yaml knob, §16 untouched).</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromHours(24);

    public string StreamName => SyncCursorStreams.DeletedCommentSweep;

    public async Task<StreamResult> SyncAsync(StreamRun run, CancellationToken cancellationToken)
    {
        var cursor = await SyncCursors.GetOrCreateAsync(run.Db, run.Repository.Id, StreamName, cancellationToken);
        if (cursor.LastSuccessAt is { } lastSuccess && DateTime.UtcNow - lastSuccess < Interval)
            return new StreamResult(StreamResultKind.NotModified); // due gate: no requests, no bookkeeping

        try
        {
            // ---- full listings (read-only; open PRs only for inline comments, decision 77) ----
            var listedConversation = await ListIdsAsync(run,
                $"repos/{run.Repository.Owner}/{run.Repository.Name}/issues/comments?per_page={run.Options.PerPage}",
                cancellationToken);
            var listedInline = new HashSet<long>();
            var openPrs = await run.Db.WorkItems.AsNoTracking()
                .OrderBy(w => w.Number)
                .Where(w => w.RepositoryId == run.Repository.Id
                            && w.Type == WorkItemTypes.PullRequest
                            && w.State == "open")
                .Select(w => w.Number)
                .ToListAsync(cancellationToken);
            foreach (var pr in openPrs)
                listedInline.UnionWith(await ListIdsAsync(run,
                    $"repos/{run.Repository.Owner}/{run.Repository.Name}/pulls/{pr}/comments?per_page={run.Options.PerPage}",
                    cancellationToken));

            // ---- diff against stored rows ----
            var workItems = await run.Db.WorkItems.AsNoTracking()
                .Where(w => w.RepositoryId == run.Repository.Id)
                .Select(w => new { w.Id, w.Number })
                .ToDictionaryAsync(w => w.Id, w => w.Number, cancellationToken);
            var stored = await run.Db.GitHubComments.AsTracking()
                .Where(c => workItems.Keys.Contains(c.WorkItemId))
                .ToListAsync(cancellationToken);

            var missing = new List<GitHubComment>();
            foreach (var row in stored)
            {
                var listed = row.Kind == GitHubCommentKinds.IssueComment
                    ? listedConversation.Contains(row.GithubCommentId)
                    : listedInline.Contains(row.GithubCommentId);
                if (!listed)
                    missing.Add(row); // absent from the full listing — candidate, never tombstoned on listing alone
            }

            // ---- direct re-checks: a confirmed 404 tombstones; everything else is ambiguous ----
            var tombstones = new List<GitHubComment>();
            foreach (var row in missing)
            {
                var path = row.Kind == GitHubCommentKinds.IssueComment
                    ? $"repos/{run.Repository.Owner}/{run.Repository.Name}/issues/comments/{row.GithubCommentId}"
                    : $"repos/{run.Repository.Owner}/{run.Repository.Name}/pulls/comments/{row.GithubCommentId}";
                var single = await run.Client.GetSingleAsync(path, cancellationToken);
                if (single.Found)
                    continue; // still there — listing/pagination artifact, no tombstone
                tombstones.Add(row);
            }

            // ---- one transaction: tombstones + events + run-state success ----
            await using var transaction = await run.Db.Database.BeginTransactionAsync(cancellationToken);
            try
            {
                var now = DateTime.UtcNow;
                foreach (var row in tombstones)
                {
                    row.IsDeleted = true;
                    row.DeletedAt = now;
                    row.LastSyncedAt = now;
                    await EventWriter.WriteAsync(run.Db, run.Repository.Id, row.WorkItemId,
                    [
                        new Detection(
                            row.Kind == GitHubCommentKinds.IssueComment
                                ? GitHubEventTypes.IssueCommentDeleted
                                : GitHubEventTypes.ReviewCommentDeleted,
                            GitHubEventSources.FullReconciliation,
                            $"{CommentsPageSync.KeyPrefix(row.Kind)}:{row.GithubCommentId}:deleted",
                            new CommentDeletedEventPayload(row.GithubCommentId,
                                workItems[row.WorkItemId], row.AuthorLogin),
                            now),
                    ], cancellationToken);
                }
                SyncCursors.RecordSuccess(cursor, null);
                await run.Db.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
            }
            catch
            {
                await transaction.RollbackAsync(cancellationToken);
                throw;
            }

            return new StreamResult(tombstones.Count > 0 ? StreamResultKind.Completed : StreamResultKind.NotModified);
        }
        catch (GitHubRateLimitedException)
        {
            throw; // the cycle must stop for the whole run — nothing persisted (decision 53/77)
        }
        catch (Exception exception)
        {
            return await SyncCursors.RecordFailureAsync(run.Db, run.Repository.Id, StreamName, exception, cancellationToken);
        }
    }

    /// <summary>Walks every page of a full listing collecting resource ids — no ETags (rationale in
    /// the class doc); `Link`-header paging like the other streams.</summary>
    private static async Task<HashSet<long>> ListIdsAsync(StreamRun run, string firstPath,
        CancellationToken cancellationToken)
    {
        var ids = new HashSet<long>();
        var requestPath = firstPath;
        while (true)
        {
            var page = await run.Client.GetPageAsync(requestPath, null, cancellationToken);
            foreach (var element in page.Items)
                ids.Add(element.GetProperty("id").GetInt64());
            if (page.NextPageUri is not { } nextPage)
                break;
            requestPath = nextPage.PathAndQuery;
        }
        return ids;
    }
}
