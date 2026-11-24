using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PiAgent.Hosts.Tests.Commands;
using PiAgent.Hosts.Tests.Config;
using PiAgent.Infrastructure.Persistence;
using PiAgent.Orchestrator.Config;
using PiAgent.Orchestrator.Orchestration;
using PiAgent.OutboxWorker;
using PiAgent.OutboxWorker.GitHub;
using Xunit;

namespace PiAgent.Hosts.Tests.Outbox;

/// <summary>
/// The AI-driven board status moves (`ai_statuses`): the Orchestrator-side resolution
/// (BoardStatusOutbox — the configured board found through the synced ProjectItems, the outbox
/// `project_status` action enqueued with concrete owner/number/item ids) and the Outbox Worker
/// delivery (GraphQL: resolve the field/option ids by exact-case name, then
/// updateProjectV2ItemFieldValue; a renamed/missing column or option is a permanent failure).
/// </summary>
public class ProjectStatusOutboxTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private const string Pat = "s3cret-write-pat";

    /// <summary>Serves the fields query, then the mutation; `FieldNames` controls the resolved schema.</summary>
    private sealed class GraphQLStubHandler(params string[] fieldNames) : HttpMessageHandler
    {
        public readonly List<string> Bodies = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            Bodies.Add(body);
            if (body.Contains("updateProjectV2ItemFieldValue", StringComparison.Ordinal))
                return Json("""{"data":{"updateProjectV2ItemFieldValue":{"projectV2Item":{"id":"PVTI_1"}}}}""");
            var options = string.Join(",", fieldNames.Select(name =>
                "{\"id\":\"OPT_" + name + "\",\"name\":\"" + name + "\"}"));
            return Json("{\"data\":{\"user\":{\"projectV2\":{\"id\":\"PVTPROJ_1\",\"fields\":{\"nodes\":[" +
                "{\"id\":\"FIELD_1\",\"name\":\"Status\",\"options\":[" + options + "]}]}}}}}");
        }

        private static HttpResponseMessage Json(string content) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(content, Encoding.UTF8, "application/json"),
        };
    }

    private static (OutboxDispatcher Dispatcher, GraphQLStubHandler Handler) NewDispatcher(
        string connectionString, params string[] fieldNames)
    {
        var handler = new GraphQLStubHandler(fieldNames);
        var services = new ServiceCollection();
        services.AddSingleton(NullLoggerFactory.Instance);
        services.AddDbContext<AppDbContext>(options => options
            .UseNpgsql(connectionString)
            .UseSnakeCaseNamingConvention());
        services.AddScoped<OutboxActionStore>();
        var provider = services.BuildServiceProvider();
        var client = new GitHubWriteClient(
            new HttpClient(handler) { BaseAddress = new Uri("https://api.github.com/") }, Pat);
        var dispatcher = new OutboxDispatcher(
            provider.GetRequiredService<IServiceScopeFactory>(),
            client,
            Options.Create(new OutboxWorkerOptions(TimeSpan.FromHours(24))),
            new OutboxState(),
            NullLogger<OutboxDispatcher>.Instance);
        return (dispatcher, handler);
    }

    private static ConfigurationState NewConfiguration(string? processing = "In Progress", string? review = "In Review") 
    {
        var configuration = new ConfigurationState();
        configuration.SetProjectStatusConfigs(
            [new ProjectStatusConfig("aliaksandrZh", 7, "Status", processing, review)]);
        configuration.MarkSynchronized("jsxhuz", 10);
        return configuration;
    }

    private async Task<(Repository Repo, WorkItem Item, long ItemDbId)> SeedOnBoardAsync()
    {
        await postgres.ResetAsync();
        await using var db = postgres.NewDbContext();
        var (repo, workItem, _) = await CommandTestSupport.SeedAsync(db);
        var sync = new ProjectSync { Id = Guid.NewGuid(), Owner = "aliaksandrZh", ProjectNumber = 7, UpdatedAt = DateTime.UtcNow };
        db.ProjectSyncs.Add(sync);
        db.ProjectItems.Add(new ProjectItem
        {
            Id = Guid.NewGuid(),
            ProjectSyncId = sync.Id,
            ItemId = 42,
            ItemNodeId = "PVTI_test-item-42",
            ContentType = "Issue",
            RepositoryId = repo.Id,
            RepositoryFullName = repo.FullName,
            Number = (int)workItem.Number,
            Title = "An issue",
            FieldValues = "[{\"id\":1,\"name\":\"Status\",\"value\":\"Ready\"}]",
            CreatedAtGithub = DateTime.UtcNow,
            UpdatedAtGithub = DateTime.UtcNow,
            LastSyncedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return (repo, workItem, 42);
    }

    [Fact]
    public async Task MoveAsync_ResolvesTheConfiguredBoard_AndEnqueuesTheConcreteIds()
    {
        var (repo, workItem, _) = await SeedOnBoardAsync();
        await using var db = postgres.NewDbContext();

        await BoardStatusOutbox.MoveAsync(db, NewConfiguration(), repo.Id, workItem.Id, workItem.Number,
            null, BoardStatusOutbox.State.Processing, default);

        db.ChangeTracker.Clear();
        var action = await db.OutboxActions.AsNoTracking().SingleAsync();
        Assert.Equal(OutboxActionTypes.ProjectStatus, action.ActionType);
        using var payload = JsonDocument.Parse(action.Payload);
        Assert.Equal("aliaksandrZh", payload.RootElement.GetProperty("owner").GetString());
        Assert.Equal(7, payload.RootElement.GetProperty("project_number").GetInt32());
        Assert.Equal(42, payload.RootElement.GetProperty("item_id").GetInt64());
        Assert.Equal("PVTI_test-item-42", payload.RootElement.GetProperty("item_node_id").GetString()); // the GraphQL mutation resolves only the GLOBAL node id
        Assert.Equal("Status", payload.RootElement.GetProperty("field").GetString());
        Assert.Equal("In Progress", payload.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public async Task MoveAsync_ItemNotOnAConfiguredBoard_NoEnqueue()
    {
        var (repo, workItem, _) = await SeedOnBoardAsync();
        await using var db = postgres.NewDbContext();
        var configuration = new ConfigurationState();
        configuration.SetProjectStatusConfigs([new ProjectStatusConfig("someone", 9, "Status", "X", "Y")]);
        configuration.MarkSynchronized("jsxhuz", 10);

        await BoardStatusOutbox.MoveAsync(db, configuration, repo.Id, workItem.Id, workItem.Number,
            null, BoardStatusOutbox.State.Processing, default);

        db.ChangeTracker.Clear();
        Assert.Equal(0, await db.OutboxActions.CountAsync());
    }

    [Fact]
    public async Task MoveAsync_StateNotConfigured_NoEnqueue()
    {
        var (repo, workItem, _) = await SeedOnBoardAsync();
        await using var db = postgres.NewDbContext();

        await BoardStatusOutbox.MoveAsync(db, NewConfiguration(processing: null), repo.Id, workItem.Id,
            workItem.Number, null, BoardStatusOutbox.State.Processing, default);

        db.ChangeTracker.Clear();
        Assert.Equal(0, await db.OutboxActions.CountAsync());
    }

    [Fact]
    public async Task Delivery_ResolvesIdsByExactName_ThenMutates()
    {
        var (repo, workItem, _) = await SeedOnBoardAsync();
        await using var db = postgres.NewDbContext();
        await BoardStatusOutbox.MoveAsync(db, NewConfiguration(), repo.Id, workItem.Id, workItem.Number,
            null, BoardStatusOutbox.State.Review, default);
        db.ChangeTracker.Clear();
        var (dispatcher, handler) = NewDispatcher(postgres.ConnectionString, "Todo", "In Progress", "In Review", "Done");

        await dispatcher.RunCycleAsync(default);

        await using var verify = postgres.NewDbContext();
        Assert.Equal(OutboxStatuses.Completed, (await verify.OutboxActions.AsNoTracking().SingleAsync()).Status);
        // two GraphQL calls: the fields query, then the mutation carrying the resolved ids
        Assert.Equal(2, handler.Bodies.Count);
        Assert.Contains("In Review", handler.Bodies[1]);
        Assert.Contains("PVTPROJ_1", handler.Bodies[1]);
        Assert.Contains("FIELD_1", handler.Bodies[1]);
        Assert.Contains("OPT_In Review", handler.Bodies[1]);
        Assert.Contains("\"item\":\"PVTI_test-item-42\"", handler.Bodies[1].Replace(" ", "")); // the mutation binds the GLOBAL node id, not the database id
    }

    [Fact]
    public async Task Delivery_MissingOption_IsPermanent()
    {
        var (repo, workItem, _) = await SeedOnBoardAsync();
        await using var db = postgres.NewDbContext();
        await BoardStatusOutbox.MoveAsync(db, NewConfiguration(review: "Renamed Away"), repo.Id, workItem.Id,
            workItem.Number, null, BoardStatusOutbox.State.Review, default);
        db.ChangeTracker.Clear();
        // the board no longer has the configured column value
        var (dispatcher, _) = NewDispatcher(postgres.ConnectionString, "Todo", "In Progress", "In Review", "Done");

        await dispatcher.RunCycleAsync(default);

        await using var verify = postgres.NewDbContext();
        var action = await verify.OutboxActions.AsNoTracking().SingleAsync();
        Assert.Equal(OutboxStatuses.DeadLetter, action.Status);
        Assert.Contains("no option 'Renamed Away'", action.LastError);
    }
}
