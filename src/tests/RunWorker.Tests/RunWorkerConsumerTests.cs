using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PiAgent.Events;
using PiAgent.Events.Postgres;
using PiAgent.RunWorker;
using PiAgent.Tests.Postgres;
using PiAgent.RunWorker.Docker;
using Xunit;

namespace PiAgent.RunWorker.Tests;

/// <summary>
/// The run worker's input contract (docs/run-worker.md "Run input (pointers v2)"):
/// input.json v2 in the run tree, the entry-facing pointer subset in ContainerPlan.env,
/// and the completed callback's git facts carried into RunCompleted.
/// </summary>
[Collection("PostgresLog")]
public sealed class RunWorkerConsumerTests : IAsyncLifetime
{
    private readonly PostgresFixture _fx;
    private readonly EventQueueStore _log;

    public Task InitializeAsync() => _fx.ResetCoreAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private readonly RecordingProvisioner _docker = new();
    private readonly RunRegistry _registry = new();
    private readonly string _workspace;
    private readonly RunWorkerConsumer _consumer;

    public RunWorkerConsumerTests(PostgresFixture fx)
    {
        _fx = fx;
        _log = fx.Log;
        _workspace = Path.Combine(Path.GetTempPath(), "pi-runworker-tests", Guid.NewGuid().ToString("N"));
        _consumer = new RunWorkerConsumer(
            _registry, _docker, _log,
            Options.Create(new RunWorkerOptions { WorkspaceRoot = _workspace }),
            NullLogger<RunWorkerConsumer>.Instance);
    }

    /// <summary>A consumer over explicit provisioning paths (the secret/bind test variants).</summary>
    private RunWorkerConsumer ConsumerWith(
        string? secretsDir = null, string? secretsHostDir = null,
        string? workspaceHostRoot = null, string? stubBehavior = null) =>
        new(_registry, _docker, _log,
            Options.Create(new RunWorkerOptions
            {
                WorkspaceRoot = _workspace,
                SecretsDir = secretsDir ?? "",
                SecretsHostDir = secretsHostDir ?? "",
                WorkspaceHostRoot = workspaceHostRoot ?? "",
                StubBehavior = stubBehavior ?? "succeed",
            }),
            NullLogger<RunWorkerConsumer>.Instance);

    private static RunInput PlannerInput() => new(
        Owner: "o", Repo: "r", ItemNumber: 1, PrNumber: null,
        Command: "/plan", PlanCommentId: 0, Profile: "planner",
        AgentBranch: null, DefaultBranch: "main", ReviewHeadSha: null);

    private async Task StartRunAsync(RunInput input, long runId = 7,
        IReadOnlyList<string>? secrets = null)
    {
        await _consumer.HandleAsync(new RunScheduled(runId, TaskId: 3, WorkflowId: 2, input, secrets),
            attempt: 1, CancellationToken.None);
    }

    [Fact]
    public async Task Scheduled_run_writes_input_json_v2_and_exports_the_pointer_env()
    {
        await StartRunAsync(PlannerInput());

        var inputJson = await File.ReadAllTextAsync(Path.Combine(_workspace, "run-7", "input.json"));
        using var doc = JsonDocument.Parse(inputJson);
        Assert.Equal("planner", doc.RootElement.GetProperty("profile").GetString());
        Assert.Equal("main", doc.RootElement.GetProperty("defaultBranch").GetString());
        Assert.True(doc.RootElement.TryGetProperty("agentBranch", out var agentBranch)
            && agentBranch.ValueKind == JsonValueKind.Null);
        Assert.True(doc.RootElement.TryGetProperty("reviewHeadSha", out var pin)
            && pin.ValueKind == JsonValueKind.Null);

        var env = _docker.LastPlan!.Env;
        Assert.Contains("GIT_CLONE_REPO=o/r", env);
        Assert.Contains("GIT_CLONE_BRANCH=main", env);
        // rows without a SessionId stay bootable: the run id is the legacy session identity
        Assert.Contains("PI_SESSION_ID=7", env);
        // the default branch as its own pointer: the reviewer entry derives the review base itself
        Assert.Contains("GIT_DEFAULT_BRANCH=main", env);
        Assert.DoesNotContain(env, e => e.StartsWith("REVIEW_HEAD_SHA="));
        Assert.DoesNotContain(env, e => e.StartsWith("REVIEW_PR_NUMBER="));
        Assert.DoesNotContain(env, e => e.StartsWith("REVIEW_BASE_SHA=")); // the review base is derived, never exported
    }

