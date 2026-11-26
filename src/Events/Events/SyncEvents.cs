namespace PiAgent.Events;

/// <summary>events.md "Sync events (Poller → EventLog)" — raw normalized GitHub state changes.
/// Sync-emitted records carry an optional content-fingerprinted EventId: the EventLog's unique
/// (type, eventId) index drops a re-detected identical change silently (the poller's overlap
/// window re-fetches by design; the fingerprint makes the re-fetch harmless — main poller's
/// CommentKey idea, ported to the log).</summary>

/// <summary>Issue/PR: title, body, state, labels — the item's whole visible state in one
/// record; the eventId fingerprints all of it, so a label change is an item change.</summary>
public sealed record ItemChanged(
    string Owner,
    string Repo,
    long Number,
    bool IsPullRequest,
    string Title,
    string Body,
    string State,
    IReadOnlyList<string> Labels,
    string? EventId = null) : IEvent
{
    public string StreamKey => new ItemRef(Owner, Repo, Number).Key;
}

/// <summary>Explicit terminal beat when the root item closes — closes the Workflow.</summary>
public sealed record ItemClosed(
    string Owner,
    string Repo,
    long Number,
    string? EventId = null) : IEvent
{
    public string StreamKey => new ItemRef(Owner, Repo, Number).Key;
}

/// <summary>Comment added / edited / deleted (tombstone flag — deletions stay visible).</summary>
public sealed record CommentChanged(
    string Owner,
    string Repo,
    long ItemNumber,
    long CommentId,
    string Author,
    string Body,
    bool Deleted,
    string? EventId = null) : IEvent
{
    public string StreamKey => new ItemRef(Owner, Repo, ItemNumber).Key;
}

/// <summary>Submitted review: verdict + inline comments.</summary>
public sealed record ReviewAdded(
    string Owner,
    string Repo,
    long PrNumber,
    long ReviewId,
    string Author,
    string Verdict,
    string Body,
    string? EventId = null) : IEvent
{
    public string StreamKey => new ItemRef(Owner, Repo, PrNumber).Key;
}

/// <summary>New commits land on the PR — starts the next review round.</summary>
public sealed record PrUpdated(
    string Owner,
    string Repo,
    long PrNumber,
    string HeadSha,
    string? EventId = null) : IEvent
{
    public string StreamKey => new ItemRef(Owner, Repo, PrNumber).Key;
}

/// <summary>Board card: column, link to its item (Projects v2).</summary>
public sealed record ProjectItemChanged(
    string Owner,
    int ProjectNumber,
    string ItemNodeId,
    string? Column,
    string? LinkedOwner,
    string? LinkedRepo,
    long? LinkedItemNumber) : IEvent
{
    public string StreamKey => LinkedOwner is null
        ? Streams.Repo(Owner, "board")
        : new ItemRef(LinkedOwner, LinkedRepo!, LinkedItemNumber!.Value).Key;
}

