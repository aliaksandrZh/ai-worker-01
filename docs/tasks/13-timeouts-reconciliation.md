# 13: Host Monitor + timeouts + reconciliation

**What to build:** The Execution Host Monitor process (session-file activity, process trees/cgroups, container state, managed-storage usage via the local Docker API → telemetry to the Runtime API with its own credential; orphan-candidate detection in `/var/lib/pi-agent/workflows/` — candidate when the DB record is gone or storage already marked deleted, ≥24 h grace, 24 h quarantine before physical deletion, nothing deleted while PostgreSQL is down or containers use the tree). Timeouts: `startup_timeout` (10 min) fails a Run that never became ready; `idle_timeout` (60 min, all observable activity absent while alive) makes the Run a reconciliation candidate — the Host Monitor checks child processes/CPU/IO before any stop (a long-running build/test is not killed for silence), a confirmed hang fails the Run with `error_code = idle_timeout`; `max_run_duration` (4 h) ends the Run `TimedOut` independently, no automatic retry for it. `permission_blocked` handling: fail-closed — the Bridge records the event, returns an explicit denial, never parks a dialog; impossible mandatory work → Run fails `required_permission_denied`, Task Failed, Workflow `waiting_for_human`, Outbox notification. Reconciler compares PostgreSQL desired vs Komodo observed state and recovers stale runs.

**Spec:** `docs/pi-agent-architecture/01-architecture-and-flows.md` §2 (Host Monitor), §12 (signals, timelines); `03-state-machines.md` §5, §9; `05-v1-decisions.md` decisions 22, 23, 30, 34, 62, 68, 69, 77 (orphan reconciliation); `04-api-contracts.md` §4 (monitor telemetry route).

**Blocked by:** 09

**Status:** ready-for-agent

- [ ] A stub that never becomes ready → Run fails `startup_timeout` after 10 min
- [ ] A silent-but-alive stub → idle candidate → reconciled (processes checked) → confirmed hang → Failed `idle_timeout`; a busy child process prevents the kill
- [ ] A wall-clock stub exceeding 4 h → Run `TimedOut`; `/retry` is the only recovery (no automatic retry)
- [ ] Effective limits honor the pinned profile-version execution-limit defaults (`AgentProfileVersion.startup_timeout`/`max_run_duration`, optionally `idle_timeout`) — a profile may lower them but never bypass the system constraints (`01 §16`, `02 §12`, decisions 30, 54; pinned by ticket 02)
- [ ] Denied tool operation → `permission_blocked` RunEvent + explicit denial; impossible mandatory operation → Run Failed `required_permission_denied` + notification
- [ ] Host-Monitor telemetry stored in PostgreSQL with its own credential; the control plane never reads execution-host paths or process tables directly
- [ ] Orphan candidates detected with the grace/quarantine timing; nothing deleted while the DB is unavailable
- [ ] A container dies without emitting a terminal callback → reconciliation marks the Run Failed with a clear error and the notification path fires (never silent recovery — `01 §12`)
- [ ] Komodo/Periphery outage: desired state is preserved, reconciler retries without corrupting state; recovery resumes cleanly when reachable (decision 22)
- [ ] Acceptance scenario "Timeouts" (`05` matrix) passes
