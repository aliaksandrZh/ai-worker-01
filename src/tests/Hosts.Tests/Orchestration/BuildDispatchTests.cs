using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PiAgent.Hosts.Tests.Config;
using PiAgent.Infrastructure.Persistence;
using PiAgent.Orchestrator.Builds;
using PiAgent.Orchestrator.Config;
using Xunit;

namespace PiAgent.Hosts.Tests.Orchestration;

/// <summary>
/// Ticket 10, decision 81 orchestration side: durable BuildJob per draft version (buildId persisted
/// BEFORE dispatch), `draft → building` in the same write, async 202 dispatch, status polling to the
/// validated terminal outcomes (`ready` / `build_failed`), transient backoff keeps `building`, the 404
/// redispatch path, and restart-reconciler semantics (this same cycle over durable rows).
/// </summary>
public class BuildDispatchTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private static CancellationToken TestToken => default;

    public sealed class StubHttp : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];
        public List<string> Bodies { get; } = [];
        public Func<HttpRequestMessage, HttpResponseMessage> Answer { get; set; } =
            _ => new HttpResponseMessage(HttpStatusCode.Accepted);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            Bodies.Add(request.Content is null ? "" : request.Content.ReadAsStringAsync().GetAwaiter().GetResult());
            return Task.FromResult(Answer(request));
        }

        public void Reset()
        {
            Requests.Clear();
            Bodies.Clear();
        }
    }

    private static (ProfileBuildWorker Worker, StubHttp Stub, BuildMetrics Metrics) NewWorker(
        string connectionString, string? serviceToken = "tok-orch")
    {
        var services = new ServiceCollection();
        services.AddDbContext<AppDbContext>(options => options
            .UseNpgsql(connectionString)
            .UseSnakeCaseNamingConvention());
        var stub = new StubHttp();
        var clientOptions = Options.Create(new BuildWorkerOptions
        {
            BaseUrl = "http://build-worker.test",
            ServiceToken = serviceToken ?? "",
            InitialBackoffSeconds = 1,
            MaxBackoffSeconds = 4,
        });
        var client = new BuildWorkerApiClient(new HttpClient(stub) { BaseAddress = new Uri("http://build-worker.test") }, clientOptions);
        var metrics = new BuildMetrics();
        var worker = new ProfileBuildWorker(
            services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            Synchronized(), client, clientOptions, metrics, NullLogger<ProfileBuildWorker>.Instance);
        return (worker, stub, metrics);
    }

    private static ConfigurationState Synchronized()
    {
        var configuration = new ConfigurationState();
        configuration.MarkSynchronized("test-bot", 10);
        return configuration;
    }

    private static async Task<(AgentProfile Profile, AgentProfileVersion Version)> SeedVersionAsync(
        AppDbContext db, string state, string? configSnapshot = null, string name = "stub")
    {
        var profile = new AgentProfile
        {
            Id = Guid.NewGuid(),
            Name = name,
            IsEnabled = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        var version = new AgentProfileVersion
        {
            Id = Guid.NewGuid(),
            AgentProfileId = profile.Id,
            Version = 2,
            SourceCommitSha = "abc123",
            SourceDirectory = $"config/agents/{name}",
            ConfigSnapshot = configSnapshot ?? """{"profile":"stub"}""",
            ConfigHash = "fake-hash",
            RuntimeType = "pi",
            MaxContextTokens = 1000,
            State = state,
            CreatedAt = DateTime.UtcNow,
        };
        db.AgentProfiles.Add(profile);
        db.AgentProfileVersions.Add(version);
        await db.SaveChangesAsync();
        return (profile, version);
    }

    private static BuildStatusResponse Status(BuildJob job, string state, string? tag = null, string? imageId = "sha256:abc", string? log = "/var/lib/pi-agent/builds/logs/x.log") =>
        new(job.Id, state, tag, imageId, imageId, log, state == WorkerBuildStates.Failed ? "docker_build_failed" : null,
            state == WorkerBuildStates.Failed ? "docker build failed: RUN exited 1" : null);

    private static HttpResponseMessage Json(BuildStatusResponse status) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(status, BuildStatuses.PayloadJson), Encoding.UTF8, "application/json"),
    };

    [Fact]
    public async Task DraftVersion_OpensDurableBuildJob_FlipsVersionBuilding_BeforeAnyDispatch()
    {
        await postgres.ResetAsync();
        await using var db = postgres.NewDbContext();
        var (profile, version) = await SeedVersionAsync(db, ProfileVersionStates.Draft);

        var (worker, stub, _) = NewWorker(postgres.ConnectionString);
        stub.Answer = _ => throw new OperationCanceledException("no dispatch must happen in the open phase"); // POST would fail loudly
        stub.Reset();

        // first cycle: only OpenBuildJobs can run because dispatch throws → the durable row must already exist
        try
        {
            await worker.RunCycleAsync(TestToken);
        }
        catch (OperationCanceledException)
        {
            // expected: the dispatch attempt hit the throwing stub AFTER the durable open
        }
        await db.Entry(version).ReloadAsync();
        var job = await db.BuildJobs.AsNoTracking().SingleAsync();
        Assert.Equal(version.Id, job.AgentProfileVersionId);
        Assert.Equal(BuildJobStates.Pending, job.State);
        // buildId is generated and persisted on the job row; the version flipped `building` in the same write
        Assert.NotEqual(Guid.Empty, job.Id);
        Assert.Equal(ProfileVersionStates.Building, version.State);
        await using var check = postgres.NewDbContext();
        var persisted = await check.BuildJobs.AsNoTracking().SingleAsync();
        Assert.Equal(job.Id, persisted.Id); // durable: the same buildId survives a new context
    }

    [Fact]
    public async Task Dispatch202_MovesJobPolling_CarriesPinnedPayloadAndCredential()
    {
        await postgres.ResetAsync();
        await using var db = postgres.NewDbContext();
        var (profile, version) = await SeedVersionAsync(db, ProfileVersionStates.Draft);
        var (worker, stub, _) = NewWorker(postgres.ConnectionString);
        stub.Answer = _ => new HttpResponseMessage(HttpStatusCode.Accepted);

        await worker.RunCycleAsync(TestToken);

        var post = stub.Requests.Single();
        Assert.Equal("/internal/builds", post.RequestUri!.AbsolutePath);
        Assert.Equal("tok-orch", post.Headers.GetValues("X-Service-Token").Single());
        using var body = JsonDocument.Parse(stub.Bodies.Single());
        var job = await db.BuildJobs.AsNoTracking().SingleAsync();
        Assert.Equal(job.Id, body.RootElement.GetProperty("buildId").GetGuid());
        Assert.Equal(profile.Name, body.RootElement.GetProperty("profile").GetString());
        Assert.Equal(2, body.RootElement.GetProperty("profileVersion").GetInt32());
        Assert.Equal("abc123", body.RootElement.GetProperty("sourceCommitSha").GetString());
        Assert.Equal("config/agents/stub", body.RootElement.GetProperty("buildContext").GetProperty("directory").GetString());
        // the lineage jsonb on the row records the same contract
        Assert.Contains(profile.Name, job.DispatchPayload);
        // job accepted → polling
        Assert.Equal(BuildJobStates.Polling, job.State);
        await db.Entry(version).ReloadAsync();
        Assert.Equal(ProfileVersionStates.Building, version.State); // dispatch ≠ terminal: still building
    }

    [Fact]
    public async Task TransientDispatchFailure_StaysPendingWithBackoff_SameBuildIdRedispatched()
    {
        await postgres.ResetAsync();
        await using var db = postgres.NewDbContext();
        var (profile, _) = await SeedVersionAsync(db, ProfileVersionStates.Draft);
        var (worker, stub, _) = NewWorker(postgres.ConnectionString);
        stub.Answer = _ => throw new HttpRequestException("connection refused");

        await worker.RunCycleAsync(TestToken);

        var job = await db.BuildJobs.AsNoTracking().SingleAsync();
        Assert.Equal(BuildJobStates.Pending, job.State);
        Assert.Equal(1, job.Attempts);
        Assert.NotNull(job.NextRetryAt);
        Assert.Contains("connection refused", job.LastError);
        await db.Entry(profile).ReloadAsync();
        var version = await db.AgentProfileVersions.AsNoTracking().SingleAsync();
        Assert.Equal(ProfileVersionStates.Building, version.State); // transient ≠ build_failed

        // once the backoff expires, the SAME buildId is redispatched (worker-side idempotency key)
        await db.BuildJobs.Where(j => j.Id == job.Id)
            .ExecuteUpdateAsync(set => set.SetProperty(j => j.NextRetryAt, DateTime.UtcNow.AddMinutes(-1)));
        stub.Reset();
        stub.Answer = _ => new HttpResponseMessage(HttpStatusCode.Accepted);
        await worker.RunCycleAsync(TestToken);
        var post2 = stub.Requests.Single();
        using var body2 = JsonDocument.Parse(stub.Bodies.Single());
        Assert.Equal(job.Id, body2.RootElement.GetProperty("buildId").GetGuid());
        var redispatched = await db.BuildJobs.AsNoTracking().SingleAsync();
        Assert.Equal(BuildJobStates.Polling, redispatched.State);
    }

    [Fact]
    public async Task PollBuildingAndSucceeded_RecordsValidatedResult_SetsVersionReady()
    {
        await postgres.ResetAsync();
        await using var db = postgres.NewDbContext();
        var (profile, version) = await SeedVersionAsync(db, ProfileVersionStates.Draft);
        var (worker, stub, _) = NewWorker(postgres.ConnectionString);
        stub.Answer = _ => new HttpResponseMessage(HttpStatusCode.Accepted);
        await worker.RunCycleAsync(TestToken); // open + dispatch
        var job = await db.BuildJobs.AsNoTracking().SingleAsync();
        Assert.Equal(BuildJobStates.Polling, job.State);

        stub.Answer = request => request.Method == HttpMethod.Get
            ? Json(new BuildStatusResponse(job.Id, WorkerBuildStates.Building, null, null, null, null, null, null))
            : throw new InvalidOperationException("no dispatch expected");
        await worker.RunCycleAsync(TestToken);
        job = await db.BuildJobs.AsNoTracking().SingleAsync();
        Assert.Equal(BuildJobStates.Polling, job.State);
        Assert.Equal(0, job.Attempts);

        stub.Answer = request => Json(Status(job, WorkerBuildStates.Succeeded, tag: $"agent-{profile.Name}:v2"));
        await worker.RunCycleAsync(TestToken);
        job = await db.BuildJobs.AsNoTracking().SingleAsync();
        Assert.Equal(BuildJobStates.Completed, job.State);
        Assert.Equal($"agent-{profile.Name}:v2", job.ImageTag);
        Assert.Equal("sha256:abc", job.ImageDigest);
        Assert.NotNull(job.BuildLogReference);
        Assert.Null(job.NextRetryAt);
        await db.Entry(version).ReloadAsync();
        Assert.Equal(ProfileVersionStates.Ready, version.State);
        Assert.Equal($"agent-{profile.Name}:v2", version.ContainerImage);
        Assert.Equal("sha256:abc", version.ImageDigest);
    }

    [Fact]
    public async Task PollFailed_IsTerminal_BuildFailedWithReasonRecorded()
    {
        await postgres.ResetAsync();
        await using var db = postgres.NewDbContext();
        var (profile, version) = await SeedVersionAsync(db, ProfileVersionStates.Draft);
        var (worker, stub, _) = NewWorker(postgres.ConnectionString);
        stub.Answer = _ => new HttpResponseMessage(HttpStatusCode.Accepted);
        await worker.RunCycleAsync(TestToken);
        var job = await db.BuildJobs.AsNoTracking().SingleAsync();

        stub.Answer = request => Json(Status(job, WorkerBuildStates.Failed));
        await worker.RunCycleAsync(TestToken);
        job = await db.BuildJobs.AsNoTracking().SingleAsync();
        Assert.Equal(BuildJobStates.Failed, job.State);
        Assert.Contains("docker_build_failed", job.LastError);
        await db.Entry(version).ReloadAsync();
        Assert.Equal(ProfileVersionStates.BuildFailed, version.State);

        // a terminal poll outcome never re-polls (the cycle touches only pending/polling jobs)
        stub.Reset();
        stub.Answer = _ => throw new InvalidOperationException("poll after terminal");
        await worker.RunCycleAsync(TestToken);
    }

    [Fact]
    public async Task Poll404_ReturnsToPending_SameBuildIdRedispatched()
    {
        await postgres.ResetAsync();
        await using var db = postgres.NewDbContext();
        var (profile, version) = await SeedVersionAsync(db, ProfileVersionStates.Draft);
        var (worker, stub, _) = NewWorker(postgres.ConnectionString);
        stub.Answer = _ => new HttpResponseMessage(HttpStatusCode.Accepted);
        await worker.RunCycleAsync(TestToken);
        var job = await db.BuildJobs.AsNoTracking().SingleAsync();

        stub.Answer = _ => new HttpResponseMessage(HttpStatusCode.NotFound);
        await worker.RunCycleAsync(TestToken);
        job = await db.BuildJobs.AsNoTracking().SingleAsync();
        Assert.Equal(BuildJobStates.Pending, job.State); // the worker lost local state: redispatch with the SAME buildId
        Assert.Equal(1, job.Attempts);

        await db.BuildJobs.Where(j => j.Id == job.Id)
            .ExecuteUpdateAsync(set => set.SetProperty(j => j.NextRetryAt, DateTime.UtcNow.AddMinutes(-1)));
        stub.Reset();
        stub.Answer = _ => new HttpResponseMessage(HttpStatusCode.Accepted);
        await worker.RunCycleAsync(TestToken);
        using var body = JsonDocument.Parse(stub.Bodies.Single());
        var redelivered = await db.BuildJobs.AsNoTracking().SingleAsync();
        Assert.Equal(job.Id, body.RootElement.GetProperty("buildId").GetGuid());
        Assert.Equal(BuildJobStates.Polling, redelivered.State);
    }

    [Fact]
    public async Task InvalidSucceededResult_NeverTransitions_KeepsPollingUnderBackoff()
    {
        await postgres.ResetAsync();
        await using var db = postgres.NewDbContext();
        var (profile, version) = await SeedVersionAsync(db, ProfileVersionStates.Draft);
        var (worker, stub, metrics) = NewWorker(postgres.ConnectionString);
        stub.Answer = _ => new HttpResponseMessage(HttpStatusCode.Accepted);
        await worker.RunCycleAsync(TestToken);
        var job = await db.BuildJobs.AsNoTracking().SingleAsync();

        // tag mismatch + missing image id/digest/log reference — decision 81 validation rejects it
        stub.Answer = _ => Json(new BuildStatusResponse(job.Id, WorkerBuildStates.Succeeded,
            "agent-somewhere-else:v9", null, null, null, null, null));
        await worker.RunCycleAsync(TestToken);
        job = await db.BuildJobs.AsNoTracking().SingleAsync();
        Assert.Equal(BuildJobStates.Polling, job.State);
        Assert.Equal(1, job.Attempts);
        await db.Entry(version).ReloadAsync();
        Assert.Equal(ProfileVersionStates.Building, version.State); // never build_failed on a bad RESULT
        Assert.True(metrics.InvalidPollResultsTotal >= 1);
    }

    [Fact]
    public async Task RestartReconciler_DurablePendingAndPollingRowsResume()
    {
        await postgres.ResetAsync();
        await using var db = postgres.NewDbContext();
        var (profile, version) = await SeedVersionAsync(db, ProfileVersionStates.Draft, name: "dual");

        var first = NewWorker(postgres.ConnectionString);
        first.Stub.Answer = _ => new HttpResponseMessage(HttpStatusCode.Accepted);
        await first.Worker.RunCycleAsync(TestToken); // open + dispatch → polling
        var job = await db.BuildJobs.AsNoTracking().SingleAsync();

        // "restart": a fresh worker instance over the same durable rows — polling continues with the same buildId
        var second = NewWorker(postgres.ConnectionString);
        second.Stub.Answer = request => request.Method == HttpMethod.Get
            ? Json(Status(job, WorkerBuildStates.Succeeded, tag: $"agent-{profile.Name}:v2"))
            : throw new InvalidOperationException("no redispatch expected (a worker restart resumes polling)");
        await second.Worker.RunCycleAsync(TestToken);

        var resumed = await db.BuildJobs.AsNoTracking().SingleAsync();
        Assert.Equal(job.Id, resumed.Id);
        Assert.Equal(BuildJobStates.Completed, resumed.State);
        await using var check = postgres.NewDbContext();
        var finished = await check.AgentProfileVersions.AsNoTracking().SingleAsync(v => v.Id == version.Id);
        Assert.Equal(ProfileVersionStates.Ready, finished.State);
    }

    [Fact]
    public async Task ColdStartGate_NotSynchronized_NoBuildJobsOpened()
    {
        await postgres.ResetAsync();
        await using var db = postgres.NewDbContext();
        var (_, version) = await SeedVersionAsync(db, ProfileVersionStates.Draft);
        var (worker, stub, _) = NewWorker(postgres.ConnectionString);

        var unsynchronized = new ConfigurationState();
        unsynchronized.MarkFailed("config sync failed");
        var services = new ServiceCollection();
        services.AddDbContext<AppDbContext>(options => options
            .UseNpgsql(postgres.ConnectionString)
            .UseSnakeCaseNamingConvention());
        var gated = new ProfileBuildWorker(
            services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            unsynchronized,
            new BuildWorkerApiClient(new HttpClient(stub), Options.Create(new BuildWorkerOptions())),
            Options.Create(new BuildWorkerOptions()),
            new BuildMetrics(),
            NullLogger<ProfileBuildWorker>.Instance);

        await gated.RunCycleAsync(TestToken);

        Assert.Equal(0, await db.BuildJobs.CountAsync());
        Assert.Equal(ProfileVersionStates.Draft, version.State);
    }

    [Fact]
    public async Task Recovery_BuildingVersionWithoutJob_GetsTheJobBack()
    {
        await postgres.ResetAsync();
        await using var db = postgres.NewDbContext();
        var (_, version) = await SeedVersionAsync(db, ProfileVersionStates.Building);
        var (worker, stub, _) = NewWorker(postgres.ConnectionString);
        // the recovery path never dispatches in the same opening pass for a `building` version —
        // actually it does: the job opens `pending` and the same cycle's dispatch phase picks it up.
        stub.Answer = _ => new HttpResponseMessage(HttpStatusCode.Accepted);

        await worker.RunCycleAsync(TestToken);

        var job = await db.BuildJobs.AsNoTracking().SingleAsync();
        Assert.Equal(version.Id, job.AgentProfileVersionId);
        await db.Entry(version).ReloadAsync();
        Assert.Equal(ProfileVersionStates.Building, version.State);
    }
}
