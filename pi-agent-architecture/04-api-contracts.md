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
| `/proceed` | Issue | Coder implements approved plan, runs checks, commits/pushes branch, reports result | Yes |
| `/review` | PR | Reviewer inspects current Head SHA in a fresh workspace and submits findings | Yes |
| `/test-cases` | Issue in V1 | Test designer produces test scenarios/cases Artifact; does not implement/run tests by default | Yes |
| `/debug` | Issue or PR | Debugger diagnoses and reports evidence; read-only by default | Yes |
| `/status` | Issue or PR | Reports current/last Workflow, Task, Run, queue and result | No |
| `/stop` | Issue or PR | Gracefully cancels active Task for that WorkItem; retains storage | No |
| `/help` | Issue or PR | Lists available commands for the target type | No |

Common processing sequence: synchronize comment → deduplicate by GitHub comment ID → parse mention/command → verify GitHub Write permission and optional allowlist → validate target/state → persist AgentCommand → create Task/Job if required → acknowledge or reject via GitHub Outbox.

If a task-producing command arrives while the global execution slot is occupied, it is persisted and queued. Preconditions are checked again when the job is claimed. `/stop` is prioritized for the targeted active Task.

## 3. Agent Runtime API

### Start a Task

`POST /internal/runtime/tasks/{taskId}/start`

```json
{
  "runId": "run_123",
  "agentProfileVersionId": "profile-version-coder-3",
  "sessionId": "session_123",
  "workspaceId": "workspace_123",
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
  "sessionId": "session_123",
  "containerInstanceId": "container_123",
  "status": "starting"
}
```

### Send input to active runtime

`POST /internal/runtime/sessions/{sessionId}/input`

```json
{
  "taskId": "task_123",
  "runId": "run_123",
  "message": "Please address the additional requirement."
}
```

V1 normally creates a new session per Task, so this endpoint is primarily for active runs or runtime-level interaction, not cross-task session reuse.

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

The reviewer Task input should include the current PR Head SHA, diff, relevant conversation comments, submitted reviews, and inline review threads. The reviewer must report which SHA it reviewed. Agent profiles may also have GitHub MCP access; the Outbox is used for reliable orchestration-owned writes and retryable notifications.

External writes should be queued in an Outbox for retry and idempotency.

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

- Start Task: idempotency key = Task ID + Run attempt.
- Callback: unique event ID per Run.
- Command: unique GitHub comment ID; duplicate poll deliveries cannot repeat the action.
- GitHub write: outbox idempotency key.
- Polling: external IDs/fingerprints.
- Task claim: transaction/lease.
- V1 global execution concurrency limit = 1.
- Commands are idempotent by GitHub comment ID; `/status`, `/stop`, and `/help` do not create Tasks.
