# 05: Poller extras — timeline events, WorkItemLink, deleted comments

**What to build:** The remaining Poller streams: per-issue/PR timeline events (`cross-referenced`, `closed` with closing keywords, `connected`, `disconnected`, `review_requested`, `review_request_removed`) — the first two feed `WorkItemLink` relations (`closes`, `related_to` — those two only in V1; branch names never create links alone; `connected`/`disconnected` are synced as raw timeline records without V1 link semantics); the daily full sweep for deleted comments (all conversation comments via issues/comments, all inline per open PR via pulls/{n}/comments) that tombstones a comment only on a confirmed direct 404 (403/rate limits/network errors never mean deletion), with its own run state that never touches the incremental cursors; `work_item_closed` with `close_reason = merged` for merged PRs.

**Spec:** `docs/pi-agent-architecture/02-data-model.md` §4 (WorkItemLink), §5 (tombstones); `05-v1-decisions.md` decisions 51, 77, 78; `01-architecture-and-flows.md` §2 (deleted-comment sweep).

**Blocked by:** 04

**Status:** done-pending-review (plan: `docs/superpowers/plans/2026-11-20-poller-extras.md`, branch: `task-5-poller-extras`)

- [x] A PR closing an issue produces a `closes` WorkItemLink; a cross-reference produces `related_to`
      (`TimelineSyncTests.ClosedWithClosingKeywords_CreatesClosesLink`, `TimelineSyncTests.CrossReference_*`)
- [x] Branch naming (`agent/issue-<n>`) never creates a link on its own
      (`TimelineSyncTests.BranchNamingAlone_NeverCreatesLink` — full negative through the issues/pulls/timeline streams, branch name present in the PR payload, no link-creating event)
- [x] Deleted comment → `is_deleted` tombstone excluded from future Task context; 403/rate-limit outcomes do not tombstone
      (`DeletedCommentSweepTests.Confirmed404_Tombstones_AndEmitsDeletedEvent`, `NonRateLimit403_NeverTombstones_SweepFails`, `NetworkFailure_NeverTombstones`, `PrimaryRateLimit_Propagates_NothingTombstoned`; context exclusion is the `is_deleted` flag consumed by ticket 11)
- [x] The sweep has its own run state and does not advance incremental cursors
      (`DeletedCommentSweepTests.Success_DoesNotTouchIncrementalCursors` — all six incremental `cursor_value`s asserted byte-identical before and after tombstone passes)
- [x] `review_request_removed` events are produced from the timeline (the event-based detection source for decision 31/`06 §11`)
      (`TimelineSyncTests.ReviewRequestEvents_ProducedFromTimeline`)
- [x] Merged PR produces `work_item_closed` with `close_reason = merged` (no separate merged event)
      (pre-existing task-04 coverage, re-run in the suite: `WorkItemSyncTests` merged assertions, `SnapshotDiffTests`)
- [x] The timeline stream has its own per-repository sync cursor (`issues/{n}/timeline` among the stream families, decision 53/`02 §8`)
      (`TimelineCursor` per-item entries asserted in `TimelineSyncTests`; the `timeline` cursor row is created by onboarding (`SyncCursorStreams.All`) and lazily by `SyncCursors.GetOrCreateAsync`)
- [x] `connected`/`disconnected` timeline events are synced as raw records and create no WorkItemLink rows (no V1 link semantics — decision 79)
      (`TimelineSyncTests.ConnectDisconnect_RawRecords_NoLinks`)
- [x] Acceptance tie-in: deleted-comment checks and WorkItemLink rows are asserted end-to-end in ticket 21 (scenarios "Polling" and "Cleanup") — noted there

## What was done

- **`WorkItemLink` store (migration `AddWorkItemLink`, additive):** table `work_item_link` with typed role
  columns `issue_work_item_id` + `pr_work_item_id` (user decision D1 — role follows the item's type instead of
  the conceptual source/target pair, so no direction convention; 02 §4 allows name/type adaptation), `relation_type`
  (`closes`|`related_to`), `source` (`github_closing_reference`|`github_cross_reference`), unique
  `(issue_work_item_id, pr_work_item_id, relation_type)`, both FKs → `work_item` cascade; self-relations impossible
  by type. Raw timeline `event_type` names (`cross-referenced`, `closed`, `connected`, `disconnected`) centralized
  in `GitHubTimelineEvents` (Infrastructure) for ticket 07.
