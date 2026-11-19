# 08: Control commands — /help and /status round trip

**What to build:** The first complete path through the whole system, with no agent involved: `/help` publishes the command reference adapted to the target type, phase gates and current Workflow state (phase-gated/profile-gated/invalid commands omitted); `/status` reports Workflow state, current/last Task, profile, Run state (`Run attempt: #N` — absolute attempt number, no denominator), queue position/pending jobs, work-item state vs system-wide capacity, and the last result/error — without creating a Workflow, Task, Run, Session or Container when none exists. Replies published through the Outbox, exactly once per triggering comment.

**Spec:** `docs/pi-agent-architecture/06-command-reference.md` §7 (/status draft format), §9 (/help); `05-v1-decisions.md` decision 79 (attempt display); `04-api-contracts.md` §2 (command behavior table).

**Blocked by:** 07, 03

**Status:** ready-for-agent

- [ ] `/help` on an Issue lists only Issue-valid commands; phase-gated commands (e.g. `/code` without coder profile) omitted
- [ ] `/status` on a WorkItem without a Workflow replies "no agent workflow started" and creates nothing
- [ ] `/status` with an active/last Task renders the draft format incl. `Run attempt: #N` and queue position
- [ ] Exactly one reply per triggering comment (dedupe by comment ID)
- [ ] `status`/`help` leave Workflow/Task/Run rows untouched
- [ ] Acceptance scenario "Command parser" end-to-end (through the reply) passes
