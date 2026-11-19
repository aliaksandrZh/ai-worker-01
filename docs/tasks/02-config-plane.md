# 02: Configuration plane + repository onboarding

**What to build:** YAML configuration (`config/system.yaml`, `config/repositories.yaml`, `config/routing.yaml`, `config/agents/<profile>/profile.yaml`) and the Config Loader as a startup component of the Orchestrator: validate the configuration, create Repository records + sync cursors, create AgentProfile/AgentProfileVersion records (state `draft`; build dispatch is Orchestrator-owned per decision 81 — the image build itself is ticket 10) pinning Git commit SHA / config snapshot / file hashes, and create the seeded global routing rules. Validate `github.bot_login` against the PAT identity (`GET /user`) with a mismatch failing startup as a configuration error. No hot reload, no admin API.

**Spec:** `docs/pi-agent-architecture/01-architecture-and-flows.md` §2 (Configuration plane); `02-data-model.md` §12, §12a, §21; `05-v1-decisions.md` decisions 55, 56 (records part), 66 (bot identity), 81 (records part); `06-command-reference.md` §1 (identity config).

**Blocked by:** 01

**Status:** ready-for-agent

- [ ] Onboarding a repository via `repositories.yaml` creates the Repository row + per-stream sync cursors
- [ ] `profile.yaml` creates AgentProfile + AgentProfileVersion rows with pinned commit SHA, config snapshot and file hashes; config changes produce a new version
- [ ] Version rows are created in state `draft`; the Config Loader never dispatches or builds images — dispatch, retries and version state transitions are Orchestrator-owned (decision 81, ticket 10)
- [ ] `routing.yaml` creates the seeded global rule set (`repository_id = NULL`) with per-repo override capability
- [ ] `github.bot_login` mismatch with `GET /user` identity fails startup as a configuration error
- [ ] Schema-invalid YAML fails startup with a diagnosable error and no partial DB rows
- [ ] Cross-knob validation per `01 §16`: `poll_interval ≥ 30 s`, `heartbeat_interval < idle_timeout`, positive timeouts/windows, `per_page` within the GitHub API maximum, mandatory per-profile `max_context_tokens` (presence/positivity validated in V1 — the model-window fit check is V2, decision 56) — any violation fails startup with a diagnosable error
- [ ] Cold-start gate (`01 §13`): until configuration synchronization completes successfully, the Orchestrator accepts no task-producing commands (rejected/deferred); `/status`, `/help`, and health endpoints remain available
- [ ] Onboarding seeds each new repository's allowlist with the repository owner's account by default (decisions 7, 49)
- [ ] `profile.yaml` bridge configuration (Runtime Bridge version + heartbeat/event cadence) is pinned into the AgentProfileVersion row (`02 §12`, decision 54)
- [ ] Profile execution-limit defaults (`startup_timeout`, `max_run_duration`, `idle_timeout`) and `resource_configuration` (CPU/memory) from `profile.yaml` are pinned into the `AgentProfileVersion` row and validated: a profile version may lower these limits but can never exceed the system constraints (`01 §16`, `02 §12`, decisions 30, 54)
- [ ] Re-processing unchanged configuration (same `config_hash`) creates neither a duplicate `AgentProfileVersion` nor a build dispatch (decision 81)
- [ ] Acceptance scenario "Repository onboarding" (`05` matrix) passes
