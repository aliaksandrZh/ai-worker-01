# 10: Build Worker + profile image pipeline

**What to build:** The Build Worker service executing Agent Profile image builds locally on the execution host: `agents/<profile>/` (profile.yaml + Dockerfile + prompts/skills context) → local Docker build tagged `agent-<profile>:v<n>` (no registry), version state `building → ready` / `build_failed` with the failure logged; the profile's Pi configuration baked read-only at `/opt/pi-agent/profile` and selected via `PI_CODING_AGENT_DIR`; the Pi version pinned at build time (never floating `latest`). Only `ready` versions are selectable; any configuration change means a new version.

**Spec:** `docs/pi-agent-architecture/01-architecture-and-flows.md` §2 (Build Worker), §3 (profile images); `02-data-model.md` §12 (version fields/state); `05-v1-decisions.md` decisions 35, 54, 56; `01 §15` Pi mapping (`PI_CODING_AGENT_DIR`).

**Blocked by:** 02

**Status:** ready-for-agent

- [ ] Creating a version triggers a build; success → `ready`, failure → `build_failed` with the logged reason
- [ ] The built image contains the pinned Pi version and the baked `/opt/pi-agent/profile` config directory
- [ ] Only `ready` versions are selectable by the Orchestrator; a failed build does not flip version state
- [ ] A changed `profile.yaml` always produces a new version (pinned SHA/snapshot changes)
- [ ] Build Worker exposes heartbeat health and never touches the control-plane database directly
