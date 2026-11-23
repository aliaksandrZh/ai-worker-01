namespace PiAgent.Orchestrator.Context;

using PiAgent.Infrastructure.Persistence;

/// <summary>
/// The per-task-type directive the assembler carries into every input ("any configured
/// instructions"). The profile's actual system prompt is NOT duplicated here — the baked profile image
/// is the sole effective runtime configuration; this text only frames the task for the
/// agent. The agent-facing artifact type / result-manifest wording matches the manifest contract.
/// </summary>
public static class ContextInstructions
{
    public static string For(string taskType) => taskType switch
    {
        OrchestrationTaskTypes.Plan =>
            "You are the planner agent. Analyze the issue and the repository context, identify gaps and " +
            "open questions, and prepare an implementation plan. Publish the plan and any clarifying " +
            "questions to GitHub as an issue comment via GitHub MCP, and record the plan as a `plan` " +
            "artifact in your result manifest.",
        OrchestrationTaskTypes.Implement =>
            "You are the coder agent. Implement the approved plan in the task workspace on the agent " +
            "branch: commit the changes, push the branch, and publish a summary comment to the issue via " +
            "GitHub MCP. Record the summary as an `implementation_summary` artifact in your result manifest.",
        OrchestrationTaskTypes.Review =>
            "You are the reviewer agent. Examine the diff between the pinned target base and target head " +
            "revisions in your read-only workspace, then submit a GitHub review with inline comments on the " +
            "pull request (answer existing review threads where relevant). Record the report as a " +
            "`review_report` artifact in your result manifest.",
        OrchestrationTaskTypes.TestDesign =>
            "You are the test designer agent. Produce structured test scenarios and cases for the issue, " +
            "record them as a `test_cases` artifact in your result manifest, and publish a summary comment " +
            "via GitHub MCP. This designs tests; it does not implement or run them.",
        _ => throw new InvalidOperationException($"no context instructions configured for task type '{taskType}'"),
    };
}
