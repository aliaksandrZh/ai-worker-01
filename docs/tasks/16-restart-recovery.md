# 16: Restart recovery & callback outage

**What to build:** Survivability end to end: Bridge restart → undelivered callbacks redelivered from the durable spool (same eventIds, terminal callbacks never dropped); Runtime API restart mid-run → reconciler restores desired/observed state from PostgreSQL, reattaches without duplicate execution; Orchestrator restart → claimed jobs return to `pending` on lease expiry without double execution; PostgreSQL restart services recover connections; the Workflow-scoped Run serialization holds across restarts (a Workflow's next Run never starts before the previous Run's terminal callback is persisted); different Workflows still run concurrently under the cap; Poller restarts rely on cursors + the overlap window without losing or duplicating commands.

**Spec:** `docs/pi-agent-architecture/01-architecture-and-flows.md` §12 (reconciliation, recoverability); `03-state-machines.md` §8 (lease expiry), §9; `04-api-contracts.md` §4 (sender resilience), §8 (idempotency); `05-v1-decisions.md` decisions 22, 60, 62.

**Blocked by:** 12, 13

**Status:** implemented (2026-11-22), branch `task-16-restart-recovery`, pending review. Full suite green:
`dotnet test PiAgent.slnx` → 2 + 9 + 47 + 548 passing, 0 failed; `node --test src/config/agents/stub/runtime-bridge/*.test.mjs` → 36/36.
LIVE-stack walkthrough executed (see "Live verification" below): every restart/outage scenario was driven
against `aliaksandrZh/04` with the stack rebuilt from this branch; commands injected through the task-08
`/internal/commands` hook because both repo PATs belong to `github.bot_login` (bot-authored comments never
trigger, decision 6). Sub-second crash windows and knob-dependent verdicts are unit-verified and listed as such.

- [x] Terminating the Runtime API mid-run → callbacks spooled and redelivered; Run reaches the correct terminal state exactly once
- [x] Terminating the Orchestrator with a claimed job → lease expiry returns it to pending; no duplicate execution
- [x] Next Run of the same Workflow does not start before the previous terminal callback is persisted; Runs of different Workflows continue concurrently
- [x] PostgreSQL restart → every service reconnects and resumes (health endpoints report recovery)
- [x] Poller restart loses nothing: dedupe + overlap cover the gap; commands processed exactly once
- [x] Acceptance scenarios "Restart recovery" and "Callbacks" (`05` matrix) pass

## What was done (2026-11-22)

The spool/redelivery plane (decision 60), callback idempotency (04 §8), the death/death-restart
reconcilers and the poller/queue/Outbox lease machinery from tickets 03–15 already covered most of this
ticket; the recovery paths below completed it.

**Hand-off-aware claim-lease sweep** (`src/Hosts/Orchestrator/Orchestration/JobQueueWorker.cs`,
`SweepExpiredLeasesAsync`). The claim window ends when `POST /start` succeeds — but the Runtime API flips
the Run out of `pending` inside `/start`, so a crash *after* acceptance rewound the Task `claimed →
queued` and re-executed the same work the next time the Run settled: the lease sweep now consults the
Task's latest Run. Run beyond `pending` → restore the hand-off state (Task `starting`, `started_at`
pinned, job `completed`) instead of rewinding — the terminal-Run consumer adopts it; Run still `pending`
/ absent → the existing rewind (delete pending Run rows, job `pending`) is unchanged and cannot duplicate
execution. A `/stop`-cancelled task whose hung claim's lease expires keeps its job `cancelled` (previously
resurrected to `pending` and re-failed on the next claim). Tests: `ExpiredLease_AfterHandoff_RestoresHandoffState_NeverRewinds`,
`ExpiredLease_StoppedTask_JobCancelledNotResurrected` (JobQueueWorkerTests; existing rewind tests unchanged).

**Dispatcher recovery** (`src/Hosts/Orchestrator/Orchestration/RunDispatch.cs`). A transport failure used
to classify every outcome as `Transient` — including one where `/start` was accepted and the Run already
left `pending` (release ⇒ re-execute). The dispatcher now re-reads the Run row: still `pending` →
`Transient` (clean release); beyond `pending` → recovered as `HandedOff` with the ordinary write-back
(extracted to idempotent `CompleteHandoffAsync`, shared with the success path); run gone → `Superseded`.
The 409-with-terminal-Run branch now persists the same write-back so the consumer adopts the terminal Run
immediately instead of waiting for lease-expiry recovery. Tests: `TransportFailure_AfterTheRuntimeAcceptedTheStart_RecoversAsHandedOff`,
extended `IdempotentRedispatch_WhenTheRunIsAlreadyTerminal_CompletesTheClaim`.