    [Fact]
    public async Task Profile_secrets_bind_read_only_into_the_agent_container()
    {
        var secretsDir = Path.Combine(_workspace, "secrets");
        Directory.CreateDirectory(secretsDir);
        await File.WriteAllTextAsync(Path.Combine(secretsDir, "github-write-pat.txt"), "token");
        var consumer = ConsumerWith(secretsDir: secretsDir, secretsHostDir: "/host/secrets",
            workspaceHostRoot: "/host/runs");

        await consumer.HandleAsync(
            new RunScheduled(7, TaskId: 3, WorkflowId: 2, PlannerInput(),
                Secrets: ["github-write-pat.txt"]),
            attempt: 1, CancellationToken.None);

        var binds = _docker.LastPlan!.Binds;
        // the per-Run tree and the secret files mount by their HOST paths, the secret read-only
        Assert.Contains("/host/runs/run-7/workspace:/workspace", binds);
        Assert.Contains("/host/runs/run-7/logs:/logs", binds);
        Assert.Contains("/host/runs/run-7/input.json:/run/input.json:ro", binds);
        Assert.Contains("/host/secrets/github-write-pat.txt:/run/secrets/github-write-pat.txt:ro", binds);
        // secrets never enter env vars, images, or input.json — the mount is the only delivery
        Assert.DoesNotContain(_docker.LastPlan.Env, e => e.Contains("token"));
    }

    [Fact]
    public async Task A_missing_profile_secret_fails_the_run_before_the_container_exists()
    {
        var secretsDir = Path.Combine(_workspace, "secrets-missing");
        Directory.CreateDirectory(secretsDir); // no github-write-pat.txt inside
        var consumer = ConsumerWith(secretsDir: secretsDir, secretsHostDir: "/host/secrets");

        await consumer.HandleAsync(
            new RunScheduled(7, TaskId: 3, WorkflowId: 2, PlannerInput(),
                Secrets: ["github-write-pat.txt"]),
            attempt: 1, CancellationToken.None);

        var failed = Assert.Single((await _fx.AppendedAsync()).OfType<RunFailed>());
        Assert.Contains("secret_missing", failed.Cause);
        Assert.Contains("github-write-pat.txt", failed.Cause);
        Assert.Null(_docker.LastPlan);      // fail closed: nothing was provisioned
        Assert.Null(_registry.Get(7));      // nothing in flight
    }

    [Fact]
    public async Task The_stub_behavior_lever_rides_the_entry_env()
    {
        var consumer = ConsumerWith(stubBehavior: "no_completion");

        await consumer.HandleAsync(
            new RunScheduled(7, TaskId: 3, WorkflowId: 2, PlannerInput() with { Profile = "stub" }),
            attempt: 1, CancellationToken.None);

        Assert.Contains("STUB_BEHAVIOR=no_completion", _docker.LastPlan!.Env);
    }

    [Fact]
    public async Task Fix_round_clones_the_agent_branch()
    {
        var input = PlannerInput() with
        {
            Profile = "coder",
            Command = "implement",
            AgentBranch = "ai/task-9",
        };
        await StartRunAsync(input);

        Assert.Contains("GIT_CLONE_BRANCH=ai/task-9", _docker.LastPlan!.Env);
        Assert.Contains("GIT_CLONE_REPO=o/r", _docker.LastPlan.Env);
        Assert.DoesNotContain(_docker.LastPlan.Env, e => e.StartsWith("REVIEW_"));
    }

