# 09: Run execution pipeline with a stub agent

**What to build:** The Runtime API + Runtime Bridge pipeline proven end to end against a **stub agent container** (a fake headless CLI speaking a tiny JSON protocol — same shape as pi RPC: one prompt input, a few stdout event types, settled). Runtime API: `POST /internal/runtime/runs/{runId}/start` (the only creation endpoint — Orchestrator owns DB rows, API provisions physical resources), stop, status, and the callback routes (heartbeat / events / completed / failed) with a fresh per-Run bearer credential delivered as a read-only secret file, stored hashed, scoped to that runId, revoked after terminal delivery. Bridge: launches the agent, reads the stdout JSONL event stream, applies the pi→RunEvent mapping, spools callbacks durably (`runs/{run_id}/callback-spool/`) before sending, retries network/5xx/429 with the same eventId, coalesces heartbeats, never drops lifecycle/terminal callbacks. Run result: manifest `result.json` (schemaVersion, summary, artifacts[] with paths, requiredPublications[] with toolCallId/kind/externalId) validated in-place; `branch`/`commitSha` derived via Git; events → Artifact rows within the 256 KiB limit. The receiver persists every delivered callback as a `RunEvent` before acknowledgement, covering the full `02 §18` type set: `artifact_produced` per artifact created from the validated manifest, and `heartbeat` events from the heartbeat route (sender-side coalescing allowed, `04 §4`). Run logs: the per-Run directory carries the trio `logs/{stdout.log, stderr.log, bridge.log}` captured by the Bridge with a 50 MiB per-Run cap and rotation; `Run.log_storage_reference`, `log_size_bytes`, `logs_completed_at` populated on completion and `logs_deleted_at` on cleanup; a `log_reference` Artifact points at the directory without copying content. Komodo provisions the container and mounts the per-Run session/log/spool directories from the `/var/lib/pi-agent/workflows/` tree.

**Spec:** `docs/pi-agent-architecture/04-api-contracts.md` §3–§4 (endpoints, Bridge contract, log trio); `03-state-machines.md` §3–§4 (Run/AgentSession machines, one-shot 1:1 sessions); `05-v1-decisions.md` decisions 33 (no Pi extension — the Bridge is a supervisor), 43 (log contract), 44, 57 (session dirs), 58 (event mapping), 59 (result manifest), 60 (credential + spool), 37 (isolation); `01-architecture-and-flows.md` §10 (lifecycle), §2 (Runtime API).

**Blocked by:** 01, 07

**Status:** done-pending-review (branch `task-9-run-pipeline-stub`, staged uncommitted pending review;
automated verification 483/483 green via `dotnet test src/PiAgent.slnx` — Architecture 2, Platform 9,
Infrastructure 46, Hosts 426 — plus `node --test src/config/agents/stub/runtime-bridge/*.test.mjs` 17/17.
**Live Komodo walkthrough EXECUTED 2026-11-21** (owner go-ahead): happy path, needs_human → Task Blocked,
fail → Run Failed + Outbox-less recorded reason, spool redelivery through a Runtime API outage, per-Run
credential scoping negatives, isolation checks, reconciler observation — see the live record below; the
teardown verified 0 running containers, volumes kept (dev DB state retained). The walk surfaced live bugs
(fixed in this branch, each with a regression test where testable): Komodo client lacked the
x-api-key/x-api-secret headers and mis-parsed CreateDeployment/GetDeploymentContainer shapes; the
workspace-content validation used the host path instead of the Runtime API's managed mount; dispatcher
snapshot not committed before POST /start; two Run-state write-back races (`starting` could clobber a
terminal state from both the dispatcher and the /start handler — now conditional atomic updates); the
bridge never sent the RPC prompt and its 30s grace killed agents that had not settled — grace now arms
only after `agent_settled`.)

