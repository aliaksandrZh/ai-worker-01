using System.Text.Json;
using PiAgent.Events;

namespace PiAgent.RunWorker;

/// <summary>
/// Deterministic garbage seatbelt (docs/run-worker.md route tool): an unknown tool name or
/// a payload that does not match the contract shape fails the Run with the cause — the
/// Orchestrator's auto-retry policy schedules the fresh Run. Transient failures throw and
/// ride the EventLog's redelivery → dead-letter.
/// </summary>
public sealed class ToolCallbackException(string message) : Exception(message);

/// <summary>One observed communication tool call (docs/run-worker.md route tool body).</summary>
public sealed record ToolCallback(string EventId, string ToolName, JsonElement Args);

/// <summary>
/// The static tool→event map (docs/run-worker.md "Communication tool callbacks (route
/// tool)"): each of the ten payload-carrying communication tools of docs/flows/tools.md
/// translates into exactly one typed event — the payload record from the call's args plus
/// the envelope from the Run context — appended to the root item's stream (not Global).
/// The agent's tool manifest is not dragged into .NET: the map is contract-checked
/// against tools.md by a test. report_completion is deliberately absent — the Bridge
/// renders its observed call as the terminal `completed` callback (the settle mandate),
/// so the route tool handler must not map it (the consumer skips the settle marker).
/// </summary>
public static class ToolEventAdapter
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static bool IsCommunicationTool(string toolName) => Map.ContainsKey(Normalize(toolName));

    /// <summary>report_completion rides the route too (the Bridge's vocabulary includes it) but
    /// carries no payload event: its observation is the terminal-completion mandate. The
    /// consumer skips it instead of failing the Run as unknown.</summary>
    public static bool IsSettleMarker(string toolName) => Normalize(toolName) is "report_completion";

    /// <summary>The Bridge normalizes the call name to its last `__` segment; the seatbelt repeats it.</summary>
    public static string Normalize(string toolName)
    {
        var index = toolName.LastIndexOf("__", StringComparison.Ordinal);
        return index >= 0 ? toolName[(index + 2)..] : toolName;
    }

    /// <summary>Parses the route tool body { eventId, occurredAt, toolName, args }.</summary>
    public static ToolCallback Parse(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
            throw new ToolCallbackException("tool callback without a body");

        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (!root.TryGetProperty("toolName", out var name) || name.ValueKind is not JsonValueKind.String)
                throw new ToolCallbackException("tool callback without a toolName");
            if (!root.TryGetProperty("args", out var args))
                throw new ToolCallbackException("tool callback without args");

            var eventId = root.TryGetProperty("eventId", out var id) && id.ValueKind is JsonValueKind.String
                ? id.GetString()!
                : "";
            return new ToolCallback(eventId, Normalize(name.GetString()!), args.Clone());
        }
        catch (JsonException ex)
        {
            throw new ToolCallbackException($"tool callback body is not JSON: {ex.Message}");
        }
    }

    /// <summary>Translates one observed call into the typed event of docs/flows/events.md.</summary>
    public static IEvent Translate(ToolCallback callback, RunEntry entry)
    {
        if (!Map.TryGetValue(callback.ToolName, out var build))
            throw new ToolCallbackException(
                $"unknown communication tool '{callback.ToolName}' (route tool vocabulary: docs/flows/tools.md)");

        try
        {
            return build(callback.Args, entry);
        }
        catch (ToolCallbackException)
        {
            throw;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or ArgumentException)
        {
            // the cause travels to a user-facing comment — never leak raw framework text
            throw new ToolCallbackException(
                $"tool {callback.ToolName}: payload does not match the contract shape (field '{(ex is JsonException j ? j.Path : callback.ToolName)}')");
        }
    }

    private static T Payload<T>(JsonElement args) where T : class
    {
        if (args.ValueKind is not JsonValueKind.Object)
            throw new InvalidOperationException($"payload is {args.ValueKind}, expected an object");
        return args.Deserialize<T>(Json)
            ?? throw new InvalidOperationException("payload deserialized to null");
    }

    /// <summary>The Run context envelope every typed event rides on.</summary>
    private static (string Owner, string Repo, long ItemNumber) Envelope(RunEntry entry) =>
        (entry.Input.Owner, entry.Input.Repo, entry.Input.ItemNumber);

    private static readonly IReadOnlyDictionary<string, Func<JsonElement, RunEntry, IEvent>> Map =
        new Dictionary<string, Func<JsonElement, RunEntry, IEvent>>(StringComparer.Ordinal)
        {
            ["save_plan"] = (args, entry) =>
            {
                var p = Payload<PlanPayload>(args);
                var (owner, repo, item) = Envelope(entry);
                return new PlanSaved
                {
                    Owner = owner,
                    Repo = repo,
                    ItemNumber = item,
                    WorkflowId = entry.WorkflowId,
                    Goals = p.Goals,
                    Scope = p.Scope,
                    AcceptanceCriteria = p.AcceptanceCriteria,
                    Steps = p.Steps,
                };
            },
            ["request_clarification"] = (args, entry) =>
            {
                var p = Payload<ClarificationPayload>(args);
                var (owner, repo, item) = Envelope(entry);
                return new ClarificationRequested { Owner = owner, Repo = repo, ItemNumber = item, RunId = entry.RunId, Question = p.Question, Context = p.Context };
            },
            ["report_blocker"] = (args, entry) =>
            {
                var p = Payload<BlockerPayload>(args);
                var (owner, repo, item) = Envelope(entry);
                return new BlockerReported { Owner = owner, Repo = repo, ItemNumber = item, RunId = entry.RunId, Description = p.Description, Cause = p.Cause, Options = p.Options };
            },
            ["submit_review_comments"] = (args, entry) =>
            {
                var p = Payload<ReviewComments>(args);
                var (owner, repo, item) = Envelope(entry);
                return new ReviewCommentsSubmitted
                {
                    Owner = owner,
                    Repo = repo,
                    ItemNumber = item,
                    PrNumber = entry.Input.PrNumber ?? 0,
                    RunId = entry.RunId,
                    Comments = p.Comments,
                };
            },
            ["request_decision"] = (args, entry) =>
            {
                var p = Payload<DecisionPayload>(args);
                var (owner, repo, item) = Envelope(entry);
                return new ReviewDecisionRequested
                {
                    Owner = owner,
                    Repo = repo,
                    ItemNumber = item,
                    RunId = entry.RunId,
                    Issue = p.Issue,
                    Context = p.Context,
                    Options = p.Options,
                };
            },
            ["report_bug"] = (args, entry) =>
            {
                var p = Payload<BugPayload>(args);
                var (owner, repo, item) = Envelope(entry);
                return new BugReported
                {
                    Owner = owner,
                    Repo = repo,
                    ItemNumber = item,
                    TaskId = entry.TaskId,
                    Summary = p.Summary,
                    ReproSteps = p.ReproSteps,
                    TestResults = p.TestResults,
                };
            },
            ["report_env_issue"] = (args, entry) =>
            {
                var p = Payload<EnvIssuePayload>(args);
                var (owner, repo, item) = Envelope(entry);
                return new EnvIssueReported { Owner = owner, Repo = repo, ItemNumber = item, TaskId = entry.TaskId, Description = p.Description, WhatIsNeeded = p.WhatIsNeeded };
            },
            ["request_criteria_clarification"] = (args, entry) =>
            {
                var p = Payload<ClarificationPayload>(args);
                var (owner, repo, item) = Envelope(entry);
                return new CriteriaClarificationRequested
                {
                    Owner = owner,
                    Repo = repo,
                    ItemNumber = item,
                    TaskId = entry.TaskId,
                    Question = p.Question,
                    Context = p.Context,
                };
            },
            ["report_tests_passed"] = (args, entry) =>
            {
                var p = Payload<TestsSummary>(args);
                var (owner, repo, item) = Envelope(entry);
                return new TestsPassed { Owner = owner, Repo = repo, ItemNumber = item, TaskId = entry.TaskId, Summary = p.Summary };
            },
            ["submit_pr_description"] = (args, entry) =>
            {
                var p = Payload<PrDescription>(args);
                var (owner, repo, item) = Envelope(entry);
                return new PrDescriptionSubmitted
                {
                    Owner = owner,
                    Repo = repo,
                    ItemNumber = item,
                    PrNumber = entry.Input.PrNumber,
                    RunId = entry.RunId,
                    Title = p.Title,
                    Body = p.Body,
                };
            },
        };
}
