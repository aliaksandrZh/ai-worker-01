using PiAgent.Infrastructure.Persistence;

namespace PiAgent.Orchestrator.Commands;

/// <summary>
/// The persisted `AgentCommand.rejection_reason` vocabulary plus the one-line GitHub reply text for
/// each ("Command rejected … → Yes (concise reason)"). Every rejection persists its
/// reason in `agent_command` and enqueues exactly one reply through the Outbox.
/// </summary>
public static class AgentCommandRejectionReasons
{
    public const string UnknownCommand = "unknown_command";
    public const string AmbiguousCommand = "ambiguous_command";
    public const string MalformedCommand = "malformed_command";
    public const string CommandInQuoteOrCodeBlock = "command_in_quote_or_code_block";
    public const string CommandEdited = "command_edited";
    public const string NotPermittedCommand = "not_permitted_command";
    public const string WrongTargetType = "wrong_target_type";
    public const string WorkItemTypeNotPermitted = "work_item_type_not_permitted";
    public const string RepositoryPaused = "repository_paused";
    public const string NotInAllowlist = "not_in_allowlist";
    public const string UserNotCollaborator = "user_not_collaborator";
    public const string InsufficientPermission = "insufficient_permission";

    // admission/admission-time reasons
    public const string UnfinishedTaskExists = "unfinished_task_exists";
    public const string WorkflowFailed = "workflow_failed";
    public const string WorkItemClosed = "work_item_closed";
    public const string AgentProfileNotConfigured = "agent_profile_not_configured";
    public const string AgentImageNotReady = "agent_image_not_ready";
    public const string PlanNotAvailable = "plan_not_available";
    /// <summary>SILENT admission gate: the item does not carry the task type's AI-scope label
    /// (AI:Plan/AI:Code/AI:Review/AI:Test) — the command is stored rejected but never answered.
    /// The owner adds the label; the scope-label scanner then starts the run when conditions converge.</summary>
    public const string LabelScopeMissing = "label_scope_missing";
    /// <summary>/retry rejected — the most recent non-cancelled task is not a failed one.</summary>
    public const string TaskNotRetryable = "task_not_retryable";
    /// <summary>/retry rejected — the pinned profile version is disabled or not `ready`.</summary>
    public const string AgentProfileNotReady = "agent_profile_not_ready";
    /// <summary>Claim-time reason only: the PR Head SHA drifted while the review was queued.</summary>
    public const string ReviewHeadChanged = "review_head_changed_before_execution";
    /// <summary>Claim-time reason: the bot's review request was withdrawn before
    /// the automatic initial review Task started. An executing review is never interrupted.</summary>
    public const string ReviewRequestRemovedBeforeExecution = "review_request_removed_before_execution";
    /// <summary>Claim-time reason only: the Runtime API rejected the run dispatch (4xx).</summary>
    public const string RuntimeDispatchFailed = "runtime_dispatch_failed";
    /// <summary>Not a rejection: the reason code recorded on Tasks/commands cancelled by `/stop`,
    /// passed to the Runtime API as the stop-signal reason.</summary>
    public const string StopRequested = "stop_requested";
    /// <summary>Admission gate: the same task-producing command repeated
    /// with no new human signal since its previous same-type Task succeeded.</summary>
    public const string NoNewInput = "no_new_input";
}

