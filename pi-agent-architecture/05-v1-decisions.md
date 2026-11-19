# V1 Scope and Decisions

## Accepted decisions

1. Architecture is language/framework/ORM neutral. No implementation language has been selected.
2. The current repository is a proof of concept. Existing code and APIs may be fully rewritten.
3. Polling is used; public IP and inbound GitHub webhooks are not required for V1.
4. Polling uses the GitHub REST API; the default interval is 60 seconds per repository (configurable). Authentication across the whole installation uses fine-grained GitHub PATs.
5. Issue assignment to the bot is not a V1 prerequisite or trigger. Assigning or unassigning the bot on an Issue does not start or stop work.
6. Issue commands require a bot mention and a recognized slash command in the same comment. Only the original (unedited) comment version is parsed; the command must be the first non-empty line; bot-authored comments never trigger commands; one command directive per comment.
7. Base command requirement: GitHub Write permission. The allowlist is enabled by default and is seeded with the repository owner's account only; a non-empty allowlist additionally requires membership, and an explicitly emptied allowlist falls back to the Write-permission baseline. Configuration lives in repository configuration.
8. Removing a user from the allowlist or revoking their permission does not interrupt an already-running Task; later commands from that user are rejected.
9. One Workflow per root WorkItem in V1. An Issue and its related PR are separate WorkItems/Workflows, with an explicit relationship record.
10. Per-Workflow invariant: a Workflow holds at most one unfinished Task (further same-Workflow commands are rejected at admission). A global cap `max_concurrent_runs` (default 10) in the Orchestrator configuration bounds the total number of concurrently running Runs across Workflows; extra work waits in the durable FIFO queue.
11. A new Pi session and task-specific workspace are created for each Task; every Run attempt uses a new Pi session.
12. A Run represents one execution attempt. A retry creates a new Run and a new Pi session; workspace reuse follows task-type policy (a coder Task continues its agent branch; a reviewer always gets a fresh workspace); a new Task never reuses another Task's session.
13. Agent Profiles are independently configurable and versioned: Docker image, Pi settings, prompt, skills, MCP servers, tools/capabilities, environment references, and resources.
14. Coder pushes directly to the original repository on `agent/issue-<number>` (fine-grained GitHub PAT with write access, dedicated bot-account commit identity, force push forbidden) and reports the result in the Issue before implementation Task completion. The coder phase is enabled later (see decision 36).
15. Automatic PR creation by coder is not assumed in V1; it remains a separate decision/command/configuration.
16. Reviewer receives a fresh workspace at a specific PR Head SHA, separate from coder workspace, read-only where possible.
17. Initial PR review is triggered when the bot is requested as reviewer — exactly one automatic initial review per PR/Workflow lifetime. A review Task pins the PR Head SHA at creation (`target_head_sha`); SHA drift before a queued Task starts cancels it, and removing the review request before execution cancels the not-yet-started Task. Subsequent reviews are manual via `/review`; a new commit alone does not trigger a review.
18. Containers stop after Task completion; the task workspace directory and per-Run `pi-session/` and `logs/` remain on persistent host storage (under `/var/lib/pi-agent/workflows/{workflow_id}/`) until retry/continuation or root cleanup.
19. PostgreSQL stores metadata/orchestration state; session and workspace files are stored on persistent Docker-host storage in V1.
20. Closing a root Issue/PR triggers cleanup of its containers and the Workflow storage tree (workspace directories, Pi sessions, run logs); DB history remains with deletion/cleanup flags.
21. Closing an Issue does not implicitly close/clean its related PR Workflow; each root is handled independently.
22. Reconciliation checks active containers/runtime state. Runtime heartbeat and task progress are separate signals.
23. The 60-minute idle timeout triggers on the absence of ALL observable activity while the container is alive (heartbeat keeps running as a separate liveness signal). It is configurable and initiates reconciliation; it does not map to `TimedOut`.
24. File/object storage and parallel execution are future enhancements.
25. GitHub Projects are out of scope for V1,
26. `/code` replaces `/proceed`: the command approves the latest successful plan Artifact and starts the coder Task against it. Plan iteration is comment-driven: the user answers plan questions as ordinary comments and reruns `/plan`; the new planning Task reads the Issue description plus all comments. No `/plan-continue`-style aliases (considered and dropped): users repeat `/plan`//`/code`.
27. `/retry` repeats the last failed Task of the Workflow as a new Run with a new Pi session; `attempt_number` is incremented by the Orchestrator. The admin endpoint `POST /api/v1/tasks/{taskId}/retry` remains.
28. Automatic retries are limited: only transient infrastructure errors, only before the Run starts executing; defaults `max_automatic_retries = 1`, `initial_backoff = 30s`, configurable. Policy levels: `AgentProfileVersion.retry_policy` default → Repository override → effective policy pinned on the Task.
29. The Orchestrator publishes system notifications to GitHub through the Outbox when a Task finally fails (including retries exhausted and `TimedOut`) and when `/stop` cancels Tasks; root-closure cleanup is silent. Agent-owned GitHub writes go through GitHub MCP and never through the Outbox.
30. Independent execution limits: `idle_timeout` 60 min (no observable activity while the container is alive; triggers reconciliation, never `TimedOut`), `max_run_duration` 4 h (wall clock; the Run ends `TimedOut`), `startup_timeout` 10 min (the Run fails with `startup_timeout`). All configurable; `permission_blocked` resets no timers.
31. A review Task pins the PR Head SHA at creation; SHA drift before a queued Task starts cancels it (`review_head_changed_before_execution`); removing the review request before execution cancels the initial review Task (`review_request_removed_before_execution`).
32. Artifacts are stored inline in PostgreSQL and delivered through the next Task's input; artifacts are immutable; limits 256 KiB per artifact / 512 KiB per Task input; formats `markdown`, `json`, `text` only (no `file_reference`).
33. Callbacks are emitted by the Runtime Bridge inside the agent container (launches Pi `--mode rpc`, reads JSONL events, posts events / 30 s heartbeat / completion / failure to the Runtime API). No Pi extension in V1.
34. Unattended permission policy: a per-profile allow-list of tools/MCP operations; denied operations are rejected immediately (fail-closed); no interactive permission dialogs; `permission_blocked` is a RunEvent.
35. V1 runs on exactly one configured execution host; profile images are built locally per profile version (tag `agent-<profile>:v<n>`, local Docker image store, no registry); only `ready` versions are selectable; a missing/unready image rejects the command (`agent_image_not_ready`).
36. Rollout phases: phase 1 ships only the Planner profile with `/plan`, `/status`, `/stop`, `/help`; `/code`, `/review`, and `/test-cases` are enabled when their profiles are configured. `/debug` was removed entirely; no `debugger` profile exists.
37. Agent containers have no host shell access (no host-bridge capability).
38. Queue: single FIFO by `available_at` then `priority`; `/stop` is handled immediately outside the queue.
39. Comment sync: edits update the stored body; deletions create tombstones excluded from future context; `AgentCommand.source_body_snapshot` freezes the parsed text; command grammar — first non-empty line, exactly one directive, bot-authored comments ignored.
40. A merged Pull Request is treated as a closed WorkItem for lifecycle and cleanup purposes; no separate merged event.
41. AgentCommand statuses drop `waiting`: queue waiting is expressed by Task/OrchestrationJob states, human waiting by `Workflow.status = waiting_for_human`.
42. Successful publication of mandatory results to GitHub is a precondition for Task `succeeded` for every agent type (planner — plan/questions; coder — push + result summary; reviewer — review + inline comments; test-designer — scenarios). The agent retries transient GitHub/MCP failures within the Run, verifying GitHub before re-posting to avoid duplicates; otherwise Run/Task fail with `error_code = github_publication_failed` (artifacts are kept), the Orchestrator posts a system notification via the Outbox (it never re-publishes the agent's result), and `/retry` is the recovery path.
43. Run logs are captured by the Runtime Bridge into a per-Run directory on persistent host storage (`stdout.log`, `stderr.log`, `bridge.log`), created and mounted by the Runtime API; size limit 50 MiB per Run (configurable, with rotation); retained until root cleanup and deleted together with session files; `Run.log_storage_reference` points to them; content is not copied into PostgreSQL.
44. The Runtime API exposes a single Run-scoped start endpoint (`POST /internal/runtime/runs/{runId}/start`). Orchestrator owns the AgentSession/Workspace DB rows (Session `provisioning` per Run; Workspace created on the first Run and reused by retries); the Runtime API provisions physical resources and returns runtime metadata. No separate create endpoints.
45. Mention-only dispatch is decided: NO. A bot mention without a recognized slash command never triggers work; the comment stays context. A future conversational mode may arrive via an explicit `/respond`//`/ask` (mention-only dispatch may be considered later).
46. No `/reply` command in V1: the reviewer answers existing review threads as part of `/review`; arbitrary thread replies without starting the reviewer are future work.
47. Coder PR creation is not in V1; the preferred future path is an explicit `/create-pr` command (an explicit user action), not implicit profile configuration.
48. `/test-cases` targets Issues only in V1; a PR target is rejected with a clear explanation. PR support may be added later when a diff/Head-SHA-based test-design scenario is defined.
49. Allowlist: enabled by default, seeded with the repository owner's account only. A non-empty allowlist requires Write permission AND membership; an explicitly emptied allowlist disables list filtering, leaving the Write-permission baseline. Configuration lives in repository configuration.
50. The whole integration uses fine-grained GitHub PATs (no GitHub App in V1): a read-only PAT for the Poller/sync and write-capable PATs for agent MCP/Git operations, both repository-scoped. This supersedes the earlier GitHub App installation-token suggestion for coder push; the commit identity is the dedicated bot account owning the write PAT.
51. Issue↔PR links come from GitHub timeline events synced by the Poller: a closed event whose commit carries closes/fixes semantics → relation `closes`; a cross-referenced event → relation `related_to`; branch naming (`agent/issue-<n>`) is a hint only, never a standalone basis. If GitHub reports no explicit link, none is created; V1 has no manual link management.
52. Storage layout: plain directories on persistent host storage, not named Docker volumes — `/var/lib/pi-agent/workflows/{workflow_id}/tasks/{task_id}/workspace` and `.../runs/{run_id}/{pi-session, logs/{stdout.log, stderr.log, bridge.log}}`; UUID-based names; containers mount what they need. Backups: PostgreSQL and profile configuration only; workspace/session/log backups are an accepted V1 risk. Cleanup: root closure deletes the Workflow storage tree after containers stop; no age-based cleanup of open Workflows; periodic reconciliation removes orphaned directories.
53. Polling parameters: GitHub REST API; default interval 60 s per repository; page size 100 with `Link` headers; 5-minute overlap window for timestamp queries; per-repository/per-stream cursors (endpoint families: issues, pulls, issues/comments, pulls/{n}/reviews, pulls/{n}/comments, issues/{n}/timeline); a cursor advances only after durable save; deleted comments are discovered via a daily full sweep of known IDs; review requests pending an initial review are rechecked each cycle. Rate limits: `ETag`/`If-None-Match` conditional requests (304 does not consume the primary limit; 5 000 req/h baseline); `403`/`429` honor `Retry-After`/`X-RateLimit-Reset`; secondary limits use bounded exponential backoff.
54. Profile configuration is YAML in Git — `agents/<profile>/profile.yaml` plus Dockerfile, `prompts/`, `skills/` (runtime/image, prompt and skills references, MCP servers/tools, permission allow-list, resource limits, retry policy, startup/idle/execution timeouts, secret references). Creating an `AgentProfileVersion` pins the Git commit SHA, a configuration snapshot, and file hashes; any configuration change creates a new version. Secrets live in host-managed files (e.g. `/etc/pi-agent/secrets/`), referenced by name in YAML and mounted read-only in-container at `/run/secrets/<name>`; least-privilege per profile; secrets never travel via env vars or image layers.

## V1 command syntax

Canonical form: `@<bot-account> /<command> [arguments]`. The mention is required in the same comment. `/agent <command>` may be considered as a future alias.

Rollout phases: phase 1 (planner only) enables `/plan`, `/status`, `/stop`, `/help`. `/code`, `/review`, and `/test-cases` become available when their profiles are configured; commands for missing/unready profiles are rejected with `agent_profile_not_configured`.

See [06-command-reference.md](06-command-reference.md) for the full command contract.

| Command | V1 meaning |
|---|---|
| `/plan` | Planner analyzes an Issue, identifies gaps/questions, creates plan Artifact and GitHub comment; then waits for human input |
| `/code` | Coder implements the approved plan (the command approves the latest plan), runs checks, commits and pushes a branch, and reports back; then waits for human input |
| `/review` | Reviewer reviews the pinned PR Head SHA in a fresh read-only workspace and submits review/comments |
| `/test-cases` | Test designer creates test scenarios/cases as an Artifact and comment; it does not implement/run tests |
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
- Free-form natural-language comment handling without a recognized command (decided: not in V1; a conversational mode via `/respond`//`/ask` may be a future extension).

## Open decisions

None remain — all former open items are resolved (see decisions 45–54). Future enhancement candidates (not in V1): conversational mode via `/respond`//`/ask`, an explicit `/create-pr`, a `/reply`-style thread command, PR support for `/test-cases`, and a `/agent <command>` alias.
