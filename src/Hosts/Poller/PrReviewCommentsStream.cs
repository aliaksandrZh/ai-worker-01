using PiAgent.Infrastructure.Persistence;

namespace PiAgent.Poller;

/// <summary>`pr_review_comments` stream (decision 53): repo-wide `GET /repos/{o}/{r}/pulls/comments` —
/// inline PR review comments (the per-PR listing path stays reserved for the deleted-comment sweep,
/// ticket 05); work-item resolution + boundary holdback in CommentsPageSync.</summary>
public sealed class PrReviewCommentsStream : IRepositoryStream
{
    public string StreamName => SyncCursorStreams.PrReviewComments;

    public Task<StreamResult> SyncAsync(StreamRun run, CancellationToken cancellationToken) =>
        CommentsPageSync.SyncAsync(run, StreamName,
            $"repos/{run.Repository.Owner}/{run.Repository.Name}/pulls/comments?sort=updated&direction=desc&per_page={run.Options.PerPage}",
            GitHubMappers.MapReviewComment, cancellationToken);
}
