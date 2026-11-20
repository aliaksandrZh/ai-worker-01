# 02: Configuration plane + repository onboarding

**What to build:** YAML configuration (`config/system.yaml`, `config/repositories.yaml`, `config/routing.yaml`, `config/agents/<profile>/profile.yaml`) and the Config Loader as a startup component of the Orchestrator: validate the configuration, create Repository records + sync cursors, create AgentProfile/AgentProfileVersion records (state `draft`; build dispatch is Orchestrator-owned per decision 81 — the image build itself is ticket 10) pinning Git commit SHA / config snapshot / file hashes, and create the seeded global routing rules. Validate `github.bot_login` against the PAT identity (`GET /user`) with a mismatch failing startup as a configuration error. No hot reload, no admin API.

**Spec:** `docs/pi-agent-architecture/01-architecture-and-flows.md` §2 (Configuration plane); `02-data-model.md` §12, §12a, §21; `05-v1-decisions.md` decisions 55, 56 (records part), 66 (bot identity), 81 (records part); `06-command-reference.md` §1 (identity config).

**Blocked by:** 01

**Status:** done

- [x] Onboarding a repository via `repositories.yaml` creates the Repository row + per-stream sync cursors
- [x] `profile.yaml` creates AgentProfile + AgentProfileVersion rows with pinned commit SHA, config snapshot and file hashes; config changes produce a new version
- [x] Version rows are created in state `draft`; the Config Loader never dispatches or builds images — dispatch, retries and version state transitions are Orchestrator-owned (decision 81, ticket 10)
- [x] `routing.yaml` creates the seeded global rule set (`repository_id = NULL`) with per-repo override capability
- [x] `github.bot_login` mismatch with `GET /user` identity fails startup as a configuration error
- [x] Schema-invalid YAML fails startup with a diagnosable error and no partial DB rows
- [x] Cross-knob validation per `01 §16`: `poll_interval ≥ 30 s`, `heartbeat_interval < idle_timeout`, positive timeouts/windows, `per_page` within the GitHub API maximum, mandatory per-profile `max_context_tokens` (presence/positivity validated in V1 — the model-window fit check is V2, decision 56) — any violation fails startup with a diagnosable error
- [x] Cold-start gate (`01 §13`): until configuration synchronization completes successfully, the Orchestrator accepts no task-producing commands (rejected/deferred); `/status`, `/help`, and health endpoints remain available
- [x] Onboarding seeds each new repository's allowlist with the repository owner's account by default (decisions 7, 49)
- [x] `profile.yaml` bridge configuration (Runtime Bridge version + heartbeat/event cadence) is pinned into the AgentProfileVersion row (`02 §12`, decision 54)
- [x] Profile execution-limit defaults (`startup_timeout`, `max_run_duration`, `idle_timeout`) and `resource_configuration` (CPU/memory) from `profile.yaml` are pinned into the `AgentProfileVersion` row and validated: a profile version may lower these limits but can never exceed the system constraints (`01 §16`, `02 §12`, decisions 30, 54)
- [x] Re-processing unchanged configuration (same `config_hash`) creates neither a duplicate `AgentProfileVersion` nor a build dispatch (decision 81)
- [x] Acceptance scenario "Repository onboarding" (`05` matrix) passes — verified live 2026-11-20: YAML registered, loader log `created 1 (cursors 6), 1 new version(s)`, live psql counts 1/6, restart idempotent, negative path DB-untouched (see manual test); row-level shape pinned by `ConfigSyncServiceTests.Onboarding_CreatesRepositoryCursorsProfileVersionRules` over real postgres. Initial-sync semantics (NULL cursors) are ticket 04's input.

## Done (branch `task-2-config-plane`, 2026-11-20)

- Config plane in `src/config/` (`system.yaml`, `repositories.yaml`, `routing.yaml`,
  `agents/planner/profile.yaml` sample) — strict YamlDotNet parsing, §16 cross-knob validation,
  unit-string knobs (`60s`, `50MiB`, `2GiB`).
- Config Loader = Orchestrator startup component (`src/Hosts/Orchestrator/Config/`): parse →
  validate → `GET /user` identity check → `GET /repos/{owner}/{repo}` onboarding lookups →
  one-transaction DB sync ([ConfigSyncService]). Failure anywhere aborts Orchestrator startup
  with a diagnosable error; `/readyz` carries the `config-sync` gate (01 §13) for `compose --wait`.
- Schema: `repository`, `sync_cursor` ×6 (decision-53 streams, `cursor_value` NULL — bootstrap is
  ticket 04), `agent_profile`, `agent_profile_version` (`draft`, immutable snapshot = normalized
  YAML + per-file sha256 + file contents; `source_commit_sha` optional metadata per owner ruling),
  `agent_profile_routing_rule` — migration `AddConfigurationPlane`.
- 112 automated tests green (`dotnet test src/PiAgent.slnx`; Hosts.Tests needs Docker —
  Testcontainers postgres 17).
- Crash-exit hardening from the walkthrough: loader failure exits deterministically (diagnosable
  log + `Environment.Exit(1)` via an injectable hook), and compose services run under
  `init: true` so any unhandled startup exception terminates instead of spinning as PID 1.

### Manual test (owner)

> **Verified line-by-line on the owner's stack 2026-11-20** — every command below was executed as
> written (expected outputs are real ones from those runs). Step 3b needs a second PAT and was not
> executed — marked optional.

Prerequisites:
- After any `src/` change run `task stack-rebuild` once — it is the only step that rebuilds images (`task stack` alone reuses them).
- Step 0 below prepares and holds everything else: `src/.env`, the PAT file, `github.bot_login`, one registered repository.

**What each command does — in one line, before you run it:**

