# 22: Drop Komodo — Runtime API manages agent containers directly over the Docker socket

**What to build:** Remove Komodo Core from the system entirely. The Runtime API provisions and supervises
agent containers itself: the same container lifecycle it exercises today through Komodo Core RPC
(`CreateDeployment` → `Deploy` → `GetDeploymentContainer` → `StopDeployment` → `DeleteDeployment`)
becomes five calls against the local Docker Engine HTTP API over the existing unix socket
(`containers/create` → `start` → `inspect` → `stop` → `DELETE`), in the same in-house raw-HTTP style the
Build Worker (`DockerEngineClient`) and Host Monitor (`DockerMonitorClient`) already use — **not**
`Docker.DotNet` (netstandard2.0, huge generated surface, still needs a custom unix-socket handler we
already have in `Platform/Http/SocketHttpClient`). Container naming (`pi-session-{runId}`), the `pi-run`/
`pi-task` labels (Host Monitor discovery), `pi-agent-net` membership, all bind mounts (host paths — the
daemon is on the same host), env (`PI_CALLBACK_*`, `PI_RUN_ID`, review pins, secrets as
`/run/secrets/<name>:ro`), `--init`/`--cpus`/`--memory` limits, and the 90s wait-running deadline carry
over unchanged. **The pi↔Runtime API data plane is untouched**: the Bridge still spawns `pi --mode rpc`
in-container over stdio JSONL and speaks HTTP callbacks/pull channels over the compose network — Komodo
never carried that traffic in `src/` (it did only in the frozen PoC, where it was the PTY-framing
liability this change eliminates). Komodo's implicit concurrency/failure semantics move to us explicitly
(see the four semantics gaps below).

**Spec:** `docs/pi-agent-architecture/01-architecture-and-flows.md` §2/§12 (Runtime API abstracts the
provisioner), `04-api-contracts.md` §3 (provisioning) §4 (bridge data plane), `02-data-model.md` §15
(ContainerInstance); decision amendment to record (next free number): "the Runtime API provisions
containers directly over the Docker Engine socket; Komodo dropped — the single-host decision 35 makes
Komodo's fleet/build/sync story dead weight; the Runtime API now holds a read-write docker socket and
scopes its operations to `pi-session-*`/`pi-run` containers by convention"; rationale + migration sketch
in `docs/komodo-vs-manual-docker.md` (append the decision there).

**Blocked by:** — (09, 10, 13, 14, 16, 17 are implemented; this reshapes their provisioning/reconcile
transport, not their state machines)

**Status:** implemented (2026-11-22), branch `task-22-drop-komodo`, uncommitted pending owner review.
Full suite green: `dotnet test src/PiAgent.slnx` → 2 + 9 + 47 + 582 = 640 passing, 0 failed.
Live smoke against the real engine executed and verified (see Implementation status below).

- [x] New `src/Hosts/RuntimeApi/DockerProvisioningClient.cs` implements the same five-op surface
      (`IContainerProvisioner`, `ContainerPlan`, `DockerProvisioningException`) over
      `Platform/Http/SocketHttpClient`; `KomodoClient.cs` deleted *(single file `DockerProvisioning.cs`
      holds options + contract + client; `KomodoReadinessCheck.cs` also deleted — readyz uses the
      Platform `DockerSocketReadinessCheck`)*
- [x] **404 vs outage distinguished**: container-`inspect`/`stop`/`remove` 404 → container gone
      (observed `removed` / stop/remove count as success); socket connect/5xx failure → transient
      exception, desired state preserved, cycle retries (decision 22 posture) — the reconciler must never
      map a docker outage to a container state change *(pinned by `DockerProvisioningTests`:
      `Stop_ToleratesAlreadyGone`, `Inspect_404_ReportsRemoved_NeverUnknown`, `*_DaemonFailure_Throws_*`;
      reconciler level: `Reconciler_Inspect404_IsRemoved_RunsTheDeathReconciliation`)*
- [x] **Idempotent stop/remove under races**: the three writers (callback delivery, TimeoutSweeper /
      StopConfirmSweeper / reconcilers, root-cleanup remove) all tolerate "already stopped / already
      removed" (304/404/409-after-gone = success); no permanent retry on a container that is simply gone
