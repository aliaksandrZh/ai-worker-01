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
/// so the route tool handler never sees it.
/// </summary>
public static class ToolEventAdapter
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static bool IsCommunicationTool(string toolName) => Map.ContainsKey(Normalize(toolName));

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
            throw new ToolCallbackException(
                $"tool {callback.ToolName}: payload does not match the contract shape: {ex.Message}");
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
                return new PlanSaved(owner, repo, item, entry.WorkflowId,
                    p.Goals, p.Scope, p.AcceptanceCriteria, p.Steps);
            },
            ["request_clarification"] = (args, entry) =>
            {
                var p = Payload<ClarificationPayload>(args);
                var (owner, repo, item) = Envelope(entry);
                return new ClarificationRequested(owner, repo, item, entry.RunId, p.Question, p.Context);
            },
            ["report_blocker"] = (args, entry) =>
            {
                var p = Payload<BlockerPayload>(args);
                var (owner, repo, item) = Envelope(entry);
                return new BlockerReported(owner, repo, item, entry.RunId, p.Description, p.Cause, p.Options);
            },
            ["submit_review_comments"] = (args, entry) =>
            {
                var p = Payload<ReviewComments>(args);
                var (owner, repo, item) = Envelope(entry);
                return new ReviewCommentsSubmitted(owner, repo, item,
                    entry.Input.PrNumber ?? 0, entry.RunId, p.Comments);
            },
            ["request_decision"] = (args, entry) =>
            {
                var p = Payload<DecisionPayload>(args);
                var (owner, repo, item) = Envelope(entry);
                return new ReviewDecisionRequested(owner, repo, item,
                    entry.RunId, p.Issue, p.Context, p.Options);
            },
            ["report_bug"] = (args, entry) =>
            {
                var p = Payload<BugPayload>(args);
                var (owner, repo, item) = Envelope(entry);
                return new BugReported(owner, repo, item, entry.TaskId,
                    p.Summary, p.ReproSteps, p.TestResults);
            },
            ["report_env_issue"] = (args, entry) =>
            {
                var p = Payload<EnvIssuePayload>(args);
                var (owner, repo, item) = Envelope(entry);
                return new EnvIssueReported(owner, repo, item, entry.TaskId, p.Description, p.WhatIsNeeded);
            },
            ["request_criteria_clarification"] = (args, entry) =>
            {
                var p = Payload<ClarificationPayload>(args);
                var (owner, repo, item) = Envelope(entry);
                return new CriteriaClarificationRequested(owner, repo, item,
                    entry.TaskId, p.Question, p.Context);
            },
            ["report_tests_passed"] = (args, entry) =>
            {
                var p = Payload<TestsSummary>(args);
                var (owner, repo, item) = Envelope(entry);
                return new TestsPassed(owner, repo, item, entry.TaskId, p.Summary);
            },
            ["submit_pr_description"] = (args, entry) =>
            {
                var p = Payload<PrDescription>(args);
                var (owner, repo, item) = Envelope(entry);
                return new PrDescriptionSubmitted(owner, repo, item,
                    entry.Input.PrNumber, entry.RunId, p.Title, p.Body);
            },
        };
}