- `task stack` — bring up the stack: postgres → migrations → all services; on start the
  Orchestrator synchronizes the YAML configuration into the DB (the loader). Never rebuilds images.
- `docker compose logs orchestrator` — the loader's startup log: the line
  `configuration synchronized ...` means success; `configuration synchronization failed ...
  exceptionMessage` is the failure cause, naming the file and the violated rule.
- `docker compose exec orchestrator curl localhost:8080/readyz` — checks the gate INSIDE the
  orchestrator container (`localhost:8080` from the host is runtime-api; the orchestrator
  publishes no host port; `config-sync:"ok"` = the cold-start gate is open).
- `docker compose exec postgres psql ...` — inspect what onboarding wrote into the DB.
- `docker compose restart orchestrator` — restarts ONLY the orchestrator: the loader
  re-synchronizes the config (idempotently); used to verify that nothing new is created.
- `sed/mv` in step 4 — break and restore `system.yaml`; restore is `mv` from the backup
  because `src/config/*` files are untracked (no commits yet) and `git checkout` cannot bring
  them back.
- `task stack-down` — stop and remove the containers (the DB volume is kept); afterwards
  `docker ps` must show zero of our containers.

```sh
cd <repo-root>

# --- 0. prerequisites: env, PAT, bot login, ONE repository (do BEFORE the first start) ---
cp src/.env.example src/.env              # only if src/.env is missing; POSTGRES_PASSWORD required
mkdir -p src/secrets
printf '<PAT>' > src/secrets/github-sync-pat.txt   # a real PAT; its GET /user login = bot identity
$EDITOR src/config/system.yaml            # set github.bot_login to that login (exact match)
$EDITOR src/config/repositories.yaml      # replace `repositories: []` with ONE repo the PAT can read:
#   repositories:
#     - owner: <your-org>
#       name: <your-repo>

# --- 1. onboarding ---
task stack
#   expected: all containers Up; the sync line appears in the orchestrator logs.
docker compose --env-file src/.env -f src/compose.yaml logs orchestrator | tail -30
#   first run on an empty volume →  "repositories created 1 (cursors 6), 1 new agent profile version(s)…"
#   re-run on an already-onboarded volume → "repositories created 0 (cursors 0), 0 new…" (also success).
docker compose --env-file src/.env -f src/compose.yaml exec -T orchestrator curl -fsS localhost:8080/readyz
#   →  {"status":"ready","checks":{"postgres":"ok","config-sync":"ok"}}

# --- 2. DB contents (onboarding worked) ---
docker compose --env-file src/.env -f src/compose.yaml exec postgres psql -U pi_agent -d pi_agent -c "select full_name, is_enabled from repository;" -c "select count(*) from sync_cursor;" -c "select a.name, v.version, v.state, v.container_image, v.source_commit_sha from agent_profile a join agent_profile_version v on v.agent_profile_id = a.id;"
#   →  your repo lowercased | t   ·   6   ·   planner | 1 | draft | <empty> | <sha or empty>

# --- 3. idempotency: restarting the orchestrator must create nothing ---
docker compose --env-file src/.env -f src/compose.yaml restart orchestrator && sleep 5
docker compose --env-file src/.env -f src/compose.yaml exec postgres psql -U pi_agent -d pi_agent -t -c "select count(*) from agent_profile_version;" -c "select count(*) from sync_cursor;"
#   →  1  and  6  (the same counts)
docker compose --env-file src/.env -f src/compose.yaml logs orchestrator 2>&1 | grep synchronized | tail -1
#   →  "…repositories created 0 (cursors 0), 0 new agent profile version(s)…"

# --- 4. invalid-config rejection (§16) — startup abort, DB untouched ---
cp src/config/system.yaml /tmp/system.yaml.bak                       # backup (src/config files are untracked!)
sed -i '' 's/poll_interval: 60s/poll_interval: 10s/' src/config/system.yaml   # break the knob
docker compose --env-file src/.env -f src/compose.yaml restart orchestrator && sleep 5
docker compose --env-file src/.env -f src/compose.yaml ps | grep orchestrator
#   →  Orchestrator restart-loops/exits (restart loop) — the designed behavior
docker compose --env-file src/.env -f src/compose.yaml logs orchestrator 2>&1 | grep -o '"exceptionMessage":"[^\]*' | tail -1
#   →  "configuration validation failed with 1 violation(s):\nsystem.yaml: poll_interval (0.00:00:10) must be ≥ 30s"
docker compose --env-file src/.env -f src/compose.yaml exec postgres psql -U pi_agent -d pi_agent -t -c "select count(*) from agent_profile_version;"
#   →  1  (nothing was written)
mv /tmp/system.yaml.bak src/config/system.yaml                       # restore
docker compose --env-file src/.env -f src/compose.yaml restart orchestrator && sleep 5
docker compose --env-file src/.env -f src/compose.yaml exec -T orchestrator curl -fsS localhost:8080/readyz
#   →  config-sync:"ok"  (the gate is open again)

# --- 5. [OPTIONAL, not executed] wrong bot_login rejection — needs a PAT of another account ---
# printf '<other-pat>' > src/secrets/github-sync-pat.txt
# docker compose --env-file src/.env -f src/compose.yaml restart orchestrator && sleep 5
# docker compose --env-file src/.env -f src/compose.yaml logs orchestrator 2>&1 | grep bot_login | tail -1
#   →  "github.bot_login '<x>' does not match the PAT identity '<y>'"  (never the PAT value)
# printf '<PAT>' > src/secrets/github-sync-pat.txt

# --- 6. down ---
task stack-down
docker ps --format '{{.Names}}'
#   →  empty (our containers are removed; the DB volume is kept)
```
