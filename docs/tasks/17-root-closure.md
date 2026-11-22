# 17: Root closure, merged PR, reopen policy

**What to build:** The root WorkItem closure flow: close/merge of the root Issue or PR → workflow marked `closed` and new commands rejected; active Tasks/Runs cancelled; containers stopped and removed (Komodo deployments); the Workflow storage tree (task workspaces, per-Run `pi-session/` and `logs/`) deleted after containers stop; DB history retained with cleanup/deletion flags (PostgreSQL records never auto-deleted). Merged PR treated as a closed WorkItem (no separate merged event). Reopen policy: a reopened Issue/PR leaves the Workflow `closed`, no second Workflow is created (`UNIQUE(work_item_id)`), commands for the reopened item rejected — root-closure events and reopen detection from Poller sync (including `work_item_closed`/`close_reason = merged` events). Issue and its related PR remain separate roots — closing one touches nothing of the other.

**Spec:** `docs/pi-agent-architecture/01-architecture-and-flows.md` §11 (closure + reopen); `02-data-model.md` §9 (workflow), §22 (root closure + retention); `03-state-machines.md` §10; `05-v1-decisions.md` decisions 20, 21, 40, 73 (reopen), 76 (retention).

**Blocked by:** 12, 05

**Status:** implemented on branch `task-17-root-closure` (staged uncommitted, pending owner review).
Automated verification: `dotnet build src/PiAgent.slnx --no-restore` 0 warnings/0 errors;
`dotnet test src/PiAgent.slnx --no-build --no-restore` **612/612 green** (Architecture 2,
Platform 9, Infrastructure 47, Hosts 554). Live verification: the full Compose stack plus the
Komodo stack were started, rebuilt (`task stack-rebuild`), and the new cleanup endpoint was
exercised against a disposable workflow; everything was stopped afterward and zero stack
containers remained (see "What has been done"). No live Komodo deployment (agent container)
removal was exercised — that path is covered by the FakeKomodo integration tests only.

## What has been done

- **Runtime API cleanup endpoint** (`src/Hosts/RuntimeApi/RunPipelineApi.cs`, `RootCleanupHandler`):
  `POST /internal/runtime/workflows/{workflowId}/cleanup` (service-token guarded). Per workflow it
  stops every non-stopped Komodo deployment (`StopDeployment`) and removes it (`DeleteDeployment`),
  writing `ContainerInstance` desired/observed `removed` + `RemovedAt`; refuses (409
  `cleanup_pending`) to delete storage while any deployment failed to stop/remove so cleanup is
  retried without data loss. It then deletes the whole `{ManagedRoot}/{workflowId}` tree
  (`RuntimeStorageService.DeleteWorkflowTree`) and records retention flags on retained DB rows:
  `Workspace.storage_status`/`status='deleted'`/`deleted_at`, `AgentSession.storage_status`/
  `file_deleted_at` — `missing` (not `deleted`) when the tree was already absent. DB rows are never
  removed. Komodo `IKomodoClient.RemoveAsync` was added (`src/Hosts/RuntimeApi/KomodoClient.cs`).
- **Orchestrator closure cycle** (`src/Hosts/Orchestrator/Orchestration/RootClosureCycle.cs`, wired
  in `Program.cs`): consumes pending `work_item_closed` GitHubEvents (Poller-written, including
  `close_reason = merged` with no separate merged event). Per root: Workflow → `closed`
  (`closed_at` set), unfinished Tasks → `cancelled` with reason `work_item_closed` (+ their
  OrchestrationJobs and triggering AgentCommands), non-terminal Runs stop-requested via the Runtime
  API and then marked `cancelled` (root closure is final — no Bridge callback can follow a removed
  deployment), then the Runtime API cleanup is invoked; `WorkItem.cleanup_status = 'completed'`.
  Failures keep the event `pending` (retried next tick). A second close (reopen → close again) is
  idempotent: the unique Workflow row stays `closed`, no second Workflow (`UNIQUE(work_item_id)`),
  nothing re-cleans. Related Issue/PR roots are untouched (closure looks up only the event's own
  WorkItem). Cleanup is silent — no Outbox notification (decision 29). Admission already rejects
  commands for closed items/Workflows with `work_item_closed` (existing gates, tests listed below).
