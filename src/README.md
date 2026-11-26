# pi-agent control plane

The real implementation of the pi-agent architecture.
The PoC lives in `../poc/` (`api/`, `github-polling/`, `pi/`, `komodo/`) as frozen reference
material; everything in this directory is the new system. Repo overview + main flow: root `../README.md`.

## Stack (finalized)

> **Amendment (2026-11-20).** The architecture is no longer stack-neutral:
> **.NET 10** (ASP.NET Core minimal APIs) · **EF Core 10 + Npgsql** (DB access) ·
> **EF Core Migrations** (versioned schema) · **xUnit** (tests).
> Two deployment notes travel with it: the stack is single-host, so services speak plain HTTP on the
> private compose network (TLS-across-hosts applies when hosts split); CI runs on
> GitHub Actions for every pull request (`.github/workflows/ci.yml`): restore, build, and
> `dotnet test` on the full solution — the same suites you run locally with `dotnet test`
> (Hosts.Tests spins a Testcontainers postgres; runners have Docker preinstalled).

## Layout

| path | what |
|---|---|
| `Platform/` | cross-service plumbing: JSON logging + correlation, `/healthz` `/readyz` `/metrics`, worker heartbeat loop, service-token auth, Docker-socket readiness |
| `Infrastructure/` | EF Core AppDbContext, `Persistence/Migrations/`, `AddPiData` (postgres readiness) |
| `Hosts/` | one executable per service: `RuntimeApi`, `Orchestrator`, `Poller`, `OutboxWorker`, `HostMonitor`, `BuildWorker` |
| `Migrator/` | one-shot EF migration host (`migrations` step) |
| `tests/` | `Platform.Tests`, `Infrastructure.Tests`, `Hosts.Tests`, `Architecture.Tests` (Hosts.Tests spins a Testcontainers postgres — Docker required for `dotnet test`) |
| `Dockerfile` | one multi-stage build for all hosts + Migrator (`--build-arg SERVICE=…`) — the only build artifact left in `src/` |

Outside `src/` (repo root):

| path | what |
|---|---|
|  `deploy/config/` | configuration-plane yamls: `system.yaml` (bot login + operational knobs), `repositories.yaml`, `routing.yaml` — mounted read-only at the config root |
| `agents/` | all agent assets: profile definitions (`agents/<name>/profile.yaml` + Dockerfile + entry…) and the shared runtime implementation `agents/_runtime/bridge/` — the Runtime Bridge modules every profile bakes. Merged into each profile's pinned build snapshot under `runtime-bridge/` by `ConfigSnapshotBuilder` (fail-closed on per-profile copies / version drift vs `bridge.json`); mounted read-only INSIDE the orchestrator's config root (`/etc/pi-agent/config/agents/`) so the pinned tree shape holds |
| `deploy/` | `compose.yaml` (the control-plane stack, private network `pi-agent-net`), `.env.example`, centralized logging config (`logging/`: loki/alloy/grafana) |
## Quick start

```sh
task stack                # postgres → migrator → the rest; --wait gates a green, ordered start
                          # after any src/ change: task stack-rebuild first (rebuilds images), then task stack
curl localhost:8080/healthz && curl localhost:8080/readyz && curl localhost:8080/metrics
task stack-logs           # each service logs JSON lines
open http://localhost:3000 # the same logs in one place: "Pi Agent Logs" dashboard (admin / GRAFANA_ADMIN_PASSWORD)
task stack-down
```

`task stack` copies `deploy/.env.example` → `deploy/.env` on first run; the runtime-api provisions agent
containers itself over the Docker socket (Komodo was dropped), so no external container orchestrator is involved.

## Configuration plane

YAML in `deploy/config/` is the desired configuration; PostgreSQL holds the runtime state. The
Config Loader — a startup component of the Orchestrator — validates everything and syncs the
DB in one transaction at each startup; no hot reload, no admin API.

- `system.yaml` — `github.bot_login` + every operational knob (unit strings: `60s`, `50MiB`).
- `repositories.yaml` — registered repositories; allowlist semantics (absent =
  enabled + seeded with the owner; explicit `[]` = filtering disabled), permitted commands/types,
  authorization policy.
- `routing.yaml` — global routing rules (`repository_id = NULL`) with per-repo overrides; the
  referenced version is pinned on the Task at creation.
- `agents/<name>/profile.yaml` — profile definition; each configuration change creates a
  new immutable `AgentProfileVersion` (`draft`): normalized YAML + per-file sha256 + file contents
  + optional `PI_AGENT_SOURCE_COMMIT` traceability metadata.

Before `task stack`: create the two secrets files and align identities

```sh
mkdir -p secrets && printf '<your-pat>' > secrets/github-sync-pat.txt   # gitignored
#   → the PAT's login (GET /user) must equal github.bot_login in deploy/config/system.yaml
printf '<your-write-pat>' > secrets/github-write-pat.txt                    # outbox comments; agent GitHub MCP (planner publication) + workspace clone
```

