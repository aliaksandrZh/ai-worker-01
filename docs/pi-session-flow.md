# Drive a pi coding agent through the pi-worker API — flow guide

> This guide shows how to run a pi coding-agent session from outside docker, with every
> docker operation executed by Komodo. You get one container per session, bidirectional
> pi RPC over HTTP, host-side command execution through pi's extension-UI protocol,
> observability of every tool call, and clean shutdown.

## The moving parts

```
your HTTP client ──> api/server.mjs (:3000) ──> Komodo Core (:9120) ──> Periphery ──> docker
                         │                         │
                         └── terminal websocket ────┘  (raw bytes = pi's RPC stdin/stdout)
                                                    │
                                      pi-session-<id> container:
                                        main process = pi --mode rpc
                                        /home/pi/.pi/agent ← pi/pi-agent-state   (shared)
                                        /workspace          ← pi/pi-worker-workspace (shared)
```

- `api/server.mjs` — the translator: HTTP → Komodo RPC (`komodo_client`) for lifecycle,
  Komodo terminal websocket for the pi data plane.
- `pi-session-<id>` — one Komodo Deployment per session. The container's main process is
  pi itself in RPC mode (`Dockerfile.pi-worker`: `ENTRYPOINT ["pi"]`, `CMD ["--mode","rpc"]`,
  `--interactive --init` extra args keep its stdin open for attach).
- `pi/pi-agent-state/extensions/host-bridge.ts` — pi extension auto-loaded inside every
  session container; exposes `host_shell` (runs commands on the host) and `/host-check`.
- pi's model comes from `pi/pi-agent-state/models.json` — Ollama on the host via
  `http://host.docker.internal:11434/v1`.

## Before you start

- Komodo Core running with an API key in `api/.env` (`KOMODO_URL`, `KOMODO_API_KEY`,
  `KOMODO_API_SECRET`) — the key must have read+write+execute scope; `CreateDeployment`
  is a write operation.
- Local image `pi-worker:latest` present on the daemon (Komodo Build `pi-worker`).
- Start the API: `task api` (runs `node --env-file-if-exists=.env server.mjs`, port 3000).

## Steps

### 1. Start a session: `POST /sessions` — MANDATORY

```bash
curl -s -XPOST localhost:3000/sessions -d '{"name":"todo-app"}'
```

Expected: `{"id":"smwmfmffwed3c","sessionId":"01a1efd6-…","sessionFile":"/home/pi/.pi/agent/sessions/--workspace--/…jsonl","name":"todo-app"}`
(the 201 also contains `name` when a body name was given).

Internally, in order:

1. The API generates the session id `s<epoch base36><4 hex>` and the deployment name
   `pi-session-<id>` — the deployment name doubles as the session id used in every other
   route.
2. `komodo.write("CreateDeployment", { name, config })` with:
   - `server_id: "Local"`, `image: pi-worker:latest`
   - `extra_args: ["--interactive","--init"]` — keeps the main process's stdin open and
     enables signal handling
   - `restart: "no"`, `auto_update: false`, `labels: "pi-session=<id>"`
   - `volumes`: `pi/pi-agent-state:/home/pi/.pi/agent` and
     `pi/pi-worker-workspace:/workspace` (both shared between sessions)
3. `komodo.execute_and_poll("Deploy", { deployment })` — **Deploy, not StartDeployment**:
   Deploy creates and starts the container; StartDeployment only does
   `docker start` and fails with "No such container" on a freshly created deployment.
   Failed deploys surface the periphery log lines in the 502 error body.
4. Poll `GetDeploymentContainer` every second up to 90 s until `state === "running"`
   (a container that reports `exited`/`dead` fails the request immediately).
5. Attach: `komodo.connect_terminal` opens `/ws/terminal?target[type]=Container…&init[mode]=attach`
   with `terminal = pi-session-<id>` and sends the api-key login JSON. Terminal mode
   attach means the terminal is a raw pipe to the container's *main* process — which is
   pi in RPC mode. Periphery runs it under a PTY, so periphery starts forwarding stdin
   only after the client sends a single `0x00` (Begin) byte, and each following input
   frame must be `payload + 0x01` (Forward tag); output arrives as raw PTY bytes
   (echoes, CRLF — the API parser drops what isn't a protocol record).
6. Handshake: the API sends `{"id":"api-1","type":"get_state"}` and the POST resolves
   only when pi's response record arrives (20 s timeout). That response's `data`
   carries `sessionId` and `sessionFile` — pi writes its session there, on disk, in the
   shared mount, from the first message on.

### 2. Drive pi: `POST /sessions/:id/rpc` — MANDATORY

The body is any pi RPC command without a client id — the API assigns ids and correlates
responses:

```bash
curl -s -XPOST localhost:3000/sessions/smwmfmffwed3c/rpc \
  -d '{"type":"prompt","message":"Create a simple todo app (vanilla HTML/CSS/JS): index.html, styles.css, app.js with localStorage persistence."}'
# → {"id":"api-2","type":"response","command":"prompt","success":true,"data":{"disposition":"started"}}
```

- `disposition: "started"` means accepted, not finished. Poll events until
  `agent_settled` (step 3) or call `get_last_assistant_text` afterward.
- `"queued"`/`"handled"` dispositions don't start a run — a run must already be streaming,
  or the prompt was an extension command (e.g. `/host-check`), which executes
  immediately.
- Useful commands: `prompt`, `steer`, `follow_up`, `bash` (runs inside the container and
  answers with `{output, exitCode, …}`), `get_state`, `get_messages`,
  `get_last_assistant_text`, `switch_session`/`new_session`, `set_model`, `compact`,
  `abort`.
- Each `/rpc` call waits for pi's matching response record (120 s default → HTTP 502
  on timeout). A `success:false` response is returned as 200 with pi's own
  `success:false` payload.

