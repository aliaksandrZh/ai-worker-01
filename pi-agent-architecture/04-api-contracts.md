# API Contracts

Conceptual contracts. Exact routes, transport, DTO naming, and implementation framework are not yet selected. The API may be internal and need not be publicly exposed.

## 1. Boundary

Two logical APIs:

1. **Orchestration API**: WorkItems, commands, workflows, tasks, status, retries, cancellation.
2. **Agent Runtime API**: Pi session and container execution mechanics.

They can initially be deployed together, but the boundary should remain clear.

## 2. Orchestration API

### WorkItems

`GET /api/v1/work-items/{workItemId}`

Response:
```json
{
  "id": "wi_123",
  "repository": "owner/repo",
  "number": 15,
  "type": "issue",
  "title": "Implement authentication",
  "state": "open",
  "workflowId": "wf_123"
}
```

### Workflow

`GET /api/v1/workflows/{workflowId}`

Returns workflow status, root WorkItem, current task, and task summaries.

### Tasks

`GET /api/v1/tasks/{taskId}`

There are no admin HTTP endpoints for task management in V1 (decision 65): task retry and cancellation flow only through the GitHub command path (`/retry`, `/stop`).

### Commands

GitHub commands are primarily ingested through polling, not through an external command endpoint. An internal endpoint may be used for testing or administrative operations:

`POST /internal/commands`

```json
{
  "repositoryId": "repo_1",
  "workItemId": "wi_123",
  "author": "collaborator",
  "commentId": "github-comment-987",
  "text": "@pi-worker /plan"
}
```

Response:
```json
{
  "commandId": "cmd_123",
  "status": "queued"
}
```

### Command behavior contract

The canonical V1 syntax is `@<bot-account> /<command> [arguments]`. The mention is required in the same comment. The full command semantics, preconditions, selected profile, and result are specified in [06-command-reference.md](06-command-reference.md).

| Command | Target | Effect | Creates Task? |
|---|---|---|---|
| `/plan` | Issue | Planner produces plan, gaps, and questions; Workflow waits for human | Yes |
| `/proceed` | Issue | (historical; replaced by `/code`) | — |
| `/code` | Issue | Coder implements the approved plan (the command approves the latest plan), runs checks, commits/pushes branch, reports result; enabled when the coder profile is configured | Yes |
| `/review` | PR | Reviewer reviews the pinned Head SHA in a fresh workspace and submits findings | Yes |
| `/test-cases` | Issue only in V1 (PR targets are rejected) | Test designer produces test scenarios/cases Artifact; does not implement/run tests by default | Yes |
| `/status` | Issue or PR | Reports current/last Workflow, Task, Run, queue and result | No |
| `/stop` | Issue or PR | Gracefully cancels the active Task and all queued Tasks of that Workflow; retains storage | No |
| `/help` | Issue or PR | Lists available commands for the target type | No |
| `/retry` | Issue or PR | Repeats the last failed Task as a new Run with a new Pi session | No (new Run on the existing Task) |

Common processing sequence: synchronize comment → deduplicate by GitHub comment ID → parse mention/command (first line only, exact bot login, no quotes/code blocks, exactly one directive) → verify GitHub Write permission and optional allowlist → validate target/state → persist AgentCommand → create Task/Job if required → reject or notify via Outbox.

Admission: if the target Workflow already has an unfinished Task (queued/claimed/starting/running), a further command for that Workflow is rejected with a recorded reason — same-Workflow commands are not queued. If the Workflow is free but execution capacity is occupied, the command is persisted and queued. Preconditions are checked again when the job is claimed; a Task that became invalid is cancelled or its command rejected with a recorded reason. `/stop` is prioritized for the targeted active Task. Execution capacity: a global cap `max_concurrent_runs` (default 10, Orchestrator configuration) bounds concurrently running Runs; queued commands start as capacity frees, subject to claim-time rechecks.

## 3. Agent Runtime API

### Start a Run

`POST /internal/runtime/runs/{runId}/start`

