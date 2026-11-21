# 11: Context Assembler + RunContextSnapshot

**What to build:** The Orchestrator-side Context Assembler producing each Run's input within the size limits and freezing it as an immutable `RunContextSnapshot` (content_hash, included_sources, excluded_sources with per-element pruning reason). Assembly per task type: instructions + profile config, user command (+ trailing comment text as context), WorkItem (title/description/state/labels), comments chronologically with author+timestamp (deleted excluded), previous Tasks' Artifacts of the Workflow, repository/Git data. Reviewer-specific: `target_head_sha`/`target_base_sha`, unresolved review threads first, then remaining threads with state/answers/file-line bindings. Limits: the profile's mandatory `max_context_tokens` plus 256 KiB per artifact / 512 KiB artifacts total / 512 KiB serialized input; pruning priority — never drop the command, mandatory inputs, or the last approved plan; keep recent user answers and unfinished discussions; drop older/less-significant comments. Mandatory context that cannot fit → Run not started, Task fails with `context_too_large`. No silent truncation anywhere.

**Spec:** `docs/pi-agent-architecture/02-data-model.md` §14a (RunContextSnapshot), §17 (artifact limits); `06-command-reference.md` §3–§6 (per-command input), §10 (PR context); `04-api-contracts.md` §3 (input delivery), §5 (reviewer input); `05-v1-decisions.md` decision 63.

**Blocked by:** 07, 04

**Status:** done-pending-review (branch `task-11-context-assembler`, staged uncommitted pending review;
automated verification 540/540 green via `dotnet test src/PiAgent.slnx` — Architecture 2, Platform 9,
Infrastructure 47, Hosts 482, including 8 new ticket-11 Context Assembler tests — plus
`node --test src/config/agents/stub/runtime-bridge/*.test.mjs` 18/18. **Live walkthrough NOT executed**
(see Manual test below — nothing live was run; every command there is marked unverified until executed).)

Owner decisions recorded before implementation (collaborative):
- **Token estimate (OBS-6, owner-requested named helper):** V1 enforces `max_context_tokens` with
  `ContextTokenEstimator` (`src/Hosts/Orchestrator/Context/ContextBudget.cs`): tokens = `ceil(utf8_bytes / 4)`
  over the serialized input; recorded in `docs/observations.md` with a V2 revisit trigger (no tokenizer until
  model selection lands, decision 56).
- **Collect-everything simplification (owner):** V1 simply COLLECTS everything available — issue body, all
  synced comments, related WorkItem links, prior artifacts — no significance heuristics. Budget-driven
  pruning only: mandatory set never pruned, everything else kept newest-first, a section stops at its first
  overflowing element and everything older is excluded with the reason.
