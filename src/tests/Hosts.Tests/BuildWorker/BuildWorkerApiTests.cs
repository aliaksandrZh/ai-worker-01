using System.Net;
using System.Text;
using System.Text.Json;
using PiAgent.BuildWorker;
using Xunit;

namespace PiAgent.Hosts.Tests.BuildWorker;

/// <summary>
/// The Build Worker internal build API + runner over the POST/GET contract (04 §6, decision 81):
/// idempotency by buildId (202 as-is; payload conflict → 409), 404 for unknown ids, service-token
/// auth, terminal docker-build failures with a durable build log, pinned-context validation, and
/// execution-retry semantics against a fake Docker Engine.
/// </summary>
public class BuildWorkerApiTests : IAsyncLifetime
{
    private BuildWorkerHost? _host;

    private BuildWorkerHost Host => _host ?? throw new InvalidOperationException("host not started");

    public async Task InitializeAsync() => _host = await BuildTestSupport.NewHostAsync();
    public async Task DisposeAsync() => await _host!.DisposeAsync();

    private static readonly JsonSerializerOptions Wire = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    [Fact]
    public async Task Post_AcceptsImmediately_202WithThePinnedTag()
    {
        var buildId = Guid.NewGuid();
        var response = await Host.PostBuildAsync(buildId, "stub", 2);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var status = await ReadStatusAsync(response);
        Assert.Equal(buildId, status!.BuildId);
        Assert.Contains(status.State, new[] { BuildStates.Queued, BuildStates.Building });
        Assert.Equal("agent-stub:v2", status.ImageTag);
        Assert.NotNull(status.BuildLogReference);
        // the durable document exists before any terminal result
        Assert.NotNull(Host.Store.TryLoad(buildId));
    }

    [Fact]
    public async Task Post_ToCompletion_IdempotentReplayNeverDuplicatesTheBuild()
    {
        var buildId = Guid.NewGuid();
        await Host.PostBuildAsync(buildId, "stub", 2);
        var (terminal, status) = await Host.WaitTerminalAsync(buildId);

        Assert.True(terminal);
        Assert.Equal(BuildStates.Succeeded, status!.State);
        Assert.Equal("agent-stub:v2", status.ImageTag);
        Assert.StartsWith("sha256:", status.ImageId);
        Assert.True(Host.Docker.Builds.Count == 1, "the build must have run exactly once");
        Assert.True(File.Exists(status.BuildLogReference), "the build log must exist on the durable root");

        // a late duplicate POST replays the finished build as-is (202)
        var replay = await Host.PostBuildAsync(buildId, "stub", 2);
        Assert.Equal(HttpStatusCode.Accepted, replay.StatusCode);
        var replayStatus = await ReadStatusAsync(replay);
        Assert.Equal(BuildStates.Succeeded, replayStatus!.State);
        Assert.Single(Host.Docker.Builds);
    }

