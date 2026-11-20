# Observations — known non-critical debt

> Created 2026-11-20 out of the task-01 self-review (`docs/tasks/01-stack-and-skeleton.md`,
> branch `task-1-stack-and-skeleton`). Owner call 2026-11-20: nothing here is critical —
> record now, revisit later. Each item names its revisit trigger, so none of them rot silently.

## OBS-1: `/healthz` `/readyz` `/metrics` are unauthenticated on the dev-published port

- **Where:** `src/compose.yaml` (`runtime-api` ports: `"${RUNTIME_API_HOST_PORT:-8080}:8080"`), `src/Platform/Http/PlatformEndpointExtensions.cs`.
- **State today:** acceptable — local dev only. The host port exists so walkthrough curls work without tokens; `X-Service-Token` auth is reserved for the internal API (later tasks), and decision-65 contour is otherwise intact (postgres private-only; Komodo's 9120 is UI/bootstrap).
- **Revisit trigger:** any deployment beyond the dev machine, or the first time the runtime-api port is reachable by someone else.
- **Fix sketch:** gate `/metrics` and `/readyz` with `RequireServiceToken()`, or stop publishing the port at all and reach the service through the compose network only.

## OBS-2: `alpine/git` image is not pinned

- **Where:** `src/compose.yaml`, `config-snapshot` service — `image: alpine/git:latest` with the comment "pin when this ships".
- **Why deferred:** the currently valid vendor tag was not verified during task 01; guessing a non-existent version would break the stack validation.
- **Revisit trigger:** first real production config-snapshot run, or any other touch of the backups plane.
- **Fix sketch:** pin an existing version (one line), the same way the Komodo vendor compose pins its images.

## OBS-3: `backup-loop.sh` hides `pg_dump` stderr

- **Where:** `src/backup/backup-loop.sh` — `pg_dump … 2>/dev/null | gzip`, error log line is only "backup failed".
- **Effect:** when a dump fails, the log shows *that* it failed, not *why* ("password authentication failed", "database does not exist"…).
- **Revisit trigger:** the first real backup failure, or when backup monitoring/alerting gets built.
- **Fix sketch:** drop the redirect and include the captured stderr in the `level:"error"` log line.

## OBS-4: Dockerfile has no restore-cache layering

- **Where:** `src/Dockerfile` — a single `dotnet publish` step after the full source `COPY`.
- **Effect:** NuGet restore re-runs on every image build (any source change invalidates the layer). Speed issue only, not correctness — negligible at current size, noticeable as packages/files grow.
- **Revisit trigger:** build times become annoying, or the package set grows.
- **Fix sketch:** standard layered pattern — copy `*.props` + `*.csproj` → `dotnet restore` → copy sources → `dotnet publish`.
