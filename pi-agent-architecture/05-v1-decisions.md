# V1 Scope and Decisions

## Accepted decisions

1. Architecture is language/framework/ORM neutral. No implementation language has been selected.
2. The current repository is a proof of concept. Existing code and APIs may be fully rewritten.
3. Polling is used; public IP and inbound GitHub webhooks are not required for V1.
4. Polling interval is configurable; 30–60 seconds is an initial proposal.
5. Issue assignment to the bot is not a V1 prerequisite or trigger. Assigning or unassigning the bot on an Issue does not start or stop work.
6. Issue commands require a bot mention and a recognized slash command in the same comment. Only the original (unedited) comment version is parsed; the command must be the first non-empty line; bot-authored comments never trigger commands; one command directive per comment.
7. Any collaborator with GitHub Write permission may command the bot, with an optional configured allowlist as an additional restriction.
8. Removing a user from the allowlist or revoking their permission does not interrupt an already-running Task; later commands from that user are rejected.
9. One Workflow per root WorkItem in V1. An Issue and its related PR are separate WorkItems/Workflows, with an explicit relationship record.
10. One global Task execution slot in V1 (`max_concurrent_runs = 1`). Parallel execution is an enhancement. (The final global-vs-per-Workflow scope wording is reconciled in 09-open-questions.md Q1.)
11. A new Pi session and task-specific workspace are created for each Task; every Run attempt uses a new Pi session.
12. A Run represents one execution attempt. A retry creates a new Run and a new Pi session; workspace reuse follows task-type policy (a coder Task continues its agent branch; a reviewer always gets a fresh workspace); a new Task never reuses another Task's session.
13. Agent Profiles are independently configurable and versioned: Docker image, Pi settings, prompt, skills, MCP servers, tools/capabilities, environment references, and resources.
14. Coder pushes directly to the original repository on `agent/issue-<number>` (GitHub App installation token credentials, bot commit identity, force push forbidden) and reports the result in the Issue before implementation Task completion. The coder phase is enabled later (see decision 36).
15. Automatic PR creation by coder is not assumed in V1; it remains a separate decision/command/configuration.
16. Reviewer receives a fresh workspace at a specific PR Head SHA, separate from coder workspace, read-only where possible.
17. Initial PR review is triggered when the bot is requested as reviewer — exactly one automatic initial review per PR/Workflow lifetime. A review Task pins the PR Head SHA at creation (`target_head_sha`); SHA drift before a queued Task starts cancels it, and removing the review request before execution cancels the not-yet-started Task. Subsequent reviews are manual via `/review`; a new commit alone does not trigger a review.
18. Containers stop after Task completion; session files and workspace volumes remain on persistent Docker-host storage until retry/continuation or root cleanup.
19. PostgreSQL stores metadata/orchestration state; session and workspace files are stored on persistent Docker-host storage in V1.
20. Closing a root Issue/PR triggers cleanup of its containers, workspace volumes, and Pi session files; DB history remains with deletion/cleanup flags.
21. Closing an Issue does not implicitly close/clean its related PR Workflow; each root is handled independently.
22. Reconciliation checks active containers/runtime state. Runtime heartbeat and task progress are separate signals.
23. The 60-minute idle timeout triggers on the absence of ALL observable activity while the container is alive (heartbeat keeps running as a separate liveness signal). It is configurable and initiates reconciliation; it does not map to `TimedOut`.
24. File/object storage and parallel execution are future enhancements.
25. GitHub Projects are out of scope for V1,
26. `/code` replaces `/proceed`: the command approves the latest successful plan Artifact and starts the coder Task against it. Plan iteration is comment-driven: the user answers plan questions as ordinary comments and reruns `/plan`; the new planning Task reads the Issue description plus all comments.
27. `/retry` repeats the last failed Task of the Workflow as a new Run with a new Pi session; `attempt_number` is incremented by the Orchestrator. The admin endpoint `POST /api/v1/tasks/{taskId}/retry` remains.
28. Automatic retries are limited: only transient infrastructure errors, only before the Run starts executing; defaults `max_automatic_retries = 1`, `initial_backoff = 30s`, configurable. Policy levels: `AgentProfileVersion.retry_policy` default → Repository override → effective policy pinned on the Task.
29. The Orchestrator publishes system notifications to GitHub through the Outbox when a Task finally fails (including retries exhausted and `TimedOut`) and when `/stop` cancels Tasks; root-closure cleanup is silent. Agent-owned GitHub writes go through GitHub MCP and never through the Outbox.
30. Independent execution limits: `idle_timeout` 60 min (no observable activity while the container is alive; triggers reconciliation, never `TimedOut`), `max_run_duration` 4 h (wall clock; the Run ends `TimedOut`), `startup_timeout` 10 min (the Run fails with `startup_timeout`). All configurable; `permission_blocked` resets no timers.
31. A review Task pins the PR Head SHA at creation; SHA drift before a queued Task starts cancels it (`review_head_changed_before_execution`); removing the review request before execution cancels the initial review Task (`review_request_removed_before_execution`).
32. Artifacts are stored inline in PostgreSQL and delivered through the next Task's input; artifacts are immutable; limits 256 KiB per artifact / 512 KiB per Task input; formats `markdown`, `json`, `text` only (no `file_reference`).
33. Callbacks are emitted by the Runtime Bridge inside the agent container (launches Pi `--mode rpc`, reads JSONL events, posts events / 30 s heartbeat / completion / failure to the Runtime API). No Pi extension in V1.
34. Unattended permission policy: a per-profile allow-list of tools/MCP operations; denied operations are rejected immediately (fail-closed); no interactive permission dialogs; `permission_blocked` is a RunEvent.
35. V1 runs on exactly one configured execution host; profile images are built locally per profile version (tag `agent-<profile>:v<n>`, local Docker image store, no registry); only `ready` versions are selectable; a missing/unready image rejects the command (`agent_image_not_ready`).
36. Rollout phases: phase 1 ships only the Planner profile with `/plan`, `/status`, `/stop`, `/help`; `/code`, `/review`, and `/test-cases` are enabled when their profiles are configured; no `debugger` profile is planned (`/debug` parked).
37. Agent containers have no host shell access (no host-bridge capability).
38. Queue: single FIFO by `available_at` then `priority`; `/stop` is handled immediately outside the queue.
39. Comment sync: edits update the stored body; deletions create tombstones excluded from future context; `AgentCommand.source_body_snapshot` freezes the parsed text; command grammar — first non-empty line, exactly one directive, bot-authored comments ignored.
40. A merged Pull Request is treated as a closed WorkItem for lifecycle and cleanup purposes; no separate merged event.
41. AgentCommand statuses drop `waiting`: queue waiting is expressed by Task/OrchestrationJob states, human waiting by `Workflow.status = waiting_for_human`.

