# 10: Build Worker + profile image pipeline

**What to build:** The Build Worker service executing Agent Profile image builds locally on the execution host via the asynchronous build contract (decision 81): `POST /internal/builds` (idempotent by `buildId`, immediate `202 Accepted`) with the payload from the Orchestrator (`buildId`, profile version, config snapshot, source commit SHA, build context reference) → local Docker build of `config/agents/<profile>/` (profile.yaml + Dockerfile + prompts/skills context) → tag `agent-<profile>:v<n>` (no registry) → durable local build-job state served through `GET /internal/builds/{buildId}` (status, then local image tag / image ID-digest / build-log reference). The version state machine (`draft → building → ready` / `build_failed`) is owned by the Orchestrator — the Worker never touches the control-plane database. The profile's Pi configuration is baked read-only at `/opt/pi-agent/profile` and selected via `PI_CODING_AGENT_DIR`; the Pi version is pinned at build time (never floating `latest`). Only `ready` versions are selectable; any configuration change means a new version.

**Spec:** `docs/pi-agent-architecture/01-architecture-and-flows.md` §2 (Build Worker), §3 (profile images); `02-data-model.md` §12, §12b; `04-api-contracts.md` §6 (Build Worker internal API); `05-v1-decisions.md` decisions 35, 54, 56, 81; `01 §15` Pi mapping (`PI_CODING_AGENT_DIR`).

**Blocked by:** 02

**Status:** implemented on branch `task-10-build-worker` (staged uncommitted, pending owner review).
Automated verification: `dotnet test src/PiAgent.slnx` **518/518 green** (Architecture 2, Platform 9,
Infrastructure 48 — one new BuildJob model suite, Hosts 460 — BuildDispatchTests over the postgres
fixture + BuildWorker endpoint/runner/restart/tar/context suites + the env-gated live Docker Engine
round-trip `DockerEngineLiveTests`, which runs only with `PI_LIVE_DOCKER=1` and was executed:
TarBuilder → socket-HTTP build → image inspect → tag cleanup; daemon API 1.56 accepted unversioned
paths), plus node bridge tests 17/17 (stub dir). Live orchestration walkthrough recorded below with
real outputs.

Owner-approved design notes (recorded 2026-11-21 before implementation):
- **Build context comes from the pinned snapshot**, not a live Git mount: the dispatch payload carries
  the version's `config_snapshot`; the worker materializes + hash-verifies each file (the version stays
  rebuildable from the durable dispatch alone, per the task-02 owner ruling) so the built image always
  matches the pinned version even if `config/agents/<profile>` moved on between version creation and build.
- **The worker stays dumb** about image content: pinned Pi + baked `/opt/pi-agent/profile` + bridge
  version are profile-Dockerfile (config-plane) responsibilities; the worker validates only the
  spec result fields (tag/imageId/digest/log reference, 04 §6). Verified live via `docker image inspect`.
- **Planner image is BUILD-ONLY** in this ticket: `config/agents/planner/Dockerfile` pins Pi (`ARG
  PI_VERSION=1.6.0`, `npm install -g --ignore-scripts @earendil-works/pi-coding-agent@${PI_VERSION}`, per
  the PoC recipe) and bakes profile.yaml + the reference Runtime Bridge at `/opt/pi-agent/profile` with
  `ENV PI_CODING_AGENT_DIR`; no LLM/provider settings are baked (decision 56: V2). No runs use this image
  until tickets 11–12 (the baked bridge still spawns the stub protocol; `PI_AGENT_COMMAND` wiring is ticket 11).
- **Stale dev-DB row:** the pre-existing planner v1 (`draft`, snapshot from before the Dockerfile existed)
  will dispatch and fail terminal `build_failed` (honest history); the next sync creates planner v2 with
  the new files and that one becomes `ready`. Zero-touch reconciliation (owner: "don't care").
