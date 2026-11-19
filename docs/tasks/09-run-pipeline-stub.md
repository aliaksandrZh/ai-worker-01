# 09: Run execution pipeline with a stub agent

**What to build:** The Runtime API + Runtime Bridge pipeline proven end to end against a **stub agent container** (a fake headless CLI speaking a tiny JSON protocol — same shape as pi RPC: one prompt input, a few stdout event types, settled). Runtime API: `POST /internal/runtime/runs/{runId}/start` (the only creation endpoint — Orchestrator owns DB rows, API provisions physical resources), stop, status, and the callback routes (heartbeat / events / completed / failed) with a fresh per-Run bearer credential delivered as a read-only secret file, stored hashed, scoped to that runId, revoked after terminal delivery. Bridge: launches the agent, reads the stdout JSONL event stream, applies the pi→RunEvent mapping, spools callbacks durably (`runs/{run_id}/callback-spool/`) before sending, retries network/5xx/429 with the same eventId, coalesces heartbeats, never drops lifecycle/terminal callbacks. Run result: manifest `result.json` (schemaVersion, summary, artifacts[] with paths, requiredPublications[] with toolCallId/kind/externalId) validated in-place; `branch`/`commitSha` derived via Git; events → Artifact rows within the 256 KiB limit. Komodo provisions the container and mounts the per-Run session/log/spool directories from the `/var/lib/pi-agent/workflows/` tree.

**Spec:** `docs/pi-agent-architecture/04-api-contracts.md` §3–§4 (endpoints, Bridge contract); `03-state-machines.md` §3–§4 (Run/AgentSession machines, one-shot 1:1 sessions); `05-v1-decisions.md` decisions 33 (no Pi extension — the Bridge is a supervisor), 44, 57 (session dirs), 58 (event mapping), 59 (result manifest), 60 (credential + spool); `01-architecture-and-flows.md` §10 (lifecycle), §2 (Runtime API).

**Blocked by:** 01, 07

**Status:** ready-for-agent

- [ ] Start endpoint creates Run + one-shot AgentSession (1:1, `run_id` unique) + container + per-Run directory tree
- [ ] Stub completes → `completed` callback persisted idempotently (duplicate eventIds ignored); Run terminal state set; container stopped, data retained
- [ ] Stub failure → `failed` callback → Run Failed with the reported error code
- [ ] Manifest artifacts become Artifact rows; oversize artifact rejected without silent truncation
- [ ] Per-Run callbacks rejected for other runIds (credential scoping); token revoked after terminal delivery
- [ ] Runtime API restart mid-run → reconciler recovers desired/observed state from PostgreSQL; Bridge spool redelivered
- [ ] `outcome = needs_human_input` from the stub → Run Succeeded, Task terminal `Blocked`, Workflow `waiting_for_human`
