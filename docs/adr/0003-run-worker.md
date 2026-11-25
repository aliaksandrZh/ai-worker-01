# The Run Worker consumes events; the Runtime API HTTP seam dies

Runtime API today is a shallow module behind an HTTP interface: 14 files whose real job
is "provision a container, relay what it says", plus four reconcilers/sweepers whose
only duty is recovering state the database already knows, plus service-token auth for
the internal HTTP it forces. We replace it with a **Run Worker** — an ordinary EventLog
consumer ([ADR-0002](0002-event-log.md)): it consumes `run_scheduled`, provisions and
supervises the container, and appends terminal events. The Bridge's egress shrinks from
six HTTP routes to one `POST /events` endpoint guarded by a per-run token. Dispatch
(`IRunDispatcher`), heartbeat callbacks and the reconciler classes become deletions:
recovery is the cursor plus the visibility timeout, liveness is docker inspect in the
worker's watchdog.

Why: the HTTP contract added auth, payload versioning and state-reconciliation classes
to hide what is fundamentally "start a container, wait for the end". The event seam
gives the same behaviour with one event type in each direction.
