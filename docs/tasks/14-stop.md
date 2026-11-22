# 14: /stop — graceful cancellation

**What to build:** The `/stop` control path: Orchestrator → Runtime API → Bridge with the graceful-stop sequence (RPC `clear_queue` first, then `abort`/`abort_bash`, wait up to ~30 s for `agent_settled`, close Pi stdin, SIGTERM then SIGKILL if needed, verify child processes stopped, post the terminal `cancelled` callback); Run + Task marked `cancelled` after runtime confirmation or reconciliation; ALL queued Tasks of the same Workflow cancelled with their Jobs; system notification via the Outbox (one comment may cover active+queued cancellations); Workflow → `waiting_for_human`; session/log/workspace storage retained; "nothing to stop" reply when there is no work; other Workflows never touched; the WorkItem itself never closed.

**Spec:** `docs/pi-agent-architecture/06-command-reference.md` §8; `03-state-machines.md` §2, §5; `04-api-contracts.md` §4 (graceful stop sequence); `05-v1-decisions.md` decisions 29, 38, 61; `01-architecture-and-flows.md` §10 (retained storage).

**Blocked by:** 12

**Status:** implemented on branch `task-14-stop` (uncommitted, pending owner review).
Owner decisions recorded before/while implementing (2026-11-22):
- **Ticket 13 will NOT be implemented** (owner) — so the "or reconciliation" leg of "Run + Task
  `cancelled` after runtime confirmation or reconciliation" moved INTO this ticket as a narrow
  stop-confirm sweep in the Runtime API (no general timeouts/idle reconciliation anywhere).
- **Bridge control channel = HTTP pull** (`GET /internal/runtime/runs/{runId}/stop-request`, per-Run
  callback token; signal state lives in PostgreSQL `run.stop_requested_at`/`run.stop_reason` — new
  migration `AddStopSignal`), not a file dropped on the mounted run tree.
- **Terminal `cancelled` callback = a new per-Run route** `POST /internal/runtime/runs/{runId}/cancelled`
  (mirrors `failed`; RunEvent type `cancelled` from the 02 §18 vocabulary).
