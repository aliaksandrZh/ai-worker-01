# 13: Host Monitor + timeouts + reconciliation

**What to build:** The Execution Host Monitor process (session-file activity, process trees/cgroups, container state, managed-storage usage via the local Docker API → telemetry to the Runtime API with its own credential; orphan-candidate detection in `/var/lib/pi-agent/workflows/` — candidate when the DB record is gone or storage already marked deleted, ≥24 h grace, 24 h quarantine before physical deletion, nothing deleted while PostgreSQL is down or containers use the tree). Timeouts: `startup_timeout` (10 min) fails a Run that never became ready; `idle_timeout` (60 min, all observable activity absent while alive) makes the Run a reconciliation candidate — the Host Monitor checks child processes/CPU/IO before any stop (a long-running build/test is not killed for silence), a confirmed hang fails the Run with `error_code = idle_timeout`; `max_run_duration` (4 h) ends the Run `TimedOut` independently, no automatic retry for it. `permission_blocked` handling: fail-closed — the Bridge records the event, returns an explicit denial, never parks a dialog; impossible mandatory work → Run fails `required_permission_denied`, Task Failed, Workflow `waiting_for_human`, Outbox notification. Reconciler compares PostgreSQL desired vs Komodo observed state and recovers stale runs.

**Spec:** `docs/pi-agent-architecture/01-architecture-and-flows.md` §2 (Host Monitor), §12 (signals, timelines); `03-state-machines.md` §5, §9; `05-v1-decisions.md` decisions 22, 23, 30, 34, 62, 68, 69, 77 (orphan reconciliation); `04-api-contracts.md` §4 (monitor telemetry route).

**Blocked by:** 09

**Status:** done