public static class AgentCommandRejectionReplies
{
    /// <summary>Concise reply for the Outbox comment; `detail` carries parse diagnostics (raw token).
    /// `permittedCommands` (when known) scopes the supported-commands line to what the rejecting
    /// repository allows — the line is always derived, never hand-maintained.</summary>
    public static string Text(string reason, string detail, IReadOnlyList<string>? permittedCommands = null) => reason switch
    {
        AgentCommandRejectionReasons.UnknownCommand =>
            $"Command rejected: `/{detail}` is not a supported command.\n{SupportedCommandsLine(permittedCommands)}",
        AgentCommandRejectionReasons.AmbiguousCommand =>
            "Command rejected: the comment contains more than one command directive — post exactly one `@<bot> /command` per comment.",
        AgentCommandRejectionReasons.MalformedCommand =>
            "Command rejected: the command must be on the first non-empty line in the form `@<bot> /command` (only whitespace before the mention).",
        AgentCommandRejectionReasons.CommandInQuoteOrCodeBlock =>
            "Command rejected: the command was inside a blockquote or code block — post it as plain text, not inside quotes/code.",
        AgentCommandRejectionReasons.CommandEdited =>
            "Command rejected: the comment was already edited when first seen; edits are never parsed as commands. Post the command as a new comment.",
        AgentCommandRejectionReasons.NotPermittedCommand =>
            $"Command rejected: `{detail}` is not a permitted command in this repository (permitted: {CommandList(permittedCommands)}).",
        AgentCommandRejectionReasons.WrongTargetType =>
            $"Command rejected: `{detail}` is not valid for this target (Issue/PR) type.",
        AgentCommandRejectionReasons.WorkItemTypeNotPermitted =>
            $"Command rejected: {detail} work items are not permitted in this repository.",
        AgentCommandRejectionReasons.RepositoryPaused =>
            "Command rejected: the repository is paused (disabled) — task-producing commands resume when it is re-enabled.",
        AgentCommandRejectionReasons.NotInAllowlist =>
            "Command rejected: your account is not in this repository's allowlist.",
        AgentCommandRejectionReasons.UserNotCollaborator =>
            "Command rejected: your account is not a collaborator with write access to this repository.",
        AgentCommandRejectionReasons.InsufficientPermission =>
            "Command rejected: your account does not have write permission in this repository (write or above is required).",
        AgentCommandRejectionReasons.UnfinishedTaskExists =>
            "Command rejected: a task is already queued or running for this work item — only one task can run at a time. "
            + "Wait for it to finish (or use /stop), then run the command again.",
        AgentCommandRejectionReasons.WorkflowFailed =>
            "Command rejected: this workflow has failed unrecoverably and needs operator intervention; "
            + "only /status and /help remain available.",
        AgentCommandRejectionReasons.WorkItemClosed =>
            "Command rejected: this issue or pull request is closed — no new agent work can be requested.",
        AgentCommandRejectionReasons.AgentProfileNotConfigured =>
            "Command rejected: no agent profile is configured (or it is disabled) for this operation.",
        AgentCommandRejectionReasons.AgentImageNotReady =>
            "Command rejected: the agent image for this operation is not ready (still building or failed to build). Try again later.",
        AgentCommandRejectionReasons.PlanNotAvailable =>
            "Command rejected: there is no completed plan to approve — run /plan first.",
        AgentCommandRejectionReasons.TaskNotRetryable =>
            "Command rejected: there is no failed task to retry — /retry repeats this work item's most recent "
            + "non-cancelled task (cancelled ones are skipped) and it must have failed. "
            + "To run new work, post the original command again.",
        AgentCommandRejectionReasons.AgentProfileNotReady =>
            "Command rejected: the failed task's agent profile version is disabled or its image is not ready "
            + "(still building or failed to build). Restore the version, then post /retry again.",
        AgentCommandRejectionReasons.NoNewInput =>
            "Command rejected: this command already ran to completion and nothing new has happened since "
            + "(no new comments, edits, or commits). Comment what should be different, then post the command again.",
        _ => "Command rejected.",
    };

    /// <summary>"Supported commands: `/plan`, …" — the repository's permitted commands when supplied,
    /// otherwise the full table (`AgentCommandNames.All`); a config permitting no known command
    /// falls back to the full table rather than an empty list. Canonical `test_cases` renders as its
    /// `/test-cases` token by the shared `_`→`-` rule.</summary>
    private static string SupportedCommandsLine(IReadOnlyList<string>? permittedCommands)
    {
        var names = KnownPermittedOrDefault(permittedCommands);
        var tokens = names.Select(name => "`/" + name.Replace('_', '-') + "`");
        return "Supported commands: " + string.Join(", ", tokens);
    }

    /// <summary>Token list for any per-reason command mention (e.g. `not_permitted_command`).</summary>
    private static string CommandList(IReadOnlyList<string>? permittedCommands)
    {
        var names = KnownPermittedOrDefault(permittedCommands);
        var tokens = names.Select(name => "`/" + name.Replace('_', '-') + "`");
        return string.Join(", ", tokens);
    }

    private static IReadOnlyList<string> KnownPermittedOrDefault(IReadOnlyList<string>? permittedCommands)
    {
        if (permittedCommands is not { Count: > 0 })
            return AgentCommandNames.All;
        var known = AgentCommandNames.All.Where(permittedCommands.Contains).ToArray();
        return known.Length > 0 ? known : AgentCommandNames.All;
    }
}