- **Orchestrator client**: `RuntimeApiClient.CleanupWorkflowAsync` (`RunDispatch.cs`).
- **Tests** (`src/tests/Hosts.Tests/Runtime/`):
  - `RootCleanupTests` — live Runtime API + FakeKomodo + disposable Postgres: stop/remove, storage
    tree deletion, `deleted` flags, retained rows; missing-tree → `missing` flags.
  - `RootClosureCycleTests` — closed event closes Workflow, cancels Tasks/Jobs/Run with
    `work_item_closed`, invokes cleanup, marks `completed`, no Outbox rows (silent, decision 29);
    reopen + second close stays idempotent (1 Workflow, both events processed); closing the Issue
    leaves the related PR's Workflow `active` with `cleanup_status` NULL; merged-PR closure cleans
    identically and keeps `merged_at`.
  - Reopen/command-rejection coverage already existing: `AdmissionTests.ClosedWorkItem_RejectedWorkItemClosed`,
    `RetryCommandTests.Retry_OnClosedWorkItem_RejectedWorkItemClosed`,
    `JobQueueWorkerTests.Recheck_WorkflowClosedWhileQueued_CancelsWorkItemClosed`.

## How to manually test

Prerequisites: Docker running; `src/.env` present (`task stack` creates it from the example);
PAT files in `src/secrets/` (already present locally); .NET 10 SDK for the automated suite.

1. Automated suite:
   - `dotnet build src/PiAgent.slnx --no-restore` → expect `Build succeeded. 0 Warning(s) 0 Error(s)`.
   - `dotnet test src/PiAgent.slnx --no-build --no-restore` → expect 612 passed, 0 failed
     (Architecture 2, Platform 9, Infrastructure 47, Hosts 554). Real output from this session:
     `Passed! - Failed: 0, Passed: 2 / 9 / 47 / 554`.
2. Live cleanup (executed in this session, commands + outputs copied from the real run):
   - Start the stacks: `task stack` and
     `docker compose --file src/komodo/ferretdb.compose.yaml --env-file src/komodo/compose.env up -d --wait`
     (all services reach `Healthy`).
   - Check readiness: `curl -fsS http://127.0.0.1:8080/readyz` →
     `{"status":"ready","checks":{"postgres":"ok","komodo":"ok"}}`.
   - Create a disposable root + workflow (fresh GUIDs, touches no existing data) and a fake storage
     tree `src/storage/<workflowId>/{runs/.../logs, tasks/.../workspace}`:
     insert one `repository`, one `work_item`, one `workflow (status='closed')` via psql in
     `pi-agent-control-plane-postgres-1`.
   - Call cleanup:
     `curl -H 'X-Service-Token: orchestrator-dev-token' -X POST http://127.0.0.1:8080/internal/runtime/workflows/<workflowId>/cleanup`
     → real output: `{"workflowId":"...","storageStatus":"deleted","deletedAt":"2026-11-22T11:26:21.0851542Z"}` HTTP 200;
     the storage tree is gone; `work_item.cleanup_status='completed'`; `outbox_action` count for the
     item = 0 (silent).
   - Tear down: `task stack-down` and the komodo `down`; verify
     `docker ps --format '{{.Names}}' | grep -E 'pi-agent|komodo'` → no output.

Unverified (needs a real agent Run through Komodo, not executed here): removal of an actual live
Komodo deployment/container during cleanup — the stop/remove RPC sequence is covered by FakeKomodo
tests only; unpushed-work-after-cleanup remains documented accepted behavior (ticket 18).

- [x] Closing the root Issue → Workflow `closed`; commands rejected; containers removed; storage tree deleted; DB rows retain history with flags
- [x] Storage status recorded at cleanup: `Workspace.storage_status`, `AgentSession.file_deleted_at`/`storage_status` — including `missing` for files already absent — plus cleanup/deletion timestamps; DB rows always retained (`02 §13`, `02 §16`, `02 §22`)
- [x] Closing the related Issue does not close/clean the PR Workflow (and vice versa) — separate roots
- [x] Merged PR → cleanup identical to closure; DB keeps `merged_at`
- [x] Reopen → Workflow stays closed, no second Workflow, commands rejected with a recorded reason
- [x] Cleanup is silent (no GitHub notifications per decision 29)
- [x] Acceptance scenario "Cleanup" (`05` matrix) passes
