# 04: Poller — work items and comments into PostgreSQL

**What to build:** The read-only Poller synchronizing Issues, PRs, issue/PR conversation comments, and PR inline review comments into WorkItem / GitHubComment / GitHubReview stores plus normalized `GitHubEvent` rows, using fine-grained read-only PATs. Per-repository/per-stream sync cursors (issues, pulls, issues/comments, pulls/{n}/comments, pulls/{n}/reviews) that advance only after durable save; external IDs / deterministic fingerprints for dedupe; conditional requests (`ETag`/`If-None-Match` — 304 does not consume the primary limit); `Retry-After`/`X-RateLimit-Reset` honoring with bounded backoff; page size 100 via `Link` headers; 5-minute overlap window; PR snapshot diffing (state, title, draft, labels, assignees, requested reviewers, merged_at) producing `work_item_*` and `review_requested` events per the normalized naming.

**Spec:** `docs/pi-agent-architecture/01-architecture-and-flows.md` §2 (Poller); `02-data-model.md` §3–§8 (entities, cursors, normalized event naming); `05-v1-decisions.md` decisions 4, 31, 39, 53, 78.

**Blocked by:** 02

**Status:** ready-for-agent

- [ ] Created/updated work items and comments appear in PostgreSQL with matching normalized events
- [ ] Re-polls and overlapping timestamp windows create no duplicates (external IDs/fingerprints)
- [ ] A failing save leaves the cursor unadvanced (crash mid-batch keeps progress safe)
- [ ] 304 Not Modified responses skip primary-limit consumption; rate limits stop and resume the cycle
- [ ] `review_requested` and `review_request_removed` events for the bot are detected (PR snapshot diff over `requested_reviewers` — one of the two detection sources, decision 31)
- [ ] A PR Head-SHA change emits `pull_request_synchronized` (normalized naming, decision 78) and updates `head_sha`
- [ ] Acceptance scenario "Polling" — pagination, overlap, deduplication, restart recovery (`05` matrix) passes
