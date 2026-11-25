# One GitHub Publisher: publication as consumed events

Writing to GitHub today happens from at least three adapters (`OutboxWorker`'s
`GitHubWriteClient` with rate-limit and dedup logic, `BoardStatusOutbox`'s enqueue-side
resolution, `Config/GitHubClient`), and review submission bypasses all of them through
agent-side tools — contradicting ADR-0001's "publication is the Orchestrator's
exclusive act". We replace this with one **Publisher** module: an EventLog consumer of
`publish_*` events carrying a coarse kind (`comment`, `pr`, `review`, `board_move`), a
deterministic idempotency key (including the GitHub entity reference), and the payload.
Rate-limit handling, GraphQL id resolution and dedup markers live inside the module;
the comment-body-dedup hack (`ListLatestCommentBodiesAsync`) is deleted.

Labels are **read-only**: AI-scope labels stay as triggers (the Poller keeps reading
them), the system never writes them — `ApplyAiStateLabelAsync` / `SetIssueLabelAsync`
paths are deletions.

Why: publication knowledge (endpoints, GraphQL schema, rate limits, dedup) gets one
locality; ADR-0001 finally holds without exception hunters; two adapters justify the
seam (REST/GraphQL in prod, fake in tests).
