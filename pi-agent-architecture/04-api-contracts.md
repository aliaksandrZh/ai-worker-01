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

`POST /api/v1/tasks/{taskId}/retry`

`POST /api/v1/tasks/{taskId}/cancel`

Retry request:
```json
{
  "reason": "Retry after correcting runtime configuration"
}
```

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

Ownership: the Orchestrator owns the DB rows — it creates the Run (`starting`), the AgentSession (`provisioning`, one per Run), and the Task's Workspace (created on the first Run, reused by retries). The Runtime API provisions the physical resources — Pi session files, workspace directory, container and mounts, the per-Run log directory — and returns runtime metadata. The request IDs act as idempotency/resource keys; there are no separate create-session/create-workspace endpoints.

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
  "sessionFilePath": "/home/pi/.pi/agent/sessions/…",
  "containerInstanceId": "container_123",
  "status": "starting"
}
```

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

The reviewer Task input should include the pinned PR Head SHA (`target_head_sha`), diff, relevant conversation comments, submitted reviews, and inline review threads. The reviewer must report which SHA it reviewed.

## 6. Polling API / internal integration

The poller may be a scheduled worker, not an HTTP API. Its contract is:
- Fetch a stream/page from GitHub.
- Normalize records.
- Persist upserts and event records.
- Advance cursor only after persistence succeeds.
- Never start agent work directly.

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

## 8. Idempotency and concurrency

- Start Run: idempotency key = `runId`; `agentSessionId`/`workspaceId` act as resource keys.
- Callback: unique event ID per Run.
- Command: unique GitHub comment ID; duplicate poll deliveries cannot repeat the action.
- GitHub write: Outbox idempotency key (system notifications only).
- Polling: external IDs/fingerprints.
- Task claim: transaction/lease.
- V1 global execution concurrency limit = 1.
- Commands are idempotent by GitHub comment ID; `/status`, `/stop`, `/help`, and `/retry` do not create new Tasks (`/retry` creates a new Run on the last failed Task).
