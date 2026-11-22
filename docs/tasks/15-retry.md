# 15: Retry — automatic (pre-execution) and /retry

**What to build:** The two retry paths. Automatic: only transient infrastructure errors only before the Run starts executing, only while retry budget remains (defaults `max_automatic_retries = 1`, initial backoff 30 s; policy levels AgentProfileVersion → Repository override → effective policy pinned on the Task); a task Failed→Queued transition happens with budget, retried silently (no notification), with a new Run and attempt number. Manual `/retry` command: in one transaction the Task moves Failed→Queued and a new OrchestrationJob is created; the Task re-enters the normal claim flow with `attempt_number + 1` and a fresh AgentSession; input artifacts (including the approved plan) reused unchanged; preconditions rechecked (open WorkItem, no unfinished Task, most recent non-cancelled task-producing attempt failed, original preconditions hold); pinned profile version disabled/not-ready → rejected `agent_profile_not_ready`, Task stays Failed.

**Spec:** `docs/pi-agent-architecture/06-command-reference.md` §9a; `03-state-machines.md` §2 (retry semantics); `02-data-model.md` §10 (retry_policy); `05-v1-decisions.md` decisions 28, 70; `06 §13` (notification table — silent retries).

**Blocked by:** 12, 13

**Status:** implemented (2026-11-22), on branch `task-15-retry`, pending review. Full suite green: `dotnet test PiAgent.slnx` → 2 + 9 + 47 + 541 passing, 0 failed. Live end-to-end executed on `aliaksandrZh/04` (disposable issue #8) — see below.

- [x] Transient pre-execution failure with budget → silent automatic retry, attempt increments, no notification
- [x] Automatic retry budget exhausted → Task finally Failed + Outbox failure notification (spec template)
- [x] A failure after execution began never auto-retries
- [x] `/retry` reuses the same Task (no new Task): same profile version, same `target_head_sha` for reviews, attempt+1, fresh session
- [x] `/retry` on a disabled/not-ready pinned version → rejected `agent_profile_not_ready`, Task stays Failed
- [x] `/retry` on a succeeded Task or non-latest failure → rejected `task_not_retryable` (`04 §7`); precondition/admission/occupancy rejections leave the Task `Failed` as well (decision 70)
- [x] Acceptance scenario "Retry" (`05` matrix) passes

## What was done

**Automatic retry** (`RunTerminalConsumer`, decisions 28/70): a terminal `failed` Run requeues its Task **silently** (`Failed → Queued`, terminal fields cleared, fresh OrchestrationJob with `available_at = now + backoff`) when all of these hold —
- the Run never began executing (no persisted `agent_started` RunEvent — the event-driven `starting → running` boundary, 03 §3),
- the error code is transient infrastructure (`RunErrorCodes.TransientPreExecution` = `startup_timeout`, or `runtime_failure` — i.e. a reconciled container death before the agent started),
- the task-lifetime budget remains: `AutomaticRetriesUsed < max_automatic_retries` from the policy pinned at Task creation (new `task.automatic_retries_used` column; manual `/retry` never consumes it). Backoff = `initial_backoff · 2^(retriesUsed−1)` (defaults: one retry after 30 s). The Workflow stays `Active` with the retried Task as its open work; the claim flow then revalidates all preconditions and runs attempt+1. Every retry-eligible pre-execution failure that is NOT requeued is final and notifies.

**Manual `/retry`** (`RetryCommandHandler`, wired via `ControlCommandHandler` like `/stop`): in one transaction requeues the Workflow's most recent non-cancelled Task if it is `failed` — same Task (pinned profile version, pinned review `target_head_sha`, reused TaskArtifactInputs and Workspace), new OrchestrationJob available immediately, Workflow → `Active`, command `processed`, **no reply** (06 §13 has no row for an accepted retry; the task's outcome publishes later). Rejections record the reason, leave the Task `Failed` (decision 70) and enqueue exactly one reply: `task_not_retryable` (no Workflow / last non-cancelled Task not failed), `unfinished_task_exists`, `work_item_closed`, `workflow_failed`, `agent_profile_not_ready` (pinned version disabled or not `ready`). Per-task-type original preconditions (plan input for implement, pinned review SHA equality, permissions) stay with the existing claim-time rechecks — owner decision: no bespoke per-command gates inside `/retry` ("the early skip should be implemented in terms of each command").

**Notifications** (decision 29, 06 §13 template): every finally-failed Task now enqueues exactly one failure notification (idempotency key = the failed Run id) — `github_publication_failed` keeps its decision-42 closing ("artifacts are kept; check for an already-posted result"); retries-exhausted failures use "Automatic retries exhausted."; everything else (incl. `TimedOut` and post-execution codes that never notified before) gets the plain resubmit closing with `(or use /retry)`. The claim query additionally only claims jobs with `available_at <= now` (backoff enforcement).

**Config note:** Repository-level retry-policy override (decision 28 middle level) is intentionally not implemented — the accepted V1 configuration shape has no key to express it (owner decision 2026-11-22); the Task pins the profile-level policy.

**Schema:** migration `AddAutomaticRetriesUsed` adds `task.automatic_retries_used int NOT NULL DEFAULT 0`. Additive: existing deployments get the column with 0 — nothing breaks, no data reconciliation needed; the stack's migrator applies it on the next `task stack`/restart.

## Verification (executed during development)

