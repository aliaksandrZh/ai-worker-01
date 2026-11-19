# 16: Restart recovery & callback outage

**What to build:** Survivability end to end: Bridge restart → undelivered callbacks redelivered from the durable spool (same eventIds, terminal callbacks never dropped); Runtime API restart mid-run → reconciler restores desired/observed state from PostgreSQL, reattaches without duplicate execution; Orchestrator restart → claimed jobs return to `pending` on lease expiry without double execution; PostgreSQL restart services recover connections; the Workflow-scoped Run serialization holds across restarts (a Workflow's next Run never starts before the previous Run's terminal callback is persisted); different Workflows still run concurrently under the cap; Poller restarts rely on cursors + the overlap window without losing or duplicating commands.

**Spec:** `docs/pi-agent-architecture/01-architecture-and-flows.md` §12 (reconciliation, recoverability); `03-state-machines.md` §8 (lease expiry), §9; `04-api-contracts.md` §4 (sender resilience), §8 (idempotency); `05-v1-decisions.md` decisions 22, 60, 62.

**Blocked by:** 12, 13

**Status:** ready-for-agent

- [ ] Terminating the Runtime API mid-run → callbacks spooled and redelivered; Run reaches the correct terminal state exactly once
- [ ] Terminating the Orchestrator with a claimed job → lease expiry returns it to pending; no duplicate execution
- [ ] Next Run of the same Workflow does not start before the previous terminal callback is persisted; Runs of different Workflows continue concurrently
- [ ] PostgreSQL restart → every service reconnects and resumes (health endpoints report recovery)
- [ ] Poller restart loses nothing: dedupe + overlap cover the gap; commands processed exactly once
- [ ] Acceptance scenarios "Restart recovery" and "Callbacks" (`05` matrix) pass
