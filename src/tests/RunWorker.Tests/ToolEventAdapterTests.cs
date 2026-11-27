using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System.Text.Json;
using PiAgent.Events;
using PiAgent.Events.Postgres;
using PiAgent.RunWorker;
using PiAgent.Tests.Postgres;
using Xunit;

namespace PiAgent.RunWorker.Tests;

/// <summary>
/// The route tool adapter (docs/run-worker.md "Communication tool callbacks"): the static
/// tool→event map against the agents/_runtime/tools.json communication dictionary, typed events on the root
/// item's stream (never Global), and the deterministic-garbage seatbelt.
/// </summary>
[Collection("PostgresLog")]
public sealed class ToolEventAdapterTests : IAsyncLifetime
{
    private readonly PostgresFixture _fx;
    private readonly EventQueueStore _log;

    private readonly RecordingProvisioner _docker = new();
    private readonly RunRegistryStore _store;
    private readonly RunWorkerConsumer _consumer;

    public ToolEventAdapterTests(PostgresFixture fx)
    {
        _fx = fx;
        _log = fx.Log;
        _store = RunContainerSchema.Store(fx);
        _consumer = new RunWorkerConsumer(
            _docker, _log, _log, _store,
            Options.Create(new RunWorkerOptions { WorkspaceRoot = Path.GetTempPath() }),
            NullLogger<RunWorkerConsumer>.Instance);
    }

    public Task InitializeAsync()
    {
        return ResetAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task ResetAsync()
    {
        await _fx.ResetCoreAsync();
        await RunContainerSchema.EnsureAsync(_fx);
        await RunContainerSchema.ResetAsync(_fx);
    }

    private static RunEntry Entry() => new(
        RunId: 7, TaskId: 3, WorkflowId: 2,
        Input: new RunInput(
            Owner: "o", Repo: "r", ItemNumber: 1, PrNumber: 12,
            Command: "review", PlanCommentId: 0, Profile: "reviewer",
            AgentBranch: null, DefaultBranch: "main", ReviewHeadSha: "cafe01"),
        ContainerName: "pi-session-7", Token: "tok", StartedUtc: DateTime.UtcNow);

    private async Task RegisterRun()
    {
        // the registry reads run facts back from orch_run — seed it with the same input
        await RunContainerSchema.SeedRunAsync(_fx, Entry().RunId, Entry().TaskId,
            input: System.Text.Json.JsonSerializer.Serialize(Entry().Input,
                new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)));
        await _store.InsertAsync(Entry(), Entry().ContainerName);
    }

    private async Task DeliverToolAsync(string toolName, string argsJson)
    {
        await _consumer.HandleAsync(
            new AgentCallback(7, "tool", Body: Body(toolName, argsJson)),
            attempt: 1, CancellationToken.None);
    }

    private static string Body(string toolName, string argsJson) =>
        "{\"eventId\":\"7-tool-1\",\"occurredAt\":\"2026-01-01T00:00:00Z\",\"toolName\":\""
        + toolName + "\",\"args\":" + argsJson + "}";

    [Fact]
    public async Task Save_plan_callback_becomes_PlanSaved_on_the_item_stream_with_steps()
    {
        await RegisterRun();
        await DeliverToolAsync("save_plan",
            """{"goals":"g","scope":"s","acceptanceCriteria":"a","steps":["read","write"]}""");

        var saved = Assert.Single((await _fx.AppendedAsync()).OfType<PlanSaved>());
        Assert.Equal(new ItemRef("o", "r", 1).Key, saved.StreamKey); // the root item's stream, not Global
        Assert.Equal(2, saved.WorkflowId);
        Assert.True(JsonElement.DeepEquals(JsonSerializer.SerializeToElement(new[] { "read", "write" }), saved.Steps ?? default));
    }

    [Fact]
    public async Task Submit_pr_description_maps_to_the_registered_PrDescriptionSubmitted()
    {
        Assert.Contains(nameof(PrDescriptionSubmitted), EventRegistry.Types.Keys); // in the dictionary

        await RegisterRun();
        await DeliverToolAsync("submit_pr_description",
            """{"title":"Add thing","body":"does the thing"}""");

        var submitted = Assert.Single((await _fx.AppendedAsync()).OfType<PrDescriptionSubmitted>());
        Assert.Equal("Add thing", submitted.Title);
        Assert.Equal("does the thing", submitted.Body);
        Assert.Equal(12, submitted.PrNumber); // the Run's linked PR, from the run input
        Assert.Equal(7, submitted.RunId);
        Assert.Equal(new ItemRef("o", "r", 1).Key, submitted.StreamKey);
    }

