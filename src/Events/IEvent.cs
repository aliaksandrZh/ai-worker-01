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

/// <summary>
/// Common root of every event payload. Fields shared by all events live here once — a new
/// common field is added to this record and exists on every event. Property names match the
/// historical positional payloads, so stored JSON stays compatible.
/// </summary>
public abstract record RootEvent : IEvent
{
    public abstract string StreamKey { get; }

    /// <summary>Deterministic dedup key for the event log's unique (type, eventId) index.</summary>
    public string? EventId { get; init; }
}

/// <summary>An event scoped to one GitHub item: the item's owner/repo live here instead of in every record.</summary>
public abstract record RepositoryEvent : RootEvent
{
    public required string Owner { get; init; }
    public required string Repo { get; init; }
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
