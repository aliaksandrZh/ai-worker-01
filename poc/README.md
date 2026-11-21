# PoC — frozen reference material

This is the original proof of concept that validated the ideas behind the real system (now
`../src/`): driving pi in per-session docker containers through Komodo terminals, GitHub
polling, and a GitHub write API. The code, API shape, and language are **not** architectural
constraints — the accepted spec lives in `../docs/pi-agent-architecture/`. Kept as reference;
the root `task` shortcuts below no longer exist in `Taskfile.yml` — run the equivalent direct
commands shown in the table.

Four services that run pi coding agents in per-session docker containers and watch GitHub repos.
Ports: Komodo `9120`, session API `3000`, webhook receiver `3900`, GitHub write-api `3901`.

```
HTTP client ──> api/src/server.mjs (:3000) ──> Komodo Core (:9120) ──> Periphery ──> docker
                      │   (komodo_client RPC + terminal websocket = pi's stdin/stdout)
                      └────> pi-session-<id> containers: pi --mode rpc,
                             /home/pi/.pi/agent ← pi/pi-agent-state (shared)

github-polling (standalone, no docker):
  src/poll.mjs (:—, console)  +  src/write-api.mjs (:3901)  →  github.com (GitHub App / PAT)

Repo session containers reach github.com directly: in-container official GitHub MCP server
(baked into the image, 4 pinned tools via pi/pi-agent-state/mcp.json) + native git push
via the clone PAT the entry script exports/reads on demand (credential helper).
```

## Folders — what each is and how to run it

| folder | what it is | run |
|---|---|---|
| `komodo/` | Komodo Core + Periphery + FerretDB (docker compose) — manages all containers | `docker compose --file komodo/ferretdb.compose.yaml --env-file komodo/compose.env up` |
| `pi/` | pi-worker image + container declarations (`pi.sync.toml` is the truth; see `pi/README.md` for bootstrap/gotchas) | built once via Komodo UI |
| `api/` | HTTP API: create/run/drop pi sessions over Komodo | `cd api && npm start` (needs `api/.env`) |
| `github-polling/` | monitors repo activity (`src/poll.mjs`) + write API (`src/write-api.mjs`) | `cd github-polling && node --env-file-if-exists=.env src/poll.mjs` / `… src/write-api.mjs` |

## Start sequence

1. Start Komodo: `docker compose --file komodo/ferretdb.compose.yaml --env-file komodo/compose.env up` — UI at `localhost:9120`, login `admin` / `changeme`.
2. Once: Komodo UI **Syncs → `pi` → Execute Sync** — creates Builder, Build `pi-worker`, Deployment `pi-worker` from `pi/pi.sync.toml`.
3. `cd api && npm start` — session API on `:3000` (needs `api/.env`, see below).
4. Drive it — every endpoint as a copy-pasteable curl (all bodies are JSON; errors come back as `{error}` with 400 bad body / 404 unknown session / 409 container not running / 502 Komodo failure):
   ```sh
   # 0. the long-lived pi-worker container itself
   curl -s localhost:3000/status                                             # {deployment, state}
   curl -s -XPOST localhost:3000/start                                       # docker start pi-worker
   curl -s -XPOST localhost:3000/stop                                        # docker stop pi-worker

   # 1. create a session → {"id":"s…","sessionId":"…","sessionFile":"…"}
   #    add "repo":"owner/name" to clone that repo into the container's /workspace at start (needs pi/.env with GIT_CLONE_TOKEN=)
   ID=$(curl -s -XPOST localhost:3000/sessions -d '{"name":"demo"}' | node -e "let s='';process.stdin.on('data',d=>s+=d).on('end',()=>console.log(JSON.parse(s).id))")
   curl -s localhost:3000/sessions                                           # list all: {sessions:[{id,state,status,connected}], stored:[{path,mtime}]}
   echo $ID

   # 2. drive the session $ID
   curl -s localhost:3000/sessions/$ID                                       # pi's session state (raw get_state)
   curl -s -XPOST localhost:3000/sessions/$ID/connect                        # reattach after an api restart (409 if container not running)
   curl -s -XPOST localhost:3000/sessions/$ID/rpc -d '{"type":"prompt","message":"do a thing"}'   # any pi RPC command {type,…}; 120s timeout
   curl -s localhost:3000/sessions/$ID/status                                # {connected, phase: idle|running|blocked, hasRun, lastSettled, pendingUi}
   curl -s "localhost:3000/sessions/$ID/wait?timeoutMs=55000"                # long-poll → {result: settled|blocked|disconnected|timeout}
   curl -s "localhost:3000/sessions/$ID/events?since=0"                      # event log → {records:[{seq,ts,record}], nextSeq}; ring keeps last 2000
   curl -s "localhost:3000/sessions/$ID/events?since=0&type=tool_execution_start"   # same, filtered by record type
   curl -s localhost:3000/sessions/$ID/ui                                    # dialogs pi waits on a human for → {pending:[{method,payload,receivedAt}]}
   curl -s -XPOST localhost:3000/sessions/$ID/ui/<requestId-from-pending> -d '{"value":"Allow"}'   # answer a dialog (404 if not pending)
   curl -s -XPOST localhost:3000/sessions/$ID/notify -d '{"url":"http://localhost:3900/hook"}'     # webhook: POSTs {id,…signal} on settled/blocked; lost on api restart
   curl -s -XDELETE localhost:3000/sessions/$ID/notify                       # unregister the webhook
   curl -s -XDELETE localhost:3000/sessions/$ID                              # stop the container + delete the deployment
   ```