Ownership: the Orchestrator owns the DB rows — it creates the Run (`starting`), the AgentSession (`provisioning`, one per Run), and the Task's Workspace (created on the first Run, reused by retries). The `input` sent here is the Context Package the Orchestrator's Context Assembler built and froze as the Run's immutable `RunContextSnapshot` (02 §14a, decision 63); the Bridge converts it to one structured Markdown message and delivers it to Pi with a single RPC `prompt`. The Runtime API provisions the physical resources — Pi session files, workspace directory, container and mounts, the per-Run log directory — and returns runtime metadata. The request IDs act as idempotency/resource keys; there are no separate create-session/create-workspace endpoints.

```json
{
  "agentSessionId": "session_123",
  "workspaceId": "workspace_123",
  "agentProfileVersionId": "profile-version-coder-3",
  "input": {
    "workItem": {
      "repository": "owner/repo",
      "number": 15,
      "title": "Implement authentication"
    },
    "command": "implement",
    "instructions": "Implement the approved plan.",
    "artifactReferences": ["artifact_plan_1"]
  }
}
```

Response:
```json
{
  "runId": "run_123",
  "runtimeSessionId": "pi-session-…",
  "sessionFilePath": "/var/lib/pi-agent/workflows/<workflow_id>/runs/<run_id>/pi-session/<session-file>",
  "containerInstanceId": "container_123",
  "status": "starting"
}
```

The start response's `sessionFilePath` is the actual path discovered by the Bridge after starting Pi with `--session-dir` pinned to the per-Run host directory and `--session-id = <runId>` (decision 57).

### Send input to active runtime (removed from V1)

`POST /internal/runtime/sessions/{sessionId}/input` has no V1 trigger (free-form replies are not an execution path and mid-run command flow does not exist in V1), so it is deliberately left out of the V1 contract. Session input may return as a future enhancement.

### Stop

`POST /internal/runtime/runs/{runId}/stop`

```json
{
  "reason": "Root WorkItem closed"
}
```

### Runtime status

`GET /internal/runtime/runs/{runId}`

Returns desired/observed runtime status, heartbeat, container reference, and error details.

## 4. Agent callbacks/events

Callbacks are routes on the same Agent Runtime API that manages Pi sessions and Komodo deployments; a separate webhook receiver service is not required. Agent containers send callbacks over the private network. The receiver must persist events before acknowledging them. If Control Plane and execution host are separated, private network reachability between them is required; public GitHub webhooks are not involved.

The sender is the **Runtime Bridge** — a process inside the agent container that launches Pi in `--mode rpc`, reads the JSONL event stream, and posts the callbacks below; on a Pi crash it reports failure with the exit code. Callback URL and credentials are passed to the container at start; Bridge version and cadence are pinned in `AgentProfileVersion`. No Pi extension is needed in V1. The Bridge also captures Pi `stdout`/`stderr` (plus its own diagnostics) into a per-Run log directory that the Runtime API creates and mounts — `/var/lib/pi-agent/workflows/{workflow_id}/runs/{run_id}/logs/{stdout.log, stderr.log, bridge.log}`; the files survive container stop, are size-limited (default 50 MiB per Run, configurable, with rotation), and are deleted at root cleanup.

`POST /internal/runtime/runs/{runId}/heartbeat`

```json
{
  "eventId": "hb_042",
  "occurredAt": "2026-11-19T10:01:30Z"
}
```

Sent on a fixed cadence (default 30 seconds). Heartbeat proves Bridge liveness; it does not reset progress-based timers.

`POST /internal/runtime/runs/{runId}/events`

```json
{
  "eventId": "evt_001",
  "type": "progress",
  "occurredAt": "2026-11-19T10:00:00Z",
  "payload": {
    "message": "Running unit tests"
  }
}
```

Completion:

`POST /internal/runtime/runs/{runId}/completed`

