namespace PiAgent.Events;

/// <summary>events.md "Publication events (Orchestrator → Publisher)".</summary>

/// <summary>Plan, bug report, notification — an issue or PR comment.</summary>
public sealed record PublishComment : RepositoryEvent
{
    public required PublishCommentKind Kind { get; init; }
    public required long EntityNumber { get; init; }
    public required string IdempotencyKey { get; init; }
    public required string Body { get; init; }

    public override string StreamKey => new ItemRef(Owner, Repo, EntityNumber).Key;
}

public enum PublishCommentKind
{
    Plan,
    Bug,
    Notification,
}

/// <summary>PR creation after an implement Run; PR update on further rounds.</summary>
public sealed record PublishPr : RepositoryEvent
{
    public required PublishPrKind Kind { get; init; }
    public required long WorkflowId { get; init; }
    public required long ItemNumber { get; init; }
    public required string IdempotencyKey { get; init; }
    public required string HeadBranch { get; init; }
    public required string BaseBranch { get; init; }
    public required string Title { get; init; }
    public required string Body { get; init; }
    public required long? PrNumber { get; init; }

    public override string StreamKey => new ItemRef(Owner, Repo, PrNumber ?? 0).Key;
}

/// <summary>
/// Publisher, after a successful PR create/update: the real PR number GitHub assigned
/// (docs/incident-2026-11-26-review-item27.md, Case B — reviews targeted the issue number).
/// The Orchestrator records it as the Workflow's head_pr_number so review runs post to
/// the actual pull request.
/// </summary>
public sealed record PrPublished : RepositoryEvent
{
    public required long ItemNumber { get; init; }
    public required long PrNumber { get; init; }

    public override string StreamKey => new ItemRef(Owner, Repo, ItemNumber).Key;
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
public sealed record PublishFailed : RepositoryEvent
{
    public required long ItemNumber { get; init; }
    public required string IdempotencyKey { get; init; }
    public required string Cause { get; init; }

    public override string StreamKey => new ItemRef(Owner, Repo, ItemNumber).Key;
}

/// <summary>Review outcome: approve / request changes + inline comments.</summary>
public sealed record PublishReview : RepositoryEvent
{
    public required long PrNumber { get; init; }
    public required string Verdict { get; init; }
    public required string Body { get; init; }
    public required long RunId { get; init; }
    public required string IdempotencyKey { get; init; }
    public string? HeadBranch { get; init; }

    public override string StreamKey => new ItemRef(Owner, Repo, PrNumber).Key;
}

/// <summary>Projects v2 card status (GraphQL option-id resolution inside the Publisher).</summary>
public sealed record PublishBoardMove : RootEvent
{
    public required string Owner { get; init; }
    public required int ProjectNumber { get; init; }
    public required string ItemNodeId { get; init; }
    public required string Status { get; init; }
    public required long RunId { get; init; }
    public required string IdempotencyKey { get; init; }

    public override string StreamKey => Streams.Repo(Owner, $"project{ProjectNumber}");
}