**Race-safe reconciled terminal** (`src/Hosts/RuntimeApi/RunReconcileService.cs`). The timeouts/death
reconciles wrote terminal facts unconditionally — a terminal callback delivered mid-reconcile could be
overwritten (`succeeded` → `timed_out`), double log-reference artifacts, double revocations. The terminal
write is now a conditional claim from `pending|starting|running|stopping`; 0 rows = the delivered callback
wins = idempotent skip, and the terminal facts (log trio, credential revoke, session terminated, workspace
release) are applied only by the winner. `StopConfirmSweeper` now calls the same service (its private copy
removed). Test: `ReconciledTerminal_NeverOverwritesADeliveredTerminalCallback`.

**Spool-evidence guard on the reconciler (refines ticket 13)** (`src/Hosts/RuntimeApi/RuntimeStorage.cs`,
`TimeoutSweeper.cs`). `startup_timeout`/`idle_timeout` adjudicate the ABSENCE of lifecycle facts — right
after a Runtime API restart those facts may still sit in the Bridge's durable spool (delivery retry backoff
up to 30 s/entry), so the first post-restart tick could kill a run that actually started (`startup_timeout`),
whose redelivered terminal then met 409 and parked. The sweeper now suppresses those two verdicts while the
Run's `callback-spool/` (shared host mount, decision 52) still holds undelivered ordered entries
(`RuntimeStorageService.SpoolHasUndeliveredEntries`; the coalesced heartbeat slot does not count, and a
healthy channel empties the spool within one sender pass). `max_run_duration` stays unguarded — the wall
clock is independent of delivery (decision 68). Ticket 13's counterfactuals hold (the `never_start` stub
emits nothing ⇒ empty spool ⇒ verdict fires as before). Tests: `StartupTimeout_SuppressedWhileTheCallbackSpoolHoldsUndeliveredEntries`,
`IdleConfirmed_SuppressedWhileTheCallbackSpoolHoldsUndeliveredEntries`.

**Serialization gate test** (`OrchestrationConstraintTests.RunPrepare_SerializationGate_HoldsAcrossTasksAndRestarts`)
— the reachable cross-Task window (/stop → new command while the `cancelled` callback is not yet persisted):
`RunPrepare` refuses while the Workflow's `stopping` Run exists and opens exactly when the terminal callback
is persisted (the check reads PostgreSQL state, so restarts cannot bypass it).

**Poller restart, Outbox lease recovery, cross-Workflow cap** — already built (cursors advanced only after
save + 5-minute overlap + dedupe keys; outbox claims expired `processing` leases; `max_concurrent_runs`
slot math). Newly-relevant coverage verified: `RestartRecovery_NewCycleInstance_NoDuplicates`,
`ClaimNext_ExpiredLease_Reclaims`, `CapLimitsClaims`/`CapCountsStartingAndRunningRuns`.

**Pre-existing flaky fix (was red on `main`)** — `AdmissionTests.SameWorkflowSecondComment_RejectedReplyEnqueued`
seeded its second comment with two separate `DateTime.UtcNow` calls (≈100 ns apart ⇒ false
`command_edited`); one captured instant.

## Automated verification (executed)

- `dotnet test src/PiAgent.slnx` → Architecture 2 + Platform 9 + Infrastructure 47 + Hosts 548 = **606 passing, 0 failed**
- `node --test src/config/agents/stub/runtime-bridge/*.test.mjs` → **36 passing, 0 failed**

## Live verification (2026-11-22, `aliaksandrZh/04`, stack rebuilt from this branch; all timestamps UTC)

