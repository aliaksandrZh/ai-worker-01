# 10: Build Worker + profile image pipeline

**What to build:** The Build Worker service executing Agent Profile image builds locally on the execution host via the asynchronous build contract (decision 81): `POST /internal/builds` (idempotent by `buildId`, immediate `202 Accepted`) with the payload from the Orchestrator (`buildId`, profile version, config snapshot, source commit SHA, build context reference) → local Docker build of `config/agents/<profile>/` (profile.yaml + Dockerfile + prompts/skills context) → tag `agent-<profile>:v<n>` (no registry) → durable local build-job state served through `GET /internal/builds/{buildId}` (status, then local image tag / image ID-digest / build-log reference). The version state machine (`draft → building → ready` / `build_failed`) is owned by the Orchestrator — the Worker never touches the control-plane database. The profile's Pi configuration is baked read-only at `/opt/pi-agent/profile` and selected via `PI_CODING_AGENT_DIR`; the Pi version is pinned at build time (never floating `latest`). Only `ready` versions are selectable; any configuration change means a new version.

**Spec:** `docs/pi-agent-architecture/01-architecture-and-flows.md` §2 (Build Worker), §3 (profile images); `02-data-model.md` §12, §12b; `04-api-contracts.md` §6 (Build Worker internal API); `05-v1-decisions.md` decisions 35, 54, 56, 81; `01 §15` Pi mapping (`PI_CODING_AGENT_DIR`).

**Blocked by:** 02

**Status:** ready-for-agent

- [ ] `POST /internal/builds` accepts a build and immediately returns `202 Accepted` with the `buildId`; repeated requests with the same `buildId` are idempotent and never start duplicate builds
- [ ] Status/result served via `GET /internal/builds/{buildId}`: success carries local image tag, image ID/digest and build-log reference; a real Docker build failure is a terminal failed result
- [ ] Build-job state is durable locally: results remain retrievable after Worker restart or network failure (retries reuse the same `buildId`)
- [ ] Orchestration side (decision 81): a durable BuildJob per version (`buildId` persisted before dispatch), version set `draft → building` at dispatch, status polling, exponential-backoff retries for transient errors (version stays `building`, `build_failed` only on real build failure), and the restart reconciler resumes unfinished BuildJobs with the same `buildId`
- [ ] The built image contains the pinned Pi version and the baked `/opt/pi-agent/profile` config directory
- [ ] Only `ready` versions are selectable by the Orchestrator; version state transitions are executed by the Orchestrator, not the Worker
- [ ] A changed `profile.yaml` always produces a new version (pinned SHA/snapshot changes)
- [ ] The version row records `bridge_configuration` (Bridge version + heartbeat/event cadence) and the image bakes that Bridge version (decision 54, `02 §12`)
- [ ] Build Worker exposes heartbeat health, secures both endpoints with a dedicated service credential (decision 65), and never accesses or modifies the control-plane database directly
