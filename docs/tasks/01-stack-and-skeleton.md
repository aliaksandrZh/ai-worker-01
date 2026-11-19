# 01: Stack decision + running skeleton

**What to build:** Make the one-time stack decision (language, framework, DB access layer, migration tool, test runner — the owner's choice that finalizes spec decision 1) and land the running skeleton it will live in: one Docker Compose project on the single execution host with the service set from the spec (postgres, poller, orchestrator, runtime-api, outbox-worker, host-monitor, build-worker; agent containers excluded — they are created by the runtime later), PostgreSQL with a versioned (initially near-empty) schema via the migration tool, health/readiness endpoints for HTTP services, heartbeat/last-cycle for workers, and the structured JSON logging baseline with the correlation field set.

**Spec:** `docs/pi-agent-architecture/01-architecture-and-flows.md` §13 (services, dependencies, health checks, startup order), §14 (observability fields); `05-v1-decisions.md` decisions 1 (stack neutrality — finalized here), 55 (config plane startup), 76 (logging/DB monitoring baseline).

**Blocked by:** None (can start immediately)

**Status:** ready-for-agent

- [ ] Stack chosen and recorded as an amendment to spec decision 1
- [ ] Compose project starts the full service set; health checks pass; startup order matches the spec
- [ ] Schema is under version control; migrations run from clean state
- [ ] Every service emits structured JSON logs with the correlation field set (timestamp, level, service, message, correlationId, repositoryId, workflowId, taskId, runId, eventId — null when absent; secrets never logged)
- [ ] HTTP services expose readiness/liveness; workers expose heartbeat/last-successful-cycle
- [ ] CI runs the unit test runner green
- [ ] Internal API contour per decision 65 holds from day one: no external HTTP ports published without need (`01 §13`), PostgreSQL reachable only on the private network, cross-host traffic TLS-capable, dedicated service credentials configured for Orchestrator→Runtime API and Host Monitor→Runtime API
- [ ] Komodo Core + Periphery deployed via their own single docker-compose (`01 §2`) and reachable by the Runtime API with API-key credentials
- [ ] Health/metrics endpoints expose the §14 baseline: Poller lag/duration/errors/rate-limit-remaining; queue pending/oldest-job-age/claim-wait; Runs active/duration/startup/heartbeat-age/progress-age/timeout counts; reconciliation mismatch + orphan counts; Outbox pending/failed/dead-letter counts; DB size
- [ ] Mandatory backups automated per decision 52/76: PostgreSQL dumps + profile/config repository snapshots on schedule; DB size monitoring in place
- [ ] Managed-storage usage / free disk space exposed via metrics (Host Monitor's storage telemetry surfaced through the metrics endpoint)