- **`TimelineStream`** (order …→ `pr_review_comments` → `timeline`): visits an item's `issues/{n}/timeline` only
  when its synced `updated_at_github` moved past the stored visit (approved D3; closure/cross-ref/review-request/
  connect-disconnect bump it); walks ALL pages ascending (new events live on the last pages; no ETags — append-only
  growth can leave deep-page etags stale); per-event id position (`tl:{event_id}` dedupe, fingerprint fallback);
  persists the six in-scope event types as `source = timeline_api` rows (raw names, normalized names for the two
  review events which are identical strings); derives `related_to` from same-repository Issue↔PR cross-references
  (owner-issue and mirror both supported) and `closes` from issue-timeline `closed` events whose `commit_id` commit
  message carries closing keywords — STRICT (user decision D2 option A) — resolving the introducing merged PR via
  the approved ad-hoc `commits/{commit_id}/pulls` read (`merged_at`-filtered). Unresolved link events hold back the
  whole item visit (cursor entry frozen → revisit next cycle; comments-stream boundary pattern; raw rows persist
  immediately, dedupe makes re-walks idempotent). HTTP lookups always outside the page transaction.
- **`DeletedCommentSweepStream`** (order after `timeline`): own `sync_cursor` row `deleted_comment_sweep`
  (`LastSuccessAt` gates the 24 h daily cadence — hardcoded default per D4, no §16 knob; `cursor_value` stays NULL);
  full listings (no ETags — a stale page-1 etag could silently miss deletions) of all conversation comments plus
  inline comments per OPEN PR (D5); stored IDs missing from listings re-checked with the new direct
  `GitHubReadClient.GetSingleAsync`; ONLY a confirmed 404 tombstones (`is_deleted`/`deleted_at` + `*_comment_deleted`
  event, `source = full_reconciliation`, `ic:|rc:{id}:deleted` dedupe); 403/rate-limit/network never tombstone
  (rate-limit pauses propagate to the cycle like the other streams); the transaction applies tombstones + events +
  run-state success together, so a failed sweep persists nothing and retries whole; the six incremental cursors are
  never read or advanced.
- **Wiring/tests:** cycle order + Program.cs registration; timeline/introducer payload mappers (`GitHubMappers`);
  new JSONB payloads (`TimelineCrossReferencePayload`, `TimelineClosedPayload`, `TimelineRecordPayload`,
  `CommentDeletedEventPayload`); onboarding now creates 7 cursor rows per repository (was 6 —
  `ConfigSyncServiceTests` updated); `TimelineSyncTests` (16) + `DeletedCommentSweepTests` (12) over real postgres
  with a stubbed GitHub; `PollerStoresModelTests` work_item_link model tests.

**Notes for ticket 07 (flags agreed in task 05):** `github_event` will contain raw-name timeline rows
(`cross-referenced`/`closed`/`connected`/`disconnected`, `source = timeline_api`) — dispatch by (source, event_type),
treat raw/unknown types as recorded no-ops. The same logical review request produces both a `snapshot_diff` row
(task 04 fingerprint) and a `timeline_api` row (`tl:{event_id}`) — dedupe by (work item, reviewer) semantics when
reacting to decision-31 events; both `review_request_removed` rows mean one withdrawal.

**Recorded amendments to the spec:** (1) `work_item_link` uses typed role columns instead of
`source_work_item_id`/`target_work_item_id` (02 §4 preamble allows adaptation; decision-79 reserved relations
`created_from`/`blocks`/`custom` will need a later migration; same-type cross-references stay raw records, no link —
tested); (2) `closes` derivation is STRICT per the approved D2 option A — keywords in the closing commit's message
required; merge-commit merge messages lack keywords, so ticket 21's disposable-repo PRs must merge by SQUASH.

## How to manually test (executed during development — every output below is real)

```sh
dotnet build src/PiAgent.slnx        # Build succeeded. (0 errors, 0 warnings)

dotnet test src/PiAgent.slnx         # Passed! 2 (Architecture) + 9 (Platform) + 24 (Infrastructure)
                                     # + 228 (Hosts.Tests, incl. 16 TimelineSyncTests + 12 DeletedCommentSweepTests)
                                     # — Failed: 0

docker compose --env-file src/.env -f src/compose.yaml config --quiet   # compose config OK

docker ps                            # empty — nothing left running after the testcontainer runs

git diff --stat HEAD -- src   # real change-set output: "15 files changed, 390 insertions(+), 19 deletions(-)"
                              # plus 7 untracked (new) files — 5 src + 2 test (see git status --short)
```

The DB-backed tests ran migrations (including the new `AddWorkItemLink`) against a real postgres 17
testcontainer — the additive migration is exercised, no separate migrator step needed.

