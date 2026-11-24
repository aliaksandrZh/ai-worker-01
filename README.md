# GitHub-native pi agent system

A control plane that runs specialized pi coding agents on GitHub Issues and Pull Requests: users
command agents with slash commands in comments, a poller syncs GitHub state into PostgreSQL, an
Orchestrator turns commands into Workflows → Tasks → Runs, and per-Run containers execute Pi
sessions whose results flow back to GitHub. PostgreSQL is the source of truth for orchestration
state; GitHub remains the source of truth for issues/PRs/comments/reviews; desired configuration
is YAML in `deploy/config/` (+ `agents/` profiles), synced into the DB only at startup.

## Main flow

```
                      ┌────────────────────── private compose network pi-agent-net ─────────────────────┐
GitHub ──REST poll──> Poller ─events─> PostgreSQL <────> Orchestrator ──build jobs──> Build Worker
  ▲                    (per-stream       (source of      │  │                          (docker build
  │                     cursors, no         truth)       │  └─ Context Assembler:      agent-<profile>:v<n>,
  │                     webhooks)                        │     freezes immutable        local image store)
  │                                                      │     RunContextSnapshot
  │                                                      ▼
  │              Outbox Worker <─notifications─ Orchestrator → Runtime API ──docker socket──> per-Run container
  │              (system comments,                      (Workflow/Task/Run      ▲         │
  │               retries, dead-letter)                  state machines,        │  Runtime Bridge:
  │                                                      one active task        │  pi --mode rpc,
  └── agents write via GitHub MCP + git push (in-container)                     │  callbacks → http
                      Host Monitor ──docker/storage telemetry→ Runtime API ─────┘  (no DB access)
```

One comment travels like this:

1. **Sync** — the Poller pulls configured repos from the GitHub REST API and persists normalized
   issues, PRs, comments, reviews, and timeline events into PostgreSQL (per-stream cursors,
   ETag conditional requests, tombstones for deletions). No inbound webhook/IP needed.
   Additionally, the Projects v2 pass lists the boards configured in `deploy/config/projects.yaml`
   (`/users/{owner}/projectsV2/{number}/items`, sync + logs scope) and emits `project_item_*`
   events for added/field-changed/removed items; a board with an `auto_plan` trigger in
   `deploy/config/projects.yaml` turns a matching status transition (e.g. Backlog → Clarifying)
   into a system-owned `/plan` on the item's work item — no comment needed. Task-producing
   signals (commands and system triggers) run only on items carrying the exact AI-scope label
   (`AI:Plan`/`AI:Code`/`AI:Review`/`AI:Test`, read from the synced labels); anything else is
   silently ignored, and the `LabelTriggerCycle` scanner starts the mapped task type when the
   label appears and all conditions converge — only if that task type never ran on the item
   before; re-runs need an explicit command (a standing label plus a fresh human comment is not
   a re-run trigger).
2. **Admission** — the Orchestrator consumes events, parses bot-mention slash commands
   (`/plan`, `/implement`, `/test-cases`, …), checks permissions, routing rules, and Workflow
   state, then creates a Workflow (one per root work item), a Task, and a first Run.
3. **Build** — each profile version starts `draft`; the Orchestrator dispatches it as a durable
   BuildJob to the Build Worker, which materializes the version's config snapshot and builds
   `agent-<profile>:v<n>` locally; the Orchestrator validates and flips it `ready` (or `build_failed`).
4. **Dispatch** — the Context Assembler assembles the Run's input (instructions, the user
   command, work item, comments, prior artifacts, repo facts) under token/size budgets and freezes
   it as a byte-exact, immutable `RunContextSnapshot`. The Runtime API provisions the session file,
   workspace, and container via Komodo and starts Pi in `--mode rpc`.
5. **Execute** — the Runtime Bridge inside the container supervises Pi, streams
   progress/heartbeat/completion/failure callbacks to the Runtime API, and captures logs. The
   agent itself reads/writes its GitHub items via the GitHub MCP server and pushes code with git.
6. **Settle** — artifacts and run results land in PostgreSQL; the Orchestrator decides the next
   transition (next task, retry, or `waiting_for_human`); orchestration-owned system notifications
   (task failure, rejections, cancellations) go out through the durable Outbox with retry and
   dead-letter handling. Workflow storage is cleaned up when the root item closes.


## Quick start

See `src/README.md` for the full walkthrough; the short version:

```sh
mkdir -p secrets               # sync PAT: its login must equal github.bot_login
printf '<pat>' > secrets/github-sync-pat.txt
printf '<write-pat>' > secrets/github-write-pat.txt
task stack                         # postgres → migrator → services (`task stack-rebuild` after src/ changes)
curl localhost:8080/healthz        # runtime-api is the only host-published port (8080)
```

CI runs on GitHub Actions for every pull request (`.github/workflows/ci.yml`: restore → build
→ `dotnet test` on the full solution). Development is local; `dotnet test` (from `src/`) runs all
suites (some spin a Testcontainers postgres, so Docker must be running).