- [x] A stub that never becomes ready → Run fails `startup_timeout` after 10 min (live at the 90 s test knob; the 10 min default is the same code path via `system.yaml`)
- [x] A silent-but-alive stub → idle candidate → reconciled (processes checked) → confirmed hang → Failed `idle_timeout`; a busy child process prevents the kill (live idle_timeout; the busy-child spare is verified by `TimeoutReconcilerTests.IdleTimeout_ConfirmedHang_FailsIdleTimeout_BusyRunIsSpared` — a live busy-child variant needs a stub that spawns a child process)
- [x] A wall-clock stub exceeding 4 h → Run `TimedOut`; `/retry` is the only recovery (no automatic retry) (live at the 300 s/1 h knobs — same evaluator path as the 4 h default; automatic retry is pre-execution-only per decision 70 and never applies after execution began)
- [x] Effective limits honor the pinned profile-version execution-limit defaults (`AgentProfileVersion.startup_timeout`/`max_run_duration`, optionally `idle_timeout`) — a profile may lower them but never bypass the system constraints (`01 §16`, `02 §12`, decisions 30, 54; pinned by ticket 02)
- [x] Denied tool operation → `permission_blocked` RunEvent + explicit denial; impossible mandatory operation → Run Failed `required_permission_denied` + notification
- [x] Host-Monitor telemetry stored in PostgreSQL with its own credential; the control plane never reads execution-host paths or process tables directly
- [x] Orphan candidates detected with the grace/quarantine timing; nothing deleted while the DB is unavailable (live full lifecycle at 60 s windows; the DB-unavailable guard is structural — the reconciler's decisions run inside DB cycles, so nothing can execute without a live PostgreSQL)
- [x] A container dies without emitting a terminal callback → reconciliation marks the Run Failed with a clear error and the notification path fires (never silent recovery — `01 §12`)
- [x] Komodo/Periphery outage: desired state is preserved, reconciler retries without corrupting state; recovery resumes cleanly when reachable (decision 22)
- [x] Acceptance scenario "Timeouts" (`05` matrix) passes (all three outcomes exercised live at short knobs + default-limits profile version rebuilt afterwards)

## Implementation report (2026-11-22, branch `task-13-timeouts-reconciliation`)

Built; all automated suites green. Live-stack verification is deferred until after user review (owner instruction: implement → review → start).

**Runtime API** (`src/Hosts/RuntimeApi/`)
- `TimeoutSweeper.cs` + pure `TimeoutEvaluator`: enforces the three limits per tick — `startup_timeout` (Run still `starting`, never ready), `idle_timeout` (silence of ALL observable activity while the heartbeat continues → idle CANDIDATE; the Host Monitor's latest telemetry decides: busy child process tree/CPU → spared, none → confirmed hang → Run `failed` with `idle_timeout`), `max_run_duration` (wall clock → Run `timed_out`, no automatic retry). No unevidenced idle kill: a stale/missing observation never authorizes a stop.
- `RunReconcileService.cs`: effective-limit computation (profile-version `execution_limits` may lower, never exceed the §16 system knobs) and the reconciled-terminal write (Komodo stop confirmed first; a Komodo outage preserves desired state and retries — decision 22) with the identical terminal facts a delivery would record.
- `RunReconciler.cs`: death reconciliation — a container observed `stopped/failed/removed` (or deployment gone) while its Run never became terminal → Run `failed` (`runtime_failure`, "reconciled: …") so the notification path fires (never silent recovery).
- `Callbacks.cs`: `agent_started` moves the Run `starting → running` (the readiness fact the startup timeout judges against).
- `MonitorTelemetryApi.cs`: `POST /internal/runtime/monitor/telemetry` behind the host-monitor service credential (decision 65); the LATEST observation is stored in PostgreSQL (`monitor_observation`).
- `OrphanReconciler.cs` (decision 77): resolves Host-Monitor directory reports against PostgreSQL; candidates persist ≥ `orphan_grace_period` (`orphan_candidate` table), are then physically renamed `<name>.orphan-quarantine`, and deleted after `orphan_quarantine_period` — never while a container bind covers the tree and never without fresh telemetry.
- `RuntimeLimits.cs`: reads the §16 knobs from the configuration plane's `config/system.yaml` (compose mounts it into `runtime-api` now).
- `RunErrorCodes` gained `startup_timeout`, `idle_timeout`, `required_permission_denied` + the notify set.

**Host Monitor** (`src/Hosts/HostMonitor/`)
- `DockerMonitorClient.cs` + `HostMonitorCycle.cs`: per tick it observes `pi-run`-labeled containers via the local Docker API (state, process tree via `/top`, CPU via `/stats`, bind mounts) and the managed-storage tree (top-level workflow dirs, mtimes, sizes) and posts one telemetry envelope to the Runtime API under its own credential. It never decides anything. The control plane never touches host paths/process tables directly (decision 62).

**Bridge + stub** (`src/config/agents/*/runtime-bridge/`, `stub-agent/`; stub and planner modules kept byte-identical, bridge version → `1.2.0`)
- Decision 69 fail-closed: a `permission_denied` stream record maps to the `permission_blocked` RunEvent and is tracked; the manifest validation fails a REQUIRED publication whose tool call was denied with `required_permission_denied` (instead of `github_publication_failed`). No parked dialogs.
- Stub levers: `never_start` (accepts the prompt, never emits `agent_start` → startup_timeout) and `permission_denied` (mandatory publication tool call denied).

**Orchestrator** (`RunTerminalConsumer.cs`): failure notifications for the ticket-13 codes (`required_permission_denied`, reconciled `startup_timeout`/`idle_timeout`, reconciled `runtime_failure`) ride the same change set as the Task transition (one per failed Run); the generic template remains ticket 15's.

**Persistence**: new tables `monitor_observation`, `orphan_candidate` (migration `AddMonitorAndOrphans`).

**Automated verification (executed)**
- `dotnet test PiAgent.slnx` → 2 + 9 + 47 + 523 green, 0 failed (Architecture/Platform/Infrastructure/Hosts suites; new: `TimeoutReconcilerTests` 16, `OrphanReconcilerTests` 4, `MonitorTelemetryTests` 2, plus updated terminal-consumer/reconciler/config-parser tests).
- `node --test src/config/agents/stub/runtime-bridge/runtime-bridge.test.mjs` → 36 pass (4 new: permission_blocked mapping, required_permission_denied validation, stub `permission_denied` + `never_start` behaviors).
- `docker compose --env-file .env -f compose.yaml config --quiet` → OK.

**Live-stack verification (executed 2026-11-22, with TEMPORARY test limits — all reverted):**
`startup_timeout/idle_timeout/max_run_duration = 90s/120s/300s(then 1h)`, `orphan_grace/quarantine = 60s`, stub levers switched per scenario, routing temporarily flipped `plan → stub` (then back to `planner`). Every config file was restored to the spec defaults afterwards, and a default-limits profile version (stub v16 / planner v8, 10m/4h) was rebuilt before shutdown.

| Scenario | Evidence (commands run + observed states) |
|---|---|
| `startup_timeout` (stub `never_start`) | synthetic `/plan` via `POST /internal/commands`; run failed `startup_timeout` ("reconciled: the run never became ready within the effective startup_timeout (00:01:30)"), container stopped, Task `failed`, Workflow `waiting_for_human`, notification comment 7076856881 published on issue #14 |
| `idle_timeout` (stub `hang`) | run stayed `running` with heartbeats; after 120 s the fresh Host-Monitor telemetry confirmed no active processes/CPU → run failed `idle_timeout`, reconciler log "reconciled: 'IdleConfirmed'" |
| `max_run_duration` (stub `hang`, 300 s limit) | run `timed_out` at the effective wall clock ("reconciled: the run exceeded the effective max_run_duration (00:05:00); /retry is the only recovery") |
| `permission_blocked` (stub `permission_denied`) | `run_event.permission_blocked` recorded with the denial reason; run failed `required_permission_denied`; Task `failed`; Workflow `waiting_for_human`; Outbox notification completed |
| container death without terminal callback | `docker kill pi-session-3e3bd4dc…` while running → reconciler logged "died without a terminal callback — reconciling failure"; run failed `runtime_failure` ("reconciled: …"); notification published |
| orphan tree | `mkdir src/storage/<uuid>` → `orphan_candidate` row ("workflow row missing") during grace → physically renamed `.orphan-quarantine` after grace → status `deleted`, tree gone after quarantine |
| Komodo outage | `docker stop komodo-core` mid-run: run stayed `running`, desired state preserved, cycles retried ("cycle failed RunReconcilerCycle … komodo-core:9120"); after `docker start komodo-core` the pending idle reconciliation completed cleanly |
| decision-30 boundary (live) | lowering only `system.yaml` made the Orchestrator refuse startup: profile execution_limits (10m/60m/4h) "exceeds the system constraint … never exceed them" — 6 violations logged |
| telemetry in PostgreSQL | `monitor_observation` row populated with container binds/processes/CPU from the Docker API; host-monitor `telemetry posted: 1 containers … 3 workflow directories` |
| metrics | runtime-api `/metrics` `runsActive: 0`; host-monitor `/metrics` `containersObserved`, `busyContainers`, `workflowDirectories`, `storageFreeBytes` |

Bugs found and fixed during the live checks: (1) `RuntimeLimits` reader choked on unrelated system.yaml keys → ignore-unmatched; (2) the host monitor never presented its `X-Service-Token` → 401; (3) `IOptions<RuntimeLimits>` resolved defaults instead of the loaded system.yaml values (limits were only ever right via the profile rows) → direct injection; (4) `agent_started` arriving mid-`/start` was a no-op on the `pending` Run and `/start` downgraded it back to `starting` (observed live) → the transition now covers `pending`/`starting` and pins `StartedAt`, and both `/start` and the dispatcher never downgrade a `running` Run; (5) the ticket-09 config validator whitelist for stub `bridge.behavior` didn't know the ticket-13 levers.

## Manual test instructions (a live pass of every scenario was executed on 2026-11-22 — see the evidence table above; the steps below reproduce it)

Prerequisites: `src/.env` with `POSTGRES_*`, `SERVICE_TOKEN_*`, `KOMODO_*`; Komodo stack running (`task agent-komodo`); `task stack-rebuild && task stack`.

**Test limits (temporary, reverted afterwards):** edit `src/config/system.yaml` → `startup_timeout: 90s`, `idle_timeout: 120s`, `max_run_duration: 300s`, `orphan_grace_period: 60s`, `orphan_quarantine_period: 60s`, then `task stack-rebuild && task stack`.

1. **startup_timeout** — set the stub profile's `bridge.behavior: never_start` in `src/config/agents/stub/profile.yaml` (rebuild), run `@bot /plan` on an Issue, wait ~90 s + 2 worker ticks: Run fails `startup_timeout`, Task `failed`, Workflow `waiting_for_human`, and the Outbox publishes the failure comment. `docker compose --env-file src/.env -f src/compose.yaml logs runtime-api | grep reconciled` shows the line.
2. **idle_timeout** — `behavior: hang`, run `@bot /plan`: the run stays alive with heartbeats; after `idle_timeout` the Host-Monitor telemetry confirms no active processes → Run `failed` `idle_timeout` + notification. Busy-run protection is covered by the automated test (a live variant needs a stub that spawns a child process).
3. **max_run_duration** — `behavior: hang`, wait past `max_run_duration`: Run `timed_out`; `/retry` is the only recovery (no automatic retry).
4. **permission_blocked** — `behavior: permission_denied`: the run records a `permission_blocked` RunEvent and fails `required_permission_denied`; Task `failed`, Workflow `waiting_for_human`, notification published. (The denial is answered by the stub's permission layer — the real-Pi RPC denial path is unexercised in V1.)
5. **Container death without terminal callback** — start `/plan`, then `docker kill <pi-session-… container>`: the reconciler marks the Run `failed` (`runtime_failure`, "reconciled: …") and the notification fires.
6. **Orphans** — `mkdir src/storage/<random-uuid>` while the stack runs: after grace the Runtime API renames it `.orphan-quarantine`, after quarantine it is deleted. `docker compose --env-file src/.env -f src/compose.yaml logs runtime-api | grep orphan`.
7. **Komodo outage** — stop the komodo-core container while a run is active: desired state preserved, nothing corrupted; recovery resumes when it returns.
8. Check `GET http://localhost:8080/metrics` (runtime-api) and the host-monitor `/metrics` for the new gauges.

Restore `src/config/system.yaml` (and the stub behavior) to the defaults afterwards and rebuild.
