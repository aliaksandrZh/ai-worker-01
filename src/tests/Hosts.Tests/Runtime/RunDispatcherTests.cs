using PiAgent.RuntimeApi.Provisioning;
using PiAgent.RuntimeApi.Runs;
using PiAgent.RuntimeApi.Api;
using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using PiAgent.Hosts.Tests.Commands;
using PiAgent.Hosts.Tests.Config;
using PiAgent.Hosts.Tests.Orchestration;
using PiAgent.Infrastructure.Persistence;
using PiAgent.Orchestrator.Context;
using PiAgent.Orchestrator.Orchestration;
using PiAgent.Orchestrator.Commands;
using Xunit;

namespace PiAgent.Hosts.Tests.Runtime;

/// <summary>
/// Ticket 09: the real IRunDispatcher — freezing the (ticket-11-scope) RunContextSnapshot and handing
/// the run to the Runtime API over the RuntimeApiClient (decision 65 service credential in the header,
/// 04 §3 payload shape). Outcome mapping: 2xx → HandedOff (job completed, Task starting), 4xx →
/// Permanent (claim-time cancel), 429/5xx/network → Transient (claim released), 409-with-terminal-run →
/// HandedOff (idempotent, restart recovery).
public sealed class DirectHttpStub(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
{
    public List<HttpRequestMessage> Requests { get; } = [];
    public List<string> Bodies { get; } = [];
    /// <summary>Mutable default responder (ticket 14 stop tests flip success/failure between cycles).</summary>
    public Func<HttpRequestMessage, HttpResponseMessage>? Fallback { get; set; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        Bodies.Add(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
        return Task.FromResult((Fallback ?? answer)(request));
    }
}

/// <summary>
/// The real dispatcher.
/// </summary>
public class RunDispatcherTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private static CancellationToken TestToken => default;

    private static RuntimeApiRunDispatcher NewDispatcher(DirectHttpStub stub) => new(
        new RuntimeApiClient(new HttpClient(stub) { BaseAddress = new Uri("http://runtime-api.test") }, Options()),
        NullLogger<RuntimeApiRunDispatcher>.Instance);

    private static Microsoft.Extensions.Options.IOptions<RuntimeApiOptions> Options() =>
        Microsoft.Extensions.Options.Options.Create(new RuntimeApiOptions { ServiceToken = "tok-orch" });


    [Fact]
    public async Task Handoff_PostsThe04Shape_CompletesTheJobAndMovesTaskStarting()
    {
        await postgres.ResetAsync();
        await using var api = await RuntimeTestSupport.NewApiAsync(postgres);
        var preparation = await RuntimeTestSupport.SeedPreparedRunAsync(api);
        await api.Db.SaveChangesAsync();

        var stub = new DirectHttpStub(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"runId":"x","status":"starting"}""", System.Text.Encoding.UTF8, "application/json"),
        });
        var outcome = await NewDispatcher(stub).DispatchAsync(api.Db, preparation, TestToken);

        Assert.Equal(DispatchOutcome.HandedOff, outcome);
        var request = stub.Requests.Single();
        Assert.Equal($"/internal/runtime/runs/{preparation.Run.Id}/start", request.RequestUri!.AbsolutePath);
        Assert.Equal("tok-orch", request.Headers.GetValues("X-Service-Token").Single());

        using var body = JsonDocument.Parse(stub.Bodies.Single());
        Assert.Equal(preparation.Session.Id, body.RootElement.GetProperty("agentSessionId").GetGuid());
        Assert.Equal(preparation.Workspace.Id, body.RootElement.GetProperty("workspaceId").GetGuid());
        Assert.Equal(preparation.ProfileVersion.Id, body.RootElement.GetProperty("agentProfileVersionId").GetGuid());
        var input = body.RootElement.GetProperty("input");
        Assert.Equal("plan", input.GetProperty("command").GetString()); // the canonical task verb
        Assert.Equal("Do the stub work.", input.GetProperty("commandComment").GetProperty("text").GetString());
        Assert.Equal(ContextInstructions.For(OrchestrationTaskTypes.Plan), input.GetProperty("instructions").GetString());

        var db = postgres.NewDbContext();
        var job = await db.OrchestrationJobs.AsNoTracking().SingleAsync(j => j.TaskId == preparation.Task.Id);
        Assert.Equal(OrchestrationJobStatuses.Completed, job.Status);
        var task = await db.OrchestrationTasks.AsNoTracking().SingleAsync(t => t.Id == preparation.Task.Id);
        Assert.Equal(OrchestrationTaskStatuses.Starting, task.Status);
        Assert.NotNull(task.StartedAt);
        var run = await db.Runs.AsNoTracking().SingleAsync(r => r.Id == preparation.Run.Id);
        Assert.Equal(RunStatuses.Starting, run.Status);

        // the frozen snapshot is IMMUTABLE, hashed, and its content is what /start carried + /input serves
        var snapshot = await db.RunContextSnapshots.AsNoTracking().SingleAsync(s => s.RunId == preparation.Run.Id);
        Assert.Equal(64, snapshot.ContentHash.Length);
        Assert.Equal(input.GetRawText(), snapshot.Content); // the payload carries the same frozen bytes
        Assert.False(snapshot.ExcludedSources is null || snapshot.ExcludedSources.Length == 0);
        Assert.Equal("[]", snapshot.ExcludedSources); // nothing pruned: the seeded input fits easily
        var includedSources = JsonDocument.Parse(snapshot.IncludedSources).RootElement;
        Assert.Equal(JsonValueKind.Array, includedSources.ValueKind);
        Assert.True(includedSources.GetArrayLength() > 0); // work_item, repository, command_comment recorded
        Assert.Contains(includedSources.EnumerateArray(),
            source => source.GetProperty("kind").GetString() == ContextAssembler.SourceKinds.WorkItem);
        Assert.Contains(includedSources.EnumerateArray(),
            source => source.GetProperty("kind").GetString() == ContextAssembler.SourceKinds.CommandComment);
    }

    /// <summary>Ticket 14: /stop cancelled the claimed Task while this claim was mid-flight — the
    /// fresh-state guard removes the just-prepared pending Run rows and never touches the Runtime API.</summary>
    [Fact]
    public async Task Dispatch_SupersededWhenTheTaskWasCancelledMidClaim()
    {
        await postgres.ResetAsync();
        await using var api = await RuntimeTestSupport.NewApiAsync(postgres);
        var preparation = await RuntimeTestSupport.SeedPreparedRunAsync(api);
        await api.Db.SaveChangesAsync();

        // the /stop cancellation landed between the snapshot read and the handoff: Task cancelled,
        // Job cancelled — the prepared pending Run rows still exist and must go, unsent
        await api.Db.OrchestrationTasks
            .Where(t => t.Id == preparation.Task.Id)
            .ExecuteUpdateAsync(u => u
                .SetProperty(t => t.Status, OrchestrationTaskStatuses.Cancelled)
                .SetProperty(t => t.FailureReason, AgentCommandRejectionReasons.StopRequested));
        await api.Db.OrchestrationJobs
            .Where(j => j.TaskId == preparation.Task.Id)
            .ExecuteUpdateAsync(u => u.SetProperty(j => j.Status, OrchestrationJobStatuses.Cancelled));

        var stub = new DirectHttpStub(_ => throw new InvalidOperationException("a superseded claim must never reach the Runtime API"));
        var outcome = await NewDispatcher(stub).DispatchAsync(api.Db, preparation, TestToken);

        Assert.Equal(DispatchOutcome.Superseded, outcome);
        Assert.Empty(stub.Requests);
        var db = postgres.NewDbContext();
        Assert.Empty(await db.Runs.AsNoTracking().Where(r => r.TaskId == preparation.Task.Id).ToListAsync());
        Assert.Empty(await db.AgentSessions.AsNoTracking().ToListAsync()); // cascaded
        Assert.Empty(await db.ContainerInstances.AsNoTracking().ToListAsync()); // cascaded
        Assert.Empty(await db.RunContextSnapshots.AsNoTracking().ToListAsync()); // cascaded
        // the terminal task/job rows are untouched (already cancelled by /stop)
        Assert.Equal(OrchestrationTaskStatuses.Cancelled,
            (await db.OrchestrationTasks.AsNoTracking().SingleAsync(t => t.Id == preparation.Task.Id)).Status);
        Assert.Equal(OrchestrationJobStatuses.Cancelled,
            (await db.OrchestrationJobs.AsNoTracking().SingleAsync(j => j.TaskId == preparation.Task.Id)).Status);
    }

    [Fact]
    public async Task PermanentRejection_MapsToPermanentOutcome()
    {
        await postgres.ResetAsync();
        await using var api = await RuntimeTestSupport.NewApiAsync(postgres);
        var preparation = await RuntimeTestSupport.SeedPreparedRunAsync(api);
        await api.Db.SaveChangesAsync();

        var stub = new DirectHttpStub(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("""{"code":"validation","message":"bad"}""", System.Text.Encoding.UTF8, "application/json"),
        });
        var outcome = await NewDispatcher(stub).DispatchAsync(api.Db, preparation, TestToken);
        Assert.Equal(DispatchOutcome.Permanent, outcome);
    }

    [Fact]
    public async Task ServerFailure_MapsToTransientOutcome_LeavingStateUntouched()
    {
        await postgres.ResetAsync();
        await using var api = await RuntimeTestSupport.NewApiAsync(postgres);
        var preparation = await RuntimeTestSupport.SeedPreparedRunAsync(api);
        await api.Db.SaveChangesAsync();

        var stub = new DirectHttpStub(_ => new HttpResponseMessage(HttpStatusCode.BadGateway));
        var outcome = await NewDispatcher(stub).DispatchAsync(api.Db, preparation, TestToken);
        Assert.Equal(DispatchOutcome.Transient, outcome);

        var db = postgres.NewDbContext();
        // nothing changed: job/task/run stay exactly as seeded (the worker releases the claim)
        Assert.Equal(OrchestrationJobStatuses.Pending, (await db.OrchestrationJobs.AsNoTracking().SingleAsync(j => j.TaskId == preparation.Task.Id)).Status);
        Assert.Equal(OrchestrationTaskStatuses.Queued, (await db.OrchestrationTasks.AsNoTracking().SingleAsync(t => t.Id == preparation.Task.Id)).Status);
        Assert.Equal(RunStatuses.Pending, (await db.Runs.AsNoTracking().SingleAsync(r => r.Id == preparation.Run.Id)).Status);
    }

    [Fact]
    public async Task TransportFailure_MapsToTransientOutcome()
    {
        await postgres.ResetAsync();
        await using var api = await RuntimeTestSupport.NewApiAsync(postgres);
        var preparation = await RuntimeTestSupport.SeedPreparedRunAsync(api);
        await api.Db.SaveChangesAsync();

        var stub = new DirectHttpStub(_ => throw new HttpRequestException("connection refused"));
        var outcome = await NewDispatcher(stub).DispatchAsync(api.Db, preparation, TestToken);
        Assert.Equal(DispatchOutcome.Transient, outcome); // still `pending` in PostgreSQL: nothing was accepted
    }

    /// <summary>Task 16: the Runtime API flips the Run out of `pending` inside /start before responding —
    /// a transport failure with the row already beyond `pending` is an ACCEPTED start: recover with the
    /// ordinary hand-off write-back instead of releasing the claim (a release would re-execute the task).</summary>
    [Fact]
    public async Task TransportFailure_AfterTheRuntimeAcceptedTheStart_RecoversAsHandedOff()
    {
        await postgres.ResetAsync();
        await using var api = await RuntimeTestSupport.NewApiAsync(postgres);
        var preparation = await RuntimeTestSupport.SeedPreparedRunAsync(api);
        // what /start persisted milliseconds before the response was lost
        await api.Db.Runs.Where(r => r.Id == preparation.Run.Id).ExecuteUpdateAsync(set => set
            .SetProperty(r => r.Status, RunStatuses.Starting)
            .SetProperty(r => r.StartedAt, DateTime.UtcNow));

        var stub = new DirectHttpStub(_ => throw new HttpRequestException("response lost"));
        var outcome = await NewDispatcher(stub).DispatchAsync(api.Db, preparation, TestToken);

        Assert.Equal(DispatchOutcome.HandedOff, outcome);
        var db = postgres.NewDbContext();
        Assert.Equal(OrchestrationJobStatuses.Completed, (await db.OrchestrationJobs.AsNoTracking().SingleAsync(j => j.TaskId == preparation.Task.Id)).Status);
        var task = await db.OrchestrationTasks.AsNoTracking().SingleAsync(t => t.Id == preparation.Task.Id);
        Assert.Equal(OrchestrationTaskStatuses.Starting, task.Status);
        Assert.NotNull(task.StartedAt);
        Assert.Equal(RunStatuses.Starting, (await db.Runs.AsNoTracking().SingleAsync(r => r.Id == preparation.Run.Id)).Status);
    }

    [Fact]
    public async Task IdempotentRedispatch_WhenTheRunIsAlreadyTerminal_CompletesTheClaim()
    {
        await postgres.ResetAsync();
        await using var api = await RuntimeTestSupport.NewApiAsync(postgres);
        var preparation = await RuntimeTestSupport.SeedPreparedRunAsync(api);
        await api.Db.SaveChangesAsync();

        // the Run already reached its terminal state (e.g. a restarted dispatch after callbacks arrived)
        await api.Db.Runs
            .Where(r => r.Id == preparation.Run.Id)
            .ExecuteUpdateAsync(set => set.SetProperty(r => r.Status, RunStatuses.Succeeded));

        var stub = new DirectHttpStub(_ => new HttpResponseMessage(HttpStatusCode.Conflict)
        {
            Content = new StringContent("""{"code":"invalid_transition"}""", System.Text.Encoding.UTF8, "application/json"),
        });
        var outcome = await NewDispatcher(stub).DispatchAsync(api.Db, preparation, TestToken);
        Assert.Equal(DispatchOutcome.HandedOff, outcome); // terminal → nothing to hand off; the consumer takes over

        // task 16: the claim's write-back (Task starting, job completed) still lands so the consumer
        // adopts the terminal Run immediately instead of waiting for a lease-expiry recovery
        var db = postgres.NewDbContext();
        Assert.Equal(OrchestrationJobStatuses.Completed, (await db.OrchestrationJobs.AsNoTracking().SingleAsync(j => j.TaskId == preparation.Task.Id)).Status);
        Assert.Equal(OrchestrationTaskStatuses.Starting,
            (await db.OrchestrationTasks.AsNoTracking().SingleAsync(t => t.Id == preparation.Task.Id)).Status);
    }

    /// <summary>Worker-level outcomes: a Permanent dispatcher triggers the claim-time cancel contract
    /// (recorded reason + outbox notice); a Transient one releases it (job back to pending, pending
    /// Run rows deleted). A Permanent outcome must only fire on a never-started Run.</summary>
    [Fact]
    public async Task PermanentDispatchOutcome_CancelsTheClaimWithRecordedReason()
    {
        await postgres.ResetAsync();
        var db = await NewSeededWorkItemAsync();
        var harness = OrchestrationTestSupport.NewWorker(postgres, testDispatcher: new PermanentDispatcher());
        await db.DisposeAsync();
        harness.Handler.Fallback = _ => AdminPermission(); // the claim-time permission recheck passes
        await harness.Worker.RunCycleAsync(TestToken);
        await AssertClaimCancelledAsync();
    }

    [Fact]
    public async Task TransientDispatchOutcome_ReleasesTheClaim()
    {
        await postgres.ResetAsync();
        var db = await NewSeededWorkItemAsync();
        var harness = OrchestrationTestSupport.NewWorker(postgres, testDispatcher: new TransientDispatcher());
        await db.DisposeAsync();
        harness.Handler.Fallback = _ => AdminPermission(); // the claim-time permission recheck passes
        await harness.Worker.RunCycleAsync(TestToken);

        var verification = postgres.NewDbContext();
        Assert.Equal(OrchestrationJobStatuses.Pending, (await verification.OrchestrationJobs.AsNoTracking().SingleAsync()).Status);
        Assert.Equal(OrchestrationTaskStatuses.Queued, (await verification.OrchestrationTasks.AsNoTracking().SingleAsync()).Status);
        Assert.False(await verification.Runs.AsNoTracking().AnyAsync());
    }

    /// <summary>One admitted task-producing command with a ready version, the claim path exercised by
    /// the real worker (permission recheck stubbed to admin per ClaimPermissionCheck).</summary>
    private async Task<AppDbContext> NewSeededWorkItemAsync()
    {
        var db = postgres.NewDbContext();
        var (repo, workItem, _) = await CommandTestSupport.SeedAsync(db);
        var (_, version, _) = await CommandTestSupport.SeedProfileAsync(db, OrchestrationTaskTypes.Plan, ProfileVersionStates.Ready);
        await OrchestrationTestSupport.SeedQueuedAsync(db, workItem, version, withCommand: true, withRun: false);
        return db;
    }

    private async Task AssertClaimCancelledAsync()
    {
        var db = postgres.NewDbContext();
        var task = await db.OrchestrationTasks.AsNoTracking().SingleAsync();
        Assert.Equal(OrchestrationTaskStatuses.Cancelled, task.Status);
        Assert.Equal(AgentCommandRejectionReasons.RuntimeDispatchFailed, task.FailureReason);
        var job = await db.OrchestrationJobs.AsNoTracking().SingleAsync();
        Assert.Equal(OrchestrationJobStatuses.Cancelled, job.Status);
        Assert.False(await db.Runs.AsNoTracking().AnyAsync());
        Assert.Equal(1, await db.OutboxActions.AsNoTracking().CountAsync()); // the claim-cancel notice
    }

    private static HttpResponseMessage AdminPermission() => new(HttpStatusCode.OK)
    {
        Content = new StringContent("""{"permission": "admin", "role_name": "admin"}"""),
    };

    private sealed class PermanentDispatcher : IRunDispatcher
    {
        public Task<DispatchOutcome> DispatchAsync(AppDbContext db, RunPreparation preparation, CancellationToken cancellationToken) =>
            Task.FromResult(DispatchOutcome.Permanent);
    }

    private sealed class TransientDispatcher : IRunDispatcher
    {
        public Task<DispatchOutcome> DispatchAsync(AppDbContext db, RunPreparation preparation, CancellationToken cancellationToken) =>
            Task.FromResult(DispatchOutcome.Transient);
    }
}

/// <summary>Regression (live finding 2026-11-21): a fast-settling container can deliver the terminal
/// callback before the dispatcher's post-/start write-back is saved — the write-back must not clobber
/// the terminal Run state (the terminal consumer owns the hand-off from there).</summary>
public class DispatchWriteBackRaceTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task Handoff_DoesNotClobberATerminalRunState()
    {
        await postgres.ResetAsync();
        await using var api = await RuntimeTestSupport.NewApiAsync(postgres);
        var preparation = await RuntimeTestSupport.SeedPreparedRunAsync(api);
        await api.Db.SaveChangesAsync();
        // the terminal callback already won the race (Run succeeded, the Runtime API's persisted state)
        await api.Db.Runs
            .Where(r => r.Id == preparation.Run.Id)
            .ExecuteUpdateAsync(set => set
                .SetProperty(r => r.Status, RunStatuses.Succeeded)
                .SetProperty(r => r.CompletedAt, DateTime.UtcNow));
        api.Db.ChangeTracker.Clear();
        // reload the (now terminal) run + task into the dispatch context, as the worker would
        var task = await api.Db.OrchestrationTasks.SingleAsync(t => t.Id == preparation.Task.Id);
        var run = await api.Db.Runs.SingleAsync(r => r.Id == preparation.Run.Id);
        preparation = preparation with { Task = task, Run = run }; // tracked entities, as the worker holds them

        var stub = new DirectHttpStub(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"status":"starting"}""", System.Text.Encoding.UTF8, "application/json"),
        });
        var dispatcher = new RuntimeApiRunDispatcher(
            new RuntimeApiClient(new HttpClient(stub) { BaseAddress = new Uri("http://runtime-api.test") },
                Microsoft.Extensions.Options.Options.Create(new RuntimeApiOptions { ServiceToken = "tok-orch" })),
            NullLogger<RuntimeApiRunDispatcher>.Instance);
        var outcome = await dispatcher.DispatchAsync(api.Db, preparation, default);
        Assert.Equal(DispatchOutcome.HandedOff, outcome);

        var db = postgres.NewDbContext();
        Assert.Equal(RunStatuses.Succeeded, (await db.Runs.AsNoTracking().SingleAsync(r => r.Id == preparation.Run.Id)).Status); // untouched by the write-back
        Assert.Equal(OrchestrationTaskStatuses.Starting, (await db.OrchestrationTasks.AsNoTracking().SingleAsync(t => t.Id == preparation.Task.Id)).Status);
    }
}
