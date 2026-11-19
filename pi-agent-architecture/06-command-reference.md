# Command Reference

This document defines the proposed V1 behavior of commands. It is intentionally more specific than the command-name list in the other architecture documents.

## 1. Command syntax and common rules

Canonical syntax:

```text
@<bot-account> /<command> [arguments]
```

Examples:

```text
@pi-worker /plan
@pi-worker /code
@pi-worker /review
```

The bot mention is required in V1. The command is a slash-prefixed verb. `/agent plan` may be supported later as an alias, but it is not the canonical syntax in this draft. The actual bot account name is configured per installation/repository.

Common rules:

1. Commands are discovered by polling GitHub comments; no public webhook endpoint is required.
2. A command is accepted only from a collaborator with GitHub Write permission and, when configured, a user allowed by the repository/installation allowlist.
3. A command comment is processed once. The GitHub comment ID is the idempotency key.
4. The bot mention and recognized command must be present in the same comment for V1.
5. Ordinary comments are synchronized and stored but do not start work by themselves. The Task context includes relevant Issue/PR discussion and existing PR review threads. A bot mention with free-form text but no recognized command is not an execution trigger in V1; support for that behavior remains an open product decision.
6. A command is validated against the WorkItem type and current Workflow/Task state.
7. Commands that start work create a Task; the Task selects a versioned AgentProfile and receives a new Pi session.
8. Only one Task may execute at a time globally in V1. Other eligible work waits in the durable queue, except `/stop`, which is a control command and is processed with priority.
9. The Orchestrator, not the agent, owns Workflow and Task state transitions.
10. Invalid or unauthorized commands receive a concise GitHub comment explaining why they were not executed.

Command recognition rules (the parsed text is preserved in `AgentCommand.source_body_snapshot`):
- Comments authored by the bot account never trigger commands (no self-trigger loops).
- Only top-level Issue comments and PR conversation comments can trigger commands; inline review comments and review-thread replies are context only.
- The command must be in the first non-empty line of the comment; only leading whitespace is allowed before the mention; the exact configured bot login must immediately precede the slash command. Text after that line is context, not commands.
- Directives inside blockquotes, fenced/indented code blocks, or inline code are ignored.
- Exactly one command directive per comment; otherwise the whole comment is rejected as ambiguous.
- Only the original comment version is parsed: if the comment was already edited when first seen (`updated_at > created_at`), it is not accepted as a command; edits after processing never retrigger it.

## 2. Command summary

Rollout: phase 1 ships `planner` only (`/plan`, `/status`, `/stop`, `/help`). `/code`, `/review`, and `/test-cases` become available when their profiles are configured; commands for missing/unready profiles are rejected with `agent_profile_not_configured`. `/debug` is parked (no profile planned).

