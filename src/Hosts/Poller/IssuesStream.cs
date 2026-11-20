using PiAgent.Infrastructure.Persistence;
using PiAgent.Platform.Http;

namespace PiAgent.Poller;

/// <summary>
/// `issues` stream (decision 53): repo-wide `GET /repos/{o}/{r}/issues?state=all&amp;sort=updated&amp;direction=desc`,
/// `since = position − overlap` on incremental passes (server-side window filter), page size via `per_page`,
/// `Link`-header pagination. Payloads carrying a `pull_request` marker are skipped — PRs are the `pulls`
/// stream's business; the two streams keep independent cursor positions. One DB transaction per page
/// (WorkItemPageSync): cursor advances only after the page is durably saved.
/// </summary>
public sealed class IssuesStream : IRepositoryStream
{
    public string StreamName => SyncCursorStreams.Issues;

    public async Task<StreamResult> SyncAsync(StreamRun run, CancellationToken cancellationToken)
    {
        var cursor = await SyncCursors.GetOrCreateAsync(run.Db, run.Repository.Id, StreamName, cancellationToken);
        var cursorData = TimestampCursor.TryParse(cursor.CursorValue);
        try
        {
            var position = cursorData?.Position;
            var requestPath =
                $"repos/{run.Repository.Owner}/{run.Repository.Name}/issues?state=all&sort=updated&direction=desc&per_page={run.Options.PerPage}";
            if (position is { } at)
                requestPath = $"{requestPath}&since={Uri.EscapeDataString((at - run.Options.Overlap).ToString("O"))}";

            while (true)
            {
                var page = await run.Client.GetPageAsync(requestPath, cursorData?.Etag, cancellationToken);
                if (page.NotModified)
                    return await FinishNotModifiedAsync(run, cursor, cancellationToken);

                var items = new List<WorkItemSnapshot>();
                var observed = DateTime.MinValue;
                foreach (var element in page.Items)
                {
                    if (GitHubMappers.UpdatedAtOf(element) is { } updated && observed < updated)
                        observed = updated; // position must include skipped PR payloads
                    if (GitHubMappers.IsPullRequestPayload(element))
                        continue;
                    items.Add(GitHubMappers.MapIssue(element));
                }
                position = await WorkItemPageSync.PersistAsync(run, cursor, items, position,
                    observed == DateTime.MinValue ? null : observed, cancellationToken);

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

    internal static async Task<StreamResult> FinishNotModifiedAsync(StreamRun run, SyncCursor cursor,
        CancellationToken cancellationToken)
    {
        SyncCursors.RecordSuccess(cursor, cursor.CursorValue); // timestamps only; position untouched
        await run.Db.SaveChangesAsync(cancellationToken);
        return new StreamResult(StreamResultKind.NotModified);
    }
}
