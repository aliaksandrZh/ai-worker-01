# pi-agent control plane

The real implementation of the event-driven design (ADR-0002..0007): one app host runs
the whole control plane as EventLog consumers. Repo overview + main flow: root `../README.md`;
component contracts: `../docs/{orchestrator,eventlog,run-worker,publisher,github-sync}.md`;
flows: `../docs/flows/`. The PoC lives in `../poc/` as frozen reference material.

## Stack (finalized)

**.NET 10** (ASP.NET Core minimal APIs) · **PostgreSQL** via Npgsql with the modules'
idempotent **bootstrap DDL** — no EF, no Migrator, the schema has no migration history
(ADR-0004 amendment) · **xUnit** (some suites spin a Testcontainers postgres — Docker
required for `dotnet test`) · single-host deployment: services speak on the private
compose network; CI (`.github/workflows/ci.yml`) runs the .NET suites and the bridge
node test suite on every pull request.

## Layout

| path | what |
|---|---|
| `Platform/` | cross-module plumbing: JSON logging + correlation, `/healthz` `/readyz` `/metrics`, worker heartbeat loop, Docker-socket readiness |
| `Events/` | the EventLog module: typed event vocabulary (`Events/`), `IEventLog` + Postgres adapter (SKIP LOCKED claim, per-stream order, visibility timeout, dead-letter + replay), in-memory adapter, consumer runner (`Delivery/`) |
| `Orchestration/` | the Orchestrator — a pure event reactor: Workflow → Task → Run state machines, admission, policies (plan approval, review rounds, run retries); Postgres + in-memory state adapters |
| `GithubSync/` | the Poller — cursor-diff GitHub polling; trigger detection at the edge (commands, labels, board columns; author permission travels in the payload); emits sync events + `user_command` |
| `Publisher/` | the only GitHub write path: a consumer of `publish_*` events, key-once idempotent; rate-limit handling lives here, nowhere else |
| `RunWorker/` | container provisioning + supervision: claims `run_scheduled`, writes the run tree (input.json pointers, secret binds), per-run tokens, the `POST /events` egress endpoint, the watchdog |
| `Hosts/AppHost/` | the single executable wiring all consumers behind worker gates (`Workers:*:Enabled`) |
| `tests/` | `Architecture.Tests` (module boundaries), `Events.Tests`, `RunWorker.Tests`, `Platform.Tests` |
| `Dockerfile` | one multi-stage build for the app host (`--build-arg SERVICE=AppHost`) |

Outside `src/` (repo root):

| path | what |
|---|---|
| `deploy/config/` | configuration-plane yamls: `system.yaml`, `repositories.yaml`, `routing.yaml`, `projects.yaml` — mounted read-only at the config root |
| `agents/` | the agents: per-agent `agent.json` (name, clone_mode, tools, prompt) + baked prompt; `agents/_runtime/` is the shared runtime — the Runtime Bridge modules, the communication-channel server, the ONE entry, the base Dockerfile |
| `deploy/` | `compose.yaml` (postgres + app-host + the observability stack, private network `pi-agent-net`), `.env.example`, logging config (`logging/`) |
| `secrets/` | host-managed secret files (gitignored), mounted read-only into app-host and bound into agent containers at provisioning |

## Quick start

```sh
task stack                # postgres → app-host (bootstrap DDL at startup) + logging stack; --wait gates a green start
                          # after any src/ change: task stack-rebuild first, then task stack
docker compose --env-file deploy/.env -f deploy/compose.yaml exec app-host curl -sf localhost:8080/healthz
task stack-logs           # each service logs JSON lines
open http://localhost:3000 # the same logs in one place: "Pi Agent Logs" dashboard (admin / GRAFANA_ADMIN_PASSWORD)
task stack-down
```

`task stack` copies `deploy/.env.example` → `deploy/.env` on first run. The app host
provisions agent containers itself over the Docker socket (`RunWorker`), so no external
orchestrator is involved; no host port is published for it (reach it through the compose
network — closes OBS-1).

Host-run debugging (Rider): `task dev-db` (postgres on 127.0.0.1:5432) + the AppHost run
configuration with `dev/env/shared.env` attached.

