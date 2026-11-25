namespace PiAgent.Events;

/// <summary>events.md "Publication events (Orchestrator → Publisher)".</summary>

/// <summary>Plan, bug report, notification — an issue or PR comment.</summary>
public sealed record PublishComment(
    PublishCommentKind Kind,
    string Owner,
    string Repo,
    long EntityNumber,
    string IdempotencyKey,
    string Body) : IEvent
{
    public string StreamKey => new ItemRef(Owner, Repo, EntityNumber).Key;
}

public enum PublishCommentKind
{
    Plan,
    Bug,
    Notification,
}

/// <summary>PR creation after an implement Run; PR update on further rounds.</summary>
public sealed record PublishPr(
    PublishPrKind Kind,
    string Owner,
    string Repo,
    long WorkflowId,
    string IdempotencyKey,
    string HeadBranch,
    string BaseBranch,
    string Title,
    string Body,
    long? PrNumber) : IEvent
{
    public string StreamKey => new ItemRef(Owner, Repo, PrNumber ?? 0).Key;
}

public enum PublishPrKind
{
    Create,
    Update,
}

/// <summary>Review outcome: approve / request changes + inline comments.</summary>
public sealed record PublishReview(
    string Owner,
    string Repo,
    long PrNumber,
    string Verdict,
    string Body,
    long RunId,
    string IdempotencyKey) : IEvent
{
    public string StreamKey => new ItemRef(Owner, Repo, PrNumber).Key;
}

/// <summary>Projects v2 card status (GraphQL option-id resolution inside the Publisher).</summary>
public sealed record PublishBoardMove(
    string Owner,
    int ProjectNumber,
    string ItemNodeId,
    string Status,
    long RunId,
    string IdempotencyKey) : IEvent
{
    public string StreamKey => Streams.Repo(Owner, $"project{ProjectNumber}");
}
