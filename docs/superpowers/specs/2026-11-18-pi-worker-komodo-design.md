# Pi worker on Komodo — short design (2026-11-18)

## Intent
Build `pi/Dockerfile.pi-worker` with Komodo on the local Docker Desktop daemon, and keep one long-lived `pi-worker` container that Komodo manages (deploy / restart / terminal / logs).

## Goals
- Image builds triggered from Komodo UI (BuildKit via Periphery → local Docker).
- One persistent pi-worker stack, deployable and manageable through Komodo.
- Pi state survives stack redeploys.

## Non-goals
- No git remote, no registry push, no CI, no remote servers.
- Not managing the `pi` config itself (image already installs pi via npm).

## Constraints
- `azh/03` is not a git repo (user: no GitHub for now) → path-based Repo.
- Host `/etc/komodo` is inside the Docker VM; instead of relocating the Periphery root, bind-mount `pi/` into it (user's pick — root stays default, exposure stays to one folder).
- Only the Dockerfile lives in `pi/` — build context is self-contained.

## Approach picked
**A. Path-based repo, no git** — Komodo's Repo/Build/Stack resources against local directories; `pi/` bind-mounted into the Periphery root at `/etc/komodo/pi`.
Rejected: **B. git-first** (needs a remote; right choice once this project gets committed — upgrade path later).
Rejected variant: **root → whole `azh/03`** (works, but exposes the entire working dir to Komodo for no gain over a single-folder mount).

## Design
1. **komodo/ferretdb.compose.yaml** (`periphery` service only) — add a bind mount next to the root mount:
   ```yaml
   - /Users/aliaksandrzhebit/Programming/azh/03/pi:/etc/komodo/pi
   ```
   Recreate just the periphery container (`docker compose up -d`); core/DB untouched.
2. **komodo/ferretdb.compose.yaml** — add a `komodo.skip` label to `core` (postgres, ferretdb, periphery already have it) so a Komodo StopAllContainers can't kill Komodo itself.
3. **pi/pi-worker.compose.yaml** (new) — the Stack's compose:
   ```yaml
   services:
     pi-worker:
       image: pi-worker:latest   # name reconciled with the Komodo Build's tagging during planning
       init: true
       restart: unless-stopped
       volumes:
         # Both real Mac folders (user call), gitignored + dockerignored
         - /Users/aliaksandrzhebit/Programming/azh/03/pi/pi-agent-state:/home/pi/.pi/agent  # sessions/config survive redeploys
         - /Users/aliaksandrzhebit/Programming/azh/03/pi/pi-worker-workspace:/workspace     # absolute Mac path: relative ./ resolves inside the VM
   ```
   **pi/.dockerignore** (new) — excludes both folders from the Build's context.
4. **Komodo resources** (created once in the web UI) — no Repo resource needed:
   - Build `pi-worker` — `files_on_host = true`, `build_path = /etc/komodo/pi`, `dockerfile_path = Dockerfile.pi-worker`, builder = the local server. Defaults keep `include_latest_tag` on → local `pi-worker:latest` image.
   - Stack `pi-worker` — run directory `/etc/komodo/pi`, compose file `pi-worker.compose.yaml`; deploys the image from the local daemon.
5. **Bootstrap order**: recreate periphery → create Build + Stack → run Build → verify the image tag exists on the daemon (`docker images | grep pi-worker` — if Komodo tagged only version tags, point the stack compose at `pi-worker:<version>`) → deploy Stack → verify `docker ps` shows `pi-worker` and the UI shows it under the Server/Stack.

## Access model (assumption marked)
`pi --mode rpc` speaks stdio; no ports published. Access is via Komodo's container terminal/exec (terminals are enabled in your compose.env). If a client later needs programmatic RPC attach, that's a separate ask — `docker exec -i` covers manual use.

## Add-on: automatic resource creation via Resource Sync (2026-11-18, user approved)
1. **pi/pi.sync.toml** (new) — declares Builder `local` (Server type → server `Local`), Build `pi-worker` (files_on_host), and a **Deployment `pi-worker`** with `deploy = true` (attached Build image, `extra_args = "--interactive --init"` for the rpc-stdin lifecycle, both folders as bind mounts). A stack was tried first and dropped: stack compose file content cannot be declared in sync TOML (ResourceToml has no file-contents field, verified in komodo_client 2.3.2 source) and host-file stacks deploy a stale snapshot — the Deployment keeps its entire config inside the sync file.
2. **komodo/ferretdb.compose.yaml** — `core` gets the same `pi/` mount so Core can read the sync file.
3. **One-time UI step (unavoidable minimum):** ResourceSync → Files on Host → Resource Path `pi.sync.toml` (relative to `/syncs/{sync_name}`; file mounted for Core at `/syncs/pi/pi.sync.toml`) → Refresh → Apply creates all three resources. Later resources also come from the TOML.