```json
{
  "eventId": "evt_002",
  "occurredAt": "2026-11-19T10:05:00Z",
  "result": {
    "summary": "Implementation completed",
    "artifacts": [
      {
        "type": "implementation_summary",
        "format": "markdown",
        "content": "Implemented authentication..."
      }
    ],
    "branch": "agent/issue-15-auth",
    "commitSha": "abc123"
  }
}
```

A `completed` callback may carry `outcome`: `completed` (default) or `needs_human_input` — the agent finished after publishing a mid-run blocker question and checkpointing work to the agent branch; the Task ends in the terminal `Blocked` state (06 §4, decision 72).

Failure:

`POST /internal/runtime/runs/{runId}/failed`

```json
{
  "eventId": "evt_003",
  "occurredAt": "2026-11-19T10:05:00Z",
  "error": {
    "code": "runtime_failure",
    "message": "Container exited unexpectedly"
  }
}
```

Callbacks should be idempotent using `eventId`. The Orchestrator owns Task/Workflow transitions; agents report facts/results but do not directly set orchestration status.

### Runtime Bridge V1 contract (decisions 58–61)

**Input delivery.** The Bridge launches Pi in `--mode rpc` and delivers the entire Task input as a single RPC `prompt` containing the structured content rendered from the Run's `RunContextSnapshot`. Task input is never passed through environment variables and never mounted as a separate input file in V1. A successful RPC response means the prompt was accepted, not that the work finished; the Bridge then waits for `agent_settled` (which also covers automatic continuations/retries).

**Event source.** Realtime events are read exclusively from Pi's RPC stdout JSONL stream; the session `.jsonl` file under the session directory is saved history, not an event source. `stderr` is used for Bridge and Pi diagnostics only. Mapping (pi event → RunEvent):

| Pi RPC event | RunEvent |
|---|---|
| `agent_start` | `agent_started` |
| `turn_start` | `turn_started` |
| `turn_end` | `turn_completed` |
| `message_update` (text delta) | `assistant_output` (aggregated) |
| `tool_execution_start` | `tool_started` |
| `tool_execution_update` | `tool_progress` |
| `tool_execution_end` | `tool_completed` |
| `agent_settled` | `agent_settled` |

Progress counts: non-empty assistant text deltas (aggregated before persistence so one DB event does not spawn per token), tool-execution start/update/end, turn completion, artifact creation. Heartbeat, thinking deltas, `queue_update`, and bare liveness are not progress.

**Run result.** After `agent_settled`, the agent must have written a structured `result.json` in the Run's writable directory:

```json
{
  "schemaVersion": 1,
  "summary": "Implementation completed",
  "artifacts": [
    { "type": "implementation_summary", "format": "markdown", "path": "artifacts/summary.md" }
  ],
  "requiredPublications": [
    { "toolCallId": "call-123", "kind": "issue_comment", "externalId": "github-comment-id" }
  ]
}
```

The Bridge validates the manifest and artifact files (they must stay within the allowed directory), derives `branch`//`commitSha` itself via Git, correlates `requiredPublications` with `tool_execution_end` records (verifying the GitHub publications actually happened and without errors — the verification mechanism behind decision 42), and posts the normalized result via `/completed`.

**Authentication.** The Runtime API creates a fresh random credential per Run, delivered to the Bridge as a read-only secret file (not a regular environment variable, never exposed to Pi). The credential is stored hashed in PostgreSQL, allows only the callbacks of that specific `runId`, and is revoked after terminal delivery is confirmed.

**Sender resilience.** Callbacks are written to a durable spool on persistent host storage (`runs/{run_id}/callback-spool/`) before being sent. Network errors, 5xx, and 429 are retried with exponential backoff using the same `eventId`; heartbeats may be coalesced to the latest value; lifecycle and terminal callbacks are never silently dropped. After a Bridge restart, undelivered callbacks are redelivered from the spool. The delivery cycle ends only when the Runtime API confirms persistence of the terminal callback — the next Run for the same Task does not start until the previous Run's termination is fixed (scope: same Task/Workflow — see `open-questions.md`).