## Environment — every var, one table

Each service reads its own `.env` at its root (see `.env.example` next to it).

| file | key | what it does |
|---|---|---|
| `api/.env` | `KOMODO_URL` / `KOMODO_API_KEY` / `KOMODO_API_SECRET` | Komodo connection; create key in Komodo UI → Config → API Keys (read+write+execute) |
| `api/.env` | `PORT`, `PI_WORKER_DEPLOYMENT`, `PI_WORKER_SERVER`, `PI_WORKER_IMAGE` | optional overrides (defaults: 3000, `pi-worker`, `Local`, `pi-worker:latest`) |
| `api/.env` | `PI_HOST_BRIDGE_CWD`, `PI_HOST_BRIDGE_TIMEOUT_MS` | optional host-bridge cwd / command timeout |
| `pi/.env` | `GIT_CLONE_TOKEN` | PAT for `POST /sessions {"repo"}` — mounted read-only into repo session containers only |
| `github-polling/.env` | `GITHUB_REPOS`, `GITHUB_POLL_INTERVAL_SEC`, `GITHUB_PAGE_CAP` | what to poll, how often, page cap |
| `github-polling/.env` | `GITHUB_USER_TOKEN` | PAT (tier 1 of auth ladder; first configured wins) |
| `github-polling/.env` | `GITHUB_APP_ID`, `GITHUB_APP_KEY_FILE`, `GITHUB_APP_INSTALLATION_ID` | installed GitHub App (tier 2); pem relative to `github-polling/` |
| `github-polling/.env` | `GITHUB_TOKEN` | fallback token (tier 4; tier 3 = logged-in `gh` CLI) |
| `github-polling/.env` | `WRITE_API_PORT`, `GITHUB_STATE_FILE` | write-api port (3901), poll cursor file (default `state.json`) |
| `komodo/compose.env` | `KOMODO_DATABASE_USERNAME/PASSWORD`, `KOMODO_INIT_ADMIN_USERNAME/PASSWORD`, `KOMODO_HOST`, `KOMODO_WEBHOOK_SECRET`, `KOMODO_JWT_SECRET`, `TZ`, `COMPOSE_KOMODO_IMAGE_TAG` | the ~8 settings you set; rest of the file is vendor defaults — keep it as-is |

All `.env` / `*.pem` / `state.json` / logs / `pi-agent-state` sessions are gitignored.

## Tests

Unit, no services needed:

| file | protects |
|---|---|
| `api/src/rpc-session.test.mjs` | pi terminal frames split correctly (CR/CRLF, echoes dropped) + session state machine: status phases, settle/block/wait semantics, webhook signals |
| `github-polling/src/poll.test.mjs` | poll state (ring cap, dedup, state.json roundtrip), snapshot→diff→event line rendering, GitHub pagination, App JWT + installation-token minting, user-token auth ladder, rate-limit/auth error mapping |
| `github-polling/src/write-api.test.mjs` | request validation, CSRF origin guard, issue/PR/review/contents passthroughs, graphql token tiers, changes flow (branch→commit→PR) |

```sh
node --test api/src/rpc-session.test.mjs
node --test github-polling/src/poll.test.mjs
node --test github-polling/src/write-api.test.mjs
```

Live:

- `api/curl-check-signals.sh` — creates a session, prompts pi, `/wait` until settled/blocked, checks status + webhook line; needs api + komodo (`cd api && node src/webhook-listener.mjs` in a second shell to watch the webhook).
- `node api/src/rpc-smoke.mjs` (from `api/`) — e2e smoke vs live api: create → events → rpc → log-file → delete; needs api running.
- `api/src/webhook-listener.mjs` — dumb webhook receiver on `:3900`, needed by check-signals.
- `github-polling/src/console.html` — browser console poking write-api on `:3901` (needs the write-api running).

## Deeper docs (details, not the entry point)

- `../docs/pi-session-flow.md` — the full walkthrough: how a session lives, RPC over Komodo terminals, host-bridge, troubleshooting flow.
- `pi/README.md` — pi-worker image: first-time bootstrap in Komodo, daily sync workflow, gotchas.
- `../docs/komodo-vs-manual-docker.md` — decision record: why Komodo manages docker here (revisit only if PTY pain grows).
- `../docs/pi-agent-architecture/` — what the real system (`../src/`) is being built from.
