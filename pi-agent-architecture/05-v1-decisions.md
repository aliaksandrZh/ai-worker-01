# V1 Scope and Decisions

## Accepted decisions

1. Architecture is language/framework/ORM neutral. No implementation language has been selected.
2. The current repository is a proof of concept. Existing code and APIs may be fully rewritten.
3. Polling is used; public IP and inbound GitHub webhooks are not required for V1.
4. Polling interval is configurable; 30–60 seconds is an initial proposal.
5. Issue assignment to the bot is not a V1 prerequisite or trigger. Assigning or unassigning the bot on an Issue does not start or stop work.
6. Issue commands require a bot mention and a recognized slash command in the same comment.
7. Any collaborator with GitHub Write permission may command the bot, with an optional configured allowlist as an additional restriction.
8. Removing a user from the allowlist or revoking their permission does not interrupt an already-running Task; later commands from that user are rejected.
9. One Workflow per root WorkItem in V1. An Issue and its related PR are separate WorkItems/Workflows, with an explicit relationship record.
10. One global Task execution slot in V1 (`max_concurrent_runs = 1`). Parallel execution is an enhancement.
11. A new Pi session and task-specific workspace are created for each Task.
12. A Run represents one execution attempt. Retrying the same Task may reuse its valid Task session/workspace; a new Task never reuses another Task's session.
13. Agent Profiles are independently configurable and versioned: Docker image, Pi settings, prompt, skills, MCP servers, tools/capabilities, environment references, and resources.
14. Coder commits and pushes a branch and reports the result in the Issue before implementation Task completion.
15. Automatic PR creation by coder is not assumed in V1; it remains a separate decision/command/configuration.
16. Reviewer receives a fresh workspace at a specific PR Head SHA, separate from coder workspace, read-only where possible.
17. Initial PR review is triggered when the bot is requested as reviewer. Subsequent reviews are manual via `/review`; a new commit alone does not trigger a review.
18. Containers stop after Task completion; session files and workspace volumes remain on persistent Docker-host storage until retry/continuation or root cleanup.
19. PostgreSQL stores metadata/orchestration state; session and workspace files are stored on persistent Docker-host storage in V1.
20. Closing a root Issue/PR triggers cleanup of its containers, workspace volumes, and Pi session files; DB history remains with deletion/cleanup flags.
21. Closing an Issue does not implicitly close/clean its related PR Workflow; each root is handled independently.
22. Reconciliation checks active containers/runtime state. Runtime heartbeat and task progress are separate signals.
23. A 60-minute heartbeat timeout is a configurable initial proposal, not a fixed requirement.
24. File/object storage and parallel execution are future enhancements.
25. GitHub Projects are out of scope for V1.

## V1 command syntax

Canonical form: `@<bot-account> /<command> [arguments]`. The mention is required in the same comment. `/agent <command>` may be considered as a future alias.

See [06-command-reference.md](06-command-reference.md) for the full command contract.

| Command | V1 meaning |
|---|---|
| `/plan` | Planner analyzes an Issue, identifies gaps/questions, creates plan Artifact and GitHub comment; then waits for human input |
| `/proceed` | Coder implements an approved plan, runs checks, commits and pushes a branch, and reports back; then waits for human input |
| `/review` | Reviewer reviews current PR Head SHA in a fresh read-only workspace and submits review/comments |
| `/test-cases` | Test designer creates test scenarios/cases as an Artifact and comment; it does not implement/run tests |
| `/debug` | Debugger diagnoses a problem and reports evidence/recommendations; read-only by default |
| `/status` | Reports Workflow/Task/Run/queue status; no Task or container |
| `/stop` | Gracefully cancels active Task for the targeted WorkItem; does not close the WorkItem or delete storage |
| `/help` | Publishes commands valid for the target type; no Task or container |

## Explicitly out of scope for V1

- GitHub Projects.
- Webhooks/public inbound networking.
- Parallel task execution.
- Automatic PR review after every commit.
- User-facing dashboard.
- External object/file storage.
- Final security design (but profile capabilities and authorization metadata should be modeled now).
- Language/framework/ORM selection.
- Automatic PR creation by coder unless later explicitly enabled.
- Free-form natural-language comment handling without a recognized command (open decision whether this should be enabled in V1).

## Open decisions

- Exact configured bot mention/account and whether an alias such as `/agent plan` is needed.
- Whether a plain `@bot` mention with natural-language text should trigger an agent response without a slash command (current V1 proposal: no; recognized slash command required).
- Whether the allowlist is enabled by default or only configured for selected repositories (Write permission remains the baseline).
- GitHub App vs PAT and installation model.
- Exact GitHub permission lookup and allowlist configuration source.
- Retry limits/backoff and whether a failed Run resumes the same Pi session or creates a replacement Task.
- Exact volume naming, backup, and cleanup implementation.
- Runtime heartbeat/progress protocol and stale-run recovery policy.
- Whether `/test-cases` should support PR targets in V1.
- Whether coder should auto-create a PR (currently not assumed).
- How Issue-created PR relations are detected (closing keywords, branch metadata, or explicit creation action).
- Whether one active Workflow per WorkItem is enforced by a DB partial unique index.
- Agent profile configuration format and versioning strategy.
