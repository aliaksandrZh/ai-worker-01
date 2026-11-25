# Agents never write to GitHub

The flow docs (`docs/flows/`) give agents read-only GitHub access: every publication —
the plan comment, review comments on the PR, bug reports on the issue, and PR
creation/updates — is performed by the Orchestrator on the agent's behalf, triggered by
an agent communication tool call (`save_plan`, `submit_review_comments`, `report_bug`,
`report_completion`). We chose orchestrated publication over direct agent writes so that
there is a single, auditable write path: an agent's run is validated by its tool calls,
agents hold no write credentials, and a misbehaving run cannot touch GitHub directly.

## Code delivery carve-out

The publication rule covers GitHub API entities. The working branch is the single
exception: the Coder alone holds the workspace, so it delivers the branch itself via
`git push` (git protocol, not the GitHub API) — the only direct write agents perform.
It requires exactly one write credential in the system: a repo-scoped, contents-only
PAT held by the Coder container (branch protection on the base branches contains the
blast radius; short-lived GitHub App installation tokens are the upgrade path). The PR
entity is still created/updated by the Orchestrator after the Coder's `run_completed` —
a PR cannot exist before its head branch.

## Considered Options

- **Direct agent writes via GitHub MCP** (the older PoC posture): fewer moving parts,
  but every agent holds a write PAT and the write path is per-agent — harder to audit
  and easier to misuse.
- **Orchestrated publication (chosen)**: agents signal through communication tools; the
  Orchestrator validates the completion call and publishes. Agents hold read-only API
  access only — the single exception is the Coder's git-scoped push credential
  (see carve-out).