For the demo prompt above pi answered: 4 tool calls (`write`, `write`, `write`, `bash`
with a `node --check` self-verification), then `agent_settled`, and the three files
materialized on the host under `pi/pi-worker-workspace/` through the shared mount.

### 3. Observe: `GET /sessions/:id/events?since=<n>&type=<csv>` — MANDATORY to track runs

```bash
curl -s "localhost:3000/sessions/smwmfmffwed3c/events?since=0&type=agent_settled,tool_execution_start"
# → {"records":[{"seq":42,"ts":…,"record":{"type":"tool_execution_start","toolName":"write",…}},…],
#     "nextSeq":57}
```

- Records are pi's stdout protocol records, buffered in memory (newest 2000 kept) and
  ordered by a monotonic `seq`. Pass `nextSeq` back as `since` to fetch only new ones. Pass `nextSeq` back as `since` to fetch only new ones.
- `type` filters by record type — the tool-call trail is
  `tool_execution_start`/`tool_execution_update`/`tool_execution_end` (they carry
  `toolCallId`, `toolName`, `args`, `result`); run boundaries are `agent_start`,
  `agent_end`, `agent_settled`; streaming text lives in `message_update` deltas.
- `GET /sessions` lists session deployments from Komodo (`state` = running/stopped/not_deployed,
  `connected` = API currently attached) plus pi's on-disk session files, so sessions
  survive API restarts. `POST /sessions/:id/connect` re-attaches the terminal to a
  running one. `GET /sessions/:id` is a `get_state` passthrough (model, streaming flag,
  messageCount, sessionFile…).

### 3b. Signals: know when a run is done or blocked without parsing records

After `POST /rpc` prompt (`disposition: "started"`) the run's end comes as `agent_settled` and a
human blocker comes as a parked `extension_ui_request`. Three routes wrap those signals; all three
drive the same state machine inside `PiRpcSession` (`api/rpc-session.mjs`):

1. **Pull — `GET /sessions/:id/status`** — derive everything from one call:

   ```bash
   curl -s localhost:3000/sessions/smwmj60v16ff2/status
   # → {"connected":true,"phase":"idle","hasRun":true,"lastSettled":{"ts":1795031107917,"aborted":false},"pendingUi":[]}
   ```

   - `phase` — `blocked` (pending dialogs, highest priority) > `running` (`agent_start` seen, not
     yet settled) > `idle`. A fresh session is `idle` with `hasRun:false` / `lastSettled:null`.
   - `lastSettled.aborted` — `true` when the run was aborted rather than finished.

2. **Await — `GET /sessions/:id/wait?timeoutMs=<ms>`** — one HTTP call that returns when the
   session's state changes, the whole point for "how long do I have to wait?":

   ```bash
   curl -s "localhost:3000/sessions/smwmj60v16ff2/wait?timeoutMs=55000"
   # → {"result":"settled","aborted":false}   work finished
   #   {"result":"blocked","pending":[...]}    a dialog wants a human — answer it via POST /sessions/:id/ui/<requestId>
   #   {"result":"disconnected"}               container/terminal lost — kill or recreate the session
   #   {"result":"timeout"}                    still working after timeoutMs — just GET /wait again
   ```

   Loop it until `settled`/`blocked`/`disconnected`:

   ```bash
   while :; do R=$(curl -s "localhost:3000/sessions/$S/wait?timeoutMs=55000"); echo "$R"; case "$R" in *'"timeout"') ;; *) break;; esac; done
   ```

   Insta-resolutions (before any waiting): already `blocked`, already `disconnected`, or idle right
   after a settled run (`hasRun && !running`). A never-run session waits out the whole `timeoutMs`
   (it can only become settled once a run starts).

3. **Push — `POST /sessions/:id/notify` {"url":"http://you:port/hook"}** — every signal is also
   POSTed to your url as `{"id":"<session>", ...signal}` (settled: `+aborted`; blocked: `+pending`;
   `disconnected`). `DELETE /sessions/:id/notify` unregisters. For testing by hand run
   `node api/webhook-listener.mjs` (console-logging receiver, port 3900).

Caveats:

- Check `disposition` first: `"queued"`/`"handled"` prompts start no run, so `/wait` would resolve
  from the foreign run instead of yours. Only wait after `"started"`.
