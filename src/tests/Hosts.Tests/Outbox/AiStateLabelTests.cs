using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PiAgent.Hosts.Tests.Commands;
using PiAgent.Hosts.Tests.Orchestration;
using PiAgent.Hosts.Tests.Config;
using PiAgent.Hosts.Tests.Poller;
using PiAgent.Infrastructure.Persistence;
using PiAgent.Orchestrator.Commands;
using PiAgent.Orchestrator.Config;
using PiAgent.Orchestrator.Orchestration;
using PiAgent.OutboxWorker;
using PiAgent.OutboxWorker.GitHub;
using Xunit;

namespace PiAgent.Hosts.Tests.Outbox;

/// <summary>
/// The AI-lifecycle labels end to end: store enqueue validation, the dispatcher's replace
/// semantics (delete the other three → 404 = fine; add the target; clear = remove all four),
/// retry after a transient failure, and the per-work-item delivery ordering. Transition wiring
/// (Scheduled at admission / system-task creation, Processing at handoff, Finished/Failed/clear
/// at the terminal-Run consumer, clear at /stop + root closure) is asserted in the suites that
/// own those flows — this file proves the delivery mechanism and the store contract.
/// </summary>
public class AiStateLabelTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private const string Pat = "s3cret-write-pat";

    private sealed class LabelStubHandler : HttpMessageHandler
    {
        public readonly List<(string Method, string Path, string? Body)> Requests = [];
        public Func<HttpMethod, HttpStatusCode> StatusOf { get; set; } = _ => HttpStatusCode.OK;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.PathAndQuery;
            var body = request.Content is { } content
                ? await content.ReadAsStringAsync(cancellationToken)
                : null;
            Requests.Add((request.Method.Method, path, body));
            return new HttpResponseMessage(StatusOf(request.Method))
            {
                Content = new StringContent(path.Contains("/labels", StringComparison.Ordinal)
                    ? """[{"id": 1, "name": "x"}]"""
                    : """{"id": 9001}"""),
            };
        }
    }

    private static (OutboxDispatcher Dispatcher, LabelStubHandler Handler) NewDispatcher(string connectionString)
    {
        var handler = new LabelStubHandler();
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

    private async Task<(Repository Repo, Guid ActionId, long IssueNumber)> SeedLabelActionAsync(string? label)
    {
        await postgres.ResetAsync();
        await using var db = postgres.NewDbContext();
        var (repo, workItem, _) = await CommandTestSupport.SeedAsync(db);
        await AiStateLabels.MarkAsync(db, repo.Id, workItem.Id, workItem.Number, null, label, default);
        db.ChangeTracker.Clear();
        var action = await db.OutboxActions.AsNoTracking().SingleAsync();
        return (repo, action.Id, workItem.Number);
    }

    // ---- delivery semantics ----

    [Fact]
    public async Task TargetLabel_RemovesTheOtherThree_ThenAddsTarget()
    {
        var (_, actionId, issueNumber) = await SeedLabelActionAsync(AiStateLabels.Processing);
        var (dispatcher, handler) = NewDispatcher(postgres.ConnectionString);

        await dispatcher.RunCycleAsync(default);

        await using var db = postgres.NewDbContext();
        var action = await db.OutboxActions.AsNoTracking().SingleAsync(a => a.Id == actionId);
        Assert.Equal(OutboxStatuses.Completed, action.Status);

        var deletes = handler.Requests.Where(r => r.Method == "DELETE").ToList();
        var posts = handler.Requests.Where(r => r.Method == "POST").ToList();
        // exactly the other three removed, no PATCH anywhere (ordinal-sorted expectation)
        Assert.Equal(["AI Failed", "AI Finished", "AI Scheduled"],
            deletes.Select(r => Uri.UnescapeDataString(r.Path.Split('/').Last())).Order().ToList());
        var target = Assert.Single(posts);
        Assert.EndsWith($"/issues/{issueNumber}/labels", target.Path);
        Assert.Equal("""{"labels":["AI Processing"]}""", target.Body);
        Assert.DoesNotContain(handler.Requests, r => r.Method == "PATCH");
    }

    [Fact]
    public async Task ClearLabel_RemovesAllFour_NoAdd()
    {
        var (_, actionId, _) = await SeedLabelActionAsync(null);
        var (dispatcher, handler) = NewDispatcher(postgres.ConnectionString);

        await dispatcher.RunCycleAsync(default);

        await using var db = postgres.NewDbContext();
        Assert.Equal(OutboxStatuses.Completed, (await db.OutboxActions.AsNoTracking().SingleAsync()).Status);
        var removed = handler.Requests.Where(r => r.Method == "DELETE")
            .Select(r => Uri.UnescapeDataString(r.Path.Split('/').Last())).Order().ToList();
        Assert.Equal(AiStateLabels.All.Order().ToList(), removed);
        Assert.DoesNotContain(handler.Requests, r => r.Method == "POST");
    }

    [Fact]
    public async Task MissingLabel_404OnDelete_CountsAsSuccess()
    {
        var (_, actionId, _) = await SeedLabelActionAsync(AiStateLabels.Finished);
        var (dispatcher, handler) = NewDispatcher(postgres.ConnectionString);
        handler.StatusOf = method => method == HttpMethod.Delete
            ? HttpStatusCode.NotFound // none of the AI labels is on the issue — all deletes 404
            : HttpStatusCode.OK;

        await dispatcher.RunCycleAsync(default);

        await using var db = postgres.NewDbContext();
        Assert.Equal(OutboxStatuses.Completed, (await db.OutboxActions.AsNoTracking().SingleAsync()).Status);
        Assert.Equal(3, handler.Requests.Count(r => r.Method == "DELETE"));
        Assert.Equal(1, handler.Requests.Count(r => r.Method == "POST"));
    }

    [Fact]
    public async Task TransientAddFailure_RetriesAndCompletes_ReapplyingIdempotently()
    {
        var (_, actionId, _) = await SeedLabelActionAsync(AiStateLabels.Scheduled);
        var (dispatcher, handler) = NewDispatcher(postgres.ConnectionString);
        var addFailures = 0;
        handler.StatusOf = method =>
            method == HttpMethod.Post && addFailures++ == 0 ? HttpStatusCode.InternalServerError : HttpStatusCode.OK;

        await dispatcher.RunCycleAsync(default);
        await using var db = postgres.NewDbContext();
        var first = await db.OutboxActions.AsNoTracking().SingleAsync(a => a.Id == actionId);
        Assert.Equal(OutboxStatuses.Pending, first.Status); // transient: retried, not dead-lettered

        // backoff elapsed → the retry re-applies the whole action (idempotent), then completes
        await db.OutboxActions.Where(a => a.Id == actionId)
            .ExecuteUpdateAsync(u => u.SetProperty(a => a.AvailableAt, DateTime.UtcNow));
        db.ChangeTracker.Clear();
        await dispatcher.RunCycleAsync(default);
        db.ChangeTracker.Clear();
        Assert.Equal(OutboxStatuses.Completed, (await db.OutboxActions.AsNoTracking().SingleAsync()).Status);
        Assert.Equal(2, handler.Requests.Count(r => r.Method == "POST")); // two full applies
    }

    // ---- store contract ----

    [Fact]
    public async Task Store_UnknownLabel_ThrowsAndWritesNothing()
    {
        await postgres.ResetAsync();
        await using var db = postgres.NewDbContext();
        var (repo, workItem, _) = await CommandTestSupport.SeedAsync(db);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            AiStateLabels.MarkAsync(db, repo.Id, workItem.Id, workItem.Number, null, "AI Deployed", default));
        Assert.Empty(await db.OutboxActions.ToListAsync());
    }

    [Fact]
    public async Task Store_ClearAndTarget_PayloadShapes()
    {
        await postgres.ResetAsync();
        await using var db = postgres.NewDbContext();
        var (repo, workItem, _) = await CommandTestSupport.SeedAsync(db);

        await AiStateLabels.MarkAsync(db, repo.Id, workItem.Id, workItem.Number, null, AiStateLabels.Failed, default);
        await AiStateLabels.ClearAsync(db, repo.Id, workItem.Id, workItem.Number, null, default);

        var actions = await db.OutboxActions.AsNoTracking().OrderBy(a => a.CreatedAt).ToListAsync();
        Assert.Equal(2, actions.Count);
        Assert.All(actions, a => Assert.Equal(OutboxActionTypes.AiLabel, a.ActionType));
        using var first = JsonDocument.Parse(actions[0].Payload);
        Assert.Equal("AI Failed", first.RootElement.GetProperty("label").GetString());
        Assert.Equal(workItem.Number, first.RootElement.GetProperty("issue_number").GetInt64());
        using var second = JsonDocument.Parse(actions[1].Payload);
        Assert.Equal(JsonValueKind.Null, second.RootElement.GetProperty("label").ValueKind);
    }

    // ---- per-work-item ordering ----

    [Fact]
    public async Task Ordering_EarlierBlockedAction_HoldsBackTheLaterOne()
    {
        await postgres.ResetAsync();
        await using var db = postgres.NewDbContext();
        var (repo, workItem, _) = await CommandTestSupport.SeedAsync(db);
        var store = new OutboxActionStore(db);
        // two transitions in order: Scheduled now, Finished enqueued right after — but Scheduled
        // sits in a retry backoff (a transient GitHub failure)
        var earlier = await store.EnqueueAiLabelAsync(
            new AiLabelEnqueueRequest(repo.Id, workItem.Id, null, workItem.Number, AiStateLabels.Scheduled), default);
        var later = await store.EnqueueAiLabelAsync(
            new AiLabelEnqueueRequest(repo.Id, workItem.Id, null, workItem.Number, AiStateLabels.Finished), default);
        await db.OutboxActions.Where(a => a.Id == earlier.Id)
            .ExecuteUpdateAsync(u => u.SetProperty(a => a.AvailableAt, DateTime.UtcNow.AddMinutes(10)));
        db.ChangeTracker.Clear();

        // the due later action is NOT claimable while the earlier one is uncompleted
        var dispatcherId = Guid.NewGuid();
        Assert.Null(await store.ClaimNextAsync(dispatcherId, TimeSpan.FromMinutes(2), TimeSpan.FromHours(24), default));

        // the earlier one turns due → it is claimed first; the later one keeps waiting
        await db.OutboxActions.Where(a => a.Id == earlier.Id)
            .ExecuteUpdateAsync(u => u.SetProperty(a => a.AvailableAt, DateTime.UtcNow));
        db.ChangeTracker.Clear();
        var claimed = await store.ClaimNextAsync(dispatcherId, TimeSpan.FromMinutes(2), TimeSpan.FromHours(24), default);
        Assert.Equal(earlier.Id, claimed!.Id);
    }

    // ---- transition wiring: admission → AI Scheduled ----

    [Fact]
    public async Task AdmittedCommand_EnqueuesScheduled()
    {
        await postgres.ResetAsync();
        await using var db = postgres.NewDbContext();
        var (repo, workItem, comment) = await CommandTestSupport.SeedAsync(db, body: "@jsxhuz /plan");
        await CommandTestSupport.SeedCommandEventAsync(db, comment, workItem);
        await CommandTestSupport.SeedProfileAsync(db, OrchestrationTaskTypes.Plan);
        db.ChangeTracker.Clear();

        var harness = CommandTestSupport.NewProcessor(postgres.ConnectionString);
        harness.Handler.EnqueueResponse(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"permission": "admin", "role_name": "admin"}"""),
        });
        await harness.Processor.RunCycleAsync(default);

        await using var verify = postgres.NewDbContext();
        var label = Assert.Single(await verify.OutboxActions.AsNoTracking()
            .Where(a => a.ActionType == OutboxActionTypes.AiLabel).ToListAsync());
        Assert.Equal(workItem.Id, label.WorkItemId);
        using var payload = JsonDocument.Parse(label.Payload);
        Assert.Equal(AiStateLabels.Scheduled, payload.RootElement.GetProperty("label").GetString());
    }

    // ---- transition wiring: terminal success → AI Finished ----

    [Fact]
    public async Task SucceededTask_EnqueuesFinished()
    {
        await postgres.ResetAsync();
        await using var db = postgres.NewDbContext();
        var (repo, workItem, _) = await CommandTestSupport.SeedAsync(db);
        var (_, version, _) = await CommandTestSupport.SeedProfileAsync(db, OrchestrationTaskTypes.Plan, ProfileVersionStates.Ready);
        var seeded = await OrchestrationTestSupport.SeedQueuedAsync(db, workItem, version, withRun: false);
        var preparation = await RunPrepare.PrepareAsync(db, seeded.Task, version, default);
        seeded.Task.Status = OrchestrationTaskStatuses.Starting;
        seeded.Job.Status = OrchestrationJobStatuses.Completed;
        preparation!.Run.Status = RunStatuses.Succeeded;
        preparation.Run.StartedAt = DateTime.UtcNow;
        preparation.Run.CompletedAt = DateTime.UtcNow;
        preparation.Run.ResultSummary = "done";
        db.RunEvents.Add(new RunEvent
        {
            Id = Guid.NewGuid(),
            RunId = preparation.Run.Id,
            EventId = "evt-terminal",
            Type = RunEventTypes.Completed,
            Payload = """{"eventId":"evt-terminal","result":{"summary":"done"}}""",
            OccurredAt = DateTime.UtcNow,
            ReceivedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var consumer = new RunTerminalConsumer(
            new ServiceCollection()
                .AddDbContext<AppDbContext>(options => options
                    .UseNpgsql(postgres.ConnectionString)
                    .UseSnakeCaseNamingConvention())
                .BuildServiceProvider()
                .GetRequiredService<IServiceScopeFactory>(),
            NullLogger<RunTerminalConsumer>.Instance);
        await consumer.RunCycleAsync(default);

        await using var verify = postgres.NewDbContext();
        Assert.Equal(OrchestrationTaskStatuses.Succeeded,
            (await verify.OrchestrationTasks.AsNoTracking().SingleAsync()).Status);
        var label = Assert.Single(await verify.OutboxActions.AsNoTracking()
            .Where(a => a.ActionType == OutboxActionTypes.AiLabel).ToListAsync());
        using var payload = JsonDocument.Parse(label.Payload);
        Assert.Equal(AiStateLabels.Finished, payload.RootElement.GetProperty("label").GetString());
    }
}
