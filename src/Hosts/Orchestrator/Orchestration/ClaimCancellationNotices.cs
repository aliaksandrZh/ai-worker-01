using PiAgent.Orchestrator.Commands;

namespace PiAgent.Orchestrator.Orchestration;

/// <summary>
/// The GitHub notification lines for claim-time cancellations of queued Tasks (04 §2: "a Task that
/// became invalid is cancelled or rejected with a recorded reason"; 06 §13: precondition-failed
/// commands notify). Published through the Outbox with the same reason code recorded on the
/// Task/Job/Command rows. Decision 31 text for `review_head_changed_before_execution` explicitly asks
/// for another `/review`.
/// </summary>
public static class ClaimCancellationNotices
{
    public static string Text(string reason) => reason switch
    {
        AgentCommandRejectionReasons.ReviewHeadChanged =>
            "A queued review was cancelled: the pull request changed after the review was requested — run /review again.",
        AgentCommandRejectionReasons.AgentProfileNotConfigured =>
            "A queued task was cancelled: the agent profile it was queued with is no longer available (missing or disabled). Run the command again.",
        AgentCommandRejectionReasons.AgentImageNotReady =>
            "A queued task was cancelled: its agent image is not ready (still building or failed to build). Run the command again once the image is ready.",
        AgentCommandRejectionReasons.PlanNotAvailable =>
            "A queued task was cancelled: the approved plan it references is no longer available. Run /plan, then /code.",
        AgentCommandRejectionReasons.ReviewRequestRemovedBeforeExecution =>
            "A queued review was cancelled: the review request for the bot was removed before the review started. Run /review to request a review again.",
        AgentCommandRejectionReasons.WorkItemClosed =>
            "A queued task was cancelled: the issue or pull request was closed before the task started.",
        AgentCommandRejectionReasons.NotInAllowlist =>
            "A queued task was cancelled: the requesting account is no longer in this repository's allowlist.",
        AgentCommandRejectionReasons.UserNotCollaborator =>
            "A queued task was cancelled: the requesting account is no longer a collaborator with write access to this repository.",
        AgentCommandRejectionReasons.InsufficientPermission =>
            "A queued task was cancelled: the requesting account no longer has write permission in this repository.",
        AgentCommandRejectionReasons.RuntimeDispatchFailed =>
            "A queued task was cancelled: the agent runtime rejected the dispatch request (invalid target state or parameters). Run the command again.",
        _ => "A queued task was cancelled.",
    };
}