    [Fact]
    public async Task Review_run_carries_its_pins_in_the_entry_env()
    {
        var input = PlannerInput() with
        {
            Profile = "reviewer",
            Command = "review",
            PrNumber = 12,
            ReviewHeadSha = "cafe01",
        };
        await StartRunAsync(input);

        var env = _docker.LastPlan!.Env;
        Assert.Contains("REVIEW_HEAD_SHA=cafe01", env);
        Assert.Contains("REVIEW_PR_NUMBER=12", env);
        Assert.Contains("GIT_CLONE_REPO=o/r", env);
        // the reviewer derives the base itself: the default branch travels, a base SHA never does
        Assert.Contains("GIT_DEFAULT_BRANCH=main", env);
        Assert.DoesNotContain(env, e => e.StartsWith("REVIEW_BASE_SHA="));
    }

    [Fact]
    public async Task The_session_id_rides_the_entry_env()
    {
        await StartRunAsync(PlannerInput() with { SessionId = "abc123" });

        Assert.Contains("PI_SESSION_ID=abc123", _docker.LastPlan!.Env);
    }

    [Fact]
    public async Task A_callback_from_another_session_is_skipped_entirely()
    {
        // the stale-spool scenario (docs/run-session-id.md "Instance-scoped callbacks"): a
        // previous DB lifetime's terminal must not settle the fresh run
        await StartRunAsync(PlannerInput() with { SessionId = "fresh" });

        await _consumer.HandleAsync(new AgentCallback(7, "completed",
            """{"sessionId":"stale","result":{"branch":"old","commitSha":"dead"}}"""),
            attempt: 1, CancellationToken.None);

        Assert.DoesNotContain(await _fx.AppendedAsync(),
            e => e is RunCompleted or RunCancelled or RunFailed); // nothing settled
        Assert.NotNull(_registry.Get(7)); // the run stays in flight
    }

    [Fact]
    public async Task A_callback_from_the_current_session_settles_normally()
    {
        await StartRunAsync(PlannerInput() with { SessionId = "fresh" });

        await _consumer.HandleAsync(new AgentCallback(7, "completed",
            """{"sessionId":"fresh","result":{"branch":"ai/task-1","commitSha":"c0ffee"}}"""),
            attempt: 1, CancellationToken.None);

        var completed = Assert.Single((await _fx.AppendedAsync()).OfType<RunCompleted>());
        Assert.Equal("ai/task-1", completed.Branch);
    }

    [Fact]
    public async Task A_callback_without_a_sessionId_is_skipped_too()
    {
        // strict guard: after the session-id rollout a session-less body has no legitimate
        // sender (docs/run-session-id.md) — pre-fix spool entries included
        await StartRunAsync(PlannerInput() with { SessionId = "fresh" });

        await _consumer.HandleAsync(new AgentCallback(7, "completed",
            """{"result":{"branch":"ai/task-1","commitSha":"c0ffee"}}"""),
            attempt: 1, CancellationToken.None);

        Assert.DoesNotContain(await _fx.AppendedAsync(), e => e is RunCompleted);
        Assert.NotNull(_registry.Get(7));
    }

    [Fact]
    public async Task Completed_callback_git_facts_become_RunCompleted_pointers()
    {
        await StartRunAsync(PlannerInput());

        await _consumer.HandleAsync(
            new AgentCallback(7, "completed",
                """{"result":{"branch":"ai/task-1","commitSha":"c0ffee"}}"""),
            attempt: 1, CancellationToken.None);

        var completed = Assert.Single((await _fx.AppendedAsync()).OfType<RunCompleted>());
        Assert.Equal("ai/task-1", completed.Branch);
        Assert.Equal("c0ffee", completed.CommitSha);
    }

    [Fact]
    public async Task Completed_callback_without_git_facts_still_settles_the_run()
    {
        await StartRunAsync(PlannerInput());

        await _consumer.HandleAsync(new AgentCallback(7, "completed", Body: null),
            attempt: 1, CancellationToken.None);

        var completed = Assert.Single((await _fx.AppendedAsync()).OfType<RunCompleted>());
        Assert.Null(completed.Branch);
        Assert.Null(completed.CommitSha);
    }
}
