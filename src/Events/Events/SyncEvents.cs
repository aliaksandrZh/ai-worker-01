namespace PiAgent.Events;

/// <summary>events.md "Sync events (Poller → EventLog)" — raw normalized GitHub state changes.
/// Sync-emitted records carry a content-fingerprinted EventId (RootEvent): the EventLog's
/// unique (type, eventId) index drops a re-detected identical change silently (the poller's
/// overlap window re-fetches by design; the fingerprint makes the re-fetch harmless — main
/// poller's CommentKey idea, ported to the log).</summary>

/// <summary>Issue/PR: title, body, state, labels — the item's whole visible state in one
/// record; the eventId fingerprints all of it, so a label change is an item change.</summary>
public sealed record ItemChanged : RepositoryEvent
{
    public required long Number { get; init; }
    public required bool IsPullRequest { get; init; }
    public required string Title { get; init; }
    public required string Body { get; init; }
    public required string State { get; init; }
    public required IReadOnlyList<string> Labels { get; init; }

    public override string StreamKey => new ItemRef(Owner, Repo, Number).Key;
}

/// <summary>Explicit terminal beat when the root item closes — closes the Workflow.</summary>
public sealed record ItemClosed : RepositoryEvent
{
    public required long Number { get; init; }

    public override string StreamKey => new ItemRef(Owner, Repo, Number).Key;
}

/// <summary>Comment added / edited / deleted (tombstone flag — deletions stay visible).</summary>
public sealed record CommentChanged : RepositoryEvent
{
    public required long ItemNumber { get; init; }
    public required long CommentId { get; init; }
    public required string Author { get; init; }
    public required string Body { get; init; }
    public required bool Deleted { get; init; }

    public override string StreamKey => new ItemRef(Owner, Repo, ItemNumber).Key;
}

/// <summary>Submitted review: verdict + inline comments.</summary>
public sealed record ReviewAdded : RepositoryEvent
{
    public required long PrNumber { get; init; }
    public required long ReviewId { get; init; }
    public required string Author { get; init; }
    public required string Verdict { get; init; }
    public required string Body { get; init; }

    public override string StreamKey => new ItemRef(Owner, Repo, PrNumber).Key;
}

/// <summary>New commits land on the PR — starts the next review round.</summary>
public sealed record PrUpdated : RepositoryEvent
{
    public required long PrNumber { get; init; }
    public required string HeadSha { get; init; }

    public override string StreamKey => new ItemRef(Owner, Repo, PrNumber).Key;
}

/// <summary>Board card: column, link to its item (Projects v2).</summary>
public sealed record ProjectItemChanged : RootEvent
{
    public required string Owner { get; init; }
    public required int ProjectNumber { get; init; }
    public required string ItemNodeId { get; init; }
    public required string? Column { get; init; }
    public required string? LinkedOwner { get; init; }
    public required string? LinkedRepo { get; init; }
    public required long? LinkedItemNumber { get; init; }

    public override string StreamKey => LinkedOwner is null
        ? Streams.Repo(Owner, "board")
        : new ItemRef(LinkedOwner, LinkedRepo!, LinkedItemNumber!.Value).Key;
}