| # | Scenario | Action | Evidence (command → output) |
|---|---|---|---|
| 0 | **Sanity + cross-Workflow concurrency (S0/D)** | `/plan` injected on issues #16 and #15 back-to-back | `docker ps` → two `pi-session-*` containers **Up 2 s / Up 5 s simultaneously**; both runs terminal `succeeded`; both tasks `succeeded`; both workflows `waiting_for_human` |
| 1 | **Callbacks — API outage, spool recovery** | run `c0cc9dbe` flipped `starting` → `docker kill runtime-api` ≈1.5 s later (compose does NOT auto-restart a killed container; recovery = `docker start`) | Bridge spooled **51 entries** during the outage, `bridge.log`: `retrying entry evt-0… in 1s/2s/4s/8s (fetch failed)`; `curl /healthz` → healthy after `docker start`; redelivery drained the spool; Run **`succeeded` 10:40:32** with the stub's result; **72/72 distinct eventIds, exactly one `completed`**; container `Exited (143)` (API stop after persistence) |
| 2 | **Orchestrator restart with a claimed job** | `/plan` on #17 → job `processing`/leased, task `claimed`, no Run yet → `docker kill orchestrator` (iteration ~17 of a 150 ms poller) → restart | lease expired 10:46:06 → sweep returned the job **`pending`** → re-claim (`attempts 1→2`) → **exactly 1 run** (`succeeded` 10:47:28, one attempt), 0 duplicate events, task `succeeded`, workflow `waiting_for_human` |
| 3 | **PostgreSQL restart** | `docker restart postgres` at 10:41:17 | every DB-backed service logged failed cycles (`cycle failed PollerCycle/CommentCommandProcessor/OutboxDispatcher/db size query failed`) and **none crashed**; `runtime-api /readyz` → `200 ready` → **`503 {"status":"notReady","failed":[{"name":"postgres","error":"57P01: terminating connection…"}]}` → `200 ready`**; the killed-claim job later re-claimed and dispatched **over the restarted PostgreSQL** (B's recovery ran after this restart) |
| 4 | **Poller restart** | `docker stop poller` 10:41:32 → 3 comments posted while down → `docker start` 10:41:56 | all 3 synced **exactly once** (`github_event` dup keys: 0), **0 AgentCommands** (bot-authored suppression correct); `/internal/commands` replay of the same `commentId` returned the same command (`processed`), 1 row |
| 5 | **Global invariants across the whole run** | session-wide SQL sweep | `duplicate run_event keys = 0`; **every task created today has exactly 1 run**; 4/4 scenario tasks `succeeded` |

## Known sub-second windows covered by tests, not live-hittable

- The sweep's **hand-off-restore branch** needs the Orchestrator to die between `/start` acceptance and its
  own write-back (sub-second); unit-verified by `ExpiredLease_AfterHandoff_RestoresHandoffState_NeverRewinds`.
- The **spool-evidence suppression** needs an outage comparable to the startup/idle knobs (10 min/60 min
  defaults; the live outage was ~45 s); unit-verified at the 10 s test-suite knobs.
- A live **/stop → next-command serialization** demo needs an agent stalled in `stopping` (hang-stub
  version + rebuild); the DB-level gate is unit-verified; live cross-Workflow concurrency was demonstrated.

## How to manually test (a live pass of every scenario was executed 2026-11-22 — see the table above; these steps reproduce it)

Prerequisites: branch `task-16-restart-recovery`; `src/.env` with `POSTGRES_*`, `SERVICE_TOKEN_*`, `KOMODO_*`;
PAT files in `src/secrets/` (**both PATs here belong to `github.bot_login`, so real comments are context-only —
commands go through the task-08 hook**, author = the allowlisted repository owner); Komodo running
(`docker network create pi-agent-net 2>/dev/null; docker compose -f src/komodo/ferretdb.compose.yaml
--env-file src/komodo/compose.env up -d`); `task stack-rebuild && task stack`. Then:

```sh
export TOK=$(grep '^SERVICE_TOKEN_ORCHESTRATOR' src/.env | cut -d= -f2)
export PSQL="docker exec pi-agent-control-plane-postgres-1 psql -U pi_agent -d pi_agent"
# disposable issues (create on GitHub with any identity; wait one poll cycle)
curl -s -X POST -H "Authorization: Bearer $(cat src/secrets/github-sync-pat.txt)" \
  https://api.github.com/repos/aliaksandrZh/04/issues \
  -d '{"title":"task16 restart-recovery","body":"disposable"}'          # → {"number": N}
$PSQL -tc "SELECT number, id FROM work_item ORDER BY number DESC LIMIT 3;"   # pick the new work_item id
```

1. **Sanity + cross-Workflow concurrency** — inject on two issues within a second (unique CommentIds):
   ```sh
   docker exec pi-agent-control-plane-orchestrator-1 curl -s -X POST -H "X-Service-Token: $TOK" \
     -H 'Content-Type: application/json' http://localhost:8080/internal/commands \
     -d '{"workItemId":"<WI-1>","author":"aliaksandrZh","commentId":987001000201,"text":"@jsxhuz /plan"}'
   # repeat for <WI-2> with commentId 987001000202
   docker ps --format '{{.Names}}\t{{.Status}}' | grep pi-session   # expect TWO pi-session-* containers Up together
   ```
   Expected (observed): two containers `Up` simultaneously; both runs `succeeded`; tasks `succeeded`;
   workflows `waiting_for_human`; each issue gets exactly one planner reply comment.
2. **Runtime API terminated mid-run** — inject `/plan` (new CommentId), poll for a fresh `starting` run and
   kill the API immediately:
   ```sh
   for i in $(seq 1 400); do RUN=$($PSQL -tAc "SELECT id FROM run WHERE status='starting' AND completed_at IS NULL AND started_at > now() - interval '90 seconds' LIMIT 1"); [ -n "$RUN" ] && break; sleep 0.25; done
   docker kill pi-agent-control-plane-runtime-api-1          # observed: killed ≈1.5 s after handoff
   ls src/storage/*/runs/$RUN/callback-spool/                 # expect: ordered entries accumulating (51 at the peak)
   tail src/storage/*/runs/$RUN/logs/bridge.log               # expect: "retrying entry … in 1s (fetch failed)" etc.
   docker start pi-agent-control-plane-runtime-api-1          # recovery (docker kill never triggers restart policies)
   curl -s http://localhost:8080/healthz                      # {"status":"healthy"}
   ```
   Expected (observed): the spool drains; the Run reaches the agent-reported terminal state (stub:
   `succeeded`) with the result persisted; `SELECT count(DISTINCT event_id), count(*) FROM run_event WHERE
   run_id='$RUN'` → equal; exactly one `completed` RunEvent; the task `succeeded`, workflow
   `waiting_for_human`; the container was stopped by the API after persistence.
3. **Orchestrator terminated with a claimed job** — inject `/plan` on another issue; catch the claim and kill:
   ```sh
   for i in $(seq 1 400); do JOB=$($PSQL -tAc "SELECT id FROM orchestration_job j JOIN task t ON t.id=j.task_id WHERE j.status='processing' AND j.created_at > now() - interval '2 minutes' ORDER BY j.created_at DESC LIMIT 1"); [ -n "$JOB" ] && break; sleep 0.15; done
   docker kill pi-agent-control-plane-orchestrator-1 && docker start pi-agent-control-plane-orchestrator-1
   $PSQL -c "SELECT j.status, t.status, r.status FROM orchestration_job j JOIN task t ON t.id=j.task_id LEFT JOIN run r ON r.task_id=t.id WHERE j.id='$JOB';"
   # expect: processing | claimed | (no run) — the lease now runs out (~300 s watch window)
   ```
   After the lease expires + a few ticks (observed: ~5 min window; the worker sweeps at :06 past the minute
   here): job `pending` → re-claimed (`attempts` 2) → one Run `succeeded` — `SELECT count(*) FROM run WHERE
   task_id=(SELECT task_id FROM orchestration_job WHERE id='$JOB')` → **1**; task `succeeded`; 0 duplicate
   `(run_id, event_id)` keys for that run.
4. **PostgreSQL restart** — while the stack is otherwise idle (this ran before step 3's expiry in the live pass):
   ```sh
   (docker restart pi-agent-control-plane-postgres-1 &)  ; watch curl -s http://localhost:8080/readyz
   ```
   Expected (observed): `200 ready` → `503 {"status":"notReady","failed":[{"name":"postgres",…57P01…}]}` →
   `200 ready` after Postgres returns; service logs show failed cycles during the window and normal ticks
   afterwards; no host exits (`docker ps` statuses unchanged).
5. **Poller restart** — stop the poller, post real comments while it is down, restart:
   ```sh
   docker stop pi-agent-control-plane-poller-1
   # post 1–3 comments via the PAT (bot-authored → context only)
   docker start pi-agent-control-plane-poller-1
   $PSQL -c "SELECT github_comment_id FROM github_comment WHERE github_comment_id > 7077882000;"   # each once
   $PSQL -c "SELECT count(*) FROM (SELECT dedupe_key FROM github_event WHERE dedupe_key LIKE 'ic:%' GROUP BY 1 HAVING count(*)>1) d;"  # 0
   ```
   Expected (observed): every comment persisted exactly once after restart; no `AgentCommand` rows from them
   (bot suppression); hook replay of an already-ingested CommentId returns the same command (`processed`).
6. **Serialization/concurrency** — covered live by step 1 (different Workflows run concurrently under the
   cap) and at the DB level by the unit test; the /stop → next-command window needs a hang-stub version
   (same rebuild dance as ticket 13's manual list) and is optional.

Teardown: `docker compose --env-file src/.env -f src/compose.yaml down` and
`docker compose -f src/komodo/ferretdb.compose.yaml --env-file src/komodo/compose.env down` (volumes kept);
verify with `docker ps -a` that nothing remains `Up` (exited per-Run `pi-session-*` containers are the
system's stopped run machines — spec-normal, removed only by cleanup/closure).
