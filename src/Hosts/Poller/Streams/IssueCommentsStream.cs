using PiAgent.Poller.GitHub;
using PiAgent.Infrastructure.Persistence;

namespace PiAgent.Poller.Streams;

/// <summary>`issue_comments` stream: repo-wide `GET /repos/{o}/{r}/issues/comments` —
/// conversation comments on Issues AND PRs; work-item resolution + boundary holdback in CommentsPageSync.</summary>
public sealed class IssueCommentsStream : IRepositoryStream
{
    public string StreamName => SyncCursorStreams.IssueComments;

    public Task<StreamResult> SyncAsync(StreamRun run, CancellationToken cancellationToken) =>
        CommentsPageSync.SyncAsync(run, StreamName,
            $"repos/{run.Repository.Owner}/{run.Repository.Name}/issues/comments?sort=updated&direction=desc&per_page={run.Options.PerPage}",
            GitHubMappers.MapIssueComment, cancellationToken);
}