## V1 command syntax

Canonical form: `@<bot-account> /<command> [arguments]`. The mention is required in the same comment. `/agent <command>` may be considered as a future alias.

Rollout phases: phase 1 (planner only) enables `/plan`, `/status`, `/stop`, `/help`. `/code`, `/review`, and `/test-cases` become available when their profiles are configured; commands for missing/unready profiles are rejected with `agent_profile_not_configured`; `/debug` is parked.

See [06-command-reference.md](06-command-reference.md) for the full command contract.

| Command | V1 meaning |
|---|---|
| `/plan` | Planner analyzes an Issue, identifies gaps/questions, creates plan Artifact and GitHub comment; then waits for human input |
| `/code` | Coder implements the approved plan (the command approves the latest plan), runs checks, commits and pushes a branch, and reports back; then waits for human input |
| `/review` | Reviewer reviews the pinned PR Head SHA in a fresh read-only workspace and submits review/comments |
| `/test-cases` | Test designer creates test scenarios/cases as an Artifact and comment; it does not implement/run tests |
| `/debug` | Debugger diagnoses a problem and reports evidence/recommendations; read-only by default |
| `/status` | Reports Workflow/Task/Run/queue status; no Task or container |
| `/stop` | Gracefully cancels the active Task and all queued Tasks for the targeted WorkItem's Workflow; does not close the WorkItem or delete storage |
| `/help` | Publishes commands valid for the target type; no Task or container |
| `/retry` | Repeats the last failed Task of the Workflow as a new Run with a new Pi session; no new Task |

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
- GitHub App vs PAT for the overall installation/sync model (coder push credentials are already decided: GitHub App installation token).
- Exact GitHub permission lookup and allowlist configuration source.
- Docker volume naming, backup, and cleanup implementation for session/workspace storage (artifact storage is decided: inline in PostgreSQL).
- Whether `/test-cases` should support PR targets in V1.
- Whether coder should auto-create a PR (currently not assumed).
- How Issue-created PR relations are detected (closing keywords, branch metadata, or explicit creation action).
- Agent profile configuration file format and secrets injection mechanism.
- Whether `/plan-continue`, `/code-continue`, or `/continue` aliases are needed, or repeated `/plan`//`/code` suffice.
- Fate of `/debug`: no `debugger` profile is planned — drop the command or remap it.
- Remaining contract points (concurrency scope, session/workspace creation, container logs): see [09-open-questions.md](09-open-questions.md).
