# Pi worker start/stop API — short design (2026-11-18)

## Intent
An HTTP API under `api/` to start/stop the Komodo Deployment `pi-worker` (managed by `pi/pi.sync.toml`) without opening the Komodo UI.

## Approach (user approved)
- **Node.js 22.9+ with `komodo_client`** — wraps Komodo's RPC, api-key auth and types. The .NET/HttpClient alternative was rejected: it would hand-roll every request body for no gain.
- One file, node's built-in `http` — no framework, no other dependencies.

## Design
- `api/package.json` — dependency `komodo_client` ^2.3.2 only; `npm start` runs `node --env-file-if-exists=.env server.mjs`.
- `api/server.mjs` (~50 lines):
  - `GET /status` → `read("GetDeploymentContainer", { deployment })` → `{ state }` (`running` / `exited` / ...)
  - `POST /start` → `execute("StartDeployment", { deployment })` → `{ ok, operation }`
  - `POST /stop` → `execute("StopDeployment", { deployment })` → `{ ok, operation }`
  - anything else → 404; upstream Komodo errors → 502 with message
- Config via env (`.env.example`): `KOMODO_URL` (default `http://localhost:9120`), `KOMODO_API_KEY`, `KOMODO_API_SECRET`, optional `PI_WORKER_DEPLOYMENT` (default `pi-worker`), `PORT` (default 3000).
- `api/curl-requests.sh` — manual test: hits all three routes in order; run the curls one by one if preferred.

## Prerequisite (one unavoidable UI step)
Create an API key once in Komodo (Config → API Keys) — the only way a machine authenticates to Komodo.

## Non-goals
- No auth/TLS on this API itself (localhost tool).
- No deploy/rebuild endpoint (redeploys stay with `auto_update` + Resource Sync + UI).
- No automated tests (user call) — `curl-requests.sh` covers manual verification.
