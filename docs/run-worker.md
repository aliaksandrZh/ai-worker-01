# Run Worker — interface contract

> The EventLog consumer that executes agent Runs ([ADR-0003](adr/0003-run-worker.md)),
> absorbing Runtime API. **Interfaces only — no implementation.** Delivery is the Event
> Log contract ([eventlog.md](eventlog.md)); this document fixes what the Run Worker
> consumes, what it emits, and its one HTTP surface.

## Events

Consumed:

| Event | Meaning |
|---|---|
| `run_scheduled` | provision the workspace/session/container and start the agent |
| `run_stop_requested` | stop the running container (cancel path) |

Emitted:

| Event | Meaning |
|---|---|
| `run_processing` | the container is up, the agent started |
| `run_completed` | settled **with** `report_completion`; carries `Branch` and `CommitSha` from the terminal body's git facts — the pointer producers for the fix-round branch and the review head pin |
| `run_failed` | settled without it, never started, or watchdog timeout |
| `run_cancelled` | stopped on `run_stop_requested` |

Everything the Bridge says beyond terminal state stays out of the log: heartbeats are
replaced by docker-inspect liveness, progress callbacks return when something consumes
them. The one exception is the `tool` route below — the agent's communication tools
produce the flow-driving events, and they are consumed.

## Communication tool callbacks (route `tool`)

The vocabulary: the eleven communication tool names of
[flows/tools.md](flows/tools.md) — `save_plan`, `request_clarification`, `report_blocker`,
`submit_review_comments`, `request_decision`, `report_bug`, `report_env_issue`,
`request_criteria_clarification`, `report_tests_passed`, `report_completion`,
`submit_pr_description`. A change to the set or a signature is a bridge contract bump
(`bridge.json` stamp).

The Bridge observes each **completed, error-free** communication tool call in the agent's
stream and turns it into exactly one callback:

```
route: "tool"
body: { eventId: "<runId>-tool-<callId>", occurredAt, toolName, args }
```

- `eventId` is deterministic (run + toolCallId): spool redelivery repeats it verbatim, and
  deduplication happens at ingestion — a unique index on `(type, payload->>'eventId')` with
  `ON CONFLICT DO NOTHING` for the callback row; no consumer sees a duplicate.
- A failed or permission-denied call produces no `tool` callback.
- The worker translates each `tool` callback into the typed event of
  [flows/events.md](flows/events.md) (tool → event mapping) and appends it to the root
  item's stream. Non-communication tool calls stay opaque progress callbacks.

## Run input (pointers v2)

The run input is a file in the run tree, written before the container starts — pointers
only ([ADR-0006](adr/0006-github-sync.md): agents read GitHub themselves):

```
RunInput {
  Owner, Repo, ItemNumber, Command, PlanCommentId, Profile,   // identity + trigger
  PrNumber?,                                                  // linked PR, if any
  AgentBranch?,                                               // fix round: continue this branch/PR
  DefaultBranch,                                              // PR base (repositories config)
  ReviewHeadSha?                                              // reviewer's pinned head
}
```

The Orchestrator fills every pointer from Workflow/Task state at `run_scheduled`:
`AgentBranch`/`HeadSha` are recorded from `run_completed`'s git facts (first implement
Run creates the branch; fix rounds continue it), `ReviewHeadSha` is pinned from
`pr_updated` when a review round starts, `DefaultBranch` comes from the repositories
config injected via Orchestrator options. The review base is deliberately absent: the
reviewer entry derives it locally (`merge-base(head, origin/<DefaultBranch>)`) — the
Orchestrator keeps zero GitHub knowledge. The worker exports the entry-facing subset as
container env (`GIT_CLONE_REPO`, `GIT_CLONE_BRANCH`, `REVIEW_HEAD_SHA`,
`REVIEW_PR_NUMBER`) and the Bridge renders the task prompt from the same file — one
input contract, not env + json.

## The one HTTP surface

```
POST /events        (Bridge → app host)
```

- The container's only credential is a **per-run token** (today's `RunCredentialService`);
  it authorizes appending events for that run and nothing else.
- The Bridge spools and retransmits (already built): terminal events are never lost —
  the EventLog's at-least-once contract makes the worker side free of deduplication
  except idempotency.
- `/input` (the run input: pointers to the item, PR, plan and command) is provisioning,
  not egress — an implementation detail behind the seam ([ADR-0006](adr/0006-github-sync.md):
  the ContextAssembler is deleted; agents read GitHub themselves).

## Invariants

1. **No dispatch interface.** The Orchestrator appends `run_scheduled`; redelivery and
   retry of the *scheduling itself* are EventLog semantics, not a dispatcher.
2. **Terminal events are never lost.** Bridge spool + at-least-once; a terminal event
   always settles its Run, even across worker restarts (cursor + visibility timeout —
   recovery, not reconcilers).
3. **The watchdog is the worker's.** Liveness = docker inspect; a Run exceeding the
   configured timeout emits `run_failed` (timed-out cause) and kills the container.
4. **Stop is an event.** `run_stop_requested` → graceful stop within the grace period →
   `run_cancelled`; no sweeper confirms it — the terminal event is the confirmation.
5. **Provisioning is internal.** Workspace, session file, run input delivery, container
   credentials (git PAT etc.) — implementation details; no other module knows Docker
   exists.
6. **One endpoint, one token scope.** The container cannot reach anything but
   `POST /events` for its own Run.

## Configuration knobs

| Knob | Meaning |
|---|---|
| `Workers:RunWorker:Enabled` | run the worker on this instance (standard consumer gating) |
| `RunTimeout` | watchdog ceiling per Run |
| `StopGracePeriod` | how long a stop waits before the container is killed |

## What it deletes from the codebase

`Hosts/RuntimeApi` HTTP surface and its DI/auth scaffolding · `Orchestrator/RunDispatch`
(`IRunDispatcher`) · `OrphanReconciler`, `RunReconciler`, `StopConfirmSweeper` ·
`TimeoutSweeper` (watchdog replaces it) · service-token auth for internal calls.
