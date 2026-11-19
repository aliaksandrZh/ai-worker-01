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
