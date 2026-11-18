# Session Signals Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans (no subagent tool exists in this harness).

**Goal:** One shared signal state machine in `PiRpcSession`, exposed three ways: `/status` (pull), `/wait` (long-poll), `/notify` (webhook push).

**Architecture:** `PiRpcSession` tracks run/blocker/disconnect transitions in `_onRecord`/`_onClose`, resolves waiters and emits signals at exactly those transitions; `server.mjs` exposes routes and POSTs webhook payloads fire-and-forget.

**Tech Stack:** Node ESM, node:test (existing pattern), no new dependencies.

**Spec:** `docs/superpowers/specs/2026-11-18-session-signals-design.md`

## Global Constraints
- No new pi extension; no new npm dependencies.
- host-bridge `editor` dialogs (title `"host-bridge"`) never count as blockers or signals.
- Webhooks fire-and-forget; failure = console.warn only.

## Review Focus
- Steer/follow-up mid-run: a second `agent_start` after `agent_settled` must flip phase back to `running` — pinned by `settled then new run clears settled` test (Task 1).
- Multiple concurrent `/wait` callers all resolve — pinned (Task 2).
- `/wait` on a fresh session (never run) must NOT insta-resolve `settled` — pinned (Task 2).
- A blocker answered before anyone looks is missed (`timeout` later) — accepted shortcut, documented in the flow guide (Task 4).

---

### Task 1: Run-state tracking + `status()` in `PiRpcSession`

**Files:**
- Modify: `api/rpc-session.mjs`
- Test: `api/rpc-session.test.mjs`

**Interfaces:**
- Produces: `status()` → `{connected, phase:"blocked"|"running"|"idle", hasRun:boolean, lastSettled:{ts,aborted}|null, pendingUi:[]}`; internal record handling in `_onRecord` unchanged.

- [ ] **Step 1: Failing tests** (`rpc-session.test.mjs`):
  - `status derives idle for a fresh session` — `phase:"idle"`, `hasRun:false`, `lastSettled:null`.
  - `agent_start then agent_settled records the settle and clears the run` — feed `{type:"agent_start"}` then `{type:"agent_settled",aborted:false}` via `_onRecord`; assert `phase:"idle"`, `hasRun:true`, `lastSettled.aborted===false`.
  - `settled then a new run goes back to running` — `agent_settled`, then `agent_start` → `phase:"running"`, `lastSettled` kept.
  - `aborted settle carries aborted` — `agent_settled` with `aborted:true` → `lastSettled.aborted===true`.
  - `pending dialogs make the phase blocked (highest priority)` — emit parked `extension_ui_request` → `phase:"blocked"`.
- [ ] **Step 2: Run — expect the new tests FAIL** (`node --test api/rpc-session.test.mjs`, grep the new names before implementing).
- [ ] **Step 3: Implement** — fields `runInProgress=false`, `hasRun=false`, `lastSettled=null` in constructor; in `_onRecord`'s known-record path: `agent_start` → `runInProgress=true, hasRun=true`; `agent_settled` → `runInProgress=false, lastSettled={ts:Date.now(), aborted: rec.aborted===true}`; `status()` method deriving phase `blocked` (pendingUi non-empty) > `running` (runInProgress) > `idle`.
- [ ] **Step 4: Run — full existing suite PASSES.**
- [ ] **Step 5: Commit `feat: session run-state tracking + status()`.**

### Task 2: `/wait` waiter registry in `PiRpcSession`

**Files:**
- Modify: `api/rpc-session.mjs`
- Test: `api/rpc-session.test.mjs`

**Interfaces:**
- Produces: `wait({timeoutMs}?)` → Promise of `{result:"settled",aborted}` | `{result:"blocked",pending}` | `{result:"disconnected"}` | `{result:"timeout"}`.
- Consumes: Task 1 fields; `_onClose` (existing).

- [ ] **Step 1: Failing tests:**
  - `wait resolves settled when the run settles` — start `wait()` (timeoutMs 1000), `_onRecord({type:"agent_settled",aborted:false})` → awaits `{result:"settled",aborted:false}`.
  - `wait resolves blocked instantly when a dialog is parked` — same with parked `extension_ui_request` → `{result:"blocked",pending:[…]}`, and pendingUi still parked.
  - `wait resolves disconnected on terminal close` — `_onClose()` → `{result:"disconnected"}`, and not connected.
  - `two waiters both resolve` — two concurrent `wait()` calls then `agent_settled` → both settled.
  - `wait times out when nothing happens` — `timeoutMs:50`, no records → `{result:"timeout"}` after ≥50ms.
  - `wait on a never-run session does not insta-settle` — fresh session, `timeoutMs:80` after microtask → not resolved settled.
  - `wait insta-resolves settled only after a run` — run started+settled, then new `wait()` (no timeoutMs needed; timeoutMs:200) → `{result:"settled"}` immediately.
  - `already blocked blocks insta-resolve too` — parked dialog then `wait()` → settled? No: → blocked instantly.
