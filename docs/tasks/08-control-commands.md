# 08: Control commands — /help and /status round trip

**What to build:** The first complete path through the whole system, with no agent involved: `/help` publishes the command reference adapted to the target type, phase gates and current Workflow state (phase-gated/profile-gated/invalid commands omitted); `/status` reports Workflow state, current/last Task, profile, Run state (`Run attempt: #N` — absolute attempt number, no denominator), queue position/pending jobs, work-item state vs system-wide capacity, and the last result/error — without creating a Workflow, Task, Run, Session or Container when none exists. Plus the Orchestration API read surface from the contract: `GET /api/v1/work-items/{id}`, `GET /api/v1/workflows/{id}`, `GET /api/v1/tasks/{id}`, and the `POST /internal/commands` test hook for injecting commands without GitHub. Replies published through the Outbox, exactly once per triggering comment.

**Spec:** `docs/pi-agent-architecture/06-command-reference.md` §7 (/status draft format), §9 (/help); `04-api-contracts.md` §2 (command behavior table + Orchestration API read endpoints + internal commands hook), §7 (error shape); `05-v1-decisions.md` decisions 29 (Outbox scope incl. control replies), 79 (attempt display).

**Blocked by:** 07, 03

**Status:** done-pending-review — owner live-verified 2026-11-21: `/status` and `/help` comments on the
real Issue produce the expected replies (issue-comment path, walkthrough Path A); the hook/API-read
steps (Path B, steps 4–5) remain unverified live

- [x] `/help` on an Issue lists only Issue-valid commands; phase-gated, profile-gated, and invalid-for-current-Workflow-state commands omitted (`06 §9`)
- [x] `/status` on a WorkItem without a Workflow replies "no agent workflow started" and creates nothing
- [x] `/status` with an active/last Task renders the draft format incl. `Run attempt: #N` and queue position
- [x] Exactly one reply per triggering comment (dedupe by comment ID)
- [x] `status`/`help` leave Workflow/Task/Run rows untouched
- [x] `/status` and `/help` still work on a paused repository (`is_enabled = false` — control commands unaffected, decision 75)
- [x] Orchestration API read endpoints return work-item/workflow/task views per `04 §2` with the standard error shape (`04 §7`)
- [x] `POST /internal/commands` injects a command for testing without going through GitHub (automated over real PostgreSQL + TestHost; the live curl of Path B remains owner-run only)
- [x] Acceptance scenario "Command parser" (through the reply) — live: owner posted `/status`//`/help` comments and confirmed the replies in the Issue (2026-11-21); rejection-reply paths live-verified in tasks 06/07 and pinned by CommentCommandProcessorTests

## What has been done (2026-11-21, agent)

- **`/status` + `/help` executed on the command pipeline** (`src/Hosts/Orchestrator/Commands/ControlReplies.cs`):
  after the existing authorization + admission (admission leaves control commands untouched), `/status`
  renders the 06 §7 draft format from current rows — Workflow (status + failure reason for `failed`),
  current task incl. `Run attempt: #N` (absolute attempt number, decision 79), Started/Duration, last
  error/result/reason line, Execution line (observed container state + heartbeat/progress, omitted when
  nothing observed), Queue (position = pending jobs ahead of this Workflow's pending job in the claim
  FIFO `available_at, id`; system-wide pending-jobs count) and the last completed Task with its recorded
  failure reason; a WorkItem without a Workflow is answered `No agent workflow started.`. `/help` lists
  only commands the pipeline would accept right now (target type, repository permitted set, Workflow
  state: closed item, failed/closed Workflow → control commands only, unfinished Task → no task-producing
  commands, `/code` needs a plan artifact (decision 80), `/retry` needs the last non-cancelled Task
  `failed` + pinned profile usable (06 §9a)). The command row becomes `processed`.
