# 07: Orchestrator core — state machines, admission, queue

**What to build:** The orchestration core: Workflow / Task / Run / AgentSession / OrchestrationJob records with the spec's state machines; the one-unfinished-Task-per-Workflow invariant enforced both by admission logic and by the partial unique index + transactional Task+Job creation with a `SELECT … FOR UPDATE` lock on the Workflow row; admission rules (same-Workflow command rejected while a Task is unfinished — not queued; cross-Workflow commands queued); transactional claim/lease with expiry returning jobs to `pending`; global FIFO by `available_at` then `priority`; `max_concurrent_runs` counting Claimed/Starting/Running/Stopping Runs (slot freed only at terminal state + confirmed stop); command → Task creation through routing rules with the selected `AgentProfileVersion` pinned (missing/disabled/unready profile → `agent_profile_not_configured`, rejected before any Task exists); Workflow-scoped Run serialization hook (a Workflow's next Run — any Task type — starts only after the previous Run's terminal callback is persisted).

**Spec:** `docs/pi-agent-architecture/03-state-machines.md` §1–4, §7–8; `02-data-model.md` §9, §10, §19, §23 (indexes); `06-command-reference.md` §12; `05-v1-decisions.md` decisions 9, 10, 35 (agent_image_not_ready), 36 (phase gating), 38, 41, 60 (serialization), 70 (invariant), 71 (Failed terminal), 75, 79 (index/lock).

**Blocked by:** 06, 02

**Status:** ready-for-agent

- [ ] A second same-Workflow command while a Task is unfinished → rejected at admission with a recorded reason (not queued)
- [ ] Cross-Workflow commands queue FIFO and claim in order when capacity frees
- [ ] The cap counts Claimed/Starting/Running/Stopping Runs; queued/pending work occupies no slot
- [ ] Concurrent workers never double-claim a job; expired leases return jobs to `pending`
- [ ] Claim-time recheck (`04 §2`, `06 §12`): a queued Task revalidates preconditions at claim — WorkItem open, plan present for `/code`, profile version still `ready`, repository not paused, pinned SHA unchanged for reviews — an invalid queued Task is cancelled or rejected with a recorded reason, never executed
- [ ] A Workflow in `Failed` is terminal: only `/status`, `/help`, and root closure succeed; other commands rejected with a recorded reason (decision 71)
- [ ] Command selecting a missing or disabled profile → rejected `agent_profile_not_configured`, no Task created
- [ ] Command selecting a known profile whose image is not `ready` → rejected `agent_image_not_ready` (decision 35, `04 §7`)
- [ ] No Run ever starts while its Task is `Failed` (Failed→Queued transition precedes any new Run)
- [ ] Within one Workflow, a code Run and a review Run never overlap (Workflow-scoped serialization)
- [ ] While `Repository.is_enabled = false`: task-producing commands rejected, queued Tasks stay unclaimed, active Runs may finish; re-enabling resumes processing (decision 75)