- [x] Start endpoint provisions the physical resources for the Orchestrator-created rows: the Run + its one-shot AgentSession (1:1, `run_id` unique) + container + per-Run directory tree (decision 44 — the Orchestrator owns DB rows, the endpoint provisions only)
- [x] The container is provisioned with the pinned profile version's `resource_configuration` limits (CPU/memory) from the `AgentProfileVersion` row (`02 §12`)
- [x] Stub completes → `completed` callback persisted idempotently (duplicate eventIds ignored); Run terminal state set; container stopped, data retained
- [x] Stub failure → `failed` callback → Run Failed with the reported error code
- [x] Callback persistence covers the full `02 §18` RunEvent type set before acknowledgement: `artifact_produced` recorded per Artifact row created from the validated manifest; `heartbeat` beats persisted idempotently by `eventId` while updating `Run.last_heartbeat_at`
- [x] Manifest artifacts become Artifact rows; oversize artifact rejected without silent truncation
- [x] Per-Run callbacks rejected for other runIds (credential scoping); token revoked after terminal delivery
- [x] Runtime API restart mid-run → reconciler recovers desired/observed state from PostgreSQL; Bridge spool redelivered
- [x] `outcome = needs_human_input` from the stub → Run Succeeded, Task terminal `Blocked`, Workflow `waiting_for_human`
- [x] The Workspace DB record is created by the Orchestrator on the Run's first attempt and reused by retries; its statuses track `03 §6` (Provisioning → Ready → InUse → CleanupPending → Deleted) with runtime metadata written back
- [x] Orchestrator→Runtime API calls authenticate with a dedicated service credential (decision 65); the per-Run callback token cannot start/stop containers or access other runs (negative test)
- [x] Per-Run log trio written per decision 43 (`logs/{stdout.log, stderr.log, bridge.log}`), 50 MiB per-Run cap with rotation; Run log fields (`log_storage_reference`, `log_size_bytes`, `logs_completed_at`) populated; `log_reference` Artifact points at the directory without copying content into PostgreSQL
- [x] The agent container has no Docker socket, no host-shell tool, and no way to reach or manage neighboring containers (decision 37, `01 §13`) — verified live in the stub run (`docker exec` → `No such file or directory` / not found; deployment binds only the storage/secrets trees, `privileged=false`, user `node`; no ServiceTokens in the agent env)
- [x] Profile-required secrets mount read-only at `/run/secrets/<name>` per ticket 11b — never via env vars or image layers

## What has been done (2026-11-21, agent)

Decision notes recorded with the owner before implementation (owner-approved):
- **No run.outcome column** — the `completed` callback body (incl. `outcome`) is persisted verbatim as a
  `completed` RunEvent payload before acknowledgement; the Orchestrator terminal consumer reads it to
  split Task `Succeeded` vs `Blocked` (04 §4 "Orchestrator owns transitions"). `Run.status` stays exactly
  the 03 §3 machine.
- **Input delivery:** new authenticated pull route `GET /internal/runtime/runs/{runId}/input`
  (per-Run bearer; a flagged addition beside the 04 §3 route list). The frozen RunContextSnapshot
  content serves it; restarts pull the same bytes.
- `RunContextSnapshot.content` jsonb column (a spec-field-list addition) so `/input` can serve the frozen
  bytes; ticket 11 replaces the minimal construction, not the immutability.
- Oversize artifact: Runtime API rejects `completed` with 422 `artifact_too_large` (nothing persisted);
  the Bridge then posts `failed` with that code (Run Failed). New code `artifact_too_large`.
- Log cap = 50 MiB total across the trio, segmented rotation (oldest segment dies first), delivered to
  the Bridge as `PI_MAX_LOG_MB` (`Storage__MaxLogMegabytes`, documented deployment copy of the §16 knob).
