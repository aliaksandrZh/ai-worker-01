# 19: /review + reviewer + PR review flows

**What to build:** The reviewer phase and both PR review flows. Explicit `/review`: pin `target_head_sha` + `target_base_sha` at Task creation, fresh read-only workspace at the pinned revisions (diff formed locally via Git — never via the PR files endpoint), reviewer profile with review/inline-comment MCP tools, submission as a GitHub review + inline comments replying in existing threads where possible. Automatic initial review: `review_requested` on a ready PR starts exactly one initial review per Workflow lifetime; on a draft PR the request is recorded but deferred — on the draft→ready transition the initial review starts only if the request is still active, none was created yet, and no explicit `/review` already ran; removal of the request before execution (detected via the `review_request_removed` timeline event + requested_reviewers snapshot diff) cancels the not-yet-started Task with `review_request_removed_before_execution`; SHA drift before a queued Task starts cancels with `review_head_changed_before_execution` + system comment. Fork PRs reviewable: checkout from the PR head ref of the base repository at the pinned SHA into a fresh read-only workspace; fork code untrusted (its scripts/builds/tests never executed); unresolvable head → Task fails `review_source_unavailable`; coder never works against forks. New commit alone (`pull_request_synchronize`) never triggers a review.

**Spec:** `docs/pi-agent-architecture/06-command-reference.md` §5 (/review), §10 (thread context), §11 (automatic first review); `01-architecture-and-flows.md` §7 (PR flow); `04-api-contracts.md` §5 (reviewer data plane); `02-data-model.md` §16 (reviewer workspace); `05-v1-decisions.md` decisions 16, 17, 31, 45, 46, 64, 74.

**Blocked by:** 12, 11, 10, 05

**Status:** ready-for-agent

- [ ] Bot requested as reviewer on a ready PR → automatic initial review exactly once per Workflow lifetime
- [ ] Request on a draft PR deferred; on Ready it starts under the three conditions; an explicit `/review` suppresses the duplicate automatic review
- [ ] Explicit `/review` → new review Task with both SHAs pinned; fresh read-only workspace; review + inline comments submitted via MCP
- [ ] Head-SHA drift before a queued Task starts → `review_head_changed_before_execution` cancel + system comment asking for `/review` again
- [ ] Review-request removal before execution → `review_request_removed_before_execution` cancel; an executing review is not interrupted
- [ ] Fork PR → read-only checkout at the pinned SHA; fork code never executed; unresolvable head → `review_source_unavailable`
- [ ] A new PR commit alone never triggers a review; each explicit `/review` is a distinct request (deduped by comment ID)
- [ ] Acceptance scenario "Reviewing" (`05` matrix) passes
