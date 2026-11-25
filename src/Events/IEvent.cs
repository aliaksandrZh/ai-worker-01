namespace PiAgent.Events;

/// <summary>
/// A typed event payload. Every event type declares its own stream key — consumers never
/// deal with streams (docs/eventlog.md). Event types are the vocabulary of
/// docs/flows/events.md; adding an event means adding one record here.
/// </summary>
public interface IEvent
{
    /// <summary>The ordering unit this event belongs to: strict order inside, parallel across.</summary>
    string StreamKey { get; }
}

/// <summary>A GitHub item (issue or pull request) — the root of most streams.</summary>
public readonly record struct ItemRef(string Owner, string Repo, long Number)
{
    public string Key => $"item:{Owner}/{Repo}#{Number}";
}

public static class Streams
{
    /// <summary>Stream for events that cannot be tied to one item.</summary>
    public const string Global = "global";

    public static string Repo(string owner, string repo) => $"repo:{owner}/{repo}";
}
