using PiAgent.Poller.GitHub;
using PiAgent.Infrastructure.Persistence;
using PiAgent.Platform.Http;

namespace PiAgent.Poller.Streams;

/// <summary>
/// `pulls` stream (decision 53): repo-wide `GET /repos/{o}/{r}/pulls?state=all&amp;sort=updated&amp;direction=desc`
/// (state=all — closed/merged PRs must be caught on their updated_at bump; there is no `since` there), so
/// paging stops client-side: the pass walks pages newest-first while items are newer than `position −
/// overlap`, filtering partial pages. One DB transaction per page — cursor advances only after durable save.
/// </summary>
public sealed class PullRequestsStream : IRepositoryStream
{
    public string StreamName => SyncCursorStreams.Pulls;

    public async Task<StreamResult> SyncAsync(StreamRun run, CancellationToken cancellationToken)
    {
        var cursor = await SyncCursors.GetOrCreateAsync(run.Db, run.Repository.Id, StreamName, cancellationToken);
        var cursorData = TimestampCursor.TryParse(cursor.CursorValue);
        try
        {
            var position = cursorData?.Position;
            var stopBelow = position is { } at ? (DateTime?)at - run.Options.Overlap : null;
            var requestPath =
                $"repos/{run.Repository.Owner}/{run.Repository.Name}/pulls?state=all&sort=updated&direction=desc&per_page={run.Options.PerPage}";

            while (true)
            {
                var page = await run.Client.GetPageAsync(requestPath, cursorData?.Etag, cancellationToken);
                if (page.NotModified)
                    return await IssuesStream.FinishNotModifiedAsync(run, cursor, cancellationToken);

                var pageItems = page.Items.Select(GitHubMappers.MapPullRequest).ToList();
                if (stopBelow is { } stop && pageItems.Count > 0 && pageItems.All(i => i.UpdatedAt < stop))
                    break; // page (and every later one) is older than the overlap floor
                var fresh = stopBelow is { } floor
                    ? pageItems.Where(i => i.UpdatedAt >= floor).ToList()
                    : pageItems;
                position = await WorkItemPageSync.PersistAsync(run, cursor, fresh, position, null, cancellationToken);

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
            return await SyncCursors.RecordFailureAsync(run.Db, run.Repository.Id, StreamName, exception, cancellationToken);
        }
    }
}
