using PiAgent.Poller.GitHub;
using PiAgent.Infrastructure.Persistence;

namespace PiAgent.Poller.Streams;

/// <summary>`pr_review_comments` stream: repo-wide `GET /repos/{o}/{r}/pulls/comments` —
/// inline PR review comments (the per-PR listing path stays reserved for the deleted-comment sweep);
/// work-item resolution + boundary holdback in CommentsPageSync.</summary>
public sealed class PrReviewCommentsStream : IRepositoryStream
{
    public string StreamName => SyncCursorStreams.PrReviewComments;

    public Task<StreamResult> SyncAsync(StreamRun run, CancellationToken cancellationToken) =>
        CommentsPageSync.SyncAsync(run, StreamName,
            $"repos/{run.Repository.Owner}/{run.Repository.Name}/pulls/comments?sort=updated&direction=desc&per_page={run.Options.PerPage}",
            GitHubMappers.MapReviewComment, cancellationToken);
}
