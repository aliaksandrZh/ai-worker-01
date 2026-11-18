# Pi RPC API — short design (2026-11-18)

## Intent
Operate pi coding agents from outside docker over HTTP: one **container per session** (user decision), started and operated entirely through Komodo; start/stop/switch sessions, see tool calls, answer pi's requests (execute them on the host), and log everything pi outputs.

## Approaches
- **Chosen (user constraint: "we send the request to Komodo — it spawns the container — we work with it"): one Komodo Deployment per session.** `POST /sessions` → `komodo.write("CreateDeployment", { name: "pi-session-<id>", config })` → `StartDeployment` → Komodo spawns the container → API talks to pi in it over a Komodo `attach`-mode terminal websocket. Every docker operation is a Komodo RPC; the API never touches docker itself.
- Rejected: shared long-lived container with per-session exec terminals (previous draft) — user wants container-per-session isolation; `docker exec -i` children or dockerode — bypasses Komodo as the docker operator.

## Design
### Per-session container
- `CreateDeployment { name: "pi-session-<id>", config: { server: "Local", image: { type: "Image", params: { image: "pi-worker:latest" } }, extra_args: "--interactive --init" (keeps stdin open for attach; init for signal handling), restart: no, auto_update: false, labels: { "pi-session": "<id>" }, volumes:
  - shared `pi/pi-agent-state` → `/home/pi/.pi/agent` (credentials, models, extensions — incl. host-bridge)
  - shared `pi/pi-worker-workspace` → `/workspace` — user decision: same workspace for all sessions for now; later the workspace mount disappears entirely (repos cloned inside the container) } }`.
- Deployment names are DNS-safe; the name doubles as the session id and terminal name. Deployments created here live outside `pi.sync.toml` (kept as the base image build); sessions persist as real resources — visible/recoverable in the Komodo UI after API restarts.
- `StopDeployment` → `DeleteDeployment` cleans a container up; a crashed pi shows as `stopped` in `ListDeployments` and the session can be reconnected.

### Protocol client (in server.mjs, no new deps)
- Modeled on pi's exported `RpcClient`/`rpc-types.ts` (`github.com/earendil-works/pi …/rpc/rpc-client.ts`, `rpc-types.ts`) — but owning a global `WebSocket` (Node 22+) directly instead of spawning a child: `RpcClient` only supports subprocess transports (`spawn("node", [cliPath, ...])` of the pi CLI) and cannot answer `extension_ui_request` dialogs, which moment 2 needs.
- Responsibilities: ws login (`ApiKeys` → `LOGGED_IN`), JSONL framing (LF split, optional CR strip, non-JSON lines dropped — PTY/echo tolerance), request/response correlation by `id`, event buffer for the events endpoint, `extension_ui_request` outbox for the UI endpoints + `host-bridge` auto-answer, raw capture for logging.
- Connection: `connect_terminal({ target: {type:"Container", params:{server:"Local", container:"pi-session-<id>"}}, terminal: "pi-session-<id>", init: { mode: "attach", recreate: "Never" } })` — the session container's main process *is* `pi --mode rpc` (image ENTRYPOINT/CMD); attach mode = raw stdin/stdout pipe to it. Readiness handshake: send `get_state`, resolve on its response.
- API restart does not kill sessions: connect again by the same terminal name (`POST /sessions/:id/connect`); container and pi keep running (`restart: no` only applies to pi exiting, not to clients detaching). Fallback if periphery attach needs a TTY: exec-mode terminal with init command `pi --mode rpc` (parser tolerates the PTY noise).

### Endpoints (extends api/server.mjs, raw node http — no new deps)
- `POST /sessions` — `CreateDeployment` + `StartDeployment`, wait until running, open terminal, wait for the `get_state` handshake → `{ id, sessionId, sessionFile, ... }` (body: optional friendly `name`; one active session expected today — the design still builds per-session deployments so multi-container operation stays verified)
- `GET /sessions` — `ListDeployments` filtered on names/labels of sessions (running/stopped) + on-disk `pi/pi-agent-state/sessions/**/*.jsonl`
- `POST /sessions/:id/connect` — (re)open the terminal websocket to a live session
- `GET /sessions/:id` — `get_state` passthrough
- `DELETE /sessions/:id` — detach ws → `StopDeployment` → `DeleteDeployment`
- `POST /sessions/:id/rpc` — generic command passthrough `{type, ...}` (prompt/steer/bash/switch_session/new_session/abort/set_model/compact/…), correlated by `id`
- `GET /sessions/:id/events?since=<n>&type=<csv>` — buffered stdout records (tool calls = `tool_execution_*` events)
- `GET /sessions/:id/ui` / `POST /sessions/:id/ui/:requestId` — pending `extension_ui_request` dialogs, answered by value/confirmed/cancelled

### Moment 2: host-side command execution via the extension UI protocol
- `pi/pi-agent-state/extensions/host-bridge.ts` (auto-loaded via the shared bind mount — no image rebuild; every session container gets it) registers a `host_shell` tool: `execute()` wraps the params in a `ctx.ui.editor("host-bridge", <json envelope>)` dialog, which pi forwards as an `extension_ui_request` and blocks on. A `/host-check` extension command performs the same round trip deterministically (no LLM needed — smoke-testable).
- The API auto-answers `editor` requests titled `host-bridge`: runs the command on the host (`child_process.exec`, cwd/timeout from env), replies `extension_ui_response` with the JSON result `{exitCode, stdout, stderr}`. Control returns to pi's tool loop with the result.
- Other dialog methods park in the pending list for human answers via the `/ui` endpoints (require-then-approve flow).

### Moment 3: logging
- Every stdout/stderr byte of every session captured to `api/logs/<session-id>.jsonl` (raw). Structured records additionally buffered in memory for the events endpoint. pi's own session files already persist under `pi/pi-agent-state/sessions/` (shared through the state mount); `export_html` available for rendering.

### Verification
- `api/rpc-smoke.mjs` drives the real endpoints: start session → prompt → wait `agent_settled` → assert tool execution events → assert host-bridge round trip (`/host-check` command → editor dialog answered by the API with a host command result) → stop session, then checks the log file exists and the Komodo deployment is gone.

### Non-goals
- No auth on the API (localhost tool, as before), no multi-user UI, no exec-attach toggle, no pi image/code changes beyond the one extension file.

## Risks / verify-at-build
- Periphery attach behavior without a TTY (echo/ANSI) — parser tolerates it; exec-mode terminal fallback.
- Whether the api key needs the `write`/`execute` permission scope for `CreateDeployment` — Komodo keys are permission-scoped; the key may need upgrading (one-time UI step).
- Cleanup: sessions created outside the sync are not removed by sync execution; abandoned session deployments must be stopped via `DELETE /sessions/:id` (or the Komodo UI).
