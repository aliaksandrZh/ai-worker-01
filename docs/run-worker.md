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
| `run_completed` | settled **with** `report_completion` |
| `run_failed` | settled without it, never started, or watchdog timeout |
| `run_cancelled` | stopped on `run_stop_requested` |

Everything the Bridge says beyond terminal state stays out of the log: heartbeats are
replaced by docker-inspect liveness, progress callbacks return when something consumes
them.

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
