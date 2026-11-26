# GitHub-native pi agent system

> **Event-driven design landed (ADR-0002..0006); the agents speak the flows contract**
> (`docs/flows/` — new tools, new control). One app host runs the whole control plane as
> EventLog consumers: **GithubSync** (trigger detection at the edge, cursor-diff — no
> GitHub mirror) → **Orchestrator** (a pure event reactor: Workflow → Task → Run state
> machines, admission, policies) → **Run Worker** (container provisioning, per-run tokens,
> `POST /events` egress) → **Publisher** (the only GitHub write path, key-once idempotent).
> The transport is a **Postgres Event Log** (`docs/eventlog.md`): at-least-once, per-stream
> order, visibility timeout, dead-letter. Agents read GitHub themselves and communicate
> only through **communication tools** — each call is exactly one event; the schema is
> owned by modules' idempotent bootstrap DDL. Flow docs: `docs/flows/`; component
> contracts: `docs/{orchestrator,eventlog,run-worker,publisher,github-sync}.md`.

A control plane that runs specialized pi coding agents on GitHub Issues and Pull Requests: users
command agents with slash commands in comments, an event log carries every signal, and
per-Run containers execute Pi sessions whose results flow back to GitHub. PostgreSQL is the
source of truth for orchestration state and the event log; GitHub remains the source of truth
for issues/PRs/comments/reviews; desired configuration is YAML in `deploy/config/` (+ `agents/`
manifests).

## Main flow

```
              ┌──────────────── private compose network pi-agent-net ────────────────┐
GitHub ──> GithubSync ──events──> EventLog (Postgres) <──events── Publisher ──> GitHub
  ▲          (cursor-diff,        │  ▲   per-stream order,     ▲   (publish_*,    (PR, comments,
  │           no webhooks;        │  │   visibility timeout,   │    key-once)      review, board)
  │           triggers at the     │  │   dead-letter)          │
  │           edge: commands,     ▼  │                         │
  │           labels, board     Orchestrator ──run_scheduled──> Run Worker
  │           columns)          (Workflow→Task→Run machines,     │  docker socket
  │                              admission, policies:            ▼
  │                              plan approval, round caps,   pi-session-<id> container
  └── agents never write        run retries)                  agent: bridge + communication tools
      to GitHub; git push is                       ▲                   │
      the coder's single carve-out                 └── POST /events ───┘  (route tool: one
                                                                    call = one event)
```

One comment travels like this:

1. **Trigger** — GithubSync pulls the configured repos (cursor-diff, no webhooks, tombstones
   for deletions) and detects triggers **at the edge**: a bot-mention slash command
   (`/plan`, `/code`, …), an AI-scope label (`AI:Plan`/`AI:Code`/`AI:Review`/`AI:Test`), or a
   board-column transition configured in `deploy/config/projects.yaml`. The author's
   permission travels in the `user_command` payload (ADR-0006) — the Orchestrator has zero
   GitHub knowledge. Review-loop caps and re-run rules are Orchestrator/board policy, not
   flow nodes.
2. **Admission** — the Orchestrator consumes events, gates on Workflow/Task state and
   policies, and schedules a Run: `run_scheduled` with the pointer set (item, PR, plan
   comment, command, branch/review pins) — no frozen context snapshot; agents read GitHub
   themselves.
3. **Provision** — the Run Worker claims the Run, writes the run tree (`input.json` —
   pointers only), binds the profile's secrets read-only, and starts the container
   (`agent-<profile>:latest` from the shared `agent-runtime-base`; the shared entry clones
   by `clone_mode`: plain / fix-round branch / pinned review).
4. **Execute** — the Runtime Bridge launches pi in `--mode rpc` and streams. Agents do
   their work with read-only GitHub tools + built-in file/git capabilities and talk back
   **only through communication tools** (`save_plan`, `report_blocker`,
   `submit_review_comments`, `submit_pr_description`, `report_tests_passed`,
   `report_completion`, …): each observed, error-free call becomes exactly one `tool`
   callback → one typed event on the root item's stream (dedup by deterministic `eventId`;
   the full payload lands in the JSONB audit). Publication is never the agent's act — the
   single carve-out is the coder's git push (ADR-0001).
5. **Control** — the Orchestrator reacts to the tool events: publishes the plan/PR/review
   through the Publisher, starts the next flow stage (plan → development → review →
   testing), parks escalations in `waiting_for_human`, applies round caps and run retries.
   A Run that ends without an observed `report_completion` settles `run_failed`
   (`outcome_missing`) and the retry policy takes over.
6. **Settle** — the terminal callback (`completed` with summary + git facts + session path,
   `failed`, `cancelled`) becomes the Run lifecycle event; the workflow advances or closes
   when the root item closes.

## Quick start

See `src/README.md` for the full walkthrough; the short version:

```sh
mkdir -p secrets               # sync PAT: its login must equal github.bot_login
printf '<pat>' > secrets/github-sync-pat.txt
printf '<write-pat>' > secrets/github-write-pat.txt
task stack                     # postgres + app-host (bootstrap DDL at startup) + logging stack
task build-agents              # agent-runtime-base + agent-<profile>:latest (idempotent)
docker compose --env-file deploy/.env -f deploy/compose.yaml exec app-host curl -sf localhost:8080/healthz
```

CI runs on GitHub Actions for every pull request (`.github/workflows/ci.yml`: the bridge
node suite → restore → build → `dotnet test` on the full solution). Development is local;
`dotnet test` (from `src/`) runs all suites (some spin a Testcontainers postgres, so Docker
must be running).
