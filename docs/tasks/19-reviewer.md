# 19: /review + reviewer + PR review flows

**What to build:** The reviewer phase and both PR review flows. Explicit `/review`: pin `target_head_sha` + `target_base_sha` at Task creation, fresh read-only workspace at the pinned revisions (diff formed locally via Git — never via the PR files endpoint), reviewer profile with review/inline-comment MCP tools, submission as a GitHub review + inline comments replying in existing threads where possible. Automatic initial review: `review_requested` on a ready PR starts exactly one initial review per Workflow lifetime; on a draft PR the request is recorded but deferred — on the draft→ready transition the initial review starts only if the request is still active, none was created yet, and no explicit `/review` already ran; removal of the request before execution (detected via the `review_request_removed` timeline event + requested_reviewers snapshot diff) cancels the not-yet-started Task with `review_request_removed_before_execution`; SHA drift before a queued Task starts cancels with `review_head_changed_before_execution` + system comment. Fork PRs reviewable: checkout from the PR head ref of the base repository at the pinned SHA into a fresh read-only workspace; fork code untrusted (its scripts/builds/tests never executed); unresolvable head → Task fails `review_source_unavailable`; coder never works against forks. New commit alone (`pull_request_synchronize`) never triggers a review.

**Spec:** `docs/pi-agent-architecture/06-command-reference.md` §5 (/review), §10 (thread context), §11 (automatic first review); `01-architecture-and-flows.md` §7 (PR flow); `04-api-contracts.md` §5 (reviewer data plane); `02-data-model.md` §16 (reviewer workspace); `05-v1-decisions.md` decisions 16, 17, 31, 45, 46, 64, 74.

**Blocked by:** 12, 11, 11b, 10, 05

**Status:** ready-for-agent

- [ ] Bot requested as reviewer on a ready PR → automatic initial review exactly once per Workflow lifetime; `Workflow.initial_review_created_at` stays set even if the Task is later cancelled (`02 §9`)
- [ ] Request on a draft PR deferred; on Ready it starts under the three conditions; an explicit `/review` suppresses the duplicate automatic review
- [ ] Explicit `/review` → new review Task with both SHAs pinned; fresh read-only workspace; review + inline comments submitted via MCP (transient MCP failures retried within the Run with GitHub verification before re-posting — decision 42)
- [ ] The review report/Artifact explicitly records which SHA was reviewed (`target_head_sha`, `04 §5`)
- [ ] Head-SHA drift before a queued Task starts → `review_head_changed_before_execution` cancel + system comment asking for `/review` again
- [ ] Review-request removal before execution → `review_request_removed_before_execution` cancel; an executing review is not interrupted
- [ ] Fork PR → read-only checkout at the pinned SHA; fork code never executed; unresolvable head → `review_source_unavailable`
- [ ] A new PR commit alone never triggers a review; each explicit `/review` is a distinct request (deduped by comment ID)
- [ ] Acceptance scenario "Reviewing" (`05` matrix) passes

---

## Implementation status (branch `task-19-reviewer`, uncommitted — pending user review)

### What was done
- **Schema (approved, additive):** `orchestration_task.trigger_github_event_id` (nullable, unique where set,
  FK → `github_event`). Migration `AddReviewTriggerEvent`. Existing rows get NULL; no data change, no drop.
- **Automatic initial review** — `Hosts/Orchestrator/Orchestration/ReviewTriggerCycle.cs`:
  `review_requested` on a ready PR → one review Task (once per Workflow via `Workflow.InitialReviewCreatedAt`);
  on a draft PR → deferred; draft→ready (`work_item_updated` with `draft` in `changed`) → starts under the
  three conditions (request still active, none created yet, no explicit `/review`); unfinished-Task invariant respected.
- **Withdrawal before execution** — `JobQueueWorker`: a queued automatic review is cancelled with
  `review_request_removed_before_execution` when the bot is no longer a requested reviewer (live recheck).
  Notice text added in `ClaimCancellationNotices`. Executing reviews are not interrupted.
- **Head drift** — existing `review_head_changed_before_execution` path now also covers automatic reviews.
- **Reviewer profile** — `src/config/agents/reviewer/` (own Dockerfile, `entry`, `profile.yaml`, `mcp.json`,
  `APPEND_SYSTEM.md`, runtime-bridge copied byte-identical). Own `git clone` path; shares no code with planner/coder.
  MCP allow-list (verified against GitHub MCP v2.2.0 source): `pull_request_read`, `pull_request_review_write`,
  `add_comment_to_pending_review`, `add_reply_to_pull_request_comment`, `issue_read`, `add_issue_comment`.
- **Entry** — clones the base repo, fetches the PR head via `refs/pull/<n>/head` (fork-safe), checks out the
  pinned `REVIEW_HEAD_SHA`, forms `base...head` locally into `.review/diff.patch`, makes the tree read-only,
  fails with `review_source_unavailable` (exit 75) when the head cannot be fetched.
- **Runtime** — review Tasks pass `REVIEW_HEAD_SHA`, `REVIEW_BASE_SHA`, `REVIEW_PR_NUMBER` into the container.

### Tests
- `ReviewTriggerTests` (11): once-per-lifetime, draft deferral, draft→ready (with/without request), explicit-review
  suppression, non-draft edit ignored, withdrawal before start (through the claim path), other-reviewer ignored,
  new commit alone no review, config-sync gate, automatic head-drift cancel + notice.
- Mutation-checked: the five load-bearing rules (once-per-lifetime, draft deferral, draft-flag check,
  explicit suppression, request-active check) each fail a test when disabled, and pass when restored.
