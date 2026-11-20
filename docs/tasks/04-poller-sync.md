# 04: Poller — work items and comments into PostgreSQL

**What to build:** The read-only Poller synchronizing Issues, PRs, issue/PR conversation comments, and PR inline review comments into WorkItem / GitHubComment / GitHubReview stores plus normalized `GitHubEvent` rows, using a single fine-grained read-only polling PAT for all configured repositories (decisions 50, 65). Per-repository/per-stream sync cursors (issues, pulls, issues/comments, pulls/{n}/comments, pulls/{n}/reviews) that advance only after durable save; external IDs / deterministic fingerprints for dedupe; conditional requests (`ETag`/`If-None-Match` — 304 does not consume the primary limit); `Retry-After`/`X-RateLimit-Reset` honoring with bounded backoff; page size 100 via `Link` headers; 5-minute overlap window; PR snapshot diffing (state, title, draft, labels, assignees, requested reviewers, merged_at) producing `work_item_*`, `pull_request_synchronized`, `review_requested`, and `review_request_removed` events per the normalized naming.

**Spec:** `docs/pi-agent-architecture/01-architecture-and-flows.md` §2 (Poller); `02-data-model.md` §3–§8 (entities, cursors, normalized event naming); `05-v1-decisions.md` decisions 4, 31, 39, 53, 78.

**Blocked by:** 02

**Status:** done-pending-review (plan: `docs/superpowers/plans/2026-11-20-poller-sync.md`, branch: `task-4-poller-sync`, commits `19d660e..287e330`)

- [x] Created/updated work items and comments appear in PostgreSQL with matching normalized events
      (`WorkItemSyncTests.*FirstRun/SecondRun*`, `CommentsSyncTests.FirstRun_CreatesCommentRows_AndCreatedEvents`)
- [x] A comment edit updates the stored `body` and emits `issue_comment_updated` / `review_comment_updated` (decision 78, `02 §5`)
      (`CommentsSyncTests.Edit_AppliesNewBody_AndEmitsUpdatedEvent`)
- [x] Re-polls and overlapping timestamp windows create no duplicates (external IDs/fingerprints)
      (`WorkItemSyncTests.Repoll_SameData_NoNewRowsOrEvents`, `CommentsSyncTests.Repoll_SameData_NoDuplicates` + fingerprint tests in `SnapshotDiffTests`)
- [x] A failing save leaves the cursor unadvanced (crash mid-batch keeps progress safe)
      (`WorkItemSyncTests.FailingSave_CursorUnadvanced_NoPartialRows` — SaveChanges interceptor + real rollback)
- [x] 304 Not Modified responses skip primary-limit consumption; rate limits stop and resume the cycle
      (`ReviewsSyncTests.NotModified_SkipsPrWithoutConsumingAnything`, `PollerCycleTests.RateLimit403_PausesCycle_WithoutAdvancingCursors` + `Resumes_AfterPauseElapses` + `PauseDelay_PrimaryLimitsHonored_SecondaryBounded`)
- [x] `review_requested` and `review_request_removed` events for the bot are detected (PR snapshot diff over `requested_reviewers` — one of the two detection sources, decision 31)
      (`SnapshotDiffTests.ReviewerSetDiff_*`, `WorkItemSyncTests.SecondRun_*`; payloads carry the reviewer login — bot filtering belongs to the Orchestrator via `github.bot_login`)
- [x] A PR Head-SHA change emits `pull_request_synchronized` (normalized naming, decision 78) and updates `head_sha`
      (`SnapshotDiffTests.HeadShaChange_EmitsSynchronized`, `WorkItemSyncTests.SecondRun_*`)
- [x] Submitted/dismissed PR reviews → GitHubReview rows + `pull_request_review_submitted` / `pull_request_review_dismissed` events (decision 78)
      (`ReviewsSyncTests.*`)
- [x] Acceptance scenario "Polling" — pagination, overlap, deduplication, restart recovery (`05` matrix) passes
      (`WorkItemSyncTests.FirstRun` pagination/`RestartRecovery_NewCycleInstance_NoDuplicates`; overlap/dedupe above)

## What was done