| Command | Valid target | Agent profile | Creates Task? | Main result |
|---|---|---|---|---|
| `/plan` | Issue | `planner` | Yes | Analysis, gaps, questions, and implementation plan |
| `/code` | Issue | `coder` | Yes | Implements the approved plan: checks, commit/push branch, result summary |
| `/review` | Pull Request | `reviewer` | Yes | Reviews the pinned PR Head SHA and submits review/comments |
| `/test-cases` | Issue; PR may be enabled by configuration | `test-designer` | Yes | Produces test scenarios/cases as an artifact and GitHub comment |
| `/debug` | Issue or Pull Request | `debugger` (parked; no profile planned) | Yes | Investigates a problem and reports diagnosis/evidence; no code changes by default |
| `/status` | Issue or Pull Request | None | No | Reports Workflow, current Task/Run, queue position, and last result |
| `/stop` | Issue or Pull Request | None | No | Requests cancellation of the active Task and queued Tasks for that Workflow |
| `/help` | Issue or Pull Request | None | No | Lists supported commands and basic usage |
| `/retry` | Issue or PR | None (reuses the failed Task's profile) | No (new Run on the last failed Task) | Restarts the last failed Task with a new Run and new Pi session |

## 3. `/plan`

Purpose: analyze the Issue and prepare a plan before implementation.

Preconditions:
- Target must be an open Issue.
- No other Task for the same Workflow may currently be running.
- Caller must pass authorization.

Behavior:
1. Create or load the Issue Workflow.
2. Create a `plan` Task and select the configured `planner` profile version.
3. Create a new AgentSession and task workspace.
4. Give the agent the Issue body, all comments up to the command time (including answers to earlier plan questions), repository context, and any configured instructions.
5. Planner analyzes requirements, identifies gaps/ambiguities, asks clarifying questions where needed, and prepares a plan.
6. Persist the plan as an Artifact and publish the result as an Issue comment.
7. Stop the container but retain the Task workspace volume and session files until root cleanup.
8. Mark the Task successful and Workflow `waiting_for_human`.

A later `/plan` is allowed when the Workflow is waiting for human input. It creates a new planning Task and a new Pi session; it does not reuse the previous Task's session. This is how plan iteration works: the user answers plan questions as ordinary comments and reruns `/plan`, and the new planning Task sees the Issue description plus the updated discussion. `/code` then implements the latest successful plan (see below).

## 4. `/code` (formerly `/proceed`)

Purpose: start implementation of the approved plan.

Preconditions:
- Target must be an open Issue.
- Workflow must be `waiting_for_human`.
- There must be a successful plan Artifact (the latest successful planning Task); sending `/code` itself is the explicit approval of that plan, unless a repository configuration explicitly permits implementation without a plan.
- No unfinished Task for the Workflow may exist (a same-Workflow command after another one is rejected at admission).

Behavior:
1. Create an `implement` Task using the `coder` profile.
2. Create a new Pi session and a task-specific workspace. The approved plan is passed as an input Artifact; user answers recorded in comments are included as context. The coder does not reuse the planner's Pi history or workspace.
3. Coder implements the plan, runs relevant checks, commits changes, pushes a branch, and posts a summary to the Issue.
4. The Task is successful only after the expected Git operations and result reporting have succeeded (or the configured policy explicitly records a partial result).
5. Stop the container and retain task data/volume until root cleanup.
6. Put the Workflow into `waiting_for_human` so the user can request another action, such as `/test-cases` or a later task.

Workspace and Git rules (applied when the coder phase is enabled):
- The first coder Task in a Workflow clones the original repository with base = current HEAD of the default branch; the base SHA is stored in `Workspace.base_sha`.
- Work happens on `agent/issue-<number>`; later coder Tasks in the same Workflow continue the existing agent branch; a retry continues the Task's branch.
- Push uses a GitHub App installation token (Contents read/write, repo-scoped, refreshed through the Runtime API for long runs); no PAT in remotes or git config.
- Commits are authored as the GitHub App bot identity (name + noreply email); no signing by default; force push is forbidden; direct push to the original repository, no fork.

Mid-run blockers: the agent posts a comment describing the blocker, the user replies with a normal comment and reruns `/code` to continue — a new Task/Run whose context includes the answers.

V1 does not assume that the coder automatically creates a Pull Request. Creating/publishing a PR can be a later explicit command or repository setting; pushing the branch and reporting it are the agreed baseline.

## 5. `/review`

Purpose: manually request a new review of a Pull Request.

Preconditions:
- Target must be an open Pull Request.
- Caller must pass authorization.
- No review Task for the same Workflow may currently be running.

Behavior:
1. Read the current PR Head SHA and Base SHA, and pin the Head SHA on the Task (`target_head_sha`).
2. Create a `review` Task using the `reviewer` profile.
3. Create a fresh workspace at the pinned Head SHA. It must not reuse the coder's writable workspace. Make it read-only where possible.
4. Create a new Pi session.
5. Reviewer examines the diff and relevant context, then produces a review report and inline findings.
6. Persist the report as an Artifact and submit the GitHub review/comments through GitHub MCP (agent-owned write).
7. Stop the container; retain data according to root cleanup policy.

SHA handling: if the PR Head SHA changed before a queued review Task starts, the Task is cancelled (`review_head_changed_before_execution`) and a system notification asks the user to run `/review` again. If the current SHA cannot be resolved at execution time, the start is deferred until sync recovers.

Each explicit `/review` comment is a distinct review request, even if the Head SHA has not changed. Duplicate delivery of the same GitHub comment must not create another review.

## 6. `/test-cases`

Purpose: generate test scenarios/cases from the Issue requirements and current repository context.

Preconditions:
- Target must be an open Issue in V1 (PR support can be enabled later).
- No Task for the Workflow may currently be running.

Behavior:
1. Create a `test_design` Task using `test-designer`.
2. Create a new Pi session and task workspace.
3. Generate structured test scenarios/cases, including assumptions and uncovered requirements where relevant.
4. Save a `test_cases` Artifact and publish a readable summary/comment to GitHub.
5. Stop the container, retain task storage until root cleanup, mark Task successful and Workflow `waiting_for_human`.

This command designs tests; it does not imply that tests are implemented or executed.

## 7. `/debug` (parked)

Purpose: investigate a reported defect or unexpected behavior.

Status: parked — no `debugger` profile is planned; the command stays documented until it is dropped or remapped (see 09-open-questions.md Q4).

Preconditions:
- Target must be an open Issue or Pull Request.
- No Task for the Workflow may currently be running.

Behavior:
1. Create a `debug` Task using `debugger`.
2. Create a new Pi session and task-specific workspace.
3. Investigate the reported symptoms, inspect relevant code/logs/tests available to the agent, and attempt reproduction where possible.
4. By default, debugger is diagnostic/read-only: it reports likely cause, evidence, reproduction steps, and recommended fix. It does not modify code.
5. Persist a diagnostic Artifact and publish a summary to GitHub.
6. Stop the container and mark Workflow `waiting_for_human`.

If a fix is desired, the user can follow with `/code` or a future explicit fix command. Automatic code modification by `/debug` is not part of V1.

## 8. `/status`

Purpose: inspect current orchestration state without starting an agent.

Behavior:
- Does not create a Workflow if none exists.
- Does not create a Task, Run, Session, or Container.
- Reports whether the Workflow exists and its status; current/last Task and profile; Run state; whether this WorkItem has work queued/running; and most recent result/error. It should distinguish this WorkItem from the global execution slot (which may be occupied by a Task for another WorkItem).
- If no Workflow exists, reports that no agent workflow has been started for this WorkItem.

Draft output format (one comment; exact Markdown is left to implementation):

```text
Workflow Status
WorkItem: Issue #123
Workflow: Active

Current Task:
- Type: implement | Status: Running | Agent: coder | Run: 1/2
- Started: 14:32 | Duration: 18m

Execution:
- Container: Running | Last heartbeat: 20s ago | Last progress: 45s ago

Queue:
- Position: 0 | Pending jobs: 2

Last completed Task:
- plan — Succeeded
```

Omit sections that do not apply (no Run before creation, no container before provisioning); include the last error/rejection reason when present.

## 9. `/stop`

Purpose: request cancellation of the active Task and any queued work of this WorkItem's Workflow.

Behavior:
1. If a Task is running, send a graceful stop/cancel request to the Agent Runtime; the Runtime asks Pi/processes to stop safely, then stops the container.
2. Mark Run/Task `cancelled` after runtime confirmation or reconciliation.
3. Cancel ALL queued Tasks of the same Workflow (together with their Jobs); otherwise queued work would start automatically after the stop, contradicting the intent of `/stop`.
4. Keep session files and workspace volumes so results can be inspected or a new Task can be created later.
5. Set Workflow to `waiting_for_human` after cancellation.
6. Publish a system notification about the cancellation(s) via the Outbox (one comment may cover active and queued cancellations).
7. Never touch other Workflows; do not close the Workflow or the GitHub Issue/PR.
8. If there is no active or queued Task for this WorkItem, respond that there is nothing to stop; do not close or delete the Workflow.

`/stop` does not close the GitHub Issue/PR and does not trigger root cleanup.

## 10. `/help`

Purpose: publish the command reference in a short GitHub comment.

Behavior:
- Does not create a Workflow or Task.
- Shows commands valid for the current target type and their short descriptions.
- May link to repository documentation for full usage.

## 10a. `/retry`

Purpose: repeat the last failed Task of the Workflow without creating a new Task.

Preconditions:
- The target WorkItem is open.
- The Workflow has no unfinished Task.
- The Workflow has a failed Task as its most recent non-cancelled task-producing attempt; `/retry` never re-runs a succeeded Task or an arbitrary older Task.
- The original preconditions of the operation still hold (e.g. for `/code` a successful plan Artifact still exists).

Behavior:
1. Keep the existing Task (no new Task; the same `AgentProfileVersion` and, for reviews, the pinned `target_head_sha` apply).
2. Create a new Run; the Orchestrator increments `attempt_number`.
3. Create a new Pi session (never continue the previous session).
4. Start the Run; further behavior follows the original command's task type.

Automatic retries exist only for transient infrastructure errors before a Run starts executing; after execution began, `/retry` is the recovery path. The admin endpoint `POST /api/v1/tasks/{taskId}/retry` remains for operators.

## 11. PR comment and review-thread context

For a PR `/review`, the reviewer receives the current diff plus relevant PR conversation comments, submitted reviews, and inline review threads. Findings are submitted as a GitHub review and inline comments. When practical, the reviewer should reply in the relevant existing thread rather than create a duplicate finding. Free-form bot-mention replies without `/review` are not an execution trigger in V1; enabling direct comment-response behavior without a slash command is still an open decision.

## 12. Automatic first PR review

Separate from comment commands:
- When GitHub reports that the bot account has been requested as a reviewer on a PR, Poller persists a `review_requested` event.
- Orchestrator starts the initial review Task if none has been created yet: exactly one automatic initial review per PR/Workflow lifetime (`Workflow.initial_review_created_at` stays set even if the Task is later cancelled).
- If the review request is removed before the Task starts, the Task is cancelled (`review_request_removed_before_execution`); a renewed request does not create a second automatic initial review — the user should run `/review` explicitly.
- Each review Task pins the PR Head SHA at creation (`target_head_sha`); SHA drift before a queued Task starts cancels the Task with `review_head_changed_before_execution` and a system comment asks for a new `/review`.
- A new commit (`pull_request_synchronize`) updates PR data but does not trigger another review automatically.
- Further reviews happen only through an explicit `@bot /review` command.

## 13. Commands received while work is running

Admission rules:
- If the target Workflow already has an unfinished Task (queued, claimed, starting, or running), a further command for that Workflow is rejected immediately with a reason — same-Workflow commands are not queued. This prevents, for example, `/code` becoming a deferred approval of a plan that is still being produced.
- If the Workflow is free but system execution capacity is occupied, the command is persisted and queued.

Claim-time recheck: a queued command starts when capacity becomes free, provided its Workflow state and preconditions are still valid at execution time (pinned PR SHA unchanged for reviews, WorkItem still open, plan still present). A Task that became invalid is cancelled or rejected with a recorded reason.

`/stop` is a control command handled immediately and prioritized for the targeted WorkItem. The final global-vs-per-Workflow concurrency scope is tracked in 09-open-questions.md Q1.


## 14. System notifications (Outbox)

The Orchestrator publishes orchestration-owned system notifications to GitHub through the Outbox (`action_type = comment`). Agent-owned writes go through GitHub MCP and never wait on the Outbox.

| Event | GitHub notification |
|---|---|
| Run failed, automatic retry scheduled | No (retry happens silently) |
| Task finally failed (auto retries exhausted, including `TimedOut`) | Yes |
| `/stop` cancelled the active Task and/or queued Tasks | Yes |
| Command rejected (unauthorized, precondition failed, ambiguous, missing profile/image) | Yes (concise reason) |
| Root WorkItem closed | No (cleanup is silent) |

Failure notifications include: the command, task type/profile, attempt count, stage, and a safe short error description with a diagnostic id — no secrets, no full stack traces. Template:

```text
Task failed: /code
Agent: coder | Attempts: 1/2 | Stage: Container startup
Failure: container image could not be pulled (id: run_123)

Automatic retries exhausted. The Workflow is waiting for human input.
Fix the underlying issue and submit the command again (or use /retry).
```

The earlier TODO about a separate "proceed after answering questions" command is resolved: plan questions and answers are ordinary comments, `/plan` reruns the planning iteration over them, and `/code` (which itself approves the latest plan) starts implementation. Optional `/plan-continue`-style aliases remain an open question (09-open-questions.md Q5).