    [Theory]
    [InlineData("request_clarification",
        """{"question":"which env?","context":"staging is ambiguous"}""", nameof(ClarificationRequested))]
    [InlineData("mcp__communication__report_blocker",
        """{"description":"d","cause":"c","options":["opt"]}""", nameof(BlockerReported))]
    [InlineData("submit_review_comments",
        """{"comments":[{"file":"a.cs","line":3,"severity":"major","message":"m"}]}""",
        nameof(ReviewCommentsSubmitted))]
    [InlineData("request_decision",
        """{"issue":"i","context":"c","options":["fixes"]}""", nameof(ReviewDecisionRequested))]
    [InlineData("report_bug",
        """{"summary":"s","reproSteps":["r1"],"testResults":"t"}""", nameof(BugReported))]
    [InlineData("report_env_issue",
        """{"description":"d","whatIsNeeded":"n"}""", nameof(EnvIssueReported))]
    [InlineData("request_criteria_clarification",
        """{"question":"q","context":"c"}""", nameof(CriteriaClarificationRequested))]
    [InlineData("report_tests_passed",
        """{"summary":"all green"}""", nameof(TestsPassed))]
    public async Task Communication_tool_callbacks_translate_to_their_typed_events(
        string toolName, string argsJson, string expectedEventType)
    {
        await RegisterRun();
        await DeliverToolAsync(toolName, argsJson);

        var typed = (await _fx.AppendedAsync()).Where(e => e is not AgentCallback).ToList();
        var evt = Assert.Single(typed);
        Assert.Equal(expectedEventType, evt.GetType().Name);
        Assert.Equal(new ItemRef("o", "r", 1).Key, evt.StreamKey);
    }

    [Fact]
    public void Map_covers_the_communication_carrier_of_the_tool_catalog()
    {
        // the contract: agents/_runtime/tools.json carrier "communication" — eleven names;
        // ten ride the map, report_completion is the Bridge's settle mandate (terminal route)
        var docPath = Path.Combine(AppContext.BaseDirectory,
            "..", "..", "..", "..", "..", "..", "agents", "_runtime", "tools.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(docPath));
        var documented = doc.RootElement.GetProperty("tools").EnumerateObject()
            .Where(t => t.Value.GetProperty("carrier").GetString() == "communication")
            .Select(t => t.Name)
            .ToHashSet();

        Assert.Equal(11, documented.Count);
        Assert.Equal(documented, ToolEventMapKeys().Append("report_completion").ToHashSet());
    }

    private static IEnumerable<string> ToolEventMapKeys()
    {
        // via the seatbelt: an unknown name is garbage, every mapped name is not
        foreach (var name in new[]
                 {
                     "save_plan", "request_clarification", "report_blocker", "submit_review_comments",
                     "request_decision", "report_bug", "report_env_issue", "request_criteria_clarification",
                     "report_tests_passed", "submit_pr_description",
                 })
        {
            Assert.True(ToolEventAdapter.IsCommunicationTool(name), name);
            yield return name;
        }
    }

    [Fact]
    public async Task Unknown_tool_name_fails_the_run_with_the_cause()
    {
        await RegisterRun();
        await DeliverToolAsync("bash", "{\"command\":\"ls\"}");

        var failed = Assert.Single((await _fx.AppendedAsync()).OfType<RunFailed>());
        Assert.Contains("unknown communication tool", failed.Cause);
        Assert.Equal(new ItemRef("o", "r", 1).Key, failed.StreamKey);
        Assert.Equal(1, _docker.StopCalls); // the dead run's container is settled
        Assert.Empty(await _store.LoadUnfinishedAsync()); // nothing left in flight
        Assert.DoesNotContain(await _fx.AppendedAsync(), e => e is not (AgentCallback or RunFailed));
    }

    [Fact]
    public async Task Payload_not_matching_the_contract_shape_fails_the_run()
    {
        await RegisterRun();
        await DeliverToolAsync("save_plan", "{\"goals\":\"g\"}"); // scope/criteria/steps missing

        var failed = Assert.Single((await _fx.AppendedAsync()).OfType<RunFailed>());
        Assert.Contains("save_plan", failed.Cause);
        Assert.Empty(await _store.LoadUnfinishedAsync());
    }

    [Fact]
    public async Task Callback_without_a_body_is_deterministic_garbage()
    {
        await RegisterRun();
        await _consumer.HandleAsync(new AgentCallback(7, "tool", Body: null),
            attempt: 1, CancellationToken.None);

        Assert.Single((await _fx.AppendedAsync()).OfType<RunFailed>());
    }

    [Fact]
    public async Task Callback_for_an_already_settled_run_is_ignored()
    {
        await DeliverToolAsync("save_plan", """{"goals":"g","scope":"s","acceptanceCriteria":"a","steps":[]}""");

        Assert.Empty(await _fx.AppendedAsync()); // the watchdog already settled this run — no envelope, no event
    }
}
