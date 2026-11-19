# 15: Retry — automatic (pre-execution) and /retry

**What to build:** The two retry paths. Automatic: only transient infrastructure errors only before the Run starts executing, only while retry budget remains (defaults `max_automatic_retries = 1`, initial backoff 30 s; policy levels AgentProfileVersion → Repository override → effective policy pinned on the Task); a task Failed→Queued transition happens with budget, retried silently (no notification), with a new Run and attempt number. Manual `/retry` command: in one transaction the Task moves Failed→Queued and a new OrchestrationJob is created; the Task re-enters the normal claim flow with `attempt_number + 1` and a fresh AgentSession; input artifacts (including the approved plan) reused unchanged; preconditions rechecked (open WorkItem, no unfinished Task, most recent non-cancelled task-producing attempt failed, original preconditions hold); pinned profile version disabled/not-ready → rejected `agent_profile_not_ready`, Task stays Failed.

**Spec:** `docs/pi-agent-architecture/06-command-reference.md` §9a; `03-state-machines.md` §2 (retry semantics); `02-data-model.md` §10 (retry_policy); `05-v1-decisions.md` decisions 28, 70; `06 §13` (notification table — silent retries).

**Blocked by:** 12, 13

**Status:** ready-for-agent

- [ ] Transient pre-execution failure with budget → silent automatic retry, attempt increments, no notification
- [ ] Automatic retry budget exhausted → Task finally Failed + Outbox failure notification (spec template)
- [ ] A failure after execution began never auto-retries
- [ ] `/retry` reuses the same Task (no new Task): same profile version, same `target_head_sha` for reviews, attempt+1, fresh session
- [ ] `/retry` on a disabled/not-ready pinned version → rejected `agent_profile_not_ready`, Task stays Failed
- [ ] `/retry` on a succeeded Task or non-latest failure → rejected
- [ ] Acceptance scenario "Retry" (`05` matrix) passes
