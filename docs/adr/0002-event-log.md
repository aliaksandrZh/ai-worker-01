# The Postgres Event Log is the only channel between modules

The orchestration plane today keeps the same "take a row, process it, mark it done"
mechanism in five separate places: `github_event`, `orchestration_job`, `run_event`,
`outbox_action` and per-cycle watermarks — each with its own claim, retry and
poison-handling implementation. We replace all five with one Postgres-backed **Event
Log** module: producers append typed events, consumers poll by stream. Delivery is
at-least-once with a visibility timeout and dead-lettering; ordering is strict inside a
stream (one root item) and parallel across streams; consumers are config-gated
in-process workers, safe to run in several instances. Postgres is enough: single-host
deployment, no RabbitMQ, no LISTEN/NOTIFY until poll latency actually hurts.

Why: every queue it replaces was a shallow re-implementation of the same delivery
module; retry, dead-letter and ordering logic concentrate once (locality), and the
event vocabulary of `docs/flows/events.md` becomes the contract. Two adapters justify
the seam — Postgres in production, in-memory in tests.
