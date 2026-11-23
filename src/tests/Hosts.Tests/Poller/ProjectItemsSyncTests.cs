using PiAgent.Poller.Streams;
using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using PiAgent.Hosts.Tests.Config;
using PiAgent.Infrastructure.Persistence;
using PiAgent.Poller;
using PiAgent.Poller.GitHub;
using Xunit;

namespace PiAgent.Hosts.Tests.Poller;

/// <summary>
/// The Projects v2 pass over real postgres with a stubbed GitHub: list-diff detection
/// (added / field-changed / removed), repository + work-item resolution on the emitted events,
/// the 304 short-circuit, dedupe idempotence, and sync-only treatment of unmappable content
/// (DraftIssue, untracked repository).
/// </summary>
public class ProjectItemsSyncTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private static readonly ProjectRef Project = new("octo-user", 6);
    private const string Board = "octo-user/6";
    private const string BoardPath = "/users/octo-user/projectsV2/6/items?per_page=100";

    private async Task<(AppDbContext Db, Repository Repo, WorkItem Issue)> NewDbAndRepoAsync()
    {
        await postgres.ResetAsync();
        var db = postgres.NewDbContext();
        var repo = await PollerTestSupport.SeedRepositoryAsync(db);
        var issue = PollerTestSupport.SeedWorkItem(db, repo, 3, WorkItemTypes.Issue);
        return (db, repo, issue);
    }

    private static GitHubReadClient Client(PollerTestSupport.StubHandler handler) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("https://api.github.com/") }, "test-pat");

    private static async Task<StreamResult> Sync(AppDbContext db, PollerTestSupport.StubHandler handler)
    {
        var result = await ProjectItemsSync.SyncAsync(db, Client(handler), Project,
            NullLogger.Instance, CancellationToken.None);
        return result;
    }

    private static string ItemJson(long itemId, string updated, string status) => $$$"""
        {
          "id": {{{itemId}}}, "node_id": "PVTI_{{{itemId}}}", "content_type": "Issue",
          "content": {
            "id": 555, "node_id": "ISSUE_3", "number": 3, "title": "Issue three", "state": "open",
            "url": "https://api.github.com/repos/octo-org/example/issues/3"
          },
          "created_at": "2026-11-19T16:00:00Z", "updated_at": "{{{updated}}}", "archived_at": null,
          "fields": [
            {"id": 1, "name": "Title", "value": "Issue three"},
            {"id": 2, "name": "Status", "value": {"id": "98236657", "name": "{{{status}}}"}}
          ]
        }
        """;

    private static string DraftItemJson(long itemId, string updated) => $$$"""
        {
          "id": {{{itemId}}}, "node_id": "PVTI_DRAFT_{{{itemId}}}", "content_type": "DraftIssue",
          "content": {
            "id": 900, "node_id": "PVDI_{{{itemId}}}", "title": "Draft note", "body": null,
            "created_at": "2026-11-19T16:00:00Z", "updated_at": "{{{updated}}}"
          },
          "created_at": "2026-11-19T16:00:00Z", "updated_at": "{{{updated}}}", "archived_at": null,
          "fields": [{"id": 2, "name": "Status", "value": {"id": "1", "name": "No status"}}]
        }
        """;

    [Fact]
    public async Task AddedEvent_MapsRepository_WorkItem_AndFields_ThenIsDedupedOnRelist()
    {
        var (db, repo, issue) = await NewDbAndRepoAsync();
        var handler = new PollerTestSupport.StubHandler();
        handler.Enqueue(PollerTestSupport.ArrayJson(ItemJson(101, "2026-11-19T16:00:00Z", "Backlog")), etag: "board-etag-1");

        var first = await Sync(db, handler);
        Assert.Null(first.Error); // surfaces the stream failure verbatim on regression
        Assert.Equal(StreamResultKind.Completed, first.Kind);
        Assert.Single(handler.Requests, r => r == BoardPath);

        var added = await db.GitHubEvents.SingleAsync(e => e.EventType == GitHubEventTypes.ProjectItemAdded);
        Assert.Equal(GitHubEventSources.ProjectItemsSync, added.Source);
        Assert.Equal($"pi:{Board}:item:101:added", added.DedupeKey);
        Assert.Equal(repo.Id, added.RepositoryId);
        Assert.Equal(issue.Id, added.WorkItemId);
        Assert.Equal(GitHubEventStatuses.Pending, added.ProcessingStatus);
        Assert.Contains("\"project\":\"octo-user/6\"", added.Payload);

        var row = await db.ProjectItems.SingleAsync(i => i.ItemId == 101);
        Assert.Equal("octo-org/example", row.RepositoryFullName);
        Assert.Equal(3, row.Number);
        Assert.Equal(repo.Id, row.RepositoryId);
        Assert.Contains("Backlog", row.FieldValues);

        var state = await db.ProjectSyncs.SingleAsync();
        Assert.Equal("octo-user", state.Owner);
        Assert.Equal(6, state.ProjectNumber);
        Assert.Equal("board-etag-1", state.Etag);
        Assert.Null(state.LastError);

        // Relist of the identical board emits nothing: added events dedupe on their stable key
        db.ChangeTracker.Clear();
        handler = new PollerTestSupport.StubHandler();
        handler.Enqueue(PollerTestSupport.ArrayJson(ItemJson(101, "2026-11-19T16:00:00Z", "Backlog")), etag: "board-etag-1");
        Assert.Equal(StreamResultKind.Completed, (await Sync(db, handler)).Kind);
        Assert.Equal(1, await db.GitHubEvents.CountAsync(e => e.EventType == GitHubEventTypes.ProjectItemAdded));
    }

    [Fact]
    public async Task StatusChange_EmitsUpdatedEvent_WithChangeMap_AndUpdatesRow()
    {
        var (db, repo, issue) = await NewDbAndRepoAsync();
        var handler = new PollerTestSupport.StubHandler();
        handler.Enqueue(PollerTestSupport.ArrayJson(ItemJson(101, "2026-11-19T16:00:00Z", "Backlog")), etag: "board-etag-1");
        await Sync(db, handler);

        db.ChangeTracker.Clear();
        handler = new PollerTestSupport.StubHandler();
        handler.Enqueue(PollerTestSupport.ArrayJson(ItemJson(101, "2026-11-19T17:30:00Z", "In Progress")), etag: "board-etag-2");
        Assert.Equal(StreamResultKind.Completed, (await Sync(db, handler)).Kind);

        var updated = await db.GitHubEvents.SingleAsync(e => e.EventType == GitHubEventTypes.ProjectItemUpdated);
        Assert.StartsWith($"pi:{Board}:item:101:upd:", updated.DedupeKey);
        Assert.Equal(repo.Id, updated.RepositoryId);
        Assert.Equal(issue.Id, updated.WorkItemId);
        using var payload = JsonDocument.Parse(updated.Payload);
        Assert.Equal("Backlog -> In Progress", payload.RootElement.GetProperty("changes").GetProperty("Status").GetString());

        var row = await db.ProjectItems.SingleAsync(i => i.ItemId == 101);
        Assert.Contains("In Progress", row.FieldValues);
        Assert.Equal("board-etag-2", (await db.ProjectSyncs.SingleAsync()).Etag);
    }

    [Fact]
    public async Task RemovedFromListing_EmitsRemovedEvent_AndDeletesRow()
    {
        var (db, repo, _) = await NewDbAndRepoAsync();
        var handler = new PollerTestSupport.StubHandler();
        handler.Enqueue(PollerTestSupport.ArrayJson(ItemJson(101, "2026-11-19T16:00:00Z", "Backlog")), etag: "board-etag-1");
        await Sync(db, handler);

        db.ChangeTracker.Clear();
        handler = new PollerTestSupport.StubHandler();
        handler.Enqueue(PollerTestSupport.ArrayJson(), etag: "board-etag-2");
        Assert.Equal(StreamResultKind.Completed, (await Sync(db, handler)).Kind);

        var removed = await db.GitHubEvents.SingleAsync(e => e.EventType == GitHubEventTypes.ProjectItemRemoved);
        Assert.Equal($"pi:{Board}:item:101:removed", removed.DedupeKey);
        Assert.Equal(repo.Id, removed.RepositoryId);
        db.ChangeTracker.Clear();
        Assert.Empty(await db.ProjectItems.ToListAsync());
    }

    [Fact]
    public async Task NotModified_ShortCircuits_WithStoredEtag()
    {
        var (db, _, _) = await NewDbAndRepoAsync();
        var handler = new PollerTestSupport.StubHandler();
        handler.Enqueue(PollerTestSupport.ArrayJson(ItemJson(101, "2026-11-19T16:00:00Z", "Backlog")), etag: "board-etag-1");
        await Sync(db, handler);

        db.ChangeTracker.Clear();
        handler = new PollerTestSupport.StubHandler();
        handler.Enqueue304("board-etag-1");
        Assert.Equal(StreamResultKind.NotModified, (await Sync(db, handler)).Kind);
        Assert.Single(handler.Requests, r => r == BoardPath);
        Assert.Contains(handler.IfNoneMatch, tag => tag == "\"board-etag-1\"");
        Assert.Equal(1, await db.GitHubEvents.CountAsync());
    }

    [Fact]
    public async Task DraftIssueAndUntrackedRepository_SyncRowsWithoutEvents()
    {
        var (db, _, _) = await NewDbAndRepoAsync();
        var handler = new PollerTestSupport.StubHandler();
        handler.Enqueue(PollerTestSupport.ArrayJson(
            ItemJson(101, "2026-11-19T16:00:00Z", "Backlog"),
            DraftItemJson(202, "2026-11-19T16:00:00Z")), etag: "board-etag-1");

        Assert.Equal(StreamResultKind.Completed, (await Sync(db, handler)).Kind);
        Assert.Equal(2, await db.ProjectItems.CountAsync());
        // only the tracked-repository issue content flows into events
        var events = await db.GitHubEvents.ToListAsync();
        var single = Assert.Single(events);
        Assert.Equal(GitHubEventTypes.ProjectItemAdded, single.EventType);
        Assert.Equal(101, JsonDocument.Parse(single.Payload).RootElement.GetProperty("item_id").GetInt64());

        var draft = await db.ProjectItems.SingleAsync(i => i.ItemId == 202);
        Assert.Equal("DraftIssue", draft.ContentType);
        Assert.Null(draft.RepositoryFullName);
        Assert.Null(draft.Number);
    }

    [Fact]
    public async Task Failure_KeepsEtag_AndRecordsLastError()
    {
        var (db, _, _) = await NewDbAndRepoAsync();
        var handler = new PollerTestSupport.StubHandler();
        handler.Enqueue(PollerTestSupport.ArrayJson(ItemJson(101, "2026-11-19T16:00:00Z", "Backlog")), etag: "board-etag-1");
        await Sync(db, handler);

        db.ChangeTracker.Clear();
        handler = new PollerTestSupport.StubHandler();
        handler.Enqueue(HttpStatusCode.InternalServerError);
        var result = await Sync(db, handler);
        Assert.Equal(StreamResultKind.Failed, result.Kind);

        var state = await db.ProjectSyncs.SingleAsync();
        Assert.Equal("board-etag-1", state.Etag); // the failure never drops the last good ETag
        Assert.NotNull(state.LastError);
        Assert.NotNull(state.LastAttemptAt);
        // rows stay intact; the next pass re-detects
        Assert.Single(await db.ProjectItems.ToListAsync());
    }
}