- [x] **Name collision (409) on create**: stale `pi-session-{runId}` from a crashed provisioning →
      **force-remove and recreate** (owner-approved simplification of "adopt if healthy": the name is
      `pi-session-{runId}` and provisioning is DB-guarded idempotent, so an existing container is stale
      by construction) *(pinned by `Create_NameCollision_*` tests)*
- [x] **Provisioning failure leftovers**: `StartRun` removes a created-but-not-confirmed container on every
      failure path (create-ok/start-fail/wait-timeout), best-effort, before returning 502
- [x] Wait-running via `inspect` poll (`State=running`; `exited`/`dead` → fail) honoring the 90s deadline;
      `HostConfig.Init/NanoCpus/Memory/NetworkMode`, `Env`, `Labels`, `Binds` from `ContainerPlan`
      *(payload pinned by `Create_Payload_CarriesImageEnvBindsLimitsNetwork`)*
- [x] Exit-code enrichment: the reconciler's death-reconciliation message records `inspect`'s
      `ExitCode`/`OOMKilled` when present (while we're in the payload anyway)
      *(verified live: "…(docker exit 137)" after a mid-run `docker kill`; pinned by
      `Reconciler_DiesWithExitFacts_RecordsDockerExitCode`)*
- [x] `ContainerInstance`: `KomodoDeploymentId` → `DockerContainerId`, `DeploymentName` → `ContainerName`
      (+ EF migration `ContainerInstanceDockerColumns` — EF-generated `RenameColumn`, data-preserving;
      write-backs updated)
- [x] Runtime API readyz checks `docker` (`DockerSocketReadinessCheck`), not `komodo`; options shrink to
      `ContainerProvisionerOptions` = `HostId` + `AgentNetwork` + `CallbackBaseUrl` (section `AgentContainer`)
- [x] `502 komodo_failure` → `502 docker_failure` (Orchestrator dispatch treats any 5xx as transient — no
      Orchestrator change); all `IKomodoClient` parameter threading updated (StartRun, RunReconcileService,
      TimeoutSweeper, StopConfirmSweeper, Callbacks, RunPipelineApi root cleanup)
- [x] `src/compose.yaml`: runtime-api mounts `/var/run/docker.sock` **read-write** (it creates/starts/stops/
      removes; Host Monitor keeps `ro`); `Komodo__*` env removed; `docker compose config --quiet` passes
- [x] `src/komodo/` deleted; `task agent-komodo` removed from `Taskfile.yml`; alloy config comment updated;
      Komodo vars removed from `.env.example` and the live `src/.env`
- [x] Tests: `FakeKomodo` → `FakeDocker` (recorders `Created/Stopped/Removed/States/StopSucceeds`, plus
      per-container exit facts); all Hosts.Tests green (582); readyz test asserts `docker`;
      13 `DockerProvisioningTests` pin the 404-vs-outage and 409 rules against a scripted HTTP transport
- [x] **Live smoke pass against the real engine** (mandatory, the step that historically catches hidden
      behavior) — executed 2026-11-22 on this machine and it did catch one: `task stack` up (all Healthy,
      `readyz` → `{postgres:ok, docker:ok}`) → synthetic `/plan` runs via the Orchestrator's
      `POST /internal/commands` hook (routing temporarily plan→stub, reverted after) → container
      created/started by the Runtime API over the socket → Bridge delivered the full callback set on
      `pi-agent-net` → Run `succeeded` with `observed_state=stopped` → a `docker kill`ed run reconciled
      `failed`/`runtime_failure` with `(docker exit 137)` → root cleanup removed containers + storage tree
      → teardown verified (`docker compose ps` and `docker ps` empty).
      **Live finding fixed:** direct `docker stop` is synchronous — on terminal delivery it SIGTERMs the
      Bridge while it is still awaiting the response (HTTP 499, request token cancelled) and the
      observed-machine write was lost; `Callbacks.TerminalAsync`'s post-commit stop block now runs on
      `CancellationToken.None` (terminal facts already committed; the response cannot reach a Bridge that
      is dying by design). Re-verified live after the fix (`succeeded` + `stopped` + `last_stopped_at`).
- [x] Docs: root + `src/README.md` (flow diagram, quick start without `task agent-komodo`, layout table);
      `docs/komodo-vs-manual-docker.md` closed with the decision; decision amendment appended to
      `docs/pi-agent-architecture/05-v1-decisions.md`; frozen tickets/`poc/` untouched

Not exercised (recorded): the real GitHub-comment path (poller → bot mention) — the synthetic
`/internal/commands` hook drives the identical admission → dispatch → runtime pipeline minus the
poller's GitHub fetch, which this task did not touch.

---

## Implementation status (branch `task-22-drop-komodo`, uncommitted — pending user review)

### What was done
- **Provisioner swap (decision 82):** `Hosts/RuntimeApi/DockerProvisioning.cs` — `IContainerProvisioner`
  (create → start+wait-running → inspect → stop → remove) over the unix socket via
  `Platform/Http/SocketHttpClient`; `KomodoClient.cs` + `KomodoReadinessCheck.cs` deleted;
  `ContainerPlan` (proper `Env[]`/`Binds[]`/`Labels{}`/`Cpus`/`MemoryBytes`) replaces the
  newline-joined Komodo strings; `ContainerProvisionerOptions` (`AgentContainer` section:
  `HostId`/`AgentNetwork`/`CallbackBaseUrl`); readyz now checks `docker` (Platform
  `DockerSocketReadinessCheck`). No `Docker.DotNet`.
- **Failure semantics in-house** (the rules Komodo's locks provided):
  - inspect/stop/remove **404 = container gone**: stop/remove are idempotent success; inspect reports
    observed state `removed` (the reconciler's death reconciliation fires on it).
  - **socket/daemon failure throws** — desired state preserved, callers retry (decision 22 posture).
  - **create 409 = stale container** (name is `pi-session-{runId}` and provisioning is
    DB-guarded idempotent) → force-remove (`?force=true`) + create again. Simplification of the
    ticket's "adopt if healthy" wording, per owner decision: any existing container with that name is
    stale by construction.
  - provisioning failure paths remove created-but-unconfirmed containers (best-effort).
- **Live-discovered fix (the smoke caught it):** direct `docker stop` is synchronous — on terminal
  delivery it SIGTERMs the very Bridge that is awaiting the response (request 499s, token cancelled)
  and the observed-machine write was lost. `Callbacks.TerminalAsync`'s post-commit stop block now
  runs on `CancellationToken.None` (the terminal facts are already committed; the response cannot
  reach a Bridge that is dying by design).
- **Exit enrichment:** inspect's `State.ExitCode`/`OOMKilled` recorded in the death-reconciliation
  message ("… (docker exit 137, OOMKilled)").
- **Schema:** `ContainerInstance.KomodoDeploymentId/DeploymentName` → `DockerContainerId/ContainerName`;
  migration `ContainerInstanceDockerColumns` (EF-generated `RenameColumn` — data-preserving);
  start-response and `/status` payload fields renamed (`dockerContainerId`, `containerName`);
  `502 komodo_failure` → `502 docker_failure` (Orchestrator treats any 5xx as transient — unchanged).
- **Runtime API registration:** `AddDockerSocket` + `DockerProvisioningClient` singleton;
  `KomodoOptions`/`"komodo"` HttpClient removed.
- **Infra:** `compose.yaml` — runtime-api mounts `/var/run/docker.sock` **read-write**; `Komodo__*` env
  removed (`AgentContainer__CallbackBaseUrl` kept); `src/komodo/` deleted; `task agent-komodo` removed
  from `Taskfile.yml`; alloy comment updated; `.env.example` (and the live `src/.env`) Komodo vars removed.
- **Docs:** decision 82 appended to `docs/pi-agent-architecture/05-v1-decisions.md`;
  `docs/komodo-vs-manual-docker.md` closed with the decision record; root + `src/README.md` updated
  (flow diagram, quick start, layout, ports); frozen tickets and `poc/` untouched.
- **Tests:** `FakeKomodo` → `FakeDocker` (recorders + per-container exit facts); 13 new
  `DockerProvisioningTests` pin the 404/outage/409/force-remove/payload rules against a scripted HTTP
  transport; reconciler tests pin inspect-404 → `removed` and the exit-fact message; readyz test asserts
  the `docker` check; `InternalsVisibleTo(PiAgent.Hosts.Tests)` on RuntimeApi (test ctor only).

### Tests
`dotnet test src/PiAgent.slnx` → **2 + 9 + 47 + 582 = 640 passing, 0 failed** (Architecture, Platform,
Infrastructure, Hosts incl. Testcontainers postgres).

### Live verification (real engine, `task stack` on this machine)
`task stack-rebuild` + `task stack` → all containers Healthy; `curl localhost:8080/readyz` →
`{"status":"ready","checks":{"postgres":"ok","docker":"ok"}}` (no komodo check). Then the full
pipeline against the real Docker daemon:
- Synthetic command via the Orchestrator's `POST /internal/commands` test hook (routing temporarily
  flipped plan→stub, reverted after) on disposable issues of `aliaksandrZh/04` → admission → image
  build (`agent-stub:v16`) → dispatch → `/start` → **container `pi-session-{runId}` created/started by
  the Runtime API over the socket** → Bridge delivered the full callback set
  (agent_started/turn_*/tool_*/agent_settled/completed) → Run **`succeeded`** — observed_state
  `stopped`, `last_stopped_at` set (the post-fix behavior).
- **Death reconciliation:** a second run's container `docker kill`ed mid-flight → reconciled
  `failed` / `runtime_failure` with "reconciled: the container died without emitting a terminal
  callback (observed by the Runtime reconciler) (docker exit 137)".
- **Root cleanup:** `POST /internal/runtime/workflows/{id}/cleanup` → containers removed from Docker,
  `ContainerInstance` `removed`/`removed`, workflow tree deleted from `STORAGE_ROOT`.
- Teardown: smoke workflows root-cleaned, routing reverted, `task stack-down` → `docker compose ps`
  empty and `docker ps` shows no pi-agent/pi-session/komodo containers (verified — nothing left running).

### Manual test (for owner review)
Prerequisites: Docker running; `src/.env` + `src/secrets/*-pat.txt` present (already are).

```sh
task stack-rebuild && task stack          # ~2–3 min image rebuild, ordered start
curl localhost:8080/readyz                # → {"status":"ready","checks":{"postgres":"ok","docker":"ok"}}
docker compose -f src/compose.yaml exec orchestrator curl -s -X POST http://localhost:8080/internal/commands \
  -H 'Content-Type: application/json' -H 'X-Service-Token: orchestrator-dev-token' \
  -d '{"repositoryId":"<repo-id>","workItemId":"<open-issue-id>","author":"<owner>","commentId":<unique-long>,"text":"@jsxhuz /plan live check"}'
# (repo/work-item ids via: docker compose -f src/compose.yaml exec postgres psql -U pi_agent -d pi_agent \
#   -c "select id from repository; select id from work_item where state='open';")
sleep 30
docker compose -f src/compose.yaml exec postgres psql -U pi_agent -d pi_agent \
  -c "select r.id, r.status, ci.observed_state, ci.last_stopped_at is not null as stop_confirmed from run r join container_instance ci on ci.run_id=r.id order by ci.created_at desc limit 1;"
#   → status 'succeeded', observed_state 'stopped', stop_confirmed 't'
task stack-down                           # nothing left running (verify: docker compose ps → empty)
```

The optional GitHub-comment path (real bot-mention comment instead of the `/internal/commands` hook)
was NOT exercised in this session — the synthetic-comment hook drives the identical admission →
dispatch → runtime pipeline minus the poller's GitHub fetch, which this task did not touch.

**Known cosmetic residue (accepted, recorded):** the Bridge's terminal spool file can outlive the
container (the Bridge dies mid-response after posting `completed` and before deleting it); it is
deleted with the workflow tree at root cleanup and no reconciler reads terminal runs' spools.

---

(Original ticket text below.)

## Implementation status

(not started)
