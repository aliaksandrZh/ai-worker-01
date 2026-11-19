# 08: Control commands — /help and /status round trip

**What to build:** The first complete path through the whole system, with no agent involved: `/help` publishes the command reference adapted to the target type, phase gates and current Workflow state (phase-gated/profile-gated/invalid commands omitted); `/status` reports Workflow state, current/last Task, profile, Run state (`Run attempt: #N` — absolute attempt number, no denominator), queue position/pending jobs, work-item state vs system-wide capacity, and the last result/error — without creating a Workflow, Task, Run, Session or Container when none exists. Plus the Orchestration API read surface from the contract: `GET /api/v1/work-items/{id}`, `GET /api/v1/workflows/{id}`, `GET /api/v1/tasks/{id}`, and the `POST /internal/commands` test hook for injecting commands without GitHub. Replies published through the Outbox, exactly once per triggering comment.

**Spec:** `docs/pi-agent-architecture/06-command-reference.md` §7 (/status draft format), §9 (/help); `04-api-contracts.md` §2 (command behavior table + Orchestration API read endpoints + internal commands hook), §7 (error shape); `05-v1-decisions.md` decisions 29 (Outbox scope incl. control replies), 79 (attempt display).

**Blocked by:** 07, 03

**Status:** ready-for-agent

- [ ] `/help` on an Issue lists only Issue-valid commands; phase-gated commands (e.g. `/code` without coder profile) omitted
- [ ] `/status` on a WorkItem without a Workflow replies "no agent workflow started" and creates nothing
- [ ] `/status` with an active/last Task renders the draft format incl. `Run attempt: #N` and queue position
- [ ] Exactly one reply per triggering comment (dedupe by comment ID)
- [ ] `status`/`help` leave Workflow/Task/Run rows untouched
- [ ] `/status` and `/help` still work on a paused repository (`is_enabled = false` — control commands unaffected, decision 75)
- [ ] Orchestration API read endpoints return work-item/workflow/task views per `04 §2` with the standard error shape (`04 §7`)
- [ ] `POST /internal/commands` injects a command for testing without going through GitHub
- [ ] Acceptance scenario "Command parser" end-to-end (through the reply) passes
