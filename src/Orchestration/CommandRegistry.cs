using PiAgent.Orchestration.State;

namespace PiAgent.Orchestration;

/// <summary>
/// The command surface: trigger names map to task types (= flow stages). The trigger set
/// is defined separately from the flows (docs/flows/events.md) — this registry is that
/// definition's orchestration side.
/// </summary>
public static class CommandRegistry
{
    public static readonly IReadOnlyDictionary<string, string> CommandToTaskType = new Dictionary<string, string>
    {
        ["/plan"] = TaskTypes.Plan,
        ["/code"] = TaskTypes.Implement,
        ["/review"] = TaskTypes.Review,
        ["/test"] = TaskTypes.TestDesign,
    };

    public static string? TaskTypeFor(string command) =>
        CommandToTaskType.TryGetValue(command.ToLowerInvariant(), out var type) ? type : null;

    /// <summary>Agent profile per flow stage (the container's agent).</summary>
    public static string ProfileFor(string taskType) => taskType switch
    {
        TaskTypes.Plan => "planner",
        TaskTypes.Implement => "coder",
        TaskTypes.Review => "reviewer",
        TaskTypes.TestDesign => "testing",
        _ => "stub",
    };
}

/// <summary>GitHub permission tiers, lowest first.</summary>
public static class Permissions
{
    private static readonly string[] Order = ["read", "triage", "write", "maintain", "admin"];

    /// <summary>True when <paramref name="actual"/> is at least <paramref name="required"/>.</summary>
    public static bool AtLeast(string actual, string required)
    {
        var actualRank = Array.IndexOf(Order, actual.ToLowerInvariant());
        var requiredRank = Array.IndexOf(Order, required.ToLowerInvariant());
        return actualRank >= 0 && requiredRank >= 0 && actualRank >= requiredRank;
    }
}
