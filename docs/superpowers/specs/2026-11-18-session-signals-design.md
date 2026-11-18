# Session signals (done / blocked / disconnected) — short design

**Intent.** The orchestrator (today: a human at the console; later: the web service)
must learn — without interpreting raw event records — when a pi run: **settles**
(work done or aborted), **blocks** (waiting for a human dialog), or goes
**disconnected** (container/terminal lost). Today `POST /rpc` prompt answers
`disposition:"started"` and the only follow-up is hand-polling `/events` + `/ui`.

**Constraints.**
- No new pi extension: every signal already arrives as stdout records the API parses.
- `agent_settled` is the settle signal (carries `aborted`; it excludes retries/steer/follow-ups
  — pi docs). `extension_ui_request` parked in `pendingUi` is the blocker signal; host-bridge
  envelopes are auto-answered and never block. Terminal close = disconnected.
- One source of truth: a small state machine in `PiRpcSession`; A/B/C all read it.

**Approaches (all three were requested and approved).**
- **A — pull:** `GET /sessions/:id/status` → derived `{phase: blocked|running|idle, connected, hasRun, lastSettled, pendingUi}`.
- **B — long-poll:** `GET /sessions/:id/wait?timeoutMs=` → resolves at once if already blocked / settled-after-run / !connected; during the wait it resolves on `blocked`, `settled`, `disconnected`, else `timeout`.
- **C — push:** `POST /sessions/:id/notify {url}` registers one webhook per session; every
  signal POSTs `{id, event, …}` fire-and-forget (no retries, lost on API restart); `DELETE /sessions/:id/notify` unregisters; failures only console.warn'ed.

**Shared semantics.**
- `agent_start` clears the "run in progress" marker; `agent_settled` records
  `lastSettled = {ts, aborted}` and ends it. Fresh session = idle, `hasRun:false`.
- `/wait` insta-resolves `settled` only *after* a run has happened (`hasRun`); a
  `disposition:"queued"/"handled"` prompt starts no run — the consumer checks dispositions
  before waiting (documented).
- A blocker that appears and is answered before a consumer looks can be missed (no
  event-replay); acceptable for v1, documented as a shortcut.
- Webhook payloads: `{id, event:"settled", aborted}` / `{id, event:"blocked", pending}` /
  `{id, event:"disconnected"}`.

**Files.** `api/rpc-session.mjs` (state machine, wait, signal emitter), `api/server.mjs`
(routes + webhook POST), `api/webhook-listener.mjs` (manual console listener for C),
`api/rpc-session.test.mjs` (state-machine tests), `docs/pi-session-flow.md` (endpoints doc).

**Explicitly skipped** (not asked): webhook retry/queueing, unblock notifications,
subscription persistence, multi-webhook.
