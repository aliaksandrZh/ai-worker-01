using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PiAgent.Events;
using PiAgent.Events.InMemory;
using PiAgent.RunWorker;
using PiAgent.RunWorker.Docker;
using Xunit;

namespace PiAgent.RunWorker.Tests;

/// <summary>
/// The run worker's input contract (docs/run-worker.md "Run input (pointers v2)"):
/// input.json v2 in the run tree, the entry-facing pointer subset in ContainerPlan.env,
/// and the completed callback's git facts carried into RunCompleted.
/// </summary>
public sealed class RunWorkerConsumerTests
{
    private readonly InMemoryEventStore _log = new();
    private readonly RecordingProvisioner _docker = new();
    private readonly RunRegistry _registry = new();
    private readonly string _workspace;
    private readonly RunWorkerConsumer _consumer;

    public RunWorkerConsumerTests()
    {
        _workspace = Path.Combine(Path.GetTempPath(), "pi-runworker-tests", Guid.NewGuid().ToString("N"));
        _consumer = new RunWorkerConsumer(
            _registry, _docker, _log,
            Options.Create(new RunWorkerOptions { WorkspaceRoot = _workspace }),
            NullLogger<RunWorkerConsumer>.Instance);
    }

    private static RunInput PlannerInput() => new(
        Owner: "o", Repo: "r", ItemNumber: 1, PrNumber: null,
        Command: "/plan", PlanCommentId: 0, Profile: "planner",
        AgentBranch: null, DefaultBranch: "main", ReviewHeadSha: null);

    private async Task StartRunAsync(RunInput input, long runId = 7)
    {
        await _consumer.HandleAsync(new RunScheduled(runId, TaskId: 3, WorkflowId: 2, input),
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
        Assert.DoesNotContain(env, e => e.StartsWith("REVIEW_HEAD_SHA="));
        Assert.DoesNotContain(env, e => e.StartsWith("REVIEW_PR_NUMBER="));
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
    }

    [Fact]
    public async Task Completed_callback_git_facts_become_RunCompleted_pointers()
    {
        await StartRunAsync(PlannerInput());

        await _consumer.HandleAsync(
            new AgentCallback(7, "completed",
                """{"result":{"branch":"ai/task-1","commitSha":"c0ffee"}}"""),
            attempt: 1, CancellationToken.None);

        var completed = Assert.Single(_log.Appended.OfType<RunCompleted>());
        Assert.Equal("ai/task-1", completed.Branch);
        Assert.Equal("c0ffee", completed.CommitSha);
    }

    [Fact]
    public async Task Completed_callback_without_git_facts_still_settles_the_run()
    {
        await StartRunAsync(PlannerInput());

        await _consumer.HandleAsync(new AgentCallback(7, "completed", Body: null),
            attempt: 1, CancellationToken.None);

        var completed = Assert.Single(_log.Appended.OfType<RunCompleted>());
        Assert.Null(completed.Branch);
        Assert.Null(completed.CommitSha);
    }
}
