namespace PiAgent.Events;

/// <summary>events.md "User events" — produced by user actions (or derived by the Poller).</summary>

/// <summary>
/// The Poller parsed a slash command (grammar, author permission and resolved target
/// travel in the payload); starts the mapped flow.
/// </summary>
public sealed record UserCommand : RepositoryEvent
{
    public required long ItemNumber { get; init; }
    public required string Command { get; init; }
    public required long CommentId { get; init; }
    public required string Author { get; init; }
    public required string AuthorPermission { get; init; }
    public required string? Text { get; init; }

    public override string StreamKey => new ItemRef(Owner, Repo, ItemNumber).Key;
}

/// <summary>
/// A decision: plan approved / changes requested; review escalation resolved; test
/// results accepted / bug found / does not meet requirements.
/// </summary>
public sealed record UserDecision : RepositoryEvent
{
    public required long ItemNumber { get; init; }
    public required DecisionKind Kind { get; init; }
    public required string Author { get; init; }
    public required string? Text { get; init; }

    public override string StreamKey => new ItemRef(Owner, Repo, ItemNumber).Key;
}

/// <summary>What the User decided (docs/flows events table).</summary>
public enum DecisionKind
{
    PlanApproved,
    PlanChangesRequested,
    ReviewFixes,
    ReviewChangeScope,
    ReviewAcceptRisk,
    TestsAccepted,
    TestsBugFound,
    TestsRequirementsNotMet,
}

/// <summary>The User cancels the workflow: runs stop, Task → cancelled, Workflow → cancelled.</summary>
public sealed record UserCancel : RepositoryEvent
{
    public required long ItemNumber { get; init; }
    public required string Author { get; init; }
    public required string? Reason { get; init; }

    public override string StreamKey => new ItemRef(Owner, Repo, ItemNumber).Key;
}
