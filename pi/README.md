# pi worker on Komodo — instructions

> This folder holds the source and Komodo declarations for a long-lived pi
> coding-agent worker: one Docker image is built from `Dockerfile.pi-worker`
> by your local Komodo instance, and one worker container ("pi-worker") runs
> persistently in `--mode rpc`, waiting for an RPC client over stdin. All
> Komodo resources — builder, build, container, mounts — are declared in
> `pi.sync.toml`; that file is the single source of truth.

## Files

| file | role |
|------|------|
| `Dockerfile.pi-worker` | the image recipe (pi v1.5.2 via npm, node:24-bookworm-slim, non-root `pi` user, `ENTRYPOINT sh pi-worker-entry` + `CMD --mode rpc`) |
| `pi.sync.toml` | Komodo Resource Sync file — declares everything: builder, build, deployment |
| `.dockerignore` | keeps the runtime state out of the image build context |
| `pi-agent-state/` | bind mount target for `/home/pi/.pi/agent` (runtime sessions; `models.json`, `settings.json` tracked in git) |
| `pi-worker-entry` | container-start script (mounted readonly as `/usr/local/bin/pi-worker-entry`, the image entrypoint): when `GIT_CLONE_REPO` is set it clones the repo into `/workspace` first, then execs pi |
| `.env` | clone PAT dotenv file (gitignored; `GIT_CLONE_TOKEN=<pat>`) — mounted into `repo` session containers only |

## Before you start

- Komodo is running from `../komodo` (core on `:9120`, login `admin` / `changeme`, server named `Local`).
- `../komodo/ferretdb.compose.yaml` contains the two pi mounts:
  - `periphery`: `- /Users/aliaksandrzhebit/Programming/azh/03/pi:/etc/komodo/pi` (build context + sync file as seen by the build)
  - `core`: `- /Users/aliaksandrzhebit/Programming/azh/03/pi/pi.sync.toml:/syncs/pi/pi.sync.toml` (sync file where Core reads it)
- The mount paths hard-code `pi/` at this location; if you move or rename this folder, update both mounts and `pi.sync.toml`'s absolute volume paths with it.

## First-time bootstrap

1. Recreate the Komodo containers so the mounts apply:
   ```bash
   cd ../komodo && docker compose -f ferretdb.compose.yaml --env-file compose.env up -d
   ```
2. Create the sync in the UI: **Syncs → New Sync** — name `pi`, **Files on Host**,
   Resource Path `pi.sync.toml`. The path resolves inside Core to
   `/syncs/pi/pi.sync.toml` — the folder is named after the sync.
3. **Refresh → Execute Sync** on the sync's page; type `pi` to confirm.
   Expected: three resources — Builder `local`, Build `pi-worker`,
   Deployment `pi-worker` — and the worker container comes up as part of the
   sync execution.

## Daily workflow

| Change | Action |
|--------|--------|
| Container config (mounts, args, image tag) | edit `pi.sync.toml` → Refresh → Execute Sync (redeploys automatically) |
| New image | edit `Dockerfile.pi-worker` (bump the `PI_VERSION` arg for a new pi release) → **Builds → pi-worker → Run Build** → the deployment's `auto_update` redeploys on the new image digest — or Execute Sync for an immediate redeploy |
| Restoring on a fresh Komodo | bootstrap once (above); everything else comes from `pi.sync.toml` |

Image tags after a build: `latest` plus the semver set (`0.0.1`, `0.0`, `0`).

## Run and verify

```bash
docker ps --filter name=pi-worker                                  # status: Up
docker inspect pi-worker --format '{{.Config.OpenStdin}}'          # true
```

Drive the RPC stream of the worker process itself (manual debugging, one
client at a time):

```bash
docker attach pi-worker
```

## Programmatic access (api/)

The `api/` HTTP server operates sessions through Komodo — one
`pi-session-<id>` Deployment per session, driven over Komodo attach-mode
terminal websockets. Endpoints (see `docs/superpowers/specs/2026-11-18-pi-rpc-api-design.md`):

```bash
cd ../api && npm start
BASE=http://localhost:3000
curl -s -XPOST $BASE/sessions -d '{"name":"demo"}'   # spawns a session container
curl -s -XPOST $BASE/sessions -d '{"name":"demo","repo":"aliaksandrZh/04"}'   # also clones that repo into /workspace at container start (needs pi/.env with GIT_CLONE_TOKEN)
curl -s $BASE/sessions
# POST  /sessions/:id/rpc      {"type":"prompt","message":"..."}   (any RPC command)
# GET   /sessions/:id/events?since=0&type=tool_execution_start
# GET   /sessions/:id/ui   +  POST /sessions/:id/ui/:requestId   (dialogs pi raised)
# POST  /sessions/:id/connect     (reattach after an api restart)
curl -s -XDELETE $BASE/sessions/:id                   # stop + delete container
node rpc-smoke.mjs                                     # end-to-end check
```

`pi/pi-agent-state/extensions/host-bridge.ts` runs inside every session
container and exposes `host_shell` (a tool that executes commands on the
host through the api) plus the `/host-check` command.

Sessions survive redeploys: state in `pi-agent-state/`. Work products live only in the
session container's `/workspace` (no host mount) — `POST /sessions {"repo": "owner/name"}`
clones the repo into that workspace at container start (dotenv `pi/.env` with key
`GIT_CLONE_TOKEN` — mounted read-only into that container only, token never touches api env).

## Gotchas

- `--interactive` in `extra_args` is not decoration: `pi --mode rpc` lives on
  stdin, and without an open stdin the container exits 0 seconds after deploy.
- The image entrypoint is `sh /usr/local/bin/pi-worker-entry` — the script is
  a read-only mount on every deployment (`pi.sync.toml`, `api/server.mjs`);
  run the image without that mount and the container exits at start.
- The deployment references `pi-worker:latest` directly, not the Build
  resource: a Build-attached deployment deploys the build's tracked version
  marker, which can name a version that was never actually built.
- Stopping and starting the container applies nothing — only a redeploy does.
- After editing a file consumed by Komodo, always Refresh before Execute
  Sync; the sync deploys what it has read, not what is on disk this second.