Startup failure modes (deliberate): missing/empty PAT file, PAT login mismatch, unknown
repository (404), schema-invalid YAML or a constraint violation all abort Orchestrator
startup with a diagnosable log line; the compose `--wait` gate shows it red.

## Ports and exposure

| exposure | port | why |
|---|---|---|
| `runtime-api` | published (`RUNTIME_API_HOST_PORT`, default 8080) | control-plane port on the host — health/readiness/metrics |
| orchestrator | private network only (host access via `docker compose exec orchestrator curl …`) | hosts the Orchestration API + internal commands hook |
| postgres, all workers, migrator | private network only | no external exposure |

Host Monitor and Build Worker never touch PostgreSQL (day-one enforcement:
`tests/Architecture.Tests/ProjectBoundaryTests.cs`); their dependency is the Docker socket
(read-only) and, for the Host Monitor, the managed storage root (`STORAGE_ROOT`, mounted at
`/var/lib/pi-agent/workflows` — plain host directories).

## Metrics baseline (on every service's `/metrics`)

Workers additionally expose `running`, `heartbeatUtc`, `lastSuccessfulCycleUtc`.
Live now: `outboxPending/outboxFailed/
outboxDeadLetter` (outbox-worker), `dbSizeBytes` (runtime-api), `storageFreeBytes`
(host-monitor), the poller set `pollerLagSeconds/pollerLastSuccessUtc/
pollerLastCycleDurationSeconds/pollerErrorsTotal/pollerRateLimitRemaining`, worker
heartbeats, and the build set — orchestrator: `buildJobsPending/buildJobsPolling/
oldestPendingBuildAgeSeconds/buildsDispatchedTotal/buildsCompletedTotal/buildsFailedTotal/
buildTransientErrorsTotal`; build-worker: `buildsActive/buildsSucceededTotal/buildsFailedTotal/
buildExecutionRetriesTotal`.

## Profile image builds

Every `AgentProfileVersion` is built asynchronously by the Build Worker: version `draft` →
durable `build_job` row (buildId persisted BEFORE dispatch) → `POST /internal/builds` → `202` →
the worker materializes the pinned build context from the version's `config_snapshot`
(hash-verified per file — a build never reads the live Git tree) and runs a local docker build
(`agent-<profile>:v<n>`, no registry) → `GET /internal/builds/{buildId}` polling → validated
result → version `ready` (or `build_failed` on a real build failure). The worker keeps durable
local JSON state under `BUILD_STATE_ROOT` (default `../runtime/builds`): results stay retrievable after
worker restarts, unfinished builds finalize from an existing image tag or rebuild idempotently
with the same `buildId`. The worker has no database (boundary-guarded) and accepts only the
Orchestrator's service credential. The shared runtime is assembled once as `agent-runtime-base`
from `agents/_runtime/` (pinned Pi + bridge + communication channel + the ONE shared entry);
each profile is a slim layer over it (see `agents/planner/Dockerfile`) — the agent delta is
data: `agent.json` (name, clone_mode, tools, prompt path) + the baked APPEND_SYSTEM.md, with
`mcp.json` generated at build from the manifest (ticket-04 generator).

## Context assembling

The claim worker runs the Context Assembler (`Hosts/Orchestrator/Context/`) BEFORE preparing the
Run: package = task-type instructions + pinned profile identity, the user command (its full
`source_body_snapshot` incl. trailing text), the work item, every conversation
comment chronologically (tombstones recorded as exclusions), `relatedWorkItems` from
WorkItemLink rows, prior Workflow artifacts (the `task_artifact_input` approved plan is mandatory
and never pruned; `log_reference` artifacts travel by reference), repository data, coder
agent-branch facts, and — for reviews — the pinned head/base SHAs plus submitted reviews and
grouped inline threads. Owner simplification: the assembler collects everything and applies only
budget-driven pruning, newest-first kept, with every exclusion recorded in the snapshot. Limits:
`ContextTokenEstimator` (bytes/4) against the profile's `max_context_tokens`, plus 256 KiB
per artifact / 512 KiB artifacts / 512 KiB serialized input. Mandatory context that cannot fit →
Task fails `context_too_large` with NO Run rows (metric `claimContextTooLargeTotal`); the frozen
snapshot is byte-exact (`content` moved jsonb → text), hashed, immutable, and served to
the Bridge via `GET /internal/runtime/runs/{runId}/input`.

## Backups

`backups` service: hourly `pg_dump → /backups/<ts>.sql.gz` (keep 24) —
`BACKUP_INTERVAL_MINUTES` tunes it. `config-snapshot`: clones `CONFIG_SNAPSHOT_REPO_URL`
daily into `/backups/config-<ts>.tar.gz`; unset URL = it logs-and-skips. DB size lives on
runtime-api `/metrics` (`dbSizeBytes`).