**Graceful stop (from `/stop`).** Orchestrator → Runtime API → Bridge control signal; then: (1) RPC `clear_queue` removes queued messages first (an `abort` alone can continue processing queued messages); (2) `abort` and `abort_bash`; (3) wait up to ~30 s for `agent_settled`; (4) close Pi's stdin for a graceful exit; (5) SIGTERM, then SIGKILL if the process remains; (6) verify child processes are stopped; (7) post the terminal `cancelled` callback.

The Execution Host Monitor posts telemetry to `POST /internal/runtime/monitor/telemetry` with its own host credential (decision 62); the Runtime API stores the signal timestamps the Orchestrator uses for reconciliation.

## 5. GitHub write adapter

Conceptual operations:
- Create issue comment.
- Update issue/PR comment if supported and desired.
- Create branch/commit/push (prefer through workspace Git operations, with GitHub API for metadata).
- Create PR.
- Synchronize/read PR reviews and review threads.
- Submit PR review.
- Create inline review comments and replies in existing review threads.
- Request reviewer.

Agent profiles perform their GitHub operations through GitHub MCP (reading, commenting, submitting reviews) under their permission allow-list; all agent-owned GitHub writes go directly through the agent. The Outbox is reserved exclusively for orchestration-owned system notifications: final Task failures, command rejections, cancellation confirmations (see 06 §13).

The reviewer Task input should include the pinned PR Head SHA (`target_head_sha`) and Base SHA (`target_base_sha`), the Git-formed diff, relevant conversation comments, submitted reviews, and inline review threads (unresolved threads first). The reviewer must report which SHA it reviewed. The diff is formed locally from the two pinned revisions during workspace provisioning — GitHub `pulls/{n}/files` is not used as the diff source (3 000-file cap). Fork-PR heads are checked out from the PR head ref of the base repository at the pinned SHA; fork code is treated as untrusted (decision 74).

## 6. Polling API / internal integration

The poller may be a scheduled worker, not an HTTP API. Its contract is:
- Fetch a stream/page from GitHub.
- Normalize records.
- Persist upserts and event records.
- Advance cursor only after persistence succeeds.
- Never start agent work directly.
- Commit history is not synchronized in V1: PR head/base SHAs are stored and commit metadata is read from Git during workspace preparation; `pulls/{number}/commits` may be consulted ad hoc (decisions 64, 77).

## 7. Errors

Standard internal error shape:
```json
{
  "code": "task_not_retryable",
  "message": "Task is in terminal state",
  "details": {}
}
```

Suggested error categories:
- Validation.
- Authorization.
- Not found.
- Conflict / invalid transition.
- Rate limit.
- External integration failure.
- Runtime failure.
- Timeout.
- Storage failure.

Error codes used across the system: `agent_profile_not_configured`, `agent_image_not_ready`, `agent_profile_not_ready`, `review_head_changed_before_execution`, `review_request_removed_before_execution`, `review_source_unavailable`, `github_publication_failed`, `startup_timeout`, `idle_timeout`, `context_too_large`, `required_permission_denied`, `task_not_retryable`.

## 8. Idempotency and concurrency

- Start Run: idempotency key = `runId`; `agentSessionId`/`workspaceId` act as resource keys.
- Callback: unique event ID per Run.
- Command: unique GitHub comment ID; duplicate poll deliveries cannot repeat the action.
- GitHub write: Outbox idempotency key (system notifications only; duplicate control via the invisible HTML-comment marker and per-action dispatcher lease — at-least-once with best-effort deduplication, decision 67).
- Polling: external IDs/fingerprints.
- Task claim: transaction/lease.
- V1 global execution cap: `max_concurrent_runs` (default 10, Orchestrator configuration; the `system.yaml` initial value for a single execution host may be lower) bounds concurrently running Runs across Workflows (see 02 §19).
- Commands are idempotent by GitHub comment ID; `/status`, `/stop`, `/help`, and `/retry` do not create new Tasks (`/retry` creates a new Run on the last failed Task).