- [ ] **Step 2: Run — new tests FAIL.**
- [ ] **Step 3: Implement** — `this.waiters = new Set()`; `wait({timeoutMs=600000})`: if `!this._connected` resolve `{result:"disconnected"}`; else if `pendingUi.size` resolve `{result:"blocked",pending:this.listPendingUi()}`; else if `hasRun && !runInProgress` resolve `{result:"settled",aborted:this.lastSettled?.aborted ?? false}`; else add resolver, single `setTimeout` → resolve `{result:"timeout"}` and remove. `_onRecord`: `agent_settled` → resolve all with settled + clear waiters; parked dialog → resolve all with blocked. `_onClose` → resolve all with disconnected before/alongside existing rejection loop and clear waiters (waiters must always clear so a Set can't leak).
- [ ] **Step 4: Run — suite PASSES.**
- [ ] **Step 5: Commit `feat: /wait waiter registry`.**

### Task 3: Signal emitter (`onSignal`)

**Files:**
- Modify: `api/rpc-session.mjs`
- Test: `api/rpc-session.test.mjs`

**Interfaces:**
- Produces: constructor option `onSignal(sig)`; `sig` ∈ `{event:"settled",aborted}` | `{event:"blocked",pending}` | `{event:"disconnected"}`; fired at the same transitions as waiter resolution (never for host-bridge dialogs, never twice for one disconnect).

- [ ] **Step 1: Failing tests:**
  - `signals fire for settled, blocked and disconnected` — spy array as `onSignal`; sequence: parked dialog → blocked; `agent_settled {aborted:true}` → settled; `_onClose` → disconnected (exactly one, `connect()` was never called so `_connected` false — assert still fired, since container death matters even without attach? No: without attach there is no session to watch. Assert: after `s._connected=true; s._onClose()` → one disconnected signal).
  - `host-bridge dialog fires no signal` — editor/title host-bridge recording → no blocked signal.
- [ ] **Step 2: Run — FAIL.**
- [ ] **Step 3: Implement** — `this.onSignal = onSignal ?? null`; a private `_emitSignal(sig)` that only runs when callbacks exist; call it at the Task 2 transition points alongside waiter resolution (settled / parked-dialog / close).
- [ ] **Step 4: Run — suite PASSES.**
- [ ] **Step 5: Commit `feat: session signal emitter`.**

### Task 4: API routes + webhook + listener + docs

**Files:**
- Modify: `api/server.mjs`
- Create: `api/webhook-listener.mjs`
- Modify: `docs/pi-session-flow.md`

**Interfaces:**
- Consumes: `session.status()`, `session.wait()`, `onSignal` (Tasks 1–3).
- Produces: `GET /sessions/:id/status`; `GET /sessions/:id/wait?timeoutMs=`; `POST /sessions/:id/notify {url}`; `DELETE /sessions/:id/notify`.

- [ ] **Step 1: Implement** — in `createSession`/`connect` construction pass `onSignal: (sig) => notifySession(id, sig)`; `const subscriptions = new Map()` (id → url); `notifySession` POSTs `JSON.stringify({id, ...sig})` with `fetch(...).catch((e) => console.warn(...))`; routes: status (404 on unknown, 509? no — same session-missing contract as existing routes: `POST /sessions` hint); wait passes `Number(url.searchParams.get("timeoutMs") ?? 0) || undefined`; notify POST validates url truthy `http(s)://`, DELETE tolerates absence (200 always). Include both new routes in the 404 help line.
- [ ] **Step 2: `webhook-listener.mjs`** — ~12 lines: `createServer` logging method+url+parsed body to console, `process.env.PORT ?? 3900`, no deps.
- [ ] **Step 3: Verify** — `node --check api/server.mjs api/webhook-listener.mjs`; full test suite passes; route contract exercised manually when komodo is up (curl walkthrough in docs step): prompt → `/wait` loop → `settled`; parked dialog → `/wait` → `blocked`; `nc`-style listener shows webhook line. If komodo is down, state that in the final report instead of faking it.
- [ ] **Step 4: Update `docs/pi-session-flow.md`** — new "Signals" section between steps 3 and 4: the three routes, curl examples mirrored from routes, disposition caveat, missed-dialog shortcut, webhook lost-on-API-restart note.
- [ ] **Step 5: Commit `feat: /status /wait /notify signal routes (+ listener, docs)`.**