- `ShippedConfig_ParsesStrictly` now asserts the reviewer parses; confirmed it fails when the profile is absent.
- Runtime pins: `ReviewTask_TravelsPinnedShas_AndPrNumber`, `NonReviewTask_CarriesNoReviewPins`.

### Evidence (commands actually run)
- `dotnet build PiAgent.slnx` → Build succeeded, 0 errors.
- `dotnet test tests/Hosts.Tests` → Passed 567/567 (two consecutive runs).
- `dotnet test tests/Infrastructure.Tests` → 47/47; `tests/Platform.Tests` → 9/9; `tests/Architecture.Tests` → 2/2.
- `docker build -t agent-reviewer:verify config/agents/reviewer` → image built (then removed).
- Inside that image: no token → refuses clone (exit 2); missing pins → refuses (exit 2); no repo → passthrough exec (exit 0).
- Local `file://` simulation of the entry's fork-safe sequence: fetch `refs/pull/7/head`, pinned checkout,
  `base...head` diff (14 lines), read-only applied — all succeeded.

### Known gaps / not yet done (be explicit)
- **Read-only** is enforced by the entry's `chmod` after checkout only; the Docker `/workspace` mount is still
  writable. "Read-only where possible" per spec — a read-only mount is a follow-up.
- **Reviewer MCP run-time behavior, the review artifact/manifest handling, and `review_source_unavailable`
  reporting to the Task are NOT verified end-to-end.** The Bridge/Orchestrator consumption of `result.json`
  (`requiredPublications` kind `review`) was not extended; the publication-kind check for `review` needs confirmation.
- **Not started:** the coder (task 18) is not on `main`; reviewer does not depend on it (per decision).
- **Live verification on PR #13 is NOT done** — requires starting the stack (needs your explicit approval).
- `ContextAssembler` review branches and `ReviewTriggerCycle` are unit/integration-tested against Postgres,
  not against a running Orchestrator process.
- Intermittent Hosts.Tests failures (4–33 failing) were observed once early on and did not reproduce in 10+
  subsequent runs; root cause not identified.

### Manual test (only commands actually executed; everything else is unverified)
Prerequisites: .NET 10 SDK, Docker, `src/` checked out on branch `task-19-reviewer`.
1. `cd src && dotnet build PiAgent.slnx` → expect `Build succeeded`.
2. `dotnet test tests/Hosts.Tests --filter "FullyQualifiedName~ReviewTriggerTests"` → expect `Passed! … Passed: 11`.
3. `cd config/agents/reviewer && docker build -t agent-reviewer:verify .` → expect image built.
4. `docker run --rm -e GIT_CLONE_REPO=o/r -e REVIEW_HEAD_SHA=abc -e REVIEW_BASE_SHA=def agent-reviewer:verify true`
   → expect `exit=2` (refuses without token).

### Live verification (2026-11-22, PR aliaksandrZh/04#13 — evidence from real runs)
- `task stack` + Komodo compose started; config sync built `agent-reviewer:v1` (`state = ready` in DB).
- Posted `@jsxhuz /review` (comment 7078844823) → command `queued`, review Task created with BOTH SHAs
  pinned to PR #13's head/base (`target_head_sha = 4b0e4c12…`, from_command = true).
- First dispatch failed on missing Komodo (`komodo-core:9120` unresolved — environment, not code); after
  starting the Komodo stack the retry dispatched: container `agent-reviewer:v1` ran, entry logged
  `checked out 4b0e4c12… (base f916007…)` — pinned fork-safe checkout + local diff, live.
- Run 1 (v1 image) submitted a real GitHub review naming the reviewed SHA, then failed `manifest_invalid`
  (artifact type `review` not in the bridge vocabulary) → fixed prompt to `review_report` → v2.
- Run 2 (v2) still `manifest_invalid`: `requiredPublications` lacked `toolCallId` → prompt fixed to copy
  the tool-call id (planner wording) → v3. NOTE: run 2 posted TWO reviews 9 s apart (a real duplicate
  submission inside one run — decision-42 violation by the agent); the v3 run did not duplicate.
- Run 3 (v3) **succeeded end-to-end**: Run `succeeded`, Task `succeeded`, `review_report` artifact
  persisted, exactly ONE review (12:21:20Z) with 11 inline comments at the pinned commit, one of them a
  reply in an existing thread (`in_reply_to` present). Reviews persist: 12:08:28, 12:14:17, 12:14:26, 12:21:20.
- Three `/review` comments created three distinct review Tasks (dedup by comment ID holds; each explicit
  `/review` is a distinct request).

### Still unverified (cannot be exercised with one GitHub account)
- **Automatic initial review start**: `gh pr edit --add-reviewer` of the PR author (jsxhuz) is silently
  rejected by GitHub (`requested_reviewers` stays empty), so no `review_requested` event fires. The rule
  is covered by the Postgres-backed `ReviewTriggerTests` (incl. mutation checks), not by a live run.
- Withdrawal before execution, draft deferral, draft→ready: same limitation (integration-tested only).
- `review_source_unavailable`: entry exit 75 verified in the image; the mapping of a dead-before-callback
  container to the Task failure reason is NOT wired (currently generic `runtime_dispatch_failed`) — left
  unwired rather than guessing the Komodo status format; needs a decision.
- Read-only Docker mount for the reviewer workspace (currently `chmod`-based after checkout; mount stays
  writable).
- Live GitHub review submission on PR https://github.com/aliaksandrZh/04/pull/13 (needs stack + your approval).
- Fork-PR checkout against a real fork.
- Reviewer container end-to-end through Komodo/Runtime API.
- `review_source_unavailable` surfaced on the Task (not wired into Task failure reasons yet).
