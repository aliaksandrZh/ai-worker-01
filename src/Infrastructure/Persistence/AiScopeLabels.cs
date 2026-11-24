using System.Text.Json;

namespace PiAgent.Infrastructure.Persistence;

/// <summary>
/// The AI-scope labels — the standing per-item switch that decides whether the system may run
/// anything on a work item at all. Exact-match, one per task type: `AI:Plan` (plan tasks, including
/// the board Backlog→Clarifying trigger), `AI:Code` (implement), `AI:Review` (reviews), `AI:Test`
/// (test_design — not implemented yet, so it gates but never fires). A task-producing command or
/// system trigger on an item without its scope label is silently ignored (no reply, no task); the
/// owner adds the label, the next poll syncs it, and the converging conditions start the run.
/// Namespaced `AI:` — distinct from the lifecycle labels (`AI Scheduled`/…, <see cref="AiStateLabels"/>).
/// Read exclusively from the synced `work_items.labels` jsonb (the Poller refreshes it every pass —
/// a label appearing shows up within one poll interval; no extra GitHub calls).
/// </summary>
public static class AiScopeLabels
{
    public const string Plan = "AI:Plan";
    public const string Code = "AI:Code";
    public const string Review = "AI:Review";
    public const string Test = "AI:Test";

    /// <summary>Task type → its exact scope label.</summary>
    public static readonly IReadOnlyDictionary<string, string> LabelForTaskType =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [OrchestrationTaskTypes.Plan] = Plan,
            [OrchestrationTaskTypes.Implement] = Code,
            [OrchestrationTaskTypes.Review] = Review,
            [OrchestrationTaskTypes.TestDesign] = Test,
        };

    /// <summary>The synced label names on the work item; unreadable jsonb counts as none (fail closed —
    /// nothing runs on an item whose scope cannot be read).</summary>
    public static IReadOnlyList<string> LabelsOf(WorkItem workItem)
    {
        if (string.IsNullOrWhiteSpace(workItem.Labels))
            return [];
        try
        {
            using var document = JsonDocument.Parse(workItem.Labels);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                return [];
            return document.RootElement.EnumerateArray()
                .Where(e => e.ValueKind == JsonValueKind.String)
                .Select(e => e.GetString() ?? "")
                .ToList();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>The exact-match gate: does the item carry the scope label of this task type?</summary>
    public static bool HasScope(WorkItem workItem, string taskType) =>
        HasScope(workItem.Labels, taskType);

    /// <summary>Same gate over the raw synced labels jsonb (scanner projections carry the string only).</summary>
    public static bool HasScope(string? labelsJson, string taskType)
    {
        if (string.IsNullOrWhiteSpace(labelsJson))
            return false;
        try
        {
            using var document = JsonDocument.Parse(labelsJson);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                return false;
            return document.RootElement.EnumerateArray().Any(e =>
                e.ValueKind == JsonValueKind.String
                && string.Equals(e.GetString(), LabelForTaskType[taskType], StringComparison.Ordinal));
        }
        catch (JsonException)
        {
            return false; // unreadable scope → nothing runs (fail closed)
        }
    }
}
