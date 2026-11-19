# 05: Poller extras — timeline events, WorkItemLink, deleted comments

**What to build:** The remaining Poller streams: per-issue timeline events (`cross-referenced`, `closed` with closing keywords) feeding `WorkItemLink` relations (`closes`, `related_to` — those two only in V1; branch names never create links alone); the daily full sweep for deleted comments (all conversation comments via issues/comments, all inline per open PR via pulls/{n}/comments) that tombstones a comment only on a confirmed direct 404 (403/rate limits/network errors never mean deletion), with its own run state that never touches the incremental cursors; `work_item_closed` with `close_reason = merged` for merged PRs.

**Spec:** `docs/pi-agent-architecture/02-data-model.md` §4 (WorkItemLink), §5 (tombstones); `05-v1-decisions.md` decisions 51, 77, 78; `01-architecture-and-flows.md` §2 (deleted-comment sweep).

**Blocked by:** 04

**Status:** ready-for-agent

- [ ] A PR closing an issue produces a `closes` WorkItemLink; a cross-reference produces `related_to`
- [ ] Branch naming (`agent/issue-<n>`) never creates a link on its own
- [ ] Deleted comment → `is_deleted` tombstone excluded from future Task context; 403/rate-limit outcomes do not tombstone
- [ ] The sweep has its own run state and does not advance incremental cursors
- [ ] Merged PR produces `work_item_closed` with `close_reason = merged` (no separate merged event)
