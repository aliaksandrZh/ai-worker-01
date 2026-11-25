# GitHub Sync — interface contract

> The adapter at the GitHub side of the EventLog ([ADR-0006](adr/0006-github-sync.md)).
> **Interfaces only — no implementation.** It is the only module that knows GitHub's
> shape: REST polling, the command grammar, and the trigger vocabulary. It has zero
> flow knowledge and never writes to GitHub.

## What it does

Polls the configured repositories, diffs against per-stream cursors, and appends:

1. **Sync events** — raw normalized GitHub state changes (vocabulary below);
2. **`user_command`** — the derived event when a `comment_changed` carries a slash
   command: the Poller parses the grammar, resolves the command author's permission
   (one API call per command) and appends the typed event with the permission in the
   payload. Command parsing happens here and nowhere else.

## Sync events

| Event | Carries |
|---|---|
| `item_changed` | issue/PR: title, body, state (open/closed) |
| `item_closed` | explicit terminal beat when state → closed |
| `comment_changed` | added / edited / **deleted** (tombstone flag — deletions stay visible) |
| `review_added` | submitted review: verdict + inline comments |
| `pr_updated` | new commits on the PR (head sha) |
| `project_item_changed` | board card: column, link to its item |
| `labels_changed` | AI-scope labels — **read-only**, trigger material |

Stream key: the root item when derivable, otherwise a repo-level stream.

## Invariants

1. **Cursors, not mirrors.** Detection is a diff against cursors (new comments since
   `id`, head sha change, column change, label change) — the Poller never stores
   GitHub content, and Postgres gains no GitHub-shaped tables.
2. **Deletions are events.** A removed comment is a tombstone `comment_changed`,
   never a silent absence.
3. **Permission travels in the event.** `user_command` carries the author's resolved
   permission; the Orchestrator gates on the payload and never reads GitHub.
4. **The grammar lives here.** The trigger set (commands, labels, columns) is defined
   separately from the flows; when it changes, this module is the only code that
   notices.
5. **Read-only module.** Every GitHub call it makes is a read.
6. **Multi-instance safe.** Repositories distribute across instances through cursor
   claims (implementation detail behind the interface).

## Configuration knobs

| Knob | Meaning |
|---|---|
| `Workers:Poller:Enabled` | run the sync on this instance (standard consumer gating) |
| `PollInterval` | per stream-type polling cadence |
| repositories / projects / triggers | config plane (`repositories.yaml`, `projects.yaml`) |

## What it deletes from the codebase

The GitHub mirror: `GitHubComment`, `GitHubReview`, `GitHubEvent`, `WorkItem`,
`WorkItemLink`, `ProjectItem`, `AiScopeLabels`, `AiStateLabels` entities and their
sync/mapper code · `ContextAssembler`, `RunContextSnapshot`, byte-exact hashing and
token-budget machinery (OBS-6/OBS-8) · the claim-time permission recheck · the
Orchestrator's GitHub read clients.