- **Auth direction** mirrors task 09: the Orchestrator presents its own `SERVICE_TOKEN_ORCHESTRATOR` and
  the build worker's endpoints accept `ServiceTokens__Orchestrator` (decision-65 contour).
- **One non-spec edge:** ambiguous build-EXECUTION failures (socket transport loss/timeout during a build)
  retry up to 3 attempts, then terminal `build_execution_failed` (distinct from `docker_build_failed`);
  both end the version in `build_failed`.
- Worker terminal error codes added to the §7 catalogue (documented addition): `build_not_found`,
  `build_payload_conflict` (same buildId, different payload → 409), `build_payload_invalid`, plus the
  failure codes `docker_build_failed`/`build_context_invalid`/`build_execution_failed` in the result body.

- [x] `POST /internal/builds` accepts a build and immediately returns `202 Accepted` with the `buildId`; repeated requests with the same `buildId` are idempotent and never start duplicate builds
- [x] Status/result served via `GET /internal/builds/{buildId}`: success carries local image tag, image ID/digest and build-log reference; a real Docker build failure is a terminal failed result
- [x] Build-job state is durable locally: results remain retrievable after Worker restart or network failure (retries reuse the same `buildId`)
- [x] Orchestration side (decision 81): a durable BuildJob per version (`buildId` persisted before dispatch), version set `draft → building` at dispatch, status polling, exponential-backoff retries for transient errors (version stays `building`, `build_failed` only on real build failure), and the restart reconciler resumes unfinished BuildJobs with the same `buildId`
- [x] The built image contains the pinned Pi version and the baked `/opt/pi-agent/profile` config directory
- [x] Only `ready` versions are selectable by the Orchestrator; version state transitions are executed by the Orchestrator, not the Worker
- [x] A changed `profile.yaml` always produces a new version (pinned SHA/snapshot changes)
- [x] The version row records `bridge_configuration` (Bridge version + heartbeat/event cadence) and the image bakes that Bridge version (decision 54, `02 §12`)
- [x] Build Worker exposes heartbeat health, secures both endpoints with a dedicated service credential (decision 65), and never accesses or modifies the control-plane database directly

## What has been done (2026-11-21, agent)

Owner approved the design notes above; implementation follows them exactly.

- `Infrastructure/`: `build_job` table + `BuildJob` entity (02 §12b: `id` = buildId, unique
  `agent_profile_version_id`, jsonb `dispatch_payload`, `state` ∈ pending/polling/completed/failed,
  `attempts`/`next_retry_at` backoff bookkeeping, validated `image_tag`/`image_digest`/
  `build_log_reference`, `last_error` for operators), migration `AddBuildJobs`; no
  AgentProfileVersion schema change (ContainerImage/ImageDigest existed already; build metadata
  lives on the BuildJob row).
- `Hosts/Orchestrator/Builds/`: `ProfileBuildWorker` IWorkerCycle — (a) opens durable BuildJobs for
  `draft` versions (and recovering `building`-without-job rows) and flips `draft → building` in the
  same write (buildId persisted before any dispatch); (b) dispatches due `pending` jobs
  (`POST /internal/builds`, `X-Service-Token`, exact pinned payload: buildId/profile/profileVersion/
  sourceCommitSha/configSnapshot/buildContext) → `polling`; (c) polls `GET /internal/builds/{buildId}`:
  queued|building = keep polling (attempts reset); succeeded = decision-81 validation (tag equals
  `agent-<profile>:v<version>`, image id + digest + log reference present) → job completed + version
  `ready` + ContainerImage/ImageDigest recorded; failed = terminal → job failed + version
  `build_failed`; transport/5xx/429/404 = backoff (`initial 10s ×2^n` capped 1 h) with the version
  STAYING `building`, same buildId reused; 404-after-accept returns the job to `pending` for a
  same-buildId redispatch. Restart reconciler = this same cycle over durable rows (no separate one).
  `BuildWorkerApiClient` (typed HTTP client, decision-65 header), `BuildWorkerOptions` (BaseUrl,
  ServiceToken, backoff, batch cap 10), `BuildMetrics` wired into /metrics. Cold-start gate: no
  dispatch before config sync. The Orchestrator performs every transition; the worker never touches DB.
