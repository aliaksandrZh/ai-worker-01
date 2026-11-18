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
| `Dockerfile.pi-worker` | the image recipe (pi v1.5.2 via npm, node:24-bookworm-slim, non-root `pi` user, `ENTRYPOINT pi` + `CMD --mode rpc`) |
| `pi.sync.toml` | Komodo Resource Sync file — declares everything: builder, build, deployment |
| `.dockerignore` | keeps the two runtime folders out of the image build context |
| `pi-agent-state/` | bind mount target for `/home/pi/.pi/agent` (sessions, config) — runtime data, ignored by git |
| `pi-worker-workspace/` | bind mount target for `/workspace` (the worker's work products) — ignored by git |

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

Drive the RPC stream of the worker process itself (one client at a time):

```bash
docker attach pi-worker
```

Sessions and files survive redeploys: state in `pi-agent-state/`, work
products in `pi-worker-workspace/`, both visible on the host.

## Gotchas

- `--interactive` in `extra_args` is not decoration: `pi --mode rpc` lives on
  stdin, and without an open stdin the container exits 0 seconds after deploy.
- The deployment references `pi-worker:latest` directly, not the Build
  resource: a Build-attached deployment deploys the build's tracked version
  marker, which can name a version that was never actually built.
- Stopping and starting the container applies nothing — only a redeploy does.
- After editing a file consumed by Komodo, always Refresh before Execute
  Sync; the sync deploys what it has read, not what is on disk this second.
