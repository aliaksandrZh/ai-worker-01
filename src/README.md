# pi-agent control plane

The real implementation of `docs/pi-agent-architecture/` (task `01-stack-and-skeleton` and onward).
The repo root is still the PoC — `api/`, `github-polling/`, `pi/`, `komodo/` are frozen reference
material; everything in this directory is the new system.

## Stack (spec decision 1, finalized)

> **Amendment to spec decision 1 (2026-11-20).** The architecture is no longer stack-neutral:
> **.NET 10** (ASP.NET Core minimal APIs) · **EF Core 10 + Npgsql** (DB access) ·
> **EF Core Migrations** (versioned schema) · **xUnit** (tests).
> Recorded here because `docs/pi-agent-architecture/` is frozen accepted-spec material.
> Two deployment notes travel with it: V1 is single-host, so services speak plain HTTP on the
> private compose network (decision 65's TLS-across-hosts applies when hosts split); there is
> **no CI** for now — development is local (`dotnet test` runs everything: 21 tests, green).

## Layout

| path | what |
|---|---|
| `Platform/` | cross-service plumbing: §14 JSON logging + correlation, `/healthz` `/readyz` `/metrics`, worker heartbeat loop, service-token auth, Docker-socket readiness |
| `Infrastructure/` | EF Core AppDbContext, `Persistence/Migrations/`, `AddPiData` (postgres readiness) |
| `Hosts/` | one executable per service: `RuntimeApi`, `Orchestrator`, `Poller`, `OutboxWorker`, `HostMonitor`, `BuildWorker` |
| `Migrator/` | one-shot EF migration host (§13's `migrations` step) |
| `tests/` | `Platform.Tests`, `Infrastructure.Tests`, `Hosts.Tests`, `Architecture.Tests` (Hosts.Tests spins a Testcontainers postgres — Docker required for `dotnet test`) |
| `config/` | configuration plane (01 §2, ticket 02): `system.yaml` (bot login + §16 knobs), `repositories.yaml`, `routing.yaml`, `agents/<name>/profile.yaml` — mounted read-only into the orchestrator, synced only at startup |
| `backup/` | scheduled `pg_dump` + config-plane Git snapshot scripts (compose: `backups`, `config-snapshot`) |
| `komodo/` | Komodo Deployment for this system (vendor duplicate; own compose, port 9120) |
| `Dockerfile` | one multi-stage build for all hosts + Migrator (`--build-arg SERVICE=…`) |
| `compose.yaml` | the §13 control-plane stack (10 services, private network `pi-agent-net`) |

## Quick start

```sh
task agent-komodo         # Komodo Core UI at localhost:9120 (admin/changeme per compose.env)
                          # → create an API key: Config → API Keys (read+write+execute)
task stack                # postgres → migrator → the rest; --wait gates a green, ordered start
                          # after any src/ change: task stack-rebuild first (rebuilds images), then task stack
curl localhost:8080/healthz && curl localhost:8080/readyz && curl localhost:8080/metrics
task stack-logs           # each service logs §14 JSON lines
task stack-down
```

`task stack` copies `src/.env.example` → `src/.env` on first run; fill `KOMODO_API_KEY/SECRET`
before starting so the runtime-api readiness probe validates Komodo reachability with intents.

## Configuration plane (ticket 02)

YAML in `config/` is the desired configuration; PostgreSQL holds the runtime state. The
Config Loader — a startup component of the Orchestrator — validates everything and syncs the
DB in one transaction at each startup; no hot reload, no admin API (decisions 55–56, 81).

- `system.yaml` — `github.bot_login` + every §16 operational knob (unit strings: `60s`, `50MiB`).
- `repositories.yaml` — registered repositories; allowlist semantics per decision 49 (absent =
  enabled + seeded with the owner; explicit `[]` = filtering disabled), permitted commands/types,
  authorization policy.
- `routing.yaml` — global routing rules (`repository_id = NULL`) with per-repo overrides; the
  referenced version is pinned on the Task at creation (02 §12a).
- `config/agents/<name>/profile.yaml` — profile definition; each configuration change creates a
  new immutable `AgentProfileVersion` (`draft`): normalized YAML + per-file sha256 + file contents
  + optional `PI_AGENT_SOURCE_COMMIT` traceability metadata. Builds/dispatches are tickets 07/10.

Before `task stack`: create the secrets file and align identities

```sh
mkdir -p src/secrets && printf '<your-pat>' > src/secrets/github-sync-pat.txt   # gitignored
#   → the PAT's login (GET /user) must equal github.bot_login in src/config/system.yaml
```

Startup failure modes (deliberate): missing/empty PAT file, PAT login mismatch, unknown
repository (404), schema-invalid YAML or a §16 constraint violation all abort Orchestrator
startup with a diagnosable log line; the compose `--wait` gate shows it red.

## Ports and exposure (decision-65 contour from day one)

| exposure | port | why |
|---|---|---|
| `runtime-api` | published (`RUNTIME_API_HOST_PORT`, default 8080) | the only control-plane port on the host — health/readiness/metrics |
| Komodo Core | published 9120 | UI bootstrap + API key management |
| postgres, all workers, migrator | private network only | decision 65 — no external exposure |

Host Monitor and Build Worker never touch PostgreSQL (day-one enforcement:
`tests/Architecture.Tests/ProjectBoundaryTests.cs`); their dependency is the Docker socket
(read-only) and, for the Host Monitor, the managed storage root (`STORAGE_ROOT`, mounted at
`/var/lib/pi-agent/workflows` — plain host directories per decision 52).

## Metrics baseline (§14 — on every service's `/metrics`)

Workers additionally expose `running`, `heartbeatUtc`, `lastSuccessfulCycleUtc`.
Placeholder `null` keys become real as their features land: poller → task 04, queue → task 07-08,
runs/reconciliation → task 13, outbox → task 03. Live now: `dbSizeBytes` (runtime-api),
`storageFreeBytes` (host-monitor), worker heartbeats.

## Observations — known non-critical debt

Four deferred items from the task-01 review (unauthenticated dev metrics port, unpinned
`alpine/git`, `pg_dump` stderr hidden, no Dockerfile restore-cache layering) are recorded with
revisit triggers and fix sketches in [`docs/observations.md`](../docs/observations.md).

## Backups (decisions 52/76)

`backups` service: hourly `pg_dump → /backups/<ts>.sql.gz` (keep 24) —
`BACKUP_INTERVAL_MINUTES` tunes it. `config-snapshot`: clones `CONFIG_SNAPSHOT_REPO_URL`
daily into `/backups/config-<ts>.tar.gz`; unset URL = it logs-and-skips. DB size lives on
runtime-api `/metrics` (`dbSizeBytes`).