- **Stores (migration `AddPollerStores`):** `work_item`, `github_comment`, `github_review`, `github_event` entities per 02 §3–§8 — unique indexes (`(repository_id, number)`, `github_node_id`, `(kind, github_comment_id)`, `github_review_id`, `(source, dedupe_key)`), jsonb sync-metadata columns, tombstone columns present (sweep lands in task 05), `github_comment.github_review_id` as external ID without FK.
- **GitHub read client:** `Link`-header pagination, `If-None-Match`/304, rate-limit classification (`403`+`remaining:0` / `429` → exceptions carrying `Retry-After`/`X-RateLimit-Reset`), PAT never in messages/logs.
- **Startup:** §16 knobs from `config/system.yaml` (`poll_interval` 60 s, `poll_overlap` 5 m, `per_page` 100 — missing/malformed abort startup), `github-sync-pat.txt` (the read-only PAT, decisions 50/65), cursor JSON (timestamp position / per-PR etag map).
- **Streams (per repository, order `issues → pulls → pr_reviews → issue_comments → pr_review_comments`):**
  - `issues`: `state=all&sort=updated` + `since=position−overlap`; PR-marked payloads skipped (position still advances).
  - `pulls`: `state=all&sort=updated` (catches closed/merged PRs — no `since` on this endpoint; client-side overlap stop).
  - `pr_reviews`: one conditional request per **open** PR with per-PR etag in the cursor JSON; 304 = free skip; pending reviews emit no event until submitted; submitted/dismissed transitions emit normalized events.
  - comments (repo-wide `issues/comments` + `pulls/comments`): work-item resolution with **boundary holdback** — a comment whose WorkItem is not yet synced pins the cursor at the boundary (position = min over boundaries) so nothing is ever lost; edits update `body` + emit `*_comment_updated`.
  - Every fetched page persists in ONE transaction (rows + events + cursor advance + commit) — crash mid-batch leaves the cursor and partial rows untouched; dedupe via `dedupe_key` fingerprints (`wi:/ic:/rc:/rv:/prs:/rr:`) makes overlap windows and restarts idempotent.
  - Failure bookkeeping writes `last_attempt_at`/`last_error` on that stream's cursor only; other streams continue.
- **Cycle:** in-memory per-repo 60 s gate; rate limits (primary 403/429) pause the whole cycle honoring `Retry-After`/`X-RateLimit-Reset` (≤1 h) with bounded exponential backoff (15 s×2ⁿ, 5-min cap) for guidance-less 429s; decision-75 pause keeps polling; real `poller*` metrics wired (replacing task-01 placeholders); events written `processing_status=pending` for the Orchestrator (task 07).
- **compose.yaml:** poller mounts `./config` + `./secrets`, env `PI_AGENT_CONFIG_DIR`, `PI_AGENT_GITHUB_PAT_FILE=/etc/pi-agent/secrets/github-sync-pat.txt`.
- Detection engine (`SnapshotDiff` + mappers) is pure and emits decision-78 normalized events with deterministic fingerprints; PR heads are pinned on first observation (drift = `pull_request_synchronized`).

## How to manually test (verification levels marked per Implementation Instructions)

Verified during development (commands + observed outputs):

```sh
dotnet build src/PiAgent.slnx          # 0 errors, 0 warnings
dotnet test src/PiAgent.slnx           # Passed! 2 (Architecture) + 9 (Platform) + 22 (Infrastructure)
                                       # + 200 (Hosts.Tests) — Failed: 0
docker compose -f src/compose.yaml config --quiet   # compose wiring parses ("compose config OK")
docker ps                              # after runs: only pre-existing containers; nothing left running
```

NOT verified (requires a real fine-grained read-only PAT + a disposable GitHub repository; unverified — run when the stack is exercised):

```sh
printf '<read-only-pat>' > src/secrets/github-sync-pat.txt
task stack-rebuild && task stack
docker compose --env-file src/.env -f src/compose.yaml logs -f poller   # expect stream-level JSON logs + cursors advancing
# in the disposable repo: create an issue / PR / comment / review → check
docker compose --env-file src/.env -f src/compose.yaml exec postgres \
  psql -U pi_agent -c 'select event_type, count(*) from github_event group by 1;'
# rate-limit behavior: exhaust the PAT limit → expect rate-limited pause log, cursors intact, auto-resume
```

Prerequisites for the live path: `github.bot_login` in `src/config/system.yaml` matching the PAT identity (the Config Loader enforces this at Orchestrator startup), repository registered in `src/config/repositories.yaml`.

## Known limitations (accepted, documented in the plan's Review Focus)

- Snapshot-diff `review_request_removed` has no GitHub event ID: removing the identical reviewer set twice dedupes to one removal event. The timeline source (task 05, real event IDs) is authoritative thereafter.
- `pull_request_review_*` events rely on the per-PR review REST listing; a review submitted and dismissed entirely between two polling passes (≥60 s apart with the etag unchanged) can be missed until the next PR update re-opens the listing — resolved naturally by overlap windows in practice.
- PRs are fetched for reviews while open only; post-merge review changes are not re-synced.
- Backfill semantics: the first sync of pre-existing data emits `work_item_created` (+ `review_requested` for already-requested reviewers).
