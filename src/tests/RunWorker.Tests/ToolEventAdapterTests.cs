using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System.Text.Json;
using PiAgent.Events;
using PiAgent.Events.InMemory;
using PiAgent.RunWorker;
using Xunit;

namespace PiAgent.RunWorker.Tests;

/// <summary>
/// The route tool adapter (docs/run-worker.md "Communication tool callbacks"): the static
/// tool→event map against the docs/flows/tools.md dictionary, typed events on the root
/// item's stream (never Global), and the deterministic-garbage seatbelt.
/// </summary>
public sealed class ToolEventAdapterTests
{
    private readonly InMemoryEventStore _log = new();
    private readonly RecordingProvisioner _docker = new();
    private readonly RunRegistry _registry = new();
    private readonly RunWorkerConsumer _consumer;

    public ToolEventAdapterTests()
    {
        _consumer = new RunWorkerConsumer(
            _registry, _docker, _log,
            Options.Create(new RunWorkerOptions { WorkspaceRoot = Path.GetTempPath() }),
            NullLogger<RunWorkerConsumer>.Instance);
    }

    private static RunEntry Entry() => new(
        RunId: 7, TaskId: 3, WorkflowId: 2,
        Input: new RunInput(
            Owner: "o", Repo: "r", ItemNumber: 1, PrNumber: 12,
            Command: "review", PlanCommentId: 0, Profile: "reviewer",
            AgentBranch: null, DefaultBranch: "main", ReviewHeadSha: "cafe01"),
        ContainerName: "pi-session-7", Token: "tok", StartedUtc: DateTime.UtcNow);

    private void RegisterRun() => _registry.Add(Entry());

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
        RegisterRun();
        await DeliverToolAsync("save_plan",
            """{"goals":"g","scope":"s","acceptanceCriteria":"a","steps":["read","write"]}""");

        var saved = Assert.Single(_log.Appended.OfType<PlanSaved>());
        Assert.Equal(new ItemRef("o", "r", 1).Key, saved.StreamKey); // the root item's stream, not Global
        Assert.Equal(2, saved.WorkflowId);
        Assert.True(JsonElement.DeepEquals(JsonSerializer.SerializeToElement(new[] { "read", "write" }), saved.Steps ?? default));
    }

    [Fact]
    public async Task Submit_pr_description_maps_to_the_registered_PrDescriptionSubmitted()
    {
        Assert.Contains(nameof(PrDescriptionSubmitted), EventRegistry.Types.Keys); // in the dictionary

        RegisterRun();
        await DeliverToolAsync("submit_pr_description",
            """{"title":"Add thing","body":"does the thing"}""");

        var submitted = Assert.Single(_log.Appended.OfType<PrDescriptionSubmitted>());
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
        RegisterRun();
        await DeliverToolAsync(toolName, argsJson);

        var typed = _log.Appended.Where(e => e is not AgentCallback).ToList();
        var evt = Assert.Single(typed);
        Assert.Equal(expectedEventType, evt.GetType().Name);
        Assert.Equal(new ItemRef("o", "r", 1).Key, evt.StreamKey);
    }

    [Fact]
    public void Map_covers_the_communication_dictionary_of_tools_md()
    {
        // the contract: docs/flows/tools.md "## 2. Communication tools" — eleven names;
        // ten ride the map, report_completion is the Bridge's settle mandate (terminal route)
        var docPath = Path.Combine(AppContext.BaseDirectory,
            "..", "..", "..", "..", "..", "..", "docs", "flows", "tools.md");
        var markdown = File.ReadAllText(docPath);
        var section = markdown.Split("## 2. Communication tools")[1].Split("## Escalation pattern")[0];
        var documented = Regex.Matches(section, @"^### `(\w+)`", RegexOptions.Multiline)
            .Select(m => m.Groups[1].Value)
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
        RegisterRun();
        await DeliverToolAsync("bash", "{\"command\":\"ls\"}");

        var failed = Assert.Single(_log.Appended.OfType<RunFailed>());
        Assert.Contains("unknown communication tool", failed.Cause);
        Assert.Equal(new ItemRef("o", "r", 1).Key, failed.StreamKey);
        Assert.Equal(1, _docker.StopCalls); // the dead run's container is settled
        Assert.Null(_registry.Get(7));      // nothing left in flight
        Assert.DoesNotContain(_log.Appended, e => e is not (AgentCallback or RunFailed));
    }

    [Fact]
    public async Task Payload_not_matching_the_contract_shape_fails_the_run()
    {
        RegisterRun();
        await DeliverToolAsync("save_plan", "{\"goals\":\"g\"}"); // scope/criteria/steps missing

        var failed = Assert.Single(_log.Appended.OfType<RunFailed>());
        Assert.Contains("save_plan", failed.Cause);
        Assert.Null(_registry.Get(7));
    }

    [Fact]
    public async Task Callback_without_a_body_is_deterministic_garbage()
    {
        RegisterRun();
        await _consumer.HandleAsync(new AgentCallback(7, "tool", Body: null),
            attempt: 1, CancellationToken.None);

        Assert.Single(_log.Appended.OfType<RunFailed>());
    }

    [Fact]
    public async Task Callback_for_an_already_settled_run_is_ignored()
    {
        await DeliverToolAsync("save_plan", """{"goals":"g","scope":"s","acceptanceCriteria":"a","steps":[]}""");

        Assert.Empty(_log.Appended); // the watchdog already settled this run — no envelope, no event
    }
}