- `Hosts/BuildWorker/`: the async build contract over the Docker Engine HTTP API (no CLI, no new
  packages): `POST /internal/builds` (idempotent by buildId: existing doc, same payload → 202 as-is;
  different payload → 409; malformed → 400) → `queued`; `BuildJobRunner` (single-consumer hosted
  service + channel): materializes the pinned context (hash checks, path-safety, Dockerfile +
  profile.yaml required), tars it (pure-ustar `TarBuilder`, deterministic, checksum-POSIX),
  `POST /build?t=agent-<profile>:v<n>` over the read-only socket → NDJSON progress appended to the
  durable build log → authoritative `GET /images/<tag>/json` inspect → succeeded with imageId
  (== digest in the local store) or terminal `failed` (`docker_build_failed` from daemon error lines;
  `build_context_invalid` for pinned-input problems; executable-transport failures retry 3× then
  `build_execution_failed`). `GET /internal/builds/{buildId}` serves the durable doc; unknown →
  404 `build_not_found`. Durable state: one JSON doc per buildId + build log + materialized context
  under `Build__StateRoot` (`/var/lib/pi-agent/builds` in compose, `BUILD_STATE_ROOT` on the host);
  atomic rename writes; startup rescan finalizes unfinished docs whose tag exists and re-enqueues the
  others (results retrievable across restarts; the daemon build dies with the worker, so rebuilds are
  the recovery, never a phantom). 5 s sweep re-enqueues stranded `queued` docs. `/readyz` keeps the
  docker-socket probe; metrics: `buildsActive/buildsSucceededTotal/buildsFailedTotal/
  buildExecutionRetriesTotal` + worker heartbeat gauges; JSON build logs per §14 (image builds,
  build errors, image IDs, no secrets).
- Docker Engine facts verified live (daemon 29.9.3 / API 1.56): unversioned paths (matching the
  Platform readiness probe style), x-tar POST, `aux`+`stream` progress lines; the client treats the
  post-stream tag inspect as the single source of truth, so builder-dialect differences don't matter.
- Config plane: `config/agents/planner/Dockerfile` (node:24-bookworm-slim, git+ripgrep, pinned
  `@earendil-works/pi-coding-agent@1.6.0`, non-root uid 1000, bakes profile.yaml + runtime-bridge at
  `/opt/pi-agent/profile`, `ENV PI_CODING_AGENT_DIR`) + `config/agents/planner/runtime-bridge/`
  (reference Bridge copy, profile.yaml bridges it at v1.0.0) — makes the shipped planner profile
  buildable; `config/agents/stub/Dockerfile` gains the same bake (build-only; stub pins NO pi on
  purpose) so both profiles go through the identical contract; runtime-bridge node tests keep
  running against the stub dir (the planner copy carries runtime modules only).
- Compose/env: build-worker gains `Build__StateRoot` + `BUILD_STATE_ROOT` volume +
  `ServiceTokens__Orchestrator`; orchestrator gains `BuildWorker__BaseUrl/ServiceToken` and a soft
  `build-worker` startup dependency (§13 order); `.env.example` documents `BUILD_STATE_ROOT`.
