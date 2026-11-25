# One app host: workers become in-process consumers

> **Amendment (implementation).** The EF migration history is not just reset to one
> baseline — EF is gone entirely: `Infrastructure` and `Migrator` were deleted, and the
> schema is owned by each module's idempotent bootstrap DDL (`EnsureSchemaAsync` at host
> startup, before consumers). One schema owner per module, no migration choreography;
> `task stack` gates on postgres health alone. Old-growth deleted in stage 3: the five
> old hosts, `OrchestrationApi`, `MonitorTelemetryApi`, service-token auth, the GitHub
> mirror, and the whole build/config plane — agent profile images are built at deploy
> time (`task build-agents`) until the build plane is reworked.

Six executables (RuntimeApi, Orchestrator, Poller, OutboxWorker, BuildWorker,
HostMonitor) each pay the full host tax — Program.cs, DI, health/readyz/metrics
endpoints, heartbeat, Dockerfile, compose entry — on a single-host deployment where the
split buys nothing. We collapse them into **one long-running app host** where Poller,
Orchestration, Run Worker, Publisher and Build Worker are EventLog consumers
([ADR-0002](0002-event-log.md)), gated per instance by a `Workers:` section in the
config plane. The compose stack shrinks to postgres, migrator (one-shot), app, and the
backup/config-snapshot tooling; the observability stack is untouched. The app host's
entire HTTP surface is `/healthz`, `/readyz`, `/metrics` and the Bridge's
`POST /events` ([ADR-0003](0003-run-worker.md)); `OrchestrationApi` is deleted (tests
call the admission logic in-process, inspection reads the DB), and `MonitorTelemetryApi`
dies with HostMonitor — whose deletion needs no debate: it has no consumer of its
telemetry. The Docker socket is mounted once and shared by the two consumers that need
it. No ports are published to the host (closes OBS-1). The database starts fresh:
existing migration history is discarded and one baseline migration is written, since
the system has never reached production.

Why: in-process consumers keep every module interface exactly as designed at the
EventLog seam — if a module ever needs to split out again, it leaves as a consumer of
the same events, not as a new HTTP contract.
