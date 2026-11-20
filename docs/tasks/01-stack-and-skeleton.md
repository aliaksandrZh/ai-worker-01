# 01: Stack decision + running skeleton

**What to build:** Make the one-time stack decision (language, framework, DB access layer, migration tool, test runner — the owner's choice that finalizes spec decision 1) and land the running skeleton it will live in: one Docker Compose project on the single execution host with the service set from the spec (postgres, poller, orchestrator, runtime-api, outbox-worker, host-monitor, build-worker; agent containers excluded — they are created by the runtime later), PostgreSQL with a versioned (initially near-empty) schema via the migration tool, health/readiness endpoints for HTTP services, heartbeat/last-cycle for workers, and the structured JSON logging baseline with the correlation field set.

**Spec:** `docs/pi-agent-architecture/01-architecture-and-flows.md` §13 (services, dependencies, health checks, startup order), §14 (observability fields); `05-v1-decisions.md` decisions 1 (stack neutrality — finalized here), 55 (config plane startup), 76 (logging/DB monitoring baseline).

**Blocked by:** None (can start immediately)

**Status:** ready-for-agent → **done, awaiting owner review** (implementation on branch `task-1-stack-and-skeleton`, uncommitted pending owner review)

- [x] Stack chosen and recorded as an amendment to spec decision 1 → recorded in `src/README.md` (spec docs are frozen): .NET 10 · ASP.NET Core minimal APIs · EF Core 10 + Npgsql · EF Core Migrations · xUnit; TLS + no-CI notes travel with the record
- [x] Compose project starts the full service set; health checks pass; startup order matches the spec (`src/compose.yaml`: postgres → migrator → build-worker / runtime-api / host-monitor → orchestrator / poller / outbox-worker; verified live via `task stack` with `--wait`)
- [x] Schema is under version control; migrations run from clean state (`Infrastructure/Persistence/Migrations/`, one-shot `Migrator` host; verified live: `exit=0` after `down -v` on a fresh volume)
- [x] Every service emits structured JSON logs with the correlation field set (all ten §14 fields, null when absent; `CorrelationJsonConsoleFormatter`; two live-found defects fixed with RED→GREEN tests: DbSizeCycle registry wiring and missing newline flush)
- [x] HTTP services expose readiness/liveness; workers expose heartbeat/last-successful-cycle (`/healthz` `/readyz` `/metrics` on every service; `WorkerLoop` heartbeats verified advancing live)
- [ ] CI runs the unit test runner green — **skipped by owner decision 2026-11-20**: development is local, no CI for now
- [x] Internal API contour per decision 65 holds from day one: only runtime-api publishes a host port (8080) and Komodo 9120; PostgreSQL private-only; `X-Service-Token` credentials configured for Orchestrator/Host-Monitor/Build-Worker (constant-time compare, fail-closed; endpoints themselves land with later tasks); single-host HTTP noted in `src/README.md`
- [x] Komodo Core + Periphery deployed via their own single docker-compose (`src/komodo/`, vendor duplicate; PoC pi/ sync mounts omitted — images come from the Build Worker per decision 81) and reachable by the Runtime API over the shared `pi-agent-net` (live: `/readyz` names `komodo: ok`; API-key env plumbed)
- [x] Health/metrics endpoints expose the §14 baseline (poller/orchestrator/runtime-api/outbox/host-monitor/build-worker key sets; `dbSizeBytes` and `storageFreeBytes` live, remaining keys null placeholders until their tasks)
- [x] Mandatory backups automated per decision 52/76: hourly `pg_dump` (keep last 24) + config-repo snapshot on schedule (logs-and-skips while no repo configured; snapshots also pruned to last 24 — self-check `sh src/backup/config-snapshot-retention-test.sh`) + DB size metric live
- [x] Managed-storage usage / free disk space exposed via metrics (host-monitor `storageFreeBytes` live through the metrics endpoint)

**Done — what was built:**

| area | content |
|---|---|
| `src/Platform/` | §14 JSON logging + correlation scope, uniform `/healthz` `/readyz` `/metrics`, worker heartbeat loop, service-token auth, Docker-socket readiness |
| `src/Infrastructure/` | `AppDbContext`, initial (near-empty) migration, `AddPiData`, postgres readiness probe |
| `src/Hosts/*` | six uniform service hosts + per-service §14 metric providers (RuntimeApi, Orchestrator, Poller, OutboxWorker, HostMonitor, BuildWorker) |
| `src/Migrator/` | one-shot EF migration host (§13 startup step, gates the stack in compose) |
| `src/compose.yaml` | control-plane stack (10 services) + `src/.env.example` + `task stack/stack-logs/stack-down` |
| `src/komodo/` | agent-system Komodo deployment (own compose, port 9120, shared network) + `task agent-komodo` |
| `src/backup/` | pg_dump + config-snapshot scheduled loops (compose services `backups`, `config-snapshot`) |
| `src/Dockerfile` | one multi-stage build for all hosts + Migrator |
| tests | 21 green: Platform 9, Infrastructure 2, Hosts 8, Architecture 2（incl. the decision-65/81 no-DB boundary guard） |
| debt | deferred non-critical items live in [`docs/observations.md`](observations.md) with revisit triggers (OBS-1…4) |

**How to manually test:**

```sh
# run the stack (komodo, then the control plane)
task agent-komodo                          # Komodo UI on :9120 (admin/changeme in src/komodo/compose.env)
# → optionally create an API key in the UI (Config → API Keys) and fill KOMODO_API_KEY/SECRET
#   in src/.env — not required for this run
task stack                                 # builds images; postgres → migrator → the rest; --wait

# health, readiness, metrics
curl localhost:8080/healthz                # {"status":"healthy"}
curl localhost:8080/readyz                 # {"status":"ready","checks":{"postgres":"ok","komodo":"ok"}}
curl localhost:8080/metrics                # dbSizeBytes > 0; heartbeatUtc advances every ~5s

# §14 JSON logs + per-worker key sets
docker logs pi-agent-control-plane-orchestrator-1 --tail 2
for s in poller orchestrator outbox-worker host-monitor build-worker; do
  docker compose --env-file src/.env -f src/compose.yaml exec -T "$s" curl -s localhost:8080/metrics; echo
done

# komodo outage → readiness flips, then recovers
docker stop komodo-core
curl localhost:8080/readyz                 # 503, failed:[{name:"komodo"}]
docker start komodo-core
curl localhost:8080/readyz                 # 200 again

# postgres outage → orchestrator notReady, heartbeat still advancing, recovery without restart
docker stop pi-agent-control-plane-postgres-1
docker compose --env-file src/.env -f src/compose.yaml exec -T orchestrator curl -s localhost:8080/readyz
# → 503, failed:[{name:"postgres"}]
docker compose --env-file src/.env -f src/compose.yaml exec -T orchestrator curl -s localhost:8080/metrics
# → heartbeatUtc still advancing
docker start pi-agent-control-plane-postgres-1
docker compose --env-file src/.env -f src/compose.yaml exec -T orchestrator curl -s localhost:8080/readyz
# → 200; the orchestrator container was never restarted

# backups
docker compose --env-file src/.env -f src/compose.yaml exec -T backups ls /backups   # hourly dumps (keep 24)

task stack-down
```