- Tests: Infrastructure `BuildJobModelTests`; Hosts `BuildDispatchTests` (9 cases: open-before-dispatch
  incl. buildId persistence, 202-payload-shape, transient stays-building + same-buildId redispatch,
  polling→ready with validated mirroring, failed→build_failed terminal, 404→redispatch, invalid
  poll-result never transitions, restart resume, cold-start gate, `building`-without-job recovery);
  Hosts `BuildWorkerApiTests` (14 cases: 202 shape, idempotent replay, conflict, 404, token auth
  (incl. wrong token), six malformed payloads, docker-failure terminal with durable log, context
  invalid (missing Dockerfile / hash mismatch), transient retry then success, exhaustion terminal,
  failed-result retrievable after restart, hung-build restart finalizes from the existing tag);
  `BuildTarAndContextTests` (tar readable + deterministic, composer happy/reject paths);
  `DockerEngineLiveTests` (env-gated live check). Worker `/metrics` keys asserted in HostsTests.

## Live verification record (2026-11-21, executed with the owner's go-ahead)

Environment: `src/.env` as left by earlier tasks (Komodo credentials present). `task stack-rebuild`
→ all 7 service images built; `task stack` → all healthy (migrator exited 0). Teardown at the end:
`komodo down` + `task stack-down` + `docker ps` → **0 containers** (verified: pi-agent/komodo/pi-session
filters all 0, volumes kept). Real outputs:

1. **Automatic dispatch, zero-touch** (13:02:40, first worker tick after config sync):
   orchestrator: `opened 4 BuildJob(s) for profile version(s) …` then four
   `build <id> for <profile> v<n> dispatched (202 accepted)`;
   build-worker: four `build <id> accepted: agent-<profile>:v<n> (context config/agents/<profile>, …)`
   — exactly the 04 §6 payload (pinned snapshot, build context directory, sourceCommit).
2. **Build results**: stub v4 built in ~5 s (cached layers) → orchestrator poll →
   `stub v4: ready, container_image=agent-stub:v4, image_digest=sha256:a320a589f837b3fc…, job completed`
   (verified SQL). Planner v3 (created by THIS startup because the Dockerfile/bridge copy landed)
   built with the pinned-Pi npm install (~45 s) → `planner v3: ready, agent-planner:v3,
   sha256:ba3aa83c822b6811…`. `docker images`: agent-stub:v4 + agent-planner:v3 present.
3. **Stale pins self-heal as agreed**: planner v1 and v2 (pre-Dockerfile snapshots from earlier dev
   runs) → worker `build_context_invalid: the pinned file list has no Dockerfile — the version predates
   the profile Dockerfile` → `AgentProfileVersion.state = build_failed`, BuildJob `failed` with the
   reason in `last_error`. No manual remediation — v3 superseded them.