- `dotnet test tests/Hosts.Tests/PiAgent.Hosts.Tests.csproj --filter "FullyQualifiedName~RunTerminalConsumerTests|FullyQualifiedName~RetryCommandTests|FullyQualifiedName~JobQueueWorkerTests|FullyQualifiedName~CommandRejectionTextTests"` → `Passed! - Failed: 0, Passed: 56/57` (final run) — real PostgreSQL via Testcontainers; covers silent requeue (fresh job at ≈+30 s), budget-exhausted notification (`Attempt: #N`, stage, "Automatic retries exhausted."), post-execution never-retry, `TimedOut` notification, decision-42 text unchanged, `/retry` acceptance (same Task/job/fresh session at attempt+1/reused Workspace + plan input/pinned review SHA), all rejection reasons, backoff non-claimability (`Claim_WaitsForJobsAvailableInTheFuture`).
- `dotnet test PiAgent.slnx` → Architecture 2, Platform 9, Infrastructure 47, Hosts 541 — all passing, 0 failed.
- Two pre-existing tests updated to the new notification contract (ticket 15 is their deferred dependency): `RunTerminalConsumerTests.OtherRunFailures_*` now expects one generic-template notification; `TimeoutReconcilerTests.ContainerDies…NotificationFires` seeds a realistic `agent_started` event for the already-executing run (post-execution death → notify; a pre-execution death with budget is now a silent retry per 06 §13).

## Live end-to-end (executed 2026-11-22, `aliaksandrZh/04`, disposable issue #8, planner v8)

Started with `task stack-rebuild` + `task stack` (migrator applied `AddAutomaticRetriesUsed`) and the Komodo compose (`agent-komodo`, detached). Commands were injected through the sanctioned `POST /internal/commands` hook (service token); pre-execution failures were induced by killing a run's container the instant docker emitted its `start` event — Pi's `agent_started` arrives ~0.2–1.5 s after boot, so a DB-poll race loses (attempt 2 proved that: killed "during starting" but after the event landed → post-execution final, as the spec requires). Sequence observed:

1. `/plan` → task claim → attempt 1 ran, **published its plan comment (10:06:54)**, was killed while `running` → reconciled `runtime_failure` → task finally `failed`, `automatic_retries_used=0`, **no auto-retry (post-execution)** → notification "…Stage: Execution…" posted 10:07:10.
2. `/retry` (processed, **no reply**) → same Task, attempt 2 → killed after `agent_started` landed → final again, notification 10:07:39.
3. `/retry` (processed, no reply) → attempt 3 killed **pre-`agent_started`** → `runtime_failure`, pre-execution → **silent automatic requeue**: `automatic_retries_used: 0→1`, fresh job at +30 s backoff, Workflow stayed `Active`, **zero notifications, no comment for attempt 3**; claim flow then created attempt 4 (`pending`) with a fresh session.
4. Attempt 4 killed the same way → budget exhausted → task finally `failed` → notification with "**Attempt: #4 | Stage: Container startup**… **Automatic retries exhausted.**" (10:10:25).
5. `/retry` again → attempt 5 with a **fresh AgentSession** completed → task `succeeded`, Workflow `waiting_for_human`, single final publication (10:11:13, deduped against attempt 1's earlier comment by the agent).

DB verdicts (evidence, `psql`): 5 Runs with 5 distinct `agent_session` rows, one reused Workspace row; the three `/retry` commands `processed` with no Outbox rows; Outbox shows exactly one notification per final failure (attempt 1, 2, 4 — run-id idempotency keys), all `completed`; runs 3/4 carry only the API-generated `artifact_produced` rows and NO `agent_started` (the boundary check the consumer uses). GitHub side effects on issue #8: 3 failure comments + 2 plan publications. An older stub-workflow run (`run 43bf45e4`) that survived yesterday's shutdown was reconciled at stack boot — final post-execution notification, separate from this task.

## How to manually test (live walkthrough)

**Prerequisites:** a disposable GitHub repository with the bot configured, `src/.env` filled (`src/.env.example`), the planner profile built and ready. The walkthrough below was executed as described in the "Live end-to-end" section above (with the container-kill trigger and the `/internal/commands` hook); the original step list is kept for reference.

1. `task stack-rebuild` (images contain the ticket code), then `task stack`, plus `task agent-komodo` for container provisioning. Check: `task stack-logs` shows services healthy.
2. Post `@<bot> /plan` on an issue → wait for the plan comment (confirms the base pipeline works).
3. **Automatic retry (silent):** make a pre-execution failure happen with budget left — e.g. kill the run's container on its docker `start` event (the DB-poll race loses; `agent_started` lands in ~0.2–1.5 s). Expected: task goes `starting → failed(startup_timeout|runtime_failure)` then **silently** back to `queued` (`automatic_retries_used=1`); **no GitHub comment** at that point; after the 30 s backoff a new Run starts (`attempt_number=2`).
4. Let it fail again (budget exhausted). Expected: task finally `failed` and exactly one "Task failed: /plan … Attempt: #2 … Startup (never became ready) … **Automatic retries exhausted.**" notification appears on the issue.
5. **Manual retry:** post `@<bot> /retry` on a failed task. Expected: no immediate comment (accepted retries are silent); the same Task re-runs — `/status` shows the same task with `Run attempt: #N+1`; a new Pi session; the failed task's history (previous attempts) preserved.
6. **Rejections:** post `/retry` on an issue with no workflow or with a succeeded/blocked latest task → reply "Command rejected: there is no failed task to retry…"; disable the pinned profile version → reply "…agent profile version is disabled or its image is not ready…" and the task stays `failed`.
7. **Post-execution failure:** let a run fail after the agent started (e.g. required publication missing) → no automatic retry; the failure notification says "The produced artifacts are kept…(or use /retry)"; `/retry` re-runs it.