## Configuration plane

YAML in `deploy/config/` is the desired configuration; PostgreSQL holds the runtime state
(the EventLog + orchestration state + cursors + dead-letter + Publisher markers — nothing
else; there is no GitHub mirror, agents read GitHub themselves per ADR-0006).

- `system.yaml` — `github.bot_login` + operational knobs.
- `repositories.yaml` — registered repositories (allowlist semantics) + the default-branch
  map the Orchestrator reads for `RunInput.DefaultBranch`.
- `routing.yaml` — routing rules; the referenced version is pinned on the Task at creation.
- `projects.yaml` — Projects v2 boards: column/label triggers (`auto_plan`/`auto_code`/
  `auto_review`/`auto_fix`) and the review-loop cap.
- `agents/<name>/agent.json` — the agent's full tool catalog + clone_mode + prompt path;
  the single source for the generated `mcp.json` (built into the image).

Before `task stack`: create the secret files and align identities

```sh
mkdir -p secrets && printf '<your-pat>' > secrets/github-sync-pat.txt   # gitignored
#   → the PAT's login (GET /user) must equal github.bot_login in deploy/config/system.yaml
printf '<your-write-pat>' > secrets/github-write-pat.txt               # the Publisher's single write path
```

Agent profiles name their own secrets in `profile.yaml` (`secrets:`) — the Orchestrator
freezes the names into the Run at scheduling and the Run Worker binds the host files
read-only at `/run/secrets/<name>`: read-only PAT for planner/reviewer, the write PAT for
the coder (git push only, ADR-0001 carve-out). Missing secret file → fail-closed
(`run_failed`, cause `secret_missing`).

## Ports and exposure

| exposure | port | why |
|---|---|---|
| postgres | `127.0.0.1:5432` (dev only) | local pgAdmin/debugging |
| loki | `127.0.0.1:3100` | log lake |
| grafana | `127.0.0.1:3000` | the provisioned log dashboard |
| app-host, agent containers | private network only | no external exposure; health via `docker compose exec` |

## Metrics baseline

`/metrics` on the app host is a JSON baseline (`Platform/Http/PlatformEndpointExtensions.cs`):
liveness/readiness of the shared dependencies plus per-worker heartbeat state — a starting
point, not a metrics system; Grafana/Loki carry the observability load for now.

## Agent images

```sh
task build-agents          # agent-runtime-base:latest, then agent-<profile>:latest (idempotent, no version tags)
```

The shared runtime is assembled once as `agent-runtime-base` from `agents/_runtime/`
(pinned pi + bridge + the communication-channel bundle built at image build + the ONE
entry; OCI labels `pi.version` / `bridge.contract` stamped from `bridge.json` — a
disagreement fails the build). Each profile is a slim layer over it: `agent.json` + the
baked prompt (+ the .NET SDK for the coder); `mcp.json` is generated from the manifest at
build. The stub agent (also on the base) drives the failure/human levers
(`RUN_WORKER_STUB_BEHAVIOR`: `succeed | no_completion | escalate | bad_payload | …`).

## A Run end-to-end

`run_scheduled` (Orchestrator, all pointers filled from Workflow/Task state) → the worker
writes the run tree (`input.json` — pointers only) and binds the profile's secrets →
container `pi-session-<id>` (image `agent-<profile>:latest`) → the shared entry clones by
`clone_mode` (`plain` / `branch-continue` / `pinned-review` — the review base is derived
locally via merge-base) → the Runtime Bridge launches pi in `--mode rpc` with the pointers
prompt → every communication tool call the bridge observes becomes one deterministic
`tool` callback (`POST /events`, per-run token) → the worker translates it into the typed
event on the root item's stream (dedup by `eventId`; the full payload is the JSONB audit)
→ the Orchestrator reacts (publication via the Publisher, next stage, `waiting_for_human`,
policies) → `report_completion` observed → terminal `completed` (summary + git facts +
session path) → `RunCompleted` (carries Branch/CommitSha). A Run without the observed
completion call settles `run_failed` (`outcome_missing`) — the Orchestrator's retry
policy takes over.
