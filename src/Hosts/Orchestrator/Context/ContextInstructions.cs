namespace PiAgent.Orchestrator.Context;

using PiAgent.Infrastructure.Persistence;

/// <summary>
/// The per-task-type directive the assembler carries into every input ("any configured
/// instructions"). The profile's actual system prompt is NOT duplicated here — the baked profile image
/// is the sole effective runtime configuration; this text only frames the task for the
/// agent. The artifact wording matches the artifacts/ directory convention the Bridge collects
/// (the file stem determines the artifact type); the mandatory publications are verified by the
/// Bridge's publication toolcheck, not reported by the agent.
/// </summary>
public static class ContextInstructions
{
    public static string For(string taskType) => taskType switch
    {
        OrchestrationTaskTypes.Plan =>
            "You are the planner agent. Analyze the issue and the repository context, identify gaps and " +
            "open questions, and prepare an implementation plan. Publish the plan and any clarifying " +
            "questions to GitHub as an issue comment via GitHub MCP, and write the full plan to " +
            "`/workspace/artifacts/plan.md`.",
        OrchestrationTaskTypes.Implement =>
            "You are the coder agent. Implement the approved plan in the task workspace on the agent " +
            "branch: commit the changes, push the branch, open the pull request into the repository's " +
            "default branch, and publish a summary comment to the issue via GitHub MCP. Write the " +
            "summary to `/workspace/artifacts/implementation-summary.md`.",
        OrchestrationTaskTypes.Review =>
            "You are the reviewer agent. Examine the diff between the pinned target base and target head " +
            "revisions in your read-only workspace, then submit a GitHub review with inline comments on the " +
            "pull request (answer existing review threads where relevant). Write the report to " +
            "`/workspace/artifacts/review.md`.",
        OrchestrationTaskTypes.TestDesign =>
            "You are the test designer agent. Produce structured test scenarios and cases for the issue, " +
            "write them to `/workspace/artifacts/test-cases.md`, and publish a summary comment via GitHub " +
            "MCP. This designs tests; it does not implement or run them.",
        _ => throw new InvalidOperationException($"no context instructions configured for task type '{taskType}'"),
    };
}
