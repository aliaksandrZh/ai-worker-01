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
/// cursor pagination, dedupe idempotence, and sync-only treatment of unmappable content
/// (DraftIssue, untracked repository). The listing is GraphQL — the REST items endpoint does not
/// return field values, so column states (Status included) are only readable there.
/// </summary>
public class ProjectItemsSyncTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private static readonly ProjectRef Project = new("octo-user", 6);
    private const string Board = "octo-user/6";
    private const string GraphQlPath = "/graphql";

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

    private static string GraphQlEnvelope(string nodesJson, bool hasNextPage = false, string? endCursor = null) =>
        "{\"data\":{\"user\":{\"projectV2\":{\"items\":{\"pageInfo\":{\"hasNextPage\":" +
        (hasNextPage ? "true" : "false") + ",\"endCursor\":" +
        (endCursor is null ? "null" : $"\"{endCursor}\"") +
        "},\"nodes\":" + nodesJson + "}}}}}";

    private static string Page(params string[] itemNodes) =>
        GraphQlEnvelope("[" + string.Join(",", itemNodes) + "]");

    private static string ItemJson(long itemId, string updated, string status) => $$$"""
        {
          "databaseId": {{{itemId}}},
          "id": "PVTI_{{{itemId}}}",
          "type": "ISSUE",
          "createdAt": "2026-11-19T16:00:00Z",
          "updatedAt": "{{{updated}}}",
          "fieldValues": {
            "nodes": [
              {"__typename": "ProjectV2ItemFieldTextValue", "text": "Issue three", "field": {"id": "PVTF_title", "name": "Title"}},
              {"__typename": "ProjectV2ItemFieldSingleSelectValue", "name": "{{{status}}}", "field": {"id": "PVTF_status", "name": "Status"}}
            ]
          },
          "content": {"__typename": "Issue", "number": 3, "title": "Issue three", "repository": {"nameWithOwner": "octo-Org/Example"}}
        }
        """;

    private static string DraftItemJson(long itemId, string updated) => $$$"""
        {
          "databaseId": {{{itemId}}},
          "id": "PVTI_DRAFT_{{{itemId}}}",
          "type": "DRAFT_ISSUE",
          "createdAt": "2026-11-19T16:00:00Z",
          "updatedAt": "{{{updated}}}",
          "fieldValues": {
            "nodes": [
              {"__typename": "ProjectV2ItemFieldTextValue", "text": "Draft note", "field": {"id": "PVTF_title", "name": "Title"}},
              {"__typename": "ProjectV2ItemFieldSingleSelectValue", "name": "No status", "field": {"id": "PVTF_status", "name": "Status"}}
            ]
          },
          "content": {"__typename": "DraftIssue", "title": "Draft note"}
        }
        """;

    [Fact]
    public async Task AddedEvent_MapsRepository_WorkItem_AndFields_ThenIsDedupedOnRelist()
    {
        var (db, repo, issue) = await NewDbAndRepoAsync();
        var handler = new PollerTestSupport.StubHandler();
        handler.Enqueue(Page(ItemJson(101, "2026-11-19T16:00:00Z", "Backlog")));

        var first = await Sync(db, handler);
        Assert.Null(first.Error); // surfaces the stream failure verbatim on regression
        Assert.Equal(StreamResultKind.Completed, first.Kind);
        Assert.Single(handler.Requests, r => r == GraphQlPath);

        var added = await db.GitHubEvents.SingleAsync(e => e.EventType == GitHubEventTypes.ProjectItemAdded);
        Assert.Equal(GitHubEventSources.ProjectItemsSync, added.Source);
        Assert.Equal($"pi:{Board}:item:101:added", added.DedupeKey);
        Assert.Equal(repo.Id, added.RepositoryId);
        Assert.Equal(issue.Id, added.WorkItemId);
        Assert.Equal(GitHubEventStatuses.Pending, added.ProcessingStatus);
        Assert.Contains("\"project\":\"octo-user/6\"", added.Payload);

        var row = await db.ProjectItems.SingleAsync(i => i.ItemId == 101);
        // the snapshot keeps GitHub's real casing; matching against repository rows is case-insensitive
        Assert.Equal("octo-Org/Example", row.RepositoryFullName);
        Assert.Equal(3, row.Number);
        Assert.Equal(repo.Id, row.RepositoryId);
        Assert.Contains("Backlog", row.FieldValues);

        var state = await db.ProjectSyncs.SingleAsync();
        Assert.Equal("octo-user", state.Owner);
        Assert.Equal(6, state.ProjectNumber);
        Assert.Null(state.LastError);

        // Relist of the identical board emits nothing: added events dedupe on their stable key
        db.ChangeTracker.Clear();
        handler = new PollerTestSupport.StubHandler();
        handler.Enqueue(Page(ItemJson(101, "2026-11-19T16:00:00Z", "Backlog")));
        Assert.Equal(StreamResultKind.Completed, (await Sync(db, handler)).Kind);
        Assert.Equal(1, await db.GitHubEvents.CountAsync(e => e.EventType == GitHubEventTypes.ProjectItemAdded));
    }

    [Fact]
    public async Task StatusChange_EmitsUpdatedEvent_WithChangeMap_AndUpdatesRow()
    {
        var (db, repo, issue) = await NewDbAndRepoAsync();
        var handler = new PollerTestSupport.StubHandler();
        handler.Enqueue(Page(ItemJson(101, "2026-11-19T16:00:00Z", "Backlog")));
        await Sync(db, handler);

        db.ChangeTracker.Clear();
        handler = new PollerTestSupport.StubHandler();
        handler.Enqueue(Page(ItemJson(101, "2026-11-19T17:30:00Z", "In Progress")));
        Assert.Equal(StreamResultKind.Completed, (await Sync(db, handler)).Kind);

        var updated = await db.GitHubEvents.SingleAsync(e => e.EventType == GitHubEventTypes.ProjectItemUpdated);
        Assert.StartsWith($"pi:{Board}:item:101:upd:", updated.DedupeKey);
        Assert.Equal(repo.Id, updated.RepositoryId);
        Assert.Equal(issue.Id, updated.WorkItemId);
        using var payload = JsonDocument.Parse(updated.Payload);
        Assert.Equal("Backlog -> In Progress", payload.RootElement.GetProperty("changes").GetProperty("Status").GetString());

        var row = await db.ProjectItems.SingleAsync(i => i.ItemId == 101);
        Assert.Contains("In Progress", row.FieldValues);
    }

    [Fact]
    public async Task RemovedFromListing_EmitsRemovedEvent_AndDeletesRow()
    {
        var (db, repo, _) = await NewDbAndRepoAsync();
        var handler = new PollerTestSupport.StubHandler();
        handler.Enqueue(Page(ItemJson(101, "2026-11-19T16:00:00Z", "Backlog")));
        await Sync(db, handler);

        db.ChangeTracker.Clear();
        handler = new PollerTestSupport.StubHandler();
        handler.Enqueue(Page());
        Assert.Equal(StreamResultKind.Completed, (await Sync(db, handler)).Kind);

        var removed = await db.GitHubEvents.SingleAsync(e => e.EventType == GitHubEventTypes.ProjectItemRemoved);
        Assert.Equal($"pi:{Board}:item:101:removed", removed.DedupeKey);
        Assert.Equal(repo.Id, removed.RepositoryId);
        db.ChangeTracker.Clear();
        Assert.Empty(await db.ProjectItems.ToListAsync());
    }

    [Fact]
    public async Task Pagination_MergesCursorPages()
    {
        var (db, _, _) = await NewDbAndRepoAsync();
        var handler = new PollerTestSupport.StubHandler();
        handler.Enqueue(GraphQlEnvelope($"[{ItemJson(101, "2026-11-19T16:00:00Z", "Backlog")}]",
            hasNextPage: true, endCursor: "cursor-1"));
        handler.Enqueue(GraphQlEnvelope($"[{ItemJson(202, "2026-11-19T16:00:00Z", "Backlog")}]"));

        Assert.Equal(StreamResultKind.Completed, (await Sync(db, handler)).Kind);
        Assert.Equal(2, handler.Requests.Count(r => r == GraphQlPath));
        Assert.Equal(2, await db.ProjectItems.CountAsync());
        Assert.Equal(2, await db.GitHubEvents.CountAsync());
    }

    [Fact]
    public async Task AlreadyCurrentRow_WithNullRepositoryLink_HealsCoordinatesWithoutEvents()
    {
        var (db, repo, _) = await NewDbAndRepoAsync();
        // a row written by a pass whose repository lookup failed: content current, link null
        var handler = new PollerTestSupport.StubHandler();
        handler.Enqueue(Page(ItemJson(101, "2026-11-19T16:00:00Z", "Backlog")));
        await Sync(db, handler);
        await db.ProjectItems.Where(i => i.ItemId == 101)
            .ExecuteUpdateAsync(u => u.SetProperty(i => i.RepositoryId, (Guid?)null));
        db.ChangeTracker.Clear();

        handler = new PollerTestSupport.StubHandler();
        handler.Enqueue(Page(ItemJson(101, "2026-11-19T16:00:00Z", "Backlog")));
        Assert.Equal(StreamResultKind.Completed, (await Sync(db, handler)).Kind);

        db.ChangeTracker.Clear();
        var row = await db.ProjectItems.SingleAsync(i => i.ItemId == 101);
        Assert.Equal(repo.Id, row.RepositoryId); // the link healed, no churn
        // no new event: the healing pass emitted nothing beyond the original `added`
        Assert.Equal(1, await db.GitHubEvents.CountAsync());
    }

    [Fact]
    public async Task DraftIssueAndUntrackedRepository_SyncRowsWithoutEvents()
    {
        var (db, _, _) = await NewDbAndRepoAsync();
        var handler = new PollerTestSupport.StubHandler();
        handler.Enqueue(Page(
            ItemJson(101, "2026-11-19T16:00:00Z", "Backlog"),
            DraftItemJson(202, "2026-11-19T16:00:00Z")));

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
    public async Task Failure_RecordsLastError_AndKeepsRows()
    {
        var (db, _, _) = await NewDbAndRepoAsync();
        var handler = new PollerTestSupport.StubHandler();
        handler.Enqueue(Page(ItemJson(101, "2026-11-19T16:00:00Z", "Backlog")));
        await Sync(db, handler);

        db.ChangeTracker.Clear();
        handler = new PollerTestSupport.StubHandler();
        handler.Enqueue(HttpStatusCode.InternalServerError);
        var result = await Sync(db, handler);
        Assert.Equal(StreamResultKind.Failed, result.Kind);

        var state = await db.ProjectSyncs.SingleAsync();
        Assert.NotNull(state.LastError);
        Assert.NotNull(state.LastAttemptAt);
        // rows stay intact; the next pass re-detects
        Assert.Single(await db.ProjectItems.ToListAsync());
    }

    [Fact]
    public async Task GraphQlErrorsWithoutData_FailTheStream()
    {
        var (db, _, _) = await NewDbAndRepoAsync();
        var handler = new PollerTestSupport.StubHandler();
        handler.Enqueue("""{"errors":[{"message":"Could not resolve to a ProjectV2 node"}]}""");
        var result = await Sync(db, handler);
        Assert.Equal(StreamResultKind.Failed, result.Kind);
        Assert.Contains("Could not resolve", (await db.ProjectSyncs.SingleAsync()).LastError);
    }
}
