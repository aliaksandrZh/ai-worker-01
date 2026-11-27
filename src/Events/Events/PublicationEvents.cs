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
    long ItemNumber,
    string IdempotencyKey,
    string HeadBranch,
    string BaseBranch,
    string Title,
    string Body,
    long? PrNumber) : IEvent
{
    public string StreamKey => new ItemRef(Owner, Repo, PrNumber ?? 0).Key;
}

/// <summary>
/// Publisher, after a successful PR create/update: the real PR number GitHub assigned
/// (docs/incident-2026-11-26-review-item27.md, Case B — reviews targeted the issue number).
/// The Orchestrator records it as the Workflow's head_pr_number so review runs post to
/// the actual pull request.
/// </summary>
public sealed record PrPublished(
    string Owner,
    string Repo,
    long ItemNumber,
    long PrNumber) : IEvent
{
    public string StreamKey => new ItemRef(Owner, Repo, ItemNumber).Key;
}

public enum PublishPrKind
{
    Create,
    Update,
}

/// <summary>
/// A PR publication died permanently (the Publisher announces it before dead-lettering):
/// the Orchestrator parks the implement Task waiting on that PR — processing must never
/// be a terminal state. Appended for PublishPr only: the one publication a Task waits
/// on; comment/board failures are best-effort and leave nothing stranded.
/// </summary>
public sealed record PublishFailed(
    string Owner,
    string Repo,
    long ItemNumber,
    string IdempotencyKey,
    string Cause) : IEvent
{
    public string StreamKey => new ItemRef(Owner, Repo, ItemNumber).Key;
}

/// <summary>Review outcome: approve / request changes + inline comments.</summary>
public sealed record PublishReview(
    string Owner,
    string Repo,
    long PrNumber,
    string Verdict,
    string Body,
    long RunId,
    string IdempotencyKey,
    string? HeadBranch = null) : IEvent
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
