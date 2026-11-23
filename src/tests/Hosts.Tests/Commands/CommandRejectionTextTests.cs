using PiAgent.Infrastructure.Persistence;
using PiAgent.Orchestrator.Commands;
using PiAgent.Orchestrator.Orchestration;
using Xunit;

namespace PiAgent.Hosts.Tests.Commands;

/// <summary>
/// The task-07 rejection/notice vocabularies: every admission rejection (AgentCommandRejectionReplies)
/// and claim-time cancellation notice (ClaimCancellationNotices) carries a distinct, actionable GitHub
/// line — the "concise reason" contract extended to the orchestration reasons.
/// </summary>
public class CommandRejectionTextTests
{
    [Fact]
    public void NewAdmissionReasons_EachHaveTheirOwnReplyLine()
    {
        const string unfinishedTask = AgentCommandRejectionReasons.UnfinishedTaskExists;
        Assert.Contains("only one task can run", AgentCommandRejectionReplies.Text(unfinishedTask, ""), StringComparison.OrdinalIgnoreCase);

        const string workflowFailed = AgentCommandRejectionReasons.WorkflowFailed;
        Assert.Contains("operator", AgentCommandRejectionReplies.Text(workflowFailed, ""), StringComparison.OrdinalIgnoreCase);

        const string workItemClosed = AgentCommandRejectionReasons.WorkItemClosed;
        Assert.Contains("closed", AgentCommandRejectionReplies.Text(workItemClosed, ""), StringComparison.OrdinalIgnoreCase);

        const string notConfigured = AgentCommandRejectionReasons.AgentProfileNotConfigured;
        Assert.Contains("no agent profile", AgentCommandRejectionReplies.Text(notConfigured, ""), StringComparison.OrdinalIgnoreCase);

        const string imageNotReady = AgentCommandRejectionReasons.AgentImageNotReady;
        Assert.Contains("not ready", AgentCommandRejectionReplies.Text(imageNotReady, ""), StringComparison.OrdinalIgnoreCase);

        const string planMissing = AgentCommandRejectionReasons.PlanNotAvailable;
        Assert.Contains("no completed plan", AgentCommandRejectionReplies.Text(planMissing, ""), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("/plan", AgentCommandRejectionReplies.Text(planMissing, ""), StringComparison.Ordinal);

        // /retry rejections get distinct actionable lines
        const string notRetryable = AgentCommandRejectionReasons.TaskNotRetryable;
        Assert.Contains("no failed task to retry", AgentCommandRejectionReplies.Text(notRetryable, ""), StringComparison.OrdinalIgnoreCase);
        Assert.NotEqual(AgentCommandRejectionReplies.Text(AgentCommandRejectionReasons.UnfinishedTaskExists, ""),
            AgentCommandRejectionReplies.Text(notRetryable, ""));
        const string profileNotReady = AgentCommandRejectionReasons.AgentProfileNotReady;
        Assert.Contains("disabled or its image is not ready", AgentCommandRejectionReplies.Text(profileNotReady, ""), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ClaimCancellationNotices_MapEveryClaimReason()
    {
        Assert.Contains("run /review again", ClaimCancellationNotices.Text(AgentCommandRejectionReasons.ReviewHeadChanged));
        Assert.Contains("agent profile", ClaimCancellationNotices.Text(AgentCommandRejectionReasons.AgentProfileNotConfigured), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("not ready", ClaimCancellationNotices.Text(AgentCommandRejectionReasons.AgentImageNotReady), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("plan", ClaimCancellationNotices.Text(AgentCommandRejectionReasons.PlanNotAvailable), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("closed", ClaimCancellationNotices.Text(AgentCommandRejectionReasons.WorkItemClosed), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("allowlist", ClaimCancellationNotices.Text(AgentCommandRejectionReasons.NotInAllowlist), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("collaborator", ClaimCancellationNotices.Text(AgentCommandRejectionReasons.UserNotCollaborator), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("permission", ClaimCancellationNotices.Text(AgentCommandRejectionReasons.InsufficientPermission), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ClaimCancellationNotices_UnknownReason_GetFallsBackToGenericLine()
    {
        Assert.Contains("cancelled", ClaimCancellationNotices.Text("something_else"), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RepoCommandConfigParser_ReadsTheRepositoryPolicyShape()
    {
        var repository = new PiAgent.Infrastructure.Persistence.Repository
        {
            Configuration = """{"permitted_commands": ["plan", "status"], "permitted_work_item_types": ["issue"], "authorization": {"minimum_permission": "write"}}""",
        };
        var config = RepoCommandConfigParser.Parse(repository);
        Assert.Equal(["plan", "status"], config.PermittedCommands);
        Assert.Equal(["issue"], config.PermittedWorkItemTypes);
        Assert.Equal("write", config.MinimumPermission);
    }

    [Fact]
    public void RepoCommandConfigParser_MissingOrBrokenJson_FallsBackToDefaults()
    {
        var fallback = RepoCommandConfigParser.Parse(new Repository { Configuration = "" });
        Assert.Contains("plan", fallback.PermittedCommands);
        Assert.Contains(AgentCommandNames.TaskProducingOrExecution[0], fallback.PermittedCommands);

        var broken = RepoCommandConfigParser.Parse(new Repository { Configuration = "{not json" });
        Assert.NotEmpty(broken.PermittedCommands);
        Assert.NotEmpty(broken.PermittedWorkItemTypes);
    }
}