- A blocker that is answered before any consumer looks at `/wait` or `/status` is never observed
  (no event replay); register `/notify` before prompting when push timing matters.
- The webhook is fire-and-forget: delivery failures only `console.warn` in the api, and
  subscriptions live in memory — lost when the api restarts.
- host-bridge dialogs (`editor`/title `host-bridge`) are auto-answered and never count as blockers.

### 4. Commands that must run on the host — the host-bridge — OPTIONAL

pi in the container can't reach host-only resources; the `host-bridge` extension closes
that gap without a second transport:

1. The model calls the `host_shell` tool (or a prompt `/host-check` runs a deterministic
   check).
2. The extension wraps the command as a dialog request:
   `ctx.ui.editor("host-bridge", JSON.stringify({ command }))` — pi emits
   `{"type":"extension_ui_request","method":"editor","title":"host-bridge","prefill":…}`
   on the wire and **blocks** the tool until it is answered.
3. The API intercepts that request before it is parked for humans, runs
   `command` on the host (`child_process.exec`, cwd = `PI_HOST_BRIDGE_CWD` || the dir the
   server was started in, timeout = `PI_HOST_BRIDGE_TIMEOUT_MS` || 60000 ms) and answers
   `{"type":"extension_ui_response","id":…,"value":"{\"exitCode\":0,\"stdout\":\"…\",\"stderr\":\"…\"}"}`.
4. pi receives the answer as the tool result and control returns to the model loop; the
   recorded result also lands in `get_messages` (for `/host-check` as a
   `customType:"host-bridge-check"` message).

Dialogs the API does *not* recognize as host-bridge (`select`/`confirm`/`input`/`editor`
raised by any extension) are parked instead:

```bash
curl -s localhost:3000/sessions/smwmfmffwed3c/ui          # pending list
curl -s -XPOST localhost:3000/sessions/smwmfmffwed3c/ui/<requestId> -d '{"value":"Allow"}'
# body: {"value":…} for select/input/editor, {"confirmed":true|false} for confirm,
# {"cancelled":true} to dismiss any of them
```

### 5. Stop a session: `DELETE /sessions/:id` — MANDATORY for cleanup

```bash
curl -s -XDELETE localhost:3000/sessions/smwmfmffwed3c
# → {"stopped":true}
```

Internally:

1. The API closes the terminal websocket — just a detach; the container's main process
   keeps running (client disconnect does not stop pi).
2. It reads the container state; only if it is `running` does it execute and poll
   `StopDeployment`. `docker stop` sends SIGTERM (the deployment's termination signal),
   which pi treats as an orderly shutdown: pending work finishes and remaining messages
   are flushed to the session file before the process exits.
3. `DeleteDeployment` removes the resource (and its container); "not found" is tolerated
   so the call is safe to repeat.
4. Skipping the stop for non-running deployments is deliberate: stopping a deployment
   whose container was never deployed produces a failed `docker stop` update that marks
   the resource busy and blocks the delete with "Deployment busy".

Verify afterwards: `GET /sessions` no longer lists the id and
`docker ps -a --filter name=pi-session-` shows nothing.

## Logging — where output lands

- `api/logs/pi-session-<id>.log` — every raw frame the API exchanged with the terminal,
  one JSON object per line: `{"ts":…,"dir":"out"|"in"|"error","line":"…"}`. Contains pi's full
  output including echoes and PTY noise (the API keeps the raw stream, the structured
  parser runs on top).
- Structured, filterable view: the `/events` endpoint (step 3).
- pi's own session history: `pi/pi-agent-state/sessions/--workspace--/<timestamp>_<uuid>.jsonl`
  — survives container deletion because the file lives on the host through the mount.
  `{"type":"export_html"}` via `/rpc` renders a session to a single HTML file.

## Gotchas

- Only one API writes to a session terminal at a time; duplicated writers mix pi's stdin.
- Session deployments live outside `pi.sync.toml` — sync execution neither creates nor
  removes them; abandoned ones linger until `DELETE /sessions/:id`.
- Deleting the container removes the deployment but not the history: both the session
  jsonl (state mount) and the API log (api/logs) stay on the host.
- The base `pi-worker` deployment (`/status`, `/start`, `/stop`) is unrelated to sessions.
- `host_shell` executes on the host with the API server's permissions — the API must not
  be exposed beyond the machine running it.

## Config

| env (api/.env) | default | meaning |
|---|---|---|
| `KOMODO_URL` / `KOMODO_API_KEY` / `KOMODO_API_SECRET` | `http://localhost:9120` / — | Komodo Core + api key |
| `PORT` | `3000` | API listen port |
| `PI_WORKER_DEPLOYMENT` | `pi-worker` | base deployment behind `/status` `/start` `/stop` |
| `PI_WORKER_SERVER` | `Local` | Komodo server for session deployments |
| `PI_WORKER_IMAGE` | `pi-worker:latest` | image for session containers |
| `PI_HOST_BRIDGE_CWD` | server startup dir | working directory for host-bridge commands |
| `PI_HOST_BRIDGE_TIMEOUT_MS` | `60000` | host-bridge command timeout |
