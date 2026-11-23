using PiAgent.Poller.GitHub;
using Microsoft.EntityFrameworkCore;
using PiAgent.Infrastructure.Persistence;
using PiAgent.Platform.Http;

namespace PiAgent.Poller.Streams;

/// <summary>
/// `pr_reviews` stream (decision 53): one conditional `GET /repos/{o}/{r}/pulls/{n}/reviews` per
/// currently-open PR, each with the PR's stored ETag — a 304 means nothing changed and costs nothing
/// against the primary limit (GitHub-side; we simply skip the PR, keeping its etag). 200 responses are
/// persisted per PR in one transaction (rows upserted by `github_review_id`, transition events written);
/// the accumulated etag map is saved only after every PR processed, so failures re-fetch cleanly next
/// cycle (row upserts + `rv:{id}:…` fingerprint events keep that idempotent).
/// </summary>
public sealed class PrReviewsStream : IRepositoryStream
{
    public string StreamName => SyncCursorStreams.PrReviews;

    public async Task<StreamResult> SyncAsync(StreamRun run, CancellationToken cancellationToken)
    {
        var cursor = await SyncCursors.GetOrCreateAsync(run.Db, run.Repository.Id, StreamName, cancellationToken);
        var cursorData = PrReviewCursor.TryParse(cursor.CursorValue);
        try
        {
            var etags = cursorData?.Etags.ToDictionary(kv => kv.Key, kv => kv.Value) ?? new Dictionary<long, string>();
            // open PRs only: reviews on closed/merged PRs were synced before closure; decision 75 pause still polls
            var openPrs = await run.Db.WorkItems
                .OrderBy(w => w.Number)
                .Where(w => w.RepositoryId == run.Repository.Id
                            && w.Type == WorkItemTypes.PullRequest
                            && w.State == "open")
                .Select(w => new { w.Id, w.Number })
                .ToListAsync(cancellationToken);

            var anyModified = false;
            foreach (var pr in openPrs)
            {
                etags.TryGetValue(pr.Number, out var etag);
                var page = await run.Client.GetPageAsync(
                    $"repos/{run.Repository.Owner}/{run.Repository.Name}/pulls/{pr.Number}/reviews?per_page={run.Options.PerPage}",
                    etag, cancellationToken);
                if (page.NotModified)
                    continue; // etag kept; primary limit untouched (decision 53)
                anyModified = true;
                await PersistAsync(run, pr.Id, pr.Number, page.Items, cancellationToken);
                if (page.Etag is { } nextEtag)
                    etags[pr.Number] = nextEtag;
            }

            SyncCursors.RecordSuccess(cursor, new PrReviewCursor(etags).Serialize());
            await run.Db.SaveChangesAsync(cancellationToken);
            return new StreamResult(anyModified ? StreamResultKind.Completed : StreamResultKind.NotModified);
        }
        catch (GitHubRateLimitedException)
        {
            throw; // the cycle must stop for the whole run — nothing advances (decision 53)
        }
        catch (Exception exception)
        {
            return await SyncCursors.RecordFailureAsync(run.Db, run.Repository.Id, StreamName, exception, cancellationToken);
        }
    }

