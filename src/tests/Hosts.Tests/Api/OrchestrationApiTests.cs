using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PiAgent.Hosts.Tests.Commands;
using PiAgent.Hosts.Tests.Config;
using PiAgent.Hosts.Tests.Orchestration;
using PiAgent.Infrastructure.Persistence;
using PiAgent.Orchestrator.Api;
using PiAgent.Orchestrator.Commands;
using PiAgent.Orchestrator.Config;
using PiAgent.Platform.Auth;
using Xunit;

namespace PiAgent.Hosts.Tests.Api;

/// <summary>
/// Task 08: the Orchestration API read surface served by the Orchestrator (04 §2 shapes, 04 §7 error
/// shape) under the decision-65 service token, plus `POST /internal/commands` — a synthetic comment
/// ingested through the exact poller pipeline (parse → policy → allowlist, permission lookup skipped
/// per owner ruling → admission → control reply/Outbox), idempotent by commentId.
/// </summary>
public class OrchestrationApiTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private const string Token = "tok-orch";

    private sealed record Api(WebApplication App, HttpClient Client, AppDbContext Db)
    {
        public async ValueTask DisposeAsync()
        {
            await App.DisposeAsync();
            await Db.DisposeAsync();
        }
    }

    private async Task<Api> NewAppAsync(bool synchronized = true)
    {
        await postgres.ResetAsync();
        var db = postgres.NewDbContext();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.WebHost.UseSetting("ConnectionStrings:DefaultDb", postgres.ConnectionString);
        var configurationState = new ConfigurationState();
        if (synchronized)
            configurationState.MarkSynchronized("jsxhuz", 10);
        builder.Services.AddSingleton(configurationState);
        builder.Services.AddSingleton(new CommandMetrics());
        builder.Services.AddSingleton(new CollaboratorPermissionGate(
            new HttpClient(), "unused-pat")); // the hook never performs GitHub permission lookups
        // ticket 14: /stop is a control command that signals the Runtime API — the test app registers a
        // stub-backed client so the minimal-API binding matches the production registration
        var runtimeStub = new PiAgent.Hosts.Tests.Runtime.DirectHttpStub(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"status":"stopping"}""", Encoding.UTF8, "application/json"),
            });
        builder.Services.AddSingleton(new PiAgent.Orchestrator.Orchestration.RuntimeApiClient(
            new HttpClient(runtimeStub) { BaseAddress = new Uri("http://runtime-api.test") },
            Microsoft.Extensions.Options.Options.Create(new PiAgent.Orchestrator.Orchestration.RuntimeApiOptions
                { BaseUrl = "http://runtime-api.test", ServiceToken = Token })));
        builder.Services.Configure<ServiceTokenOptions>(options => options.Orchestrator = Token);
        builder.Services.AddSingleton<ServiceTokenFilter>();
        builder.Services.AddDbContext<AppDbContext>(options => options
            .UseNpgsql(postgres.ConnectionString)
            .UseSnakeCaseNamingConvention());

        var app = builder.Build();
        app.MapOrchestrationApi();
        await app.StartAsync();
        var client = app.GetTestClient();
        client.DefaultRequestHeaders.Add(ServiceAuthExtensions.HeaderName, Token);
        return new Api(app, client, db);
    }

    // ---- guards ----

    [Fact]
    public async Task MissingToken_Returns401()
    {
        await using var api = await NewAppAsync();
        // a fresh client with no X-Service-Token header (NewAppAsync's default client presents the token)
        using var unauthenticated = api.App.GetTestClient();
        var unauthorized = await unauthenticated.GetAsync("/api/v1/work-items/" + Guid.NewGuid());
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
    }

    // ---- read surface ----

    [Fact]
    public async Task GetWorkItem_ReturnsViewPer04S2()
    {
        await using var api = await NewAppAsync();
        var (repo, workItem, _) = await CommandTestSupport.SeedAsync(api.Db);

        var response = await api.Client.GetAsync($"/api/v1/work-items/{workItem.Id}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var view = await Json(response);
        Assert.Equal(workItem.Id.ToString(), view.GetProperty("id").GetString());
        Assert.Equal(repo.FullName, view.GetProperty("repository").GetString());
        Assert.Equal(workItem.Number, view.GetProperty("number").GetInt64());
        Assert.Equal("issue", view.GetProperty("type").GetString());
        Assert.Equal(workItem.Title, view.GetProperty("title").GetString());
        Assert.Equal("open", view.GetProperty("state").GetString());
        Assert.Equal(JsonValueKind.Null, view.GetProperty("workflowId").ValueKind);
    }

    [Fact]
    public async Task GetWorkItemLinksWorkflowId_WhenOneExists()
    {
        await using var api = await NewAppAsync();
        var (_, workItem, _) = await CommandTestSupport.SeedAsync(api.Db);
        var (_, version, _) = await CommandTestSupport.SeedProfileAsync(api.Db, OrchestrationTaskTypes.Plan);
        var prepared = await OrchestrationTestSupport.SeedQueuedAsync(api.Db, workItem, version);

        var view = await Json(await api.Client.GetAsync($"/api/v1/work-items/{workItem.Id}"));
        Assert.Equal(prepared.Workflow.Id.ToString(), view.GetProperty("workflowId").GetString());
    }

    [Fact]
    public async Task ReadEndpoints_Missing_Returns404WithStandardErrorShape()
    {
        await using var api = await NewAppAsync();
        foreach (var path in new[]
                 {
                     $"/api/v1/work-items/{Guid.NewGuid()}",
                     $"/api/v1/workflows/{Guid.NewGuid()}",
                     $"/api/v1/tasks/{Guid.NewGuid()}",
                 })
        {
            var response = await api.Client.GetAsync(path);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Equal("not_found", (await Json(response)).GetProperty("code").GetString());
        }
    }

    [Fact]
    public async Task GetWorkflow_ReturnsStatusRootWorkItemCurrentTaskAndSummaries()
    {
        await using var api = await NewAppAsync();
        var (_, workItem, _) = await CommandTestSupport.SeedAsync(api.Db);
        var (_, version, _) = await CommandTestSupport.SeedProfileAsync(api.Db, OrchestrationTaskTypes.Plan);
        var prepared = await OrchestrationTestSupport.SeedQueuedAsync(api.Db, workItem, version);

        var view = await Json(await api.Client.GetAsync($"/api/v1/workflows/{prepared.Workflow.Id}"));
        Assert.Equal("active", view.GetProperty("status").GetString());
        Assert.Equal(workItem.Id.ToString(), view.GetProperty("rootWorkItem").GetProperty("id").GetString());
        Assert.Equal("An issue", view.GetProperty("rootWorkItem").GetProperty("title").GetString());
        Assert.Equal(prepared.Task.Id.ToString(), view.GetProperty("currentTaskId").GetString());
        var task = view.GetProperty("tasks")[0];
        Assert.Equal("plan", task.GetProperty("type").GetString());
        Assert.Equal("queued", task.GetProperty("status").GetString());
        Assert.Equal(1, task.GetProperty("sequence").GetInt64());
        Assert.StartsWith("profile-", task.GetProperty("agent").GetString());
    }

    [Fact]
    public async Task GetTask_ReturnsViewWithRuns()
    {
        await using var api = await NewAppAsync();
        var (_, workItem, _) = await CommandTestSupport.SeedAsync(api.Db);
        var (_, version, _) = await CommandTestSupport.SeedProfileAsync(api.Db, OrchestrationTaskTypes.Plan);
        var prepared = await OrchestrationTestSupport.SeedQueuedAsync(api.Db, workItem, version);

        var view = await Json(await api.Client.GetAsync($"/api/v1/tasks/{prepared.Task.Id}"));
        Assert.Equal("plan", view.GetProperty("type").GetString());
        Assert.Equal("queued", view.GetProperty("status").GetString());
        Assert.Equal(prepared.Workflow.Id.ToString(), view.GetProperty("workflowId").GetString());
        var run = view.GetProperty("runs")[0];
        Assert.Equal(1, run.GetProperty("attemptNumber").GetInt64());
        Assert.Equal("pending", run.GetProperty("status").GetString());
    }

    // ---- POST /internal/commands ----

    [Fact]
    public async Task InjectedStatus_RunsPipeline_ReplyEnqueued_Idempotent()
    {
        await using var api = await NewAppAsync();
        var (_, workItem, comment) = await CommandTestSupport.SeedAsync(api.Db, body: "@jsxhuz /status");

        var response = await Post(api, new
        {
            workItemId = workItem.Id,
            author = comment.AuthorLogin,
            commentId = comment.GithubCommentId,
            text = "@jsxhuz /status",
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await Json(response);
        Assert.Equal("processed", body.GetProperty("status").GetString());

        var command = await api.Db.AgentCommands.AsNoTracking().SingleAsync(c => c.GithubCommentId == comment.GithubCommentId);
        Assert.Equal(AgentCommandStatuses.Processed, command.Status);
        Assert.Equal(command.Id.ToString(), body.GetProperty("commandId").GetString());
        var outbox = await api.Db.OutboxActions.AsNoTracking().SingleAsync();
        Assert.Contains("No agent workflow started.", outbox.Payload, StringComparison.Ordinal);

        // duplicate inject (same commentId) → same command, no second reply
        var replay = await Post(api, new
        {
            workItemId = workItem.Id,
            author = comment.AuthorLogin,
            commentId = comment.GithubCommentId,
            text = "@jsxhuz /status",
        });
        var replayBody = await Json(replay);
        Assert.Equal(command.Id.ToString(), replayBody.GetProperty("commandId").GetString());
        Assert.Single(await api.Db.OutboxActions.AsNoTracking().ToListAsync());
        // nothing orchestration-state-changing happened
        Assert.Empty(await api.Db.Workflows.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task InjectedPlan_PassesPolicyGates_AdmittedQueued()
    {
        await using var api = await NewAppAsync();
        var (_, workItem, comment) = await CommandTestSupport.SeedAsync(api.Db);
        await CommandTestSupport.SeedProfileAsync(api.Db, OrchestrationTaskTypes.Plan);

        var response = await Post(api, new
        {
            workItemId = workItem.Id,
            author = comment.AuthorLogin,
            commentId = comment.GithubCommentId,
            text = "@jsxhuz /plan",
        });
        var body = await Json(response);
        Assert.Equal("queued", body.GetProperty("status").GetString());

        var command = await api.Db.AgentCommands.AsNoTracking().SingleAsync(c => c.GithubCommentId == comment.GithubCommentId);
        Assert.Equal(AgentCommandStatuses.Queued, command.Status);
        var workflow = await api.Db.Workflows.AsNoTracking().SingleAsync();
        Assert.Equal(workItem.Id, workflow.WorkItemId);
        Assert.Equal(WorkflowStatuses.Active, workflow.Status);
        var task = await api.Db.OrchestrationTasks.AsNoTracking().SingleAsync();
        Assert.Equal(OrchestrationTaskTypes.Plan, task.Type);
        Assert.Equal(OrchestrationTaskStatuses.Queued, task.Status);
        Assert.NotNull(await api.Db.OrchestrationJobs.AsNoTracking().SingleOrDefaultAsync(j => j.TaskId == task.Id));
    }

    [Fact]
    public async Task InjectedContextOnlyText_IsIgnored()
    {
        await using var api = await NewAppAsync();
        var (_, workItem, comment) = await CommandTestSupport.SeedAsync(api.Db, body: "just a remark");

        var body = await Json(await Post(api, new
        {
            workItemId = workItem.Id,
            author = comment.AuthorLogin,
            commentId = comment.GithubCommentId,
            text = "just a remark",
        }));
        Assert.Equal("ignored", body.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("commandId").ValueKind);
        Assert.Empty(await api.Db.AgentCommands.AsNoTracking().ToListAsync());
        Assert.Empty(await api.Db.OutboxActions.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task InjectedCommand_UnknownWorkItem_Found404()
    {
        await using var api = await NewAppAsync();
        var absent = await Post(api, new
        {
            workItemId = Guid.NewGuid(),
            author = "alice",
            commentId = 987L,
            text = "@jsxhuz /status",
        });
        Assert.Equal(HttpStatusCode.NotFound, absent.StatusCode);
        Assert.Equal("not_found", (await Json(absent)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task InjectedCommand_ValidationErrors()
    {
        await using var api = await NewAppAsync();
        var (_, workItem, _) = await CommandTestSupport.SeedAsync(api.Db);

        var noAuthor = await Post(api, new { workItemId = workItem.Id, author = "", commentId = 1L, text = "@jsxhuz /status" });
        Assert.Equal(HttpStatusCode.BadRequest, noAuthor.StatusCode);

        var badCommentId = await Post(api, new { workItemId = workItem.Id, author = "alice", commentId = 0L, text = "@jsxhuz /status" });
        Assert.Equal(HttpStatusCode.BadRequest, badCommentId.StatusCode);

        var badText = await Post(api, new { workItemId = workItem.Id, author = "alice", commentId = 2L, text = " " });
        Assert.Equal(HttpStatusCode.BadRequest, badCommentId.StatusCode);
    }

    [Fact]
    public async Task InjectedCommand_UnsynchronizedConfig_Returns503()
    {
        await using var api = await NewAppAsync(synchronized: false);
        var (_, workItem, _) = await CommandTestSupport.SeedAsync(api.Db);

        var response = await Post(api, new { workItemId = workItem.Id, author = "alice", commentId = 1L, text = "@jsxhuz /status" });
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("not_ready", (await Json(response)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task InjectedCommand_RepositoryMismatch_400()
    {
        await using var api = await NewAppAsync();
        var (_, workItem, comment) = await CommandTestSupport.SeedAsync(api.Db);

        var response = await Post(api, new
        {
            repositoryId = Guid.NewGuid(), // a different repository than the work item's
            workItemId = workItem.Id,
            author = comment.AuthorLogin,
            commentId = comment.GithubCommentId,
            text = "@jsxhuz /status",
        });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ---- helpers ----

    private static async Task<HttpResponseMessage> Post(Api api, object payload)
    {
        var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        return await api.Client.PostAsync("/internal/commands",
            new StringContent(json, Encoding.UTF8, "application/json"));
    }

    private static async Task<JsonElement> Json(HttpResponseMessage response)
        => JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
}