- **Owner rulings recorded** (decided with the owner before implementation; not in the frozen spec):
  1. "profile-gated" in 06 §9 = strict would-be-accepted-now: a task-producing command whose pinned
     version is `draft`/`building`/`build_failed` is omitted too — on the current live config (planner
     version `draft`) `/help` on an Issue lists `/status`, `/stop`, `/help` only.
  2. While the repository is paused, task-producing commands (and `/retry`) are omitted from `/help`
     (they would be `repository_paused`-rejected); `/status`/`/stop`/`/help` themselves stay available
     (decision 75, unchanged).
  3. `POST /internal/commands` skips the GitHub collaborator-permission lookup (the endpoint is
     service-token protected; the config-derived allowlist still applies).
  4. `commentId` must be a positive integer — it becomes the bigint `agent_command.github_comment_id`
     idempotency key (04 §2's `"github-comment-987"` is illustrative).
- **Orchestration API + test hook** (`src/Hosts/Orchestrator/Api/OrchestrationApi.cs`, mapped in
  `src/Hosts/Orchestrator/Program.cs`): `GET /api/v1/work-items/{id}`, `GET /api/v1/workflows/{id}`,
  `GET /api/v1/tasks/{id}` per 04 §2 (Guid ids; the exact transport was left open in the spec),
  `POST /internal/commands` ingesting a synthetic comment through the identical pipeline
  (CommandIngestion → allowlist → admission → control reply → Outbox), idempotent by `commentId`,
  503 `not_ready` before configuration sync, 404/400 with the 04 §7 error shape
  `{code, message, details}`. All four routes are service-token guarded (`X-Service-Token`,
  decision 65) via the existing Platform filter — guard kept per owner ruling.
- **Shared stage extracted**: the task-06 parse + policy stage moved into
  `src/Hosts/Orchestrator/Commands/CommandIngestion.cs` so both ingestion paths cannot drift; the
  routing/profile resolution moved into `src/Hosts/Orchestrator/Orchestration/ProfileGate.cs`, shared
  by admission and the /help filter (`CommandAdmission.LatestPlanArtifactIdAsync` likewise shared).
  One earlier pin superseded: `/status` on a paused repository now completes (`processed` + reply)
  instead of stopping at `authorized` — that is precisely this ticket's decision-75 checkbox;
  `CommentCommandProcessorTests.PausedRepository_StatusAuthorized` was updated accordingly
  (`PausedRepository_StatusStillProcessed`).
- No DB migration, no new NuGet packages, no new published port (owner ruling: test via
  `docker compose exec orchestrator curl …` over the private network; README updated).
- Tests: 16 new `ControlCommandTests` (draft format incl. attempt number + queue position, last-completed
  section, failed-workflow reason, help filtering matrix incl. not-ready version + pause + failed
  workflow, `/stop` untouched, one reply per comment on replay) and 13 `OrchestrationApiTests` (view
  shapes, 404 error shape, 401 guard, injected `/status` end-to-end to Outbox + idempotency, injected
  `/plan` admission, context-only → `ignored`, validation errors, `not_ready` 503, repository mismatch).
  Suite green: **444/444** via `dotnet test src/PiAgent.slnx` (Architecture 2, Platform 9,
  Infrastructure 42, Hosts 391); `dotnet build src/PiAgent.slnx` clean.

## How to manually test

> **Verification state:** automated verification complete (444/444, see above). **Live, owner-run
> 2026-11-21:** path A steps 1–2 — `/status` (no-workflow case, no rows created) and the filtered
> `/help` replies appeared as real comments on the configured Issue. Everything else below remains
> unexecuted in this session (would need the stack, owner permission, and real comments).

Prerequisites:
- `task stack-rebuild` (rebuild images from this branch), then `task stack`; wait for `--wait` green.
- The configured repository (currently `aliaksandrZh/04`) has an open Issue and you can comment it.
- Current config state: planner profile exists (task 02's `routing.yaml`), its `AgentProfileVersion`
  sits in `draft` until ticket 10 builds the image — this demonstrates the /help ruling exactly.

**Path A (primary, production): just comment on the Issue.** Every control command is usable as a
normal comment — mention required in V1: `@<bot> /status`, `@<bot> /help`.

1. Comment `@<bot> /status` on the open Issue (nothing started for it).
   - expected within ~1–2 min (poll_interval 60 s + processing): a new bot comment reading exactly
     `No agent workflow started.`; nothing else changes — no Workflow/Task/Run/Job rows created:
     ```sh
     docker compose --env-file src/.env -f src/compose.yaml logs outbox-worker --since 5m | grep delivered
     #   expected: outbox comment delivered <action_id> to <owner>/<repo>/<number> (attempts 1)
     ```
2. Comment `@<bot> /help` on the same Issue.
   - expected reply lists `/status`, `/stop`, `/help` only (planner version `draft` → `/plan` omitted,
     ruling 1). To see `/plan` appear: flip the planner version (one-time data change, same as the
     task-07 walkthrough:
     `UPDATE agent_profile_version SET state='ready' WHERE agent_profile_id=(SELECT id FROM agent_profile WHERE name='planner') AND version=1;`),
     then comment `@<bot> /help` again — expected: `/plan` listed as the fourth command.
3. With a workflow running (comment `@<bot> /plan` after the flip), comment `@<bot> /status` again:
   expected the full draft format — `Workflow: active`, `Current Task:` with
   `- Type: plan | Status: … `, `Run attempt: #N`, `Queue:` line, and `Last completed Task:` when one
   exists.

**Path B (hook verification — checklist item `POST /internal/commands`):** same outcomes without
GitHub: no poll delay, no collaborator requirement. Fetch the work-item UUID:
`docker compose --env-file src/.env -f src/compose.yaml exec -T postgres psql -U pi_agent -d pi_agent -t -c "SELECT id, number FROM work_item WHERE type='issue' ORDER BY number DESC;"`
then run:
   ```sh
   docker compose ... exec -T orchestrator curl -s \
     -H "X-Service-Token: $SERVICE_TOKEN_ORCHESTRATOR" -H 'Content-Type: application/json' \
     -X POST http://localhost:8080/internal/commands \
     -d '{"workItemId":"<work_item_uuid>","author":"<collaborator>","commentId":9101,"text":"@<bot> /status"}'
   #   expected: {"commandId":"<uuid>","status":"processed"}; the same reply is published via the Outbox
   #   re-POST with the same commentId → same commandId again, no second reply (idempotent by commentId)
   ```
4. **Orchestration API reads** (after a `/plan` from either path):
   ```sh
   docker compose ... exec -T orchestrator curl -s -H "X-Service-Token: $SERVICE_TOKEN_ORCHESTRATOR" \
     http://localhost:8080/api/v1/work-items/<work_item_uuid>      # {"id",…,"workflowId":null}
   docker compose ... exec -T orchestrator curl -s -H "X-Service-Token: $SERVICE_TOKEN_ORCHESTRATOR" \
     http://localhost:8080/api/v1/workflows/<workflow_uuid>        # {"status":"active","currentTaskId":…,"tasks":[…]}
   docker compose ... exec -T orchestrator curl -s -H "X-Service-Token: $SERVICE_TOKEN_ORCHESTRATOR" \
     http://localhost:8080/api/v1/tasks/<task_uuid>                # {"status":"queued","runs":[{"attemptNumber":1,…}]}
   # missing id → 404 {"code":"not_found",…}; no token header → 401
   ```
5. **Decision-75 check on a paused repository:**
   `UPDATE repository SET is_enabled=false;` → `/status` still replies (processed); `/help` reply omits
   `/plan`; a `/plan` comment is rejected `repository_paused`; `UPDATE repository SET is_enabled=true;` (re-enable).
6. Teardown: `task stack-down`; verify nothing remains: `docker ps --filter name=pi-agent --format '{{.Names}}'` → empty.