    private static async Task PersistAsync(StreamRun run, Guid workItemId, long prNumber,
        IReadOnlyList<System.Text.Json.JsonElement> reviews, CancellationToken cancellationToken)
    {
        await using var transaction = await run.Db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            var now = DateTime.UtcNow;
            var reviewIds = reviews.Select(r => r.GetProperty("id").GetInt64()).ToList();
            var existing = await run.Db.GitHubReviews
                .Where(r => r.WorkItemId == workItemId && reviewIds.Contains(r.GithubReviewId))
                .ToDictionaryAsync(r => r.GithubReviewId, cancellationToken);

            foreach (var element in reviews)
            {
                var snapshot = GitHubMappers.MapReview(element);
                if (existing.TryGetValue(snapshot.ReviewId, out var row))
                {
                    if (row.State == snapshot.State)
                        continue;
                    if (snapshot.State == GitHubReviewStates.Dismissed)
                    {
                        row.State = GitHubReviewStates.Dismissed;
                        row.LastSyncedAt = now;
                        await EventWriter.WriteAsync(run.Db, run.Repository.Id, workItemId,
                        [
                            new Detection(GitHubEventTypes.PullRequestReviewDismissed, GitHubEventSources.RestResourceSync,
                                $"rv:{snapshot.ReviewId}:dismissed",
                                new ReviewDismissedEventPayload(snapshot.ReviewId, prNumber, snapshot.State, snapshot.AuthorLogin),
                                snapshot.SubmittedAt ?? now),
                        ], cancellationToken);
                    }
                    else if (row.State == GitHubReviewStates.Pending && IsSubmitted(snapshot.State))
                    {
                        row.State = snapshot.State;
                        row.LastSyncedAt = now;
                        row.SubmittedAtGithub = snapshot.SubmittedAt;
                        row.Body = snapshot.Body ?? "";
                        row.CommitSha = snapshot.CommitSha;
                        await EventWriter.WriteAsync(run.Db, run.Repository.Id, workItemId,
                        [
                            new Detection(GitHubEventTypes.PullRequestReviewSubmitted, GitHubEventSources.RestResourceSync,
                                $"rv:{snapshot.ReviewId}:submitted",
                                new ReviewSubmittedEventPayload(snapshot.ReviewId, prNumber, snapshot.State, snapshot.AuthorLogin),
                                snapshot.SubmittedAt ?? now),
                        ], cancellationToken);
                    }
                    // transitions among submitted states do not occur on GitHub; if one ever arrives,
                    // the row updates quietly
                }
                else
                {
                    row = new GitHubReview
                    {
                        Id = Guid.NewGuid(),
                        WorkItemId = workItemId,
                        GithubReviewId = snapshot.ReviewId,
                        AuthorLogin = snapshot.AuthorLogin ?? "unknown",
                        State = snapshot.State,
                        Body = snapshot.Body ?? "",
                        CommitSha = snapshot.CommitSha,
                        SubmittedAtGithub = snapshot.SubmittedAt,
                        LastSyncedAt = now,
                    };
                    run.Db.GitHubReviews.Add(row);
                    if (snapshot.State == GitHubReviewStates.Dismissed)
                    {
                        await EventWriter.WriteAsync(run.Db, run.Repository.Id, workItemId,
                        [
                            new Detection(GitHubEventTypes.PullRequestReviewDismissed, GitHubEventSources.RestResourceSync,
                                $"rv:{snapshot.ReviewId}:dismissed",
                                new ReviewDismissedEventPayload(snapshot.ReviewId, prNumber, snapshot.State, snapshot.AuthorLogin),
                                snapshot.SubmittedAt ?? now),
                        ], cancellationToken);
                    }
                    else if (IsSubmitted(snapshot.State))
                    {
                        await EventWriter.WriteAsync(run.Db, run.Repository.Id, workItemId,
                        [
                            new Detection(GitHubEventTypes.PullRequestReviewSubmitted, GitHubEventSources.RestResourceSync,
                                $"rv:{snapshot.ReviewId}:submitted",
                                new ReviewSubmittedEventPayload(snapshot.ReviewId, prNumber, snapshot.State, snapshot.AuthorLogin),
                                snapshot.SubmittedAt ?? now),
                        ], cancellationToken);
                    }
                    // pending first-seen: no event (02 §6 — pending visibility is author-scoped; the
                    // submitted transition emits later)
                }
            }

            await run.Db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    private static bool IsSubmitted(string state) =>
        state is GitHubReviewStates.Commented or GitHubReviewStates.Approved or GitHubReviewStates.ChangesRequested;
}