4. **Image contract (build-not-run)**: `docker image inspect agent-planner:v3` →
   `PI_CODING_AGENT_DIR=/opt/pi-agent/profile` in Config.Env; `Config.User=node` (uid 1000, the Runtime
   API's chown target); `Cmd=[node,/opt/pi-agent/profile/runtime-bridge/bridge.mjs]`;
   `docker run --rm --entrypoint ls agent-planner:v3 -la /opt/pi-agent/profile` →
   `profile.yaml + runtime-bridge/` baked; `npm ls -g` inside the image →
   `@earendil-works/pi-coding-agent@1.6.0` (pinned, not floating latest). One-shot `ls`/`npm` containers only.
5. **Durable local state**: `src/builds/jobs/<buildId>.json` per version, `src/builds/logs/<buildId>.log`
   with the raw docker NDJSON progress (`Step 1/9 : FROM node:22-alpine`, layer pulls, …);
   `buildLogReference` on the served document points there.
6. **Endpoint negatives from inside the private network** (decision 65):
   `POST/GET /internal/builds*` without a token → **401**; `GET /internal/builds/<unknown>` with the
   orchestrator token → **404** `build_not_found`; with the token on a known id → **200**.
7. **Durability across restart**: `docker compose restart build-worker` → the same GET returns the
   identical succeeded result (same buildId, same imageId, same log reference). Orchestrator-side
   durability is the same row-based pattern as task 09's reconcilers.
8. **Real build failure is terminal**: throwaway `config/agents/broken/` (valid profile.yaml +
   `RUN exit 7` Dockerfile) + orchestrator restart → synced/dispatched/built → worker
   `docker build agent-broken:v1 reported an error: The command '/bin/sh -c exit 7' returned a
   non-zero code: 7` → version v1 `build_failed`, BuildJob `failed`, the build log ends with the
   docker error line + a `== terminal: docker_build_failed ==` marker. Removal + orchestrator restart
   → profile paused (`is_enabled=f`), failed row retained as history, sync logged
   `0 new agent profile version(s)`. (Two orchestrator restart loops happened on the FIRST attempt of
   the throwaway profile — an invalid YAML scalar — the 01 §13 cold-start gate + diagnosable abort
   behaved as designed; fixed with a clean profile.yaml, noted here for honesty.)
9. **Ready versions select and run**: `/plan` injected via the internal hook (author `aliaksandrZh`
   — a first attempt as `jsxhuz` was correctly rejected `not_in_allowlist`, recorded on the command)
   → queued → claimed → the run used **agent-stub:v4 (the pipeline-built image)** through Komodo →
   run `succeeded` with the result summary recorded, deployment `pi-session-<runId>` `stopped/stopped`.
   No manual SQL flips and no manual `docker build` anywhere — task 09's bootstrap steps disappear.
10. **Teardown verified**: komodo + stack down; `docker ps --filter name=pi-agent --format '{{.Names}}'`
    → empty; `name=pi-session-` → empty; `name=komodo` → empty; `docker ps -a` for those names also
    empty; `src/builds/` and DB volumes persist (durable state by design).

Live-bug notes (fixed in this branch): none in the ticket-10 code — the only friction was fixture
content (my throwaway YAML), and the `not_in_allowlist` rejection was correct repository policy.

**Not executed live (still unverified):** the `build_execution_failed` pathological path (a real
daemon outage mid-build — simulated with the fake engine only), concurrent multi-build ordering beyond
the two profiles observed (the runner is single-threaded by design), the live 409 payload-conflict path
(endpoint-tested), and long-backoff behavior at the 1 h cap (observed only at seconds scale).

## How to manually test

> Prerequisites: the standard setup from earlier tasks — `src/.env` filled (see `src/.env.example`),
> the two PAT files in `src/secrets/`, `docker compose` available. Nothing from ticket 09's manual
> SQL/`docker build` bootstrap is needed anymore: builds are automatic.

1. ⚏ `task stack-rebuild && task stack` (first start builds the images; wait green).
2. ⚏ Verify the automatic build flow end to end:
   ```sh
   docker compose --env-file src/.env -f src/compose.yaml logs orchestrator --since 5m | grep -i build
   #   expected: "opened N BuildJob(s)" then "build <id> for <profile> v<k> dispatched (202 accepted)"
   #             then "build <id> completed: agent-<profile>:v<k> (sha256:...) — version <k> is ready"
   docker compose --env-file src/.env -f src/compose.yaml logs build-worker --since 5m | grep -E "accepted|succeeded|failed"
   #   expected: "build <id> accepted: agent-planner:v2 (context config/agents/planner...)" and the
   #             stub/planner build lines; failures only for the stale pin (see step 3)
   docker images --format '{{.Repository}}:{{.Tag}}' | grep agent- | sort
   #   expected: agent-planner:v2 and agent-stub:v2 (new versions created because the Dockerfiles changed)
   ```
3. ⚏ Inspect the built planner image (the build-not-run criterion):
   ```sh
   docker image inspect agent-planner:v2 --format '{{json .Config.Env}}'
   #   expected: PI_CODING_AGENT_DIR=/opt/pi-agent/profile in the Env list
   docker run --rm --entrypoint ls agent-planner:v2 -la /opt/pi-agent/profile
   #   expected: profile.yaml + runtime-bridge/ baked (read-only image layer)
   ```
4. ⚏ Check the durable build-job state + logs on the host tree:
   ```sh
   ls src/builds/jobs/ src/builds/logs/
   #   expected: one <buildId>.json per version + matching logs; jobs completed with imageTag/imageId,
   #             the stale planner v1 entry failed with build_context_invalid (no Dockerfile in the old pin)
   cat src/builds/jobs/<some-id>.json | python3 -m json.tool | head
   ```
5. ⚏ DB side (decision-81 bookkeeping):
   ```sh
   docker compose --env-file src/.env -f src/compose.yaml exec -T postgres psql -U pi_agent -d pi_agent -c \
     "SELECT p.name, v.version, v.state, v.container_image, v.image_digest IS NOT NULL AS DigestSet, j.state AS job, j.attempts\n        FROM agent_profile_version v JOIN agent_profile p ON p.id=v.agent_profile_id LEFT JOIN build_job j ON j.agent_profile_version_id=v.id\n       ORDER BY p.name, v.version;"
   #   expected: planner v1 draft→building→build_failed (stale pin) | planner v2 ready agent-planner:v2
   #             job completed | stub v2 ready agent-stub:v2 job completed
   ```
6. ⚏ Endpoint auth/idempotency/durability negatives from INSIDE the private network:
   ```sh
   B=$(ls src/builds/jobs | head -1 | cut -d. -f1)
   docker compose --env-file src/.env -f src/compose.yaml exec -T build-worker sh -c \
     "curl -s -o /dev/null -w '%{http_code}' localhost:8080/internal/builds/$B"; echo
   #   expected: 401 (no service token)
   docker compose --env-file src/.env -f src/compose.yaml exec -T build-worker sh -c \
     "curl -s -H \"X-Service-Token: $SERVICE_TOKEN_ORCHESTRATOR\" -o /dev/null -w '%{http_code}' localhost:8080/internal/builds/00000000-0000-0000-0000-000000000000"; echo
   #   expected: 404 (unknown buildId, code build_not_found)
   docker compose --env-file src/.env -f src/compose.yaml restart build-worker
   #   then: the same GET returns 200 with the previous state (results retrievable after restart)
   ```
7. ⚏ A real build failure ends the version terminal without breaking anything else: add a throwaway
   `config/agents/broken/` (profile.yaml + a Dockerfile with a failing RUN), restart the orchestrator
   (config re-sync) → the broken version dispatches → builds → **build_failed** (worker log shows the
   docker error lines; orchestrator log: version set to build_failed). Then remove the directory and
   restart the orchestrator once more — the profile pauses (is_enabled=false), the failed version row
   remains as history. (`/status`-routing is untouched — nothing selected it.)
   ```sh
   mkdir -p src/config/agents/broken
   cp src/config/agents/planner/profile.yaml src/config/agents/broken/profile.yaml
   printf 'FROM alpine:3.20\nRUN exit 7\n' > src/config/agents/broken/Dockerfile
   docker compose --env-file src/.env -f src/compose.yaml restart orchestrator
   #   wait one worker tick, then re-run the step-5 SQL: broken v1 = build_failed
   rm -rf src/config/agents/broken && docker compose --env-file src/.env -f src/compose.yaml restart orchestrator
   ```
8. ⚏ Run the stub flow again if Komodo is up (proves ready versions still select, runs, terminal):
   inject `/plan` per the task-09 hook (step 5 there) — no SQL flips required this time; the stub v2
   image was built by the pipeline.
9. ⚏ Teardown: `task stack-down`; verify nothing remains: `docker ps --filter name=pi-agent --format '{{.Names}}'` → empty. `src/builds/` persists (durable state).

Everything in steps 1–8 was executed in the 2026-11-21 session (the live record above); step 9
depends on Komodo being up and was executed once. **Not executed live (unverified):** the
`build_execution_failed` daemon-outage path beyond simulated tests, parallel build ordering, the
409 conflict path live, and 1 h-cap backoff scale.
