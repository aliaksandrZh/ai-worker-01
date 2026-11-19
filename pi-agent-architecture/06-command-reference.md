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
@pi-worker /proceed
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

## 2. Command summary

| Command | Valid target | Agent profile | Creates Task? | Main result |
|---|---|---|---|---|
| `/plan` | Issue | `planner` | Yes | Analysis, gaps, questions, and implementation plan |
| `/review` | Pull Request | `reviewer` | Yes | Reviews the current PR Head SHA and submits review/comments |
| `/test-cases` | Issue; PR may be enabled by configuration | `test-designer` | Yes | Produces test scenarios/cases as an artifact and GitHub comment |
| `/debug` | Issue or Pull Request | `debugger` | Yes | Investigates a problem and reports diagnosis/evidence; no code changes by default |
| `/status` | Issue or Pull Request | None | No | Reports Workflow, current Task/Run, queue position, and last result |
| `/stop` | Issue or Pull Request | None | No | Requests graceful cancellation of the active Task for that WorkItem |
| `/help` | Issue or Pull Request | None | No | Lists supported commands and basic usage |

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
4. Give the agent the Issue body, relevant comments, repository context, and any configured instructions.
5. Planner analyzes requirements, identifies gaps/ambiguities, asks clarifying questions where needed, and prepares a plan.
6. Persist the plan as an Artifact and publish the result as an Issue comment.
7. Stop the container but retain the Task workspace volume and session files until root cleanup.
8. Mark the Task successful and Workflow `waiting_for_human`.

A later `/plan` is allowed when the Workflow is waiting for human input. It creates a new planning Task and a new Pi session; it does not reuse the previous Task's session.

## 4. `/proceed`

Purpose: start implementation after human approval.

Preconditions:
- Target must be an open Issue.
- Workflow must be `waiting_for_human`.
- There must be a successful plan Artifact, unless a repository configuration explicitly permits implementation without a plan.
- No Task for the Workflow may be running.

Behavior:
1. Create an `implement` Task using the `coder` profile.
2. Create a new Pi session and a new task-specific workspace. The approved plan is passed as an input Artifact; the coder does not reuse the planner's Pi history or workspace.
3. Coder implements the plan, runs relevant checks, commits changes, pushes a branch, and posts a summary to the Issue.
4. The Task is successful only after the expected Git operations and result reporting have succeeded (or the configured policy explicitly records a partial result).
5. Stop the container and retain task data/volume until root cleanup.
6. Put the Workflow into `waiting_for_human` so the user can request another action, such as `/debug`, `/test-cases`, or a later task.

V1 does not assume that the coder automatically creates a Pull Request. Creating/publishing a PR can be a later explicit command or repository setting; pushing the branch and reporting it are the agreed baseline.

## 5. `/review`

Purpose: manually request a new review of a Pull Request.

Preconditions:
- Target must be an open Pull Request.
- Caller must pass authorization.
- No review Task for the same Workflow may currently be running.

Behavior:
1. Read the current PR Head SHA and Base SHA.
2. Create a `review` Task using the `reviewer` profile.
3. Create a fresh workspace at the target Head SHA. It must not reuse the coder's writable workspace. Make it read-only where possible.
4. Create a new Pi session.
5. Reviewer examines the diff and relevant context, then produces a review report and inline findings.
6. Persist the report as an Artifact and submit the GitHub review/comments through the Outbox.
7. Stop the container; retain data according to root cleanup policy.

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

## 7. `/debug`

Purpose: investigate a reported defect or unexpected behavior.

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

If a fix is desired, the user can follow with `/proceed` or a future explicit fix command. Automatic code modification by `/debug` is not part of V1.

## 8. `/status`

Purpose: inspect current orchestration state without starting an agent.

Behavior:
- Does not create a Workflow if none exists.
- Does not create a Task, Run, Session, or Container.
- Reports whether the Workflow exists and its status; current/last Task and profile; Run state; whether this WorkItem has work queued/running; and most recent result/error. It should distinguish this WorkItem from the global execution slot (which may be occupied by a Task for another WorkItem).
- If no Workflow exists, reports that no agent workflow has been started for this WorkItem.

## 9. `/stop`

Purpose: request cancellation of the currently active Task associated with this WorkItem.

Behavior:
1. If a Task is running, send a graceful stop/cancel request to the Agent Runtime.
2. Runtime asks Pi/processes to stop safely, then stops the container.
3. Mark Run/Task `cancelled` after runtime confirmation or reconciliation.
4. Keep session files and workspace volume so the result can be inspected or a new Task can be created later.
5. Set Workflow to `waiting_for_human` after cancellation.
6. If there is no active Task for this WorkItem, respond that nothing is running; do not close or delete the Workflow.

`/stop` does not close the GitHub Issue/PR and does not trigger root cleanup.

## 10. `/help`

Purpose: publish the command reference in a short GitHub comment.

Behavior:
- Does not create a Workflow or Task.
- Shows commands valid for the current target type and their short descriptions.
- May link to repository documentation for full usage.

## 11. PR comment and review-thread context

For a PR `/review`, the reviewer receives the current diff plus relevant PR conversation comments, submitted reviews, and inline review threads. Findings are submitted as a GitHub review and inline comments. When practical, the reviewer should reply in the relevant existing thread rather than create a duplicate finding. Free-form bot-mention replies without `/review` are not an execution trigger in V1; enabling direct comment-response behavior without a slash command is still an open decision.

## 12. Automatic first PR review

Separate from comment commands:
- When GitHub reports that the bot account has been requested as a reviewer on a PR, Poller persists a `review_requested` event.
- Orchestrator starts the initial review Task if no initial review has already been created for that request/PR state.
- A new commit (`pull_request_synchronize`) updates PR data but does not trigger another review automatically.
- Further reviews happen only through an explicit `@bot /review` command.

## 13. Commands received while another Task is running

V1 has one global execution slot. Commands are still persisted and validated when received. A valid task-producing command is queued and starts when the slot becomes free, provided its Workflow state and preconditions are still valid at execution time. `/stop` is prioritized for the active Task of the targeted WorkItem. If a queued command becomes invalid because the WorkItem was closed or the Workflow changed, it is rejected/cancelled with a recorded reason.


TODO: Need a separate command to proceed action. For example we have questions in the planning. Once user answered - we need to mark that the work can be proceded.