- **`context_too_large` fails the Task at claim, before any Run row exists** (no Run/Session/Container/
  Workspace/snapshot rows). Outbox notification deferred to ticket 15 (same deliberate deferral as
  `RunTerminalConsumer`'s run-side failures — the failure is recorded on Task/Job/Command rows).
- **Review-thread resolution gap (OBS-7):** REST-review-comment sync carries no resolved/unresolved data
  (GraphQL-only). Threads carry `resolved: null`; ordering treats unknown as unresolved so the decision-64
  hook activates the moment resolution state is synced.
- **Schema amendment (OBS-8):** `run_context_snapshot.content` moved jsonb → text (migration
  `20261121142504_SnapshotContentExactBytes`, `USING content::text`): a jsonb column re-serializes the JSON
  (key order/whitespace), which would break frozen-bytes identity — the content_hash, the byte budget and
  the `/input` payload must cover the exact same bytes. Reconciles ticket 09's incidental jsonb choice;
  existing dev rows cast losslessly; the Migration step applies it on the next stack start.

- [x] Plan-Task context contains Issue body + all comments (including earlier plan answers) chronologically, tombstones excluded (`ContextAssemblerTests.PlanTask_ContainsIssueBodyAndAllCommentsChronologically_TombstonesExcluded`: `[101, 102, 104, <seeded>]` chronological, tombstone `103` recorded as `deleted_comment` exclusion)
- [x] Reviewer context puts unresolved review threads first and includes the pinned Head/Base revisions for the runtime diff (`ContextAssemblerTests.ReviewTask_PinsHeadBaseRevisions_GroupsThreads_NewestFirst`: pinned `target_head_sha`/`target_base_sha` in the `task` section, threads grouped with state/answers/file-line bindings, newest-first; `resolved: null` — OBS-7)
- [x] `RunContextSnapshot` rows are immutable, hashed, and record included/excluded sources (`RunDispatcherTests.Handoff_PostsThe04Shape...` asserts content, 64-char sha256 and both source arrays; `ContextAssemblyFreezable.FreezeOrThrow` refuses to rewrite an existing row with a different hash; `content` stores byte-exact text)
- [x] Exceeding `max_context_tokens` or the serialized-input limit → Task fails `context_too_large`, Run never starts (`ContextAssemblerTests.MandatoryContextBeyondBudget_...` + `Claim_ContextTooLarge_FailsTheTaskTerminal_NeverPreparesARun`: Task `failed`, job/command `failed` with the reason, Workflow `waiting_for_human`, zero run/session/container/workspace/snapshot rows, `claimContextTooLargeTotal` metric)
- [x] The command, mandatory inputs, and the last approved plan are never pruned; every exclusion lands in the snapshot (`ContextAssemblerTests.BudgetPrunesOlderElementsNewestFirst_CommandAndPlanSurvive`: command comment + approved plan intact, newer comment kept, older one excluded with `pruned_by_limit`)
- [x] Coder context includes the last approved plan Artifact and current agent-branch info (`ContextAssemblerTests.CoderTask_CarriesApprovedPlanArtifactAndAgentBranchFacts`: `approved_plan` purpose + content embedded, Workspace branch/base/current SHAs in the `git` section, `log_reference` by reference only)

## What has been done (2026-11-21, agent)

- **`Hosts/Orchestrator/Context/`** (new): `ContextAssembler` (assembly + budgets + pruning + source
  bookkeeping — builds the v2 context package), `ContextBudget` (`ContextBudgets` constants +
  `ContextTokenEstimator` — the named bytes/4 helper, OBS-6), `ContextAssembly` (the frozen result +
  `ContextAssemblyFreezable.FreezeOrThrow` for the immutable per-Run snapshot), `ContextInstructions`
  (per-task-type directives).
- **Claim flow (ticket 11):** `JobQueueWorker` assembles after the permission recheck and BEFORE
  `RunPrepare`; a `context_too_large` assembly fails the Task terminal via
  `TaskLifecycle.FailTaskContextTooLargeAsync` with no Run rows; otherwise the preparation carries the
  assembly and the dispatcher freezes it (immutable, hash-idempotent) and calls `/start` with the same
  frozen content. New metric `claimContextTooLargeTotal` surfaced on `/metrics`.
- **Input shape (v2 context package):** `schemaVersion`, `profile{name,version}`, `task{type,targetShas}`,
  `repository{fullName,defaultBranch}`, `workItem{repository,number,type,title,body,state,author,labels,draft}`,
  `instructions`, `command`, `commandComment{author,githubCommentId,createdAt,text}`, `git{branch,baseSha,currentSha}`
  (implement tasks), `reviews[]` + `threads[]{state,path,line,resolved,comments[]}` (review tasks),
  `artifacts[]{id,taskSequence,type,purpose,format,createdAt,content,reference}`, `relatedWorkItems[]`
  (WorkItemLink), `conversation[]{githubCommentId,kind,author,createdAt,body}`. The ticket-09 keys
  `workItem`/`command`/`instructions` survive; `artifactReferences` is superseded by the embedded
  `artifacts[]` (04 §3 is conceptual — DTO naming not selected). The Runtime API start contract takes the
  package loosely as a JSON object (`JsonElement`), validated for object-ness only.
- **Bridge prompt rendering:** `renderPrompt` (both `config/agents/{stub,planner}/runtime-bridge/manifest.mjs`
  copies, kept byte-identical) renders the package sections 1:1 (no invention, no truncation); the profile's
  system prompt stays OUT of the input (the baked image is the sole effective configuration, decision 56;
  only profile identity travels).
- **Snapshot bytes:** `run_context_snapshot.content` jsonb → text with an `USING content::text` migration
  (OBS-8) so `/input` serves exactly the hashed/budget-checked bytes; `/start` `input` is asserted
  byte-identical to the frozen snapshot in `RunDispatcherTests`.

## Manual test

Prerequisites (from source README quick start): Docker running, `task agent-komodo` (Komodo UI; create the
API key), the two secret files (`src/secrets/github-sync-pat.txt`, `src/secrets/github-write-pat.txt`),
`src/config/system.yaml` `github.bot_login` equal to the PAT identity, a registered repository in
`src/config/repositories.yaml`, then `task stack`.

**Automated — EXECUTED (evidence):**
- `dotnet test src/PiAgent.slnx` → Architecture 2 passed, Platform 9 passed, Infrastructure 47 passed,
  Hosts 482 passed (`Passed!  - Failed: 0` on all four lines, 2026-11-21).
- `node --test src/config/agents/stub/runtime-bridge/*.test.mjs` → `tests 18 / pass 18 / fail 0`
  (includes the two new ticket-11 `renderPrompt` tests).
- The `USING content::text` migration ran for real inside the Hosts.Tests Postgres 17 fixture (every test
  class migrates an empty schema through the full chain; green ⇒ the ALTER executes).

**Live walkthrough — NOT EXECUTED (unverified; run with the owner):**
1. Happy path + snapshot inspection (stub profile routes `/plan`):
   - Post `@<bot> /plan some extra user context` on an open registered Issue with at least one other
     comment; wait for the run to settle (stub completes in seconds).
   - Inspect the frozen snapshot:
     `docker compose -f src/compose.yaml exec postgres psql -U pi_agent -d pi_agent -Atc "select content_hash, length(content) from run_context_snapshot order by created_at desc limit 1;"`
   - Verify content_hash = sha256 of the served bytes:
     `docker compose -f src/compose.yaml exec postgres psql -U pi_agent -d pi_agent -Atc "select content from run_context_snapshot order by created_at desc limit 1;" > /tmp/frozen.json && shasum -a 256 < /tmp/frozen.json`
     — expect the hash to match `run_context_snapshot.content_hash` of that row.
   - Verify `/input` serves the same bytes: use the per-Run bearer token from
     `src/storage/workflows/<workflow>/runs/<run>/callback-token` with
     `curl -s -H "Authorization: Bearer <token>" http://localhost:8080/internal/runtime/runs/<runId>/input`.
   - Expect: `content` JSON containing `schemaVersion:2`, the issue body, the comment list (the command
     comment flagged `commandComment: true`), empty `excluded_sources`, and `included_sources` rows for
     work_item/repository/command_comment/comment.
2. `context_too_large` claim-time failure (no Run rows):
   - Set `max_context_tokens: 900` in `src/config/agents/stub/profile.yaml`; `task stack` (restart → the
     Config Loader creates a new profile version and builds it).
   - Post `@<bot> /plan` on an Issue whose body is > ~4 KiB (e.g. paste a long text).
   - Expect: Orchestrator log line for the context failure, Task `failed` with `context_too_large`, job and
     command `failed`, Workflow back to `waiting_for_human`, NO rows in
     `run_context_snapshot`/`run`/`agent_session`/`container_instance`, `claimContextTooLargeTotal` = 1 on
     `docker compose -f src/compose.yaml exec orchestrator curl -s localhost:8080/metrics` (via `docker compose exec`).
   - `/status` on the issue shows the failed task with the recorded reason.
   - Revert the profile config afterwards; restarting creates a fresh version.
3. Reviewer context (only meaningful after ticket 19/12 wires real reviews; for now the sections exist):
   skip live, covered by `ContextAssemblerTests.ReviewTask_...` until a review Task can actually be queued.
