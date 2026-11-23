namespace PiAgent.Infrastructure.Persistence;

/// <summary>Canonical command names stored in `agent_command.command_name`.</summary>
public static class AgentCommandNames
{
    public const string Plan = "plan";
    public const string Code = "code";
    public const string Review = "review";
    public const string TestCases = "test_cases";
    public const string Status = "status";
    public const string Stop = "stop";
    public const string Help = "help";
    public const string Retry = "retry";

    /// <summary>Task-producing commands (rejected while the repository is paused) plus
    /// /retry, which resumes execution by re-running an existing Task.</summary>
    public static readonly string[] TaskProducingOrExecution =
    [
        Plan, Code, Review, TestCases, Retry,
    ];

    /// <summary>All command names in reply-display order — the single source for every generated
    /// command list (never restate the list inline elsewhere).</summary>
    public static readonly string[] All =
    [
        Plan, Code, Review, TestCases, Status, Stop, Help, Retry,
    ];
}

/// <summary>Command handling states: `queued`/`processed`/`failed` are written by the
/// consumers; the command processor writes only received/authorized/rejected.</summary>
public static class AgentCommandStatuses
{
    public const string Received = "received";
    public const string Authorized = "authorized";
    public const string Queued = "queued";
    public const string Processed = "processed";
    public const string Rejected = "rejected";
    public const string Failed = "failed";
}

/// <summary>
/// One comment's recognized-or-rejected command. `github_comment_id` is the idempotency key —
/// unique, one row per comment ever; `source_body_snapshot` freezes the parsed text so later comment
/// edits never reinterpret a processed command. `github_comment_id` is an external
/// ID without a DB FK (the comment unique index is composite (kind, comment_id)).
/// </summary>
public class AgentCommand
{
    public Guid Id { get; set; }
    public Guid WorkItemId { get; set; }
    public long GithubCommentId { get; set; }
    public string? AuthorLogin { get; set; }
    /// <summary>Canonical name from AgentCommandNames; null only for rejections of malformed/unknown directives.</summary>
    public string? CommandName { get; set; }
    /// <summary>Always "{}" — trailing text is user context inside source_body_snapshot.</summary>
    public string Arguments { get; set; } = "{}";
    public string ParserVersion { get; set; } = "1";
    public string Status { get; set; } = AgentCommandStatuses.Received;
    /// <summary>Immutable comment text used for parsing (first parse, never rewritten by retries).</summary>
    public string SourceBodySnapshot { get; set; } = null!;
    public DateTime CreatedAt { get; set; }
    public DateTime? ParsedAt { get; set; }
    public DateTime? ProcessedAt { get; set; }
    /// <summary>Persisted reason vocabulary from the Orchestrator command processing (task 06).</summary>
    public string? RejectionReason { get; set; }
}