## Manual review guide (what to review, in order)

Command-free review (decisions ↔ code):

1. Decision ledger first: `docs/superpowers/plans/2026-11-20-poller-extras.md` — D1–D8 resolutions plus the two
   recorded spec amendments (typed role columns instead of `source_work_item_id`/`target_work_item_id` in 02 §4;
   STRICT closing keywords per the approved D2 option A).
2. `src/Infrastructure/Persistence/Entities/WorkItemLink.cs`, the model-config block in
   `src/Infrastructure/Persistence/AppDbContext.cs`, and migration `20261120141801_AddWorkItemLink.cs` —
   additive table only; unique `(issue, pr, relation)`; cascade FKs; no existing table touched.
3. `src/Hosts/Poller/TimelineStream.cs` — the class doc comment is the contract: visit gate (D3), full
   ascending walk with event-id positions (no ETags — R3 rationale), the six persistable event types,
   `related_to` from Issue↔PR cross-references (owner issue + PR mirror, same-repository guard), `closes` by
   STRICT keywords + ad-hoc `commits/{sha}/pulls`, holdback = frozen cursor entry with raw rows persisted.
4. `src/Hosts/Poller/DeletedCommentSweepStream.cs` — daily gate on its own cursor row (`cursor_value` NULL),
   full listings (inline scoped to open PRs), direct re-check tombstoning only on confirmed 404, one
   transaction for tombstones + events + run-state success.
5. `src/Hosts/Poller/PollerCycle.cs` (Order array + doc comment) and `src/Hosts/Poller/Program.cs`
   (registration) — the two streams ride the existing cycle/rate-limit machinery unchanged.

Review commands (already executed; outputs in the section above): `dotnet build`, `dotnet test`, and
`git diff --stat` — rerun them to reproduce the evidence.

## Live walkthrough for the reviewer — NOT executed during development (expectations unverified)

Prerequirements: a disposable GitHub repository registered in `src/config/repositories.yaml`; the read-only
polling PAT in `src/secrets/github-sync-pat.txt`; `github.bot_login` in `src/config/system.yaml` matching the
PAT identity (enforced at Orchestrator startup; current value `jsxhuz`); Docker running. Everything in this
section is a spec-behavior prediction, not an observed output — it becomes the ticket-21 "Polling"/"Cleanup"
acceptance basis. Standing caveats: R1 (whether every in-scope timeline event type bumps the item's
`updated_at`) and R2 (real `closed` payload fields, `commits/{sha}/pulls` shape) are only resolved by this
walkthrough.

```sh
task stack-rebuild && task stack                        # control plane incl. the two new poller streams
docker compose --env-file src/.env -f src/compose.yaml logs -f poller
```

Then, in the disposable repository:

1. Open an issue and a PR whose commit message contains `Closes #N`, then squash-merge the PR
   (⚠ merge-commit merges intentionally produce NO row — strict D2-A) → expect a `work_item_link` row with
   `relation_type = closes`, `source = github_closing_reference` within ~2 polling cycles.
2. Open a PR that mentions an issue → expect a `related_to` row with `source = github_cross_reference`;
   a PR referencing another PR, or an issue referencing an issue, or a cross-repository reference → a raw
   `cross-referenced` github_event row and NO link.
3. Request, then remove, the bot as reviewer → expect `review_requested` and `review_request_removed`
   github_event rows with `source = timeline_api` (alongside the task-04 `snapshot_diff` rows; dedupe is
   ticket 07's job).
4. Store a comment (sync it), then delete it on GitHub → expect the row tombstoned (`is_deleted = true`) by
   the next daily sweep plus an `issue_comment_deleted` event (`source = full_reconciliation`); inline
   comment on an OPEN PR → `review_comment_deleted`. Deletions of inline comments on closed/merged PRs are
   never discovered (D5, accepted).
5. Make the direct re-check ambiguous (e.g. revoke the PAT mid-sweep) → expect NO tombstone, the sweep
   cursor `last_error` set, and a retry once the cause clears (D6).

Check results with:

```sh
docker compose --env-file src/.env -f src/compose.yaml exec postgres \
  psql -U pi_agent -c "select relation_type, source, count(*) from work_item_link group by 1,2; \
  select event_type, source, count(*) from github_event group by 1,2 order by 1;"
```

Rows to look for in the second query: `cross-referenced` / `closed` / `connected` / `disconnected` (raw,
`timeline_api`), `issue_comment_deleted` / `review_comment_deleted` (`full_reconciliation`),
`review_requested` / `review_request_removed` from both `snapshot_diff` and `timeline_api`.