    [Fact]
    public async Task Post_SameBuildIdDifferentPayload_Conflicts()
    {
        var buildId = Guid.NewGuid();
        await Host.PostBuildAsync(buildId, "stub", 2);
        var conflicting = await Host.PostBuildAsync(buildId, "planner", 1);

        Assert.Equal(HttpStatusCode.Conflict, conflicting.StatusCode);
        Assert.Contains(BuildErrorCodes.BuildPayloadConflict, await conflicting.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Get_UnknownBuildId_Is404WithTheErrorCode()
    {
        var response = await Host.Client.GetAsync($"/internal/builds/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains(BuildErrorCodes.BuildNotFound, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Endpoints_RequireTheServiceCredential()
    {
        var buildId = Guid.NewGuid();
        Host.SetToken(null);
        const string ValidBody = """{"buildId":"00000000-0000-0000-0000-000000000001","profile":"stub","profileVersion":1,"configSnapshot":"{}","buildContext":{"directory":"config/agents/stub"}}""";
        var withoutToken = await Host.Client.PostAsync("/internal/builds", new StringContent(ValidBody, Encoding.UTF8, "application/json"));
        var getWithoutToken = await Host.Client.GetAsync($"/internal/builds/{buildId}");
        Host.SetToken("wrong-token");
        var withWrongToken = await Host.Client.GetAsync($"/internal/builds/{buildId}");
        Host.SetToken(BuildWorkerHost.OrchestratorToken);

        Assert.Equal(HttpStatusCode.Unauthorized, withoutToken.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, getWithoutToken.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, withWrongToken.StatusCode);
    }

    [Theory]
    [InlineData("""{"profile":"stub","profileVersion":1,"configSnapshot":"{}","buildContext":{"directory":"d"}}""")] // no buildId
    [InlineData("""{"buildId":"00000000-0000-0000-0000-000000000001","profile":"Bad Profile!","profileVersion":1,"configSnapshot":"{}","buildContext":{"directory":"d"}}""")] // bad profile name
    [InlineData("""{"buildId":"00000000-0000-0000-0000-000000000001","profile":"stub","profileVersion":0,"configSnapshot":"{}","buildContext":{"directory":"d"}}""")] // version ≤ 0
    [InlineData("""{"buildId":"00000000-0000-0000-0000-000000000001","profile":"stub","profileVersion":1,"buildContext":{"directory":"d"}}""")] // no snapshot
    [InlineData("""{"buildId":"00000000-0000-0000-0000-000000000001","profile":"stub","profileVersion":1,"configSnapshot":"{}"}""")] // no build context
    [InlineData("not json")] // body
    public async Task Post_MalformedPayload_Is400_WithTheErrorCode(string body)
    {
        var response = await Host.Client.PostAsync("/internal/builds", new StringContent(body, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(BuildErrorCodes.BuildPayloadInvalid, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task DockerBuildFailure_IsTerminal_CarriesErrorCodeAndDurableLog()
    {
        var buildId = Guid.NewGuid();
        Host.Docker.FailNext = FakeDockerEngine.Behavior.Fail;

        await Host.PostBuildAsync(buildId, "stub", 2);
        var (terminal, status) = await Host.WaitTerminalAsync(buildId);

        Assert.True(terminal);
        Assert.Equal(BuildStates.Failed, status!.State);
        Assert.Equal(BuildFailureCodes.DockerBuildFailed, status.ErrorCode);
        Assert.Contains("apt-get exited 1", status.ErrorMessage);
        Assert.True(File.Exists(status.BuildLogReference), "build-log reference must survive the failure");
        Assert.Contains("apt-get exited 1", await File.ReadAllTextAsync(status.BuildLogReference!));
        // terminal: never rebuilt afterwards (the sweep/runner skip finished docs)
        Assert.Single(Host.Docker.Builds);
    }

    [Fact]
    public async Task PinnedContext_MissingDockerfile_IsTerminalBuildContextInvalid()
    {
        var buildId = Guid.NewGuid();
        var snapshot = BuildSnapshots.BuildJson(new Dictionary<string, string> { ["profile.yaml"] = "description: x\n" });

        await Host.PostBuildAsync(buildId, "stub", 2, snapshot);
        var (terminal, status) = await Host.WaitTerminalAsync(buildId);

        Assert.True(terminal);
        Assert.Equal(BuildStates.Failed, status!.State);
        Assert.Equal(BuildFailureCodes.BuildContextInvalid, status.ErrorCode);
        Assert.Contains("Dockerfile", status.ErrorMessage);
    }

    [Fact]
    public async Task PinnedContext_HashMismatch_IsTerminalBuildContextInvalid()
    {
        var buildId = Guid.NewGuid();
        // the recorded hash pins "FROM ok"; the content says "FROM tampered" → version identity broken
        var snapshot = """{"files":[{"path":"Dockerfile","sha256":"HASHHERE"},{"path":"profile.yaml","sha256":"x"}],"file_contents":{"Dockerfile":"FROM tampered","profile.yaml":"y"}}"""
            .Replace("HASHHERE", BuildSnapshots.Sha256("FROM ok"), StringComparison.Ordinal);

        await Host.PostBuildAsync(buildId, "stub", 2, snapshot);
        var (terminal, status) = await Host.WaitTerminalAsync(buildId);

        Assert.True(terminal);
        Assert.Equal(BuildFailureCodes.BuildContextInvalid, status!.ErrorCode);
        Assert.Contains("hash mismatch", status.ErrorMessage);
    }

    [Fact]
    public async Task ExecutionFailure_RetriesTheSameBuildId_ThenSucceeds()
    {
        var buildId = Guid.NewGuid();
        Host.Docker.FailNext = FakeDockerEngine.Behavior.ExecuteError;

        await Host.PostBuildAsync(buildId, "stub", 2);
        var (terminal, status) = await Host.WaitTerminalAsync(buildId);

        Assert.True(terminal);
        Assert.Equal(BuildStates.Succeeded, status!.State); // transient failure retried (attempt 2 succeeds)
        Assert.Equal(2, Host.Docker.Builds.Count);
        Assert.All(Host.Docker.Builds, call => Assert.Equal("agent-stub:v2", call.Tag));
        var doc = Host.Store.TryLoad(buildId)!;
        Assert.Equal(1, doc.ExecutionAttempts);
        Assert.StartsWith("sha256:", doc.ImageDigest);
    }

    [Fact]
    public async Task ExecutionFailures_Exhausted_AreTerminal_BuildExecutionFailed()
    {
        var buildId = Guid.NewGuid();
        var host = await BuildTestSupport.NewHostAsync(configure: options =>
        {
            options.MaxExecutionAttempts = 2;
            options.ExecutionRetryDelaySeconds = 0;
        });
        try
        {
            host.Docker.FailNext = FakeDockerEngine.Behavior.ExecuteError;
            host.Docker.ExecuteErrorsRemaining = int.MaxValue; // every attempt fails until exhaustion

            await host.PostBuildAsync(buildId, "stub", 2);
            var (terminal, status) = await host.WaitTerminalAsync(buildId);

            Assert.True(terminal);
            Assert.Equal(BuildStates.Failed, status!.State);
            Assert.Equal(BuildFailureCodes.BuildExecutionFailed, status.ErrorCode);
            var doc = host.Store.TryLoad(buildId)!;
            Assert.Equal(2, doc.ExecutionAttempts);
        }
        finally
        {
            await host.DisposeAsync();
        }
    }

    /// <summary>Worker restart over the SAME (caller-owned) state root: a finished failed result stays
    /// retrievable, and terminal documents are never rebuilt by the resumed host.</summary>
    [Fact]
    public async Task WorkerRestart_FailedResultsAreRetrievable()
    {
        var stateRoot = Path.Combine(Path.GetTempPath(), "pi-build-worker-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var finished = Guid.NewGuid();
            var finishedHost = await BuildTestSupport.NewHostAsync(stateRoot: stateRoot);
            finishedHost.Docker.FailNext = FakeDockerEngine.Behavior.Fail;
            await finishedHost.PostBuildAsync(finished, "stub", 2);
            var (terminal, failedStatus) = await finishedHost.WaitTerminalAsync(finished);
            Assert.True(terminal);
            Assert.Equal(BuildStates.Failed, failedStatus!.State);
            await finishedHost.App.DisposeAsync(); // the "worker process" is gone; the root stays on disk

            var restarted = await BuildTestSupport.NewHostAsync(stateRoot: stateRoot);
            var after = await restarted.Client.GetAsync($"/internal/builds/{finished}");
            Assert.Equal(HttpStatusCode.OK, after.StatusCode); // durable local state kept the result
            var result = await ReadStatusAsync(after);
            Assert.Equal(BuildStates.Failed, result!.State);
            Assert.Equal(BuildFailureCodes.DockerBuildFailed, result.ErrorCode);
            Assert.Empty(restarted.Docker.Builds); // terminal documents are never rebuilt
            await restarted.App.DisposeAsync();
        }
        finally
        {
            Directory.Delete(stateRoot, recursive: true);
        }
    }

    /// <summary>An unfinished build that was mid-flight at restart finalizes from the existing image
    /// tag — the restart reconciler never rebuilds a finished image nor leaves a phantom.</summary>
    [Fact]
    public async Task WorkerRestart_UnfinishedBuildFinalizesFromTheExistingTag()
    {
        var stateRoot = Path.Combine(Path.GetTempPath(), "pi-build-worker-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var hanging = await BuildTestSupport.NewHostAsync(stateRoot: stateRoot);
            hanging.Docker.FailNext = FakeDockerEngine.Behavior.HangUntilCancellation;
            var buildId = Guid.NewGuid();
            await hanging.PostBuildAsync(buildId, "stub", 2);
            await WaitUntilBuildingAsync(hanging, buildId); // the runner is mid-build on the hung daemon
            await hanging.App.DisposeAsync(); // crash: the daemon build dies with the worker process

            var prebuilt = new FakeDockerEngine();
            prebuilt.Images["agent-stub:v2"] = "sha256:finished-while-down";
            var restarted = await BuildTestSupport.NewHostAsync(stateRoot: stateRoot, docker: prebuilt);
            var (terminal, status) = await restarted.WaitTerminalAsync(buildId);

            Assert.True(terminal);
            Assert.Equal(BuildStates.Succeeded, status!.State); // finalized from the existing tag
            Assert.Equal("sha256:finished-while-down", status.ImageId);
            Assert.Empty(restarted.Docker.Builds); // no phantom rebuild of a finished image
            await restarted.App.DisposeAsync();
        }
        finally
        {
            Directory.Delete(stateRoot, recursive: true);
        }
    }

    private static async Task WaitUntilBuildingAsync(BuildWorkerHost host, Guid buildId)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            var doc = host.Store.TryLoad(buildId);
            if (doc?.State == BuildStates.Building)
                return;
            await Task.Delay(20);
        }
        Assert.Fail("the build never started before the restart");
    }

    private static async Task<BuildResponse?> ReadStatusAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<BuildResponse>(body, Wire);
    }
}
