namespace PiAgent.Events;

/// <summary>events.md "User events" — produced by user actions (or derived by the Poller).</summary>

/// <summary>
/// The Poller parsed a slash command (grammar, author permission and resolved target
/// travel in the payload); starts the mapped flow.
/// </summary>
public sealed record UserCommand(
    string Command,
    string Owner,
    string Repo,
    long ItemNumber,
    long CommentId,
    string Author,
    string AuthorPermission,
    string? Text) : IEvent
{
    public string StreamKey => new ItemRef(Owner, Repo, ItemNumber).Key;
}

/// <summary>The User answered a clarification, blocker, or env-issue request.</summary>
public sealed record UserReply(
    string Owner,
    string Repo,
    long ItemNumber,
    long CommentId,
    string Author,
    string Text) : IEvent
{
    public string StreamKey => new ItemRef(Owner, Repo, ItemNumber).Key;
}

/// <summary>
/// A decision: plan approved / changes requested; review escalation resolved; test
/// results accepted / bug found / does not meet requirements.
/// </summary>
public sealed record UserDecision(
    DecisionKind Kind,
    string Owner,
    string Repo,
    long ItemNumber,
    string Author,
    string? Text) : IEvent
{
    public string StreamKey => new ItemRef(Owner, Repo, ItemNumber).Key;
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
public sealed record UserCancel(
    string Owner,
    string Repo,
    long ItemNumber,
    string Author,
    string? Reason) : IEvent
{
    public string StreamKey => new ItemRef(Owner, Repo, ItemNumber).Key;
}