- **`POST …/stop` semantics changed** (replaces ticket 09's immediate Komodo stop): the endpoint
  records the signal once + moves the Run `stopping`; it never stops the container itself.
- Notification/reply wording as implemented in `StopReplies`.

Automated verification: `dotnet test src/PiAgent.slnx` **559/559 green** (Architecture 2, Platform 9,
Infrastructure 47, Hosts 501) plus `node --test src/config/agents/stub/runtime-bridge/*.test.mjs`
**32/32 green** (26 pre-existing stub/bridge tests + 6 new stop tests, incl. a full-bridge stop e2e
against a fake Runtime API over a real spool/log tree). `docker compose config --quiet` OK (no
compose/env changes needed). **The live-stack walkthrough has NOT been executed in this session — it
needs Komodo + the stack up (owner go-ahead); every live step in "How to manually test" is unverified.**

- [x] `/stop` during a running planner Task → Bridge executes the full stop sequence; Run + Task `cancelled`; confirmed by terminal callback or reconciliation — *sequence + confirmation automated-tested end to end (bridge e2e + Runtime API + Orchestrator suites); live with real Pi/Komodo unverified*
- [x] Queued Tasks of the Workflow (with Jobs) are cancelled too — *automated: task/job `cancelled`, trigger command `failed (stop_requested)`, pending Run rows deleted (session/container cascaded); claimed Tasks cut through as well (decision 38); the claim-vs-/stop race is guarded (dispatcher `Superseded` + claim-cancel no-op)*
- [x] Cancellation notification published via the Outbox (exactly once for active+queued) — *automated; idempotency key = the `/stop` command row id, so replays cannot duplicate*
- [x] Storage (session files, logs, workspace) retained after stop — *automated (nothing deletes; workspace `Ready`, log trio + session rows kept); live unverified*
- [x] `/stop` with nothing running → "nothing to stop" reply; Workflow and storage untouched — *automated (including the no-Workflow-at-all surface)*
- [x] Other Workflows unaffected; the GitHub Issue/PR is not closed — *automated tests never write other-Workflow rows; no Issue/PR write code exists on this path (06 §8.8 — no close, no root cleanup)*
- [ ] Acceptance scenario "Stop" (`05` matrix) passes end to end — *requires the live walkthrough (owner)*

## What has been done (2026-11-22, agent)

- **Bridge (shared modules, byte-identical stub/planner copies, bridge version 1.1.1 on both profiles):**
  - new `stop.mjs`: `pollStopRequest` (per-Run bearer pull, 2 s cadence), `gracefulStopSequence`
    (RPC `clear_queue` → `abort` → `abort_bash` → wait ≤30 s for `agent_settled` → close stdin →
    SIGTERM → SIGKILL → snapshot/verify `/proc` child pids, SIGKILL survivors, log), and
    `cancelledTerminal` (deterministic `evt-<runId>-cancelled` id so redelivery can never duplicate).
  - `bridge.mjs`: the stop watch starts at spawn; once the signal is observed the stop path OWNS the
    terminal — checkpoints release the RPC correlator around the prompt handshake (pre-prompt check
    skips the task prompt; an agent exit while a stop is pending never posts `failed`); the normal
    tail posts `cancelled` instead of `completed`/manifest when a stop was pending; `finishCancelled`
    is idempotent. `deliverNext` treats `cancelled` as a terminal route (unbounded retry, done
    marker) and accepts "already terminal" rejections (409 / 403 `token_revoked`) as delivered —
    that covers a late Bridge terminal for a run the stop-confirm sweeper already reconciled.
  - `spool.mjs`: route `cancelled` writes the `.terminal-delivered` marker.
  - stub agent: `clear_queue`/`abort`/`abort_bash` respond pi-shaped; `abort` settles an in-flight
    run with `agent_settled {aborted:true}` and exits (the `hang` behavior is now the deterministic
    stop lever).
- **Runtime API:**
  - migration `AddStopSignal` (+ entity fields `Run.StopRequestedAt`/`StopReason`).
  - `POST /internal/runtime/runs/{runId}/stop` (service token): records the signal once
    (idempotent, including replays after Orchestrator transaction rollbacks) and moves the Run to
    `stopping` (decision 75: slot stays held awaiting confirmed stop). No Komodo call here.
  - `GET /internal/runtime/runs/{runId}/stop-request` (per-Run bearer): the Bridge pull channel.
  - `POST /internal/runtime/runs/{runId}/cancelled` (per-Run bearer): terminal callback — RunEvent
    `cancelled` persisted before ack, Run `cancelled`, log-trio reference artifact, per-Run
    credential revoked (decision 60), session `terminated`, decision-57 session path write-back,
    container stop, workspace `Ready`.
  - new `StopConfirmSweeper` worker: a Run still `stopping` with `stop_requested_at` older than the
    90 s grace window and no terminal callback gets the hard backstop — Komodo stop (idempotent;
    a Komodo outage defers, decision 22 posture), Run `cancelled` with the same terminal facts,
    then the Orchestrator's terminal-Run consumer completes Task/Workflow as usual.
- **Orchestrator:**
  - new `StopCommandHandler` (dispatched by `ControlCommandHandler` for `/stop`): 
    "nothing to stop" reply + zero row changes when no unfinished Task exists (06 §8.8 — no Workflow
    creation); queued/claimed Tasks cancelled immediately (guarded state-aware writes: Task
    `cancelled` + `stop_requested`, Job `cancelled`, trigger command `failed`, pending Run rows
    deleted with cascades); starting/running Tasks signalled via the Runtime API inside the command
    transaction (same HTTP-in-transaction precedent as the collaborator permission gate) with Task
    left `running` until runtime confirmation; Workflow → `waiting_for_human` + `CurrentTaskId=null`
    only when nothing active was signalled; exactly one Outbox notification covering active+queued.
    A transport/signal failure rolls the whole command back and releases the comment event for an
    idempotent retry (the signal recording is a one-time no-op on replay).
  - `RunTerminalConsumer`: a `cancelled` Run records `task.FailureReason = stop_requested` (the only
    V1 canceller is /stop) — existing Task/Job/Workflow/command transitions unchanged.
  - new first-canceller race guards: `RuntimeApiRunDispatcher` re-verifies the Task before the
    `/start` handoff and returns the new `Superseded` outcome (prepared pending rows deleted, no
    Runtime API call); `TaskLifecycle.CancelTaskAtClaimAsync` no-ops (no spurious claim-cancellation
    notice) when the Task is already terminal.
  - `RuntimeApiClient.StopRunAsync` (decision-65 service credential).
  - the `/internal/commands` hook handles `/stop` identically (test lever).
- **Stub profile stays byte-compatible**: the stop lever rides the same `STUB_BEHAVIOR=hang` (the
  routing rule for tickets 13–15 levers still points at `stub` unless flipped per ticket 12's note).

**Acceptance-relevant states verified in tests**: Run `stopping` occupies the capacity slot and
blocks the next Workflow Run (existing slot/serialization logic sees `stopping`); `/status` during a
stop mirrors the persisted RunEvents (terminal `cancelled` event visible); repository pause and other
Workflows are untouched by the path.

## How to manually test

> **Verification state:** automated verification complete (559 dotnet + 32 node, commands above).
> **NOTHING below has been executed live in this session — needs Komodo + the stack (owner go-ahead).**
> Prerequisites: the standard setup (src/.env, both PAT files, Komodo up) + Docker reachability for
> ghcr.io/github at build/run time + the Ollama app running on the host (OBS-9) + the
> `host.docker.internal` reachability note from ticket 12. The stack must be REBUILT (`task stack-rebuild`)
> because the bridge modules and both profile versions changed (1.1.1) and the orchestrator/runtime-api
> images carry the new code + the `AddStopSignal` migration (applies automatically at migrator start).

1. ϩ `task stack-rebuild && task stack` (wait green). Verify the new profile versions were created
   (both stub and planner bumped to bridge 1.1.1 → Config Loader creates new versions → Build Worker
   rebuilds `agent-stub:v<n+1>` and `agent-planner:v<n+1>`):
   ```sh
   docker compose --env-file src/.env -f src/compose.yaml logs orchestrator --since 10m | grep -i "planner\|stub"
   #   expected: "<name> vN: ready, container_image=agent-<name>:vN, sha256:... job completed" for the bumped versions
   ```
2. ϩ Inject `/plan` on an open registered Issue (real comment `@<bot> /plan`, or the internal hook
   per ticket 09's step 5). Once the run is underway (runtime-api log line
   `run <runId> started: deployment pi-session-<runId> ...`), inject `@<bot> /stop` as a REAL GitHub
   comment (a collaborator with write; the hook path works too but does not exercise the GitHub
   permission lookup). Expected end state (SQL per ticket 09's diagnostics map):
   - `run.status='cancelled'`, `run.stop_requested_at IS NOT NULL`, `run.stop_reason='stop_requested'`,
     `run.error_code IS NULL`; ONE `run_event` of type `cancelled` whose payload carries the stop
     reason + the discovered session path; `agent_started`/event rows before it as usual;
   - `task.status='cancelled'`, `task.failure_reason='stop_requested'`, `task.cancelled_at` set;
     `agent_command` rows: the `/plan` trigger command `failed` with `run_cancelled`; the `/stop`
     command `processed`;
   - `workflow.status='waiting_for_human'`, `current_task_id IS NULL`; `container` observed
     `stopped` + `last_stopped_at`; `run_credential.revoked_at` set; session `terminated`;
   - GitHub side: exactly ONE bot comment — the `/stop` notification mentioning the active task
     (`Active task /plan (attempt #…): graceful stop dispatched…`) + the retention line; NO task-failure
     comment, NO duplicates; the Issue stays open;
   - host tree: `src/storage/workflows/<wf>/runs/<run>/pi-session/…jsonl` + the log trio all still
     present; the task workspace directory intact (storage retained);
   - `bridge.log` ordering: `stop signal observed (stop_requested)` → `stop sequence: clear_queue,
     abort/abort_bash, then settle wait` → `agent lifecycle done (…)` → `stop sequence complete —
     agent process tree verified stopped` → `terminal persisted by the Runtime API — bridge done`.
3. ϩ Repeated `/stop` while the same run is stopping: the reply/notification is not duplicated (idempotency
   key = the `/stop` command id) and `run.stop_requested_at` keeps its original timestamp.
4. ϩ Stop with nothing running: on an Issue whose Workflow is `waiting_for_human` (or none at all),
   inject `@<bot> /stop` → exactly one comment `… nothing to stop …`; NO new Workflow rows, NO
   cancelled rows, GitHub untouched otherwise.
5. ϩ Queued-task stop: queue a stub task (e.g. flip routing to `stub`, inject `/plan` while the
   queue is busy so the Task stays `queued`), then `/stop` → the Task/Job turn `cancelled`
   (`stop_requested`), pending Run rows and their session/container rows vanish, the trigger command
   fails with `stop_requested`, Workflow `waiting_for_human`, one notification with
   `Queued tasks cancelled with their jobs: 1`. Flip the routing rule back afterwards.
6. ϩ Reconciled stop (no Bridge confirmation): start a stub run with `STUB_BEHAVIOR=hang` (a profile
   edit + rebuild) and `/stop` it in a way that prevents the Bridge from finishing (e.g. observe that
   the sweeper only acts after 90 s — for a DEMONSTRATION, temporarily lower
   `StopConfirmSweeper.GraceWindow`), and check: after the window, the Runtime API log shows
   `never confirmed its stop … — reconciling cancellation`, the run turns `cancelled` with
   `stop unconfirmed…` in `error_message`, the container is stopped by the sweeper, and the terminal
   facts (log artifact/credential/session) look exactly like a confirmed stop. Restore the constant.
7. ϩ Teardown: `task stack-down`; Komodo down (if started); verify nothing remains:
   `docker ps --filter name=pi-session- --format '{{.Names}}'` and `--filter name=pi-agent` → both
   empty (volumes kept — dev DB state by design).

**Unverified in this session** (every ϩ step above, plus): the real-Pi `abort`/`clear_queue`
behavior inside the planner image (the real-pi record shapes were probed one-shot in ticket 12; the
STOP RPCs were verified against the stub only), the real Komodo container stop through the terminal
`cancelled` delivery, the Outbox comment text rendering against a real repository, and the sweeper
live (its mechanics are covered by `StopConfirmSweeper_ReconcilesRunsThatNeverConfirmedTheirStop`).
