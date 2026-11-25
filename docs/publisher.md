# Publisher — interface contract

> The EventLog consumer that performs every GitHub write ([ADR-0005](adr/0005-github-publisher.md)),
> implementing the publication rule of ADR-0001. **Interfaces only — no implementation.**

## Events

Consumed (emitted only by the Orchestrator and the notification emitter):

| Event | Kind | Covers |
|---|---|---|
| `publish_comment` | `plan` / `bug` / `notification` | plan comments, bug reports, system notifications — an issue or PR comment |
| `publish_pr` | `create` / `update` | PR creation after an `implement` Run; PR update on further rounds |
| `publish_review` | `submit` | review outcome: approve / request changes + inline comments |
| `publish_board_move` | — | Projects v2 card status (GraphQL option-id resolution inside) |

**No label writes.** AI-scope labels are read-only triggers; adding one is a human act.

## Idempotency key

Every `publish_*` event carries a deterministic key; the Publisher never performs the
same publication twice, whatever the redelivery count:

```
{owner}/{repo}[:{entityId}]:{kind}:{source}
```

- `entityId` — the GitHub entity reference when it exists (`#123` for issue/PR
  comments, `pr#N` for reviews/PR updates, the item node id for board moves); for PR
  creation it is absent (the PR does not exist yet — the key binds the attempt to the
  Workflow instead);
- `source` — the orchestration source of the artifact (`plan:{workflowId}`,
  `review:{runId}`, `notification:{taskId}:{kind}`, `pr:{workflowId}`,
  `board:{runId}`).

The key is checked against the marker table before the call; the marker is written in
the same transaction as the successful call.

## Error handling

Each `publish_*` event is **one atomic GitHub mutation** (one REST call or one GraphQL
mutation). There are no partial publications; multi-step flows (e.g. resolving a board
option id) are read-before-write and safe to re-run from scratch on retry.

| Error class | Examples | Reaction |
|---|---|---|
| Transport | network error, timeout | throw → visibility timeout → redelivery (`attempt` grows); after `MaxAttempts` → dead-letter |
| GitHub 5xx | 500 / 502 / 503 / 504 | same as transport |
| Rate limit | 403/429 with `X-RateLimit-Remaining: 0`, secondary limits, `Retry-After` | same as transport; the `VisibilityTimeout` knob is sized above a typical rate-limit window, so precision beyond "wait and retry" is not modelled |
| Gone | 404 (issue/PR deleted), 422 (invalid payload, head branch vanished) | dead-letter immediately — retrying cannot succeed; the event stays for inspection and replay |
| Permission | 401 (bad token), 403 (not rate-limit) | dead-letter immediately + one user-facing notification — a configuration incident, not a transient failure |

Dead-lettered publication is visible in the DB and replayable; the notification about
it is best-effort (a dead-lettered notification is recorded, not re-notified).

## Invariants

1. **Single write credential.** The Publisher holds the only GitHub write PAT (the
   Coder's git push credential is the other one, by ADR-0001's carve-out); read-only
   tokens stay with the readers.
2. **Ordered publication.** `publish_*` events ride the root item's stream — artifacts
   appear on GitHub in event order, consistent with orchestration, with no extra
   coordination.
3. **Key-once.** Same key → at most one publication, regardless of redelivery count.
4. **No label writes.** The Publisher cannot add, set or remove labels; the interface
   has no such kind.
5. **Emission is Orchestrator-only.** Agents never emit `publish_*`; their tool calls
   reach the Orchestrator as tool events, which decide and emit.

## Configuration knobs

| Knob | Meaning |
|---|---|
| `Workers:Publisher:Enabled` | run the publisher on this instance |
| `Publisher:PatFile` | the write credential (mounted read-only secret) |
| `VisibilityTimeout`, `MaxAttempts`, `Batch` | inherited from the EventLog contract |

## What it deletes from the codebase

`OutboxWorker/GitHub/GitHubWriteClient.cs` (moved inside as the one adapter) ·
`ListLatestCommentBodiesAsync` dedup hack · `BoardStatusOutbox` enqueue resolution ·
`ApplyAiStateLabelAsync` / `SetIssueLabelAsync` label write paths · review submission
through agent-side tools (returns under ADR-0001) · the Outbox store itself once
notifications become `publish_comment` events.