- The stub scenario lever is config-plane-pinned: `bridge.behavior` (`succeed|fail|needs_human|hang`)
  validated by the Config Loader and carried in `AgentProfileVersion.bridge_configuration`; a scenario
  change is a new profile version (SQL state/container_image flips stand in for ticket 10's build flow).
- Task-failure Outbox notifications are deliberately not here (ticket 15 owns them).

Implementation map:
- `Infrastructure/`: migration AddRunPipeline — `run_event` (unique (run_id,event_id)), `run_credential`
  (unique run, sha256 hash only), `workspace` (task_id unique, 03 §6 machine), `run_context_snapshot`
  (run_id unique); entities + RunEventTypes vocabulary.
- `Hosts/RuntimeApi/`: KomodoClient (minimal `{url}/read|write|execute/{Type}` client with x-api-key
  headers, shape-mirrors the reference PoC), RuntimeStorage (decision-52 tree + spool + token file, libc
  chown to the container uid), RunCredentialService + bearer filter (401 unknown; 403 other-run/revoked),
  StartRun (idempotent provisioning: tree + credential + workspace dir + container via Komodo with
  --memory/--cpus from resource_configuration; metadata write-back; profile secrets as ro
  /run/secrets/<name> mounts, fail-closed on a missing secret), Callbacks (persist-before-ack, duplicate
  eventIds are no-ops, terminal finalization: artifacts + artifact_produced + log trio facts +
  log_reference artifact + revocation + container stop + workspace release), RunInputStatus (/input,
  /status), RunPipelineApi (/stop minimal real stop; the graceful Bridge sequence is ticket 14),
  RunReconciler (Komodo observed-state refresh; §14 runsActive gauge).
- `Hosts/Orchestrator/`: RuntimeApiRunDispatcher replaces NullRunDispatcher (HTTP + decision-65 header;
  freezes the snapshot; HandedOff completes the job + Task starting; 4xx → Permanent claim-cancel
  `runtime_dispatch_failed`; 429/5xx/transport → Transient release; 409-with-terminal-run → idempotent),
  RunPrepare now creates the Workspace row on the Task first attempt (reused by retries),
  RunTerminalConsumer (Task starting→running; terminal Run → Task succeeded|blocked|failed(+timed_out) +
  Workflow waiting_for_human + desired container stopped + command processed/failed).
- `config/agents/stub/`: profile.yaml (bridge config incl. behavior) + Dockerfile (node:22-alpine + git,
  non-root uid 1000) + runtime-bridge/ (bridge.mjs: spool-first delivery with same-eventId backoff
  retries, coalesced heartbeats, terminal never dropped (unbounded 30s backoff), in-place manifest
  validation + git branch/commitSha derivation + requiredPublications correlation, log trio with
  rotation; spool/events/logs/manifest modules) + stub-agent/pi-stub.mjs (pi-RPC-shaped JSON protocol;
  writes the runId.jsonl session history, result.json + artifact files, git-commits its work; fail
  behavior exits non-zero reporting planner_stub_error). 17 node tests (node --test, no npm deps).
- Config/compose: global routing rule plan → stub (dev posture; flips back to planner in ticket 12);
  runtime-api mounts the storage root + secrets dir and gains Storage__*/Komodo__ServerId envs;
  orchestrator gains RuntimeApi__BaseUrl/RuntimeApi__ServiceToken; .env.example documented
  (STORAGE_HOST_ROOT must be the ABSOLUTE host path of STORAGE_ROOT — Periphery resolves mount strings
  on the host). Migrator.csproj gained Microsoft.EntityFrameworkCore.Design (PrivateAssets=all,
  dev-time only) for future migrations.
- Dev/test note: provisioning chown to the container uid is best-effort on non-root runtimes (macOS
  dev/test logs a warning; the root api container succeeds) — deliberate, documented in code.
- Naming: deployment `pi-session-<runId>`; runtime session id = runId (decision 57); the pinned session
  file path is `<runId>.jsonl` — the stub writes exactly that file.

## Live verification record (2026-11-21, executed with the owner's go-ahead)

Environment actually used (differences from the pre-approval notes are noted): Komodo started
(`docker compose -f src/komodo/ferretdb.compose.yaml --env-file src/komodo/compose.env up -d`); the
Komodo API key was minted via HTTP (login `POST /auth/login/LoginLocalUser`, then
`POST /auth/manage/CreateApiKey` with `authorization: Bearer <jwt>` — **Bearer** prefix required) and
written into `src/.env` together with `STORAGE_HOST_ROOT=/abs/src/storage` and
`PI_AGENT_SECRETS_HOST_DIR=/abs/src/secrets` (absolute host paths; .env is gitignored).
`docker build -t agent-stub:v1 src/config/agents/stub` → built. `task stack-rebuild` + `task stack`
(both `--wait` green), the Config Loader auto-created the `stub` profile + version from the new
`config/agents/stub/`, then `UPDATE … SET state='ready', container_image='agent-stub:v1'` per the
walkthrough, `/plan` injections via the internal hook, and these live checks (real outputs):

1. **Happy path (run 7316f5b5)** — run succeeded, `result_summary = "Stub pipeline result: the agent
   completed the run."`, `log_size_bytes=760`, `logs_completed_at` set; run_event counts exactly
   agent_started/turn_started/assistant_output(aggregated)/tool_started/tool_progress/tool_completed/
   turn_completed/agent_settled/heartbeat/completed/artifact_produced×2; Artifact rows
   `implementation_summary` (markdown, inline content `# Pipeline stub result…`,
   `metadata.branch="agent/pipe-stub"`, 12-hex `commitSha`) and `log_reference` (directory path only);
   container `desired=stopped observed=stopped`, session `terminated`, workspace `ready`, credential
   `revoked=true`.
2. **needs_human (run ec4a7d45)** — Run succeeded with the persisted `completed` payload carrying
   `outcome="needs_human_input"`; the terminal consumer set **Task `blocked`** and Workflow
   `waiting_for_human` (decision 72 verified end to end).
3. **Fail (run d3feea13)** — stub exit 3 → Bridge reported the crash path; failed callback persisted;
   Run `failed` with `error_code="runtime_failure"`, `error->>'code'` in the payload, Task `failed`
   (reason recorded), Workflow `waiting_for_human`, token revoked, logs captured (stdout trio shows the
   stub's agent_start/turn_start/error lines).
4. **Credential scoping** — during a live run: the per-Run token on `/start`, `/stop` and `/status` →
   **401**; on another run's `/heartbeat` → **403 callback_scope_denied** ("scoped to another run"); no
   token → 401. No SERVICE_TOKEN values in the agent container env.
5. **Isolation** — `docker exec pi-session-<run> sh -c 'ls /var/run/docker.sock; which docker'` →
   "No such file or directory" + not found; deployment binds only
   workspace/pi-session/logs/spool/callback-token(+none for secrets — stub profile has none);
   `privileged=false`, container user `node` (uid 1000, chowned by the Runtime API).
6. **Spool redelivery (decision 60)** — with a hung-but-alive run (heartbeats 15s), `docker compose stop
   runtime-api`: bridge.log shows `POST /heartbeat transport failure: fetch failed` + retries with the
   SAME eventId; after `start`, the beat was delivered (run_event grew; unique `(run_id,event_id)` held
   — no duplicates).
7. **Reconciler** — observed-state observations logged (`reconciler: container … observed 'failed'
   (komodo 'exited')` and running→running refresh) after Runtime API restarts. Komodo's
   GetDeploymentContainer serves a core-side cache (15s+ lag) — noted for ticket 13.
8. **Teardown** — `task stack-down`, komodo `down`, `docker rm` of all 26 `pi-session-*` test containers
   and nothing left running (`docker ps` → 0 entries). Data volumes retained (dev DB state kept).

**Live-bug fixes made during the walk (all in this branch):**
- `KomodoHttpClient` now sends `x-api-key`/`x-api-secret`; CreateDeployment accepts the single-object
  response; GetDeploymentContainer tolerates the `container: null` shape (was an unhandled
  InvalidOperationException).
- In-place artifact validation reads the workspace through the Runtime API's managed mount
  (`Storage.ManagedRoot`) — the row's `storage_reference` stays the host-reference string.
- The dispatcher commits the RunContextSnapshot BEFORE POST /start (the Runtime API serves that row).
- Run-state write-back races: both the dispatcher and the /start handler now use conditional atomic
  updates (`WHERE status IN (pending, starting)`) — a sub-second container (stub) can deliver its
  terminal callback during provisioning; regression test `Handoff_DoesNotClobberATerminalRunState`.
- Bridge: the RPC prompt is now actually written to the agent's stdin (`{type:"prompt", message}`); the
  30s exit grace arms only after `agent_settled` (before, it SIGTERM'd agents that had not settled —
  the hang scenario exposed it).
- Stand-in manual reconciliations were run as documented SQL between scenarios (runs whose containers
  died pre-ticket-13: status failed + Task/Workflow reset) — recorded in the transcript; no destructive
  database changes, no volume drops.

**Not executed live (still unverified):** Outbox notifications for the failed Task (ticket 15); the
graceful-stop sequence (ticket 14); idle/max-run duration enforcement (ticket 13); the real `/status`
rendering during a run (ticket 08 surfaces Run facts only); multi-run concurrency beyond two quick runs.

## How to manually test

> **Verification state:** automated verification complete (see Status). **Nothing below has been
> executed live in this session** — owner asked for code review first; do the walkthrough after review.
> Items in the ⚏ list need one-time setup I can drive once you approve.

Prerequisites:
1. ⚏ `task agent-komodo` (long-lived). Komodo UI localhost:9120 (admin/changeme); create an API key
   (Config → API Keys, read+write+execute).
2. ⚏ `src/.env`: set KOMODO_API_KEY/KOMODO_API_SECRET, plus
   STORAGE_HOST_ROOT=/abs/path/to/src/storage and PI_AGENT_SECRETS_HOST_DIR=/abs/path/to/src/secrets
   (absolute host paths — Periphery resolves the mount strings on the host).
3. ⚏ Bootstrap the stub image once (ticket 10 automates this later):
   `docker build -t agent-stub:v1 src/config/agents/stub`
4. ⚏ `task stack-rebuild && task stack` (wait green), then one flip of the stub version (the data change
   standing in for the build flow):
   ```sh
   docker compose --env-file src/.env -f src/compose.yaml exec -T postgres psql -U pi_agent -d pi_agent -c \
     "UPDATE agent_profile_version v SET state='ready', container_image='agent-stub:v1'
        WHERE v.state='draft' AND v.agent_profile_id=(SELECT id FROM agent_profile WHERE name='stub');"
   ```
5. ⚏ Inject a /plan without the poll delay via the internal hook:
   ```sh
   docker compose --env-file src/.env -f src/compose.yaml exec -T postgres psql -U pi_agent -d pi_agent -t \
     -c "SELECT id, number FROM work_item WHERE state='open' ORDER BY number DESC;"
   docker compose --env-file src/.env -f src/compose.yaml exec -T orchestrator curl -s \
     -H "X-Service-Token: $SERVICE_TOKEN_ORCHESTRATOR" -H 'Content-Type: application/json' \
     -X POST http://localhost:8080/internal/commands \
     -d '{"workItemId":"<uuid>","author":"<collaborator>","commentId":9201,"text":"@<bot> /plan"}'
   #   expected: {"commandId":"<uuid>","status":"queued"} → claimed within one worker tick
   ```
6. Watch the run end to end (the first run waits on the image pull):
   ```sh
   docker compose --env-file src/.env -f src/compose.yaml logs runtime-api --since 5m | grep -E "started"
   #   expected: run <runId> started: deployment pi-session-<runId> image agent-stub:v1 ...
   # NOTE: run.id is a UUID — `ORDER BY id` is NOT chronological. Use started_at (NULLs = not reported):
   docker compose ... exec -T postgres psql -U pi_agent -d pi_agent -c \
     "SELECT id,status,attempt_number,started_at,completed_at,result_summary,log_size_bytes,logs_completed_at
        FROM run ORDER BY started_at DESC NULLS LAST, completed_at DESC NULLS LAST LIMIT 3;"
   #   expected: succeeded; result_summary 'Stub pipeline result: ...'; log facts set
   docker compose ... exec -T postgres psql -U pi_agent -d pi_agent -c \
     "SELECT type, count(*) FROM run_event WHERE run_id='<runId>' GROUP BY type ORDER BY type;"  \n  #   (correct: query ONE run you picked via started_at ordering)
   #   expected: agent_started/assistant_output/tool_started/tool_progress/tool_completed/turn_started/
   #   turn_completed/agent_settled/heartbeat/completed/artifact_produced(2) rows
   docker compose ... exec -T postgres psql -U pi_agent -d pi_agent -c \
     "SELECT type, format, left(content,40), metadata->>'branch', left(metadata->>'commitSha',12) FROM artifact WHERE task_id='<taskId>' ORDER BY created_at;"
   #   expected: implementation_summary markdown 'Stub plan' branch=agent/pipe-stub + 12-hex sha
   #             log_reference text <log dir path> (no content copied)
   docker compose ... exec -T postgres psql -U pi_agent -d pi_agent -c \
     "SELECT event_id, payload->>'outcome' FROM run_event WHERE run_id='<runId>' AND type='completed';"
   #   expected: the persisted OUTCOME (needs_human_input variant in step 7) — no run.outcome column
   docker compose ... exec -T postgres psql -U pi_agent -d pi_agent -c \
     "SELECT status FROM task WHERE id='<taskId>'; SELECT status FROM workflow WHERE id=(SELECT workflow_id FROM task WHERE id='<taskId>'); SELECT status, storage_reference, storage_kind FROM workspace WHERE task_id='<taskId>'; SELECT status, runtime_session_id, session_file_path FROM agent_session WHERE run_id='<runId>'; SELECT desired_state, observed_state, deployment_name, container_image FROM container_instance WHERE run_id='<runId>'; SELECT revoked_at IS NOT NULL AS revoked, created_at FROM run_credential WHERE run_id='<runId>';"
   #   expected: task succeeded | workflow waiting_for_human | workspace ready(host_path) |
   #             session terminated(runs=<runId>, pi-session path) | container stopped/stopped
   #             pi-session-<runId> agent-stub:v1 | credential revoked=true
   ```
   Physical tree on the host: `src/storage/workflows/<wf>/runs/<run>/{pi-session/<run>.jsonl,
   logs/{stdout.log, stderr.log, bridge.log}, callback-spool/}` and
   `tasks/<task>/workspace/{result.json, artifacts/summary.md, .git}`.
   Negative checks (while a NEW run is starting): `curl -H 'Authorization: Bearer <token-file-of-run A>'
   -XPOST localhost:8080/internal/runtime/runs/<runB>/heartbeat -d '{...}'` → 403 callback_scope_denied;
   `/start`, `/stop`, `/status` called with the per-Run token instead of X-Service-Token → 401
   (the token can never start/stop containers); `/status` without any token → 401.
7. Scenario variant: flip behavior (one data change), a NEW commentId, expect the FAILED path:
   `UPDATE agent_profile_version SET bridge_configuration = jsonb_set(bridge_configuration,
   '{behavior}', '"fail"') WHERE id=<stub-v1-id>;` → Run failed with error_code=planner_stub_error,
   Task failed, Workflow waiting_for_human, log trio + log_reference recorded, credential revoked,
   container stopped; undo the flip. `'"needs_human"'` → Run succeeded with the completed payload carrying
   outcome=needs_human_input, Task blocked (terminal), Workflow waiting_for_human.
8. Spool redelivery (decision 60): with a run active, `docker compose ... restart runtime-api` ⚏ — the
   bridge bridge.log shows same-eventId retries, then delivery completes; the reconciler refreshes
   observed state post-restart. Duplicate deliveries hit the unique (run_id,event_id) and are no-ops.
9. Isolation (decision 37): during the run,
   `docker exec pi-session-<runId> sh -c 'ls /var/run/docker.sock; which docker'` → "No such file or
   directory" + "not found"; the Komodo deployment config for `pi-session-<runId>` shows no docker socket
   mount, no privileged mode, no host-bind of anything above the storage/secrets trees.
10. Teardown: `task stack-down`; stop `task agent-komodo` (Ctrl-C); verify nothing remains:
    `docker ps --filter name=pi-agent --format '{{.Names}}'` and
    `docker ps --filter name=pi-session- --format '{{.Names}}'` → both empty.

### Tracing a failed run (diagnostics map)

Where each piece of evidence lives:

| what | where |
|---|---|
| Run facts (`error_code`, `error_message`, result, log size/facts) | `run` row — note `id` is a UUID: order by `started_at DESC NULLS LAST, completed_at DESC NULLS LAST`, never by `id` |
| Per-callback payloads (the terminal `error.code`, the `outcome` of a completed) | `run_event` (`payload` jsonb); a **run with `status='failed'` and ZERO run_event rows was reconciled manually** (documented pre-ticket-13 stand-ins from the walkthrough; a runtime-API failed callback ALWAYS persists the event before its 200 — ticket 13's reconciler will insert this event mechanically) |
| Runtime API side (provisioning, callback HTTP answers, reconciler) | `docker compose --env-file src/.env -f src/compose.yaml logs runtime-api` — grep the runId; response bodies are in the orchestrator's dispatcher log ("dispatch rejected (400): {code…}") |
| Orchestrator side (claims, hand-off, terminal consumer) | `docker compose … logs orchestrator` — "run … handed off", "dispatch …", "cycle failed" |
| Bridge + agent runtime logs (decision 43 trio) | host tree: `src/storage/<workflowId>/runs/<runId>/logs/{bridge.log, stdout.log, stderr.log}` — bridge.log carries the Bridge's own diagnosis (boot, prompt delivery, retries with eventIds, manifest validation, crash traces) |
| Session history on the pinned path | `src/storage/<workflowId>/runs/<runId>/pi-session/<runId>.jsonl` (the stub appends agent history there) |
| Container stdout/stderr (also duplicated into the trio) | `docker logs pi-session-<runId>` (only while the container exists — they are removed at teardown; the trio persists on the host tree) |
| Which workflow/task a run belongs to | `SELECT t.workflow_id FROM run r JOIN task t ON t.id=r.task_id WHERE r.id='<runId>'` |
| Container observed machine | `SELECT observed_state, deployment_name, last_stopped_at FROM container_instance WHERE run_id='<runId>'` |

Unverified in this session (needs the live stack above): every step of this section — in particular the
real Komodo CreateDeployment/Deploy chain (the fake covered request shapes only), container uid/permission
behavior with real mounts, the runtime-api DNS reachability from an agent container on pi-agent-net, and
the live /status rendering during an active run.
