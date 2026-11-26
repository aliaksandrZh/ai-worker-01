# Per-run session IDs

Give every Run a globally unique pi session id: the Orchestrator generates it when the run row is
created, the run row persists it, the worker exports it as container env, and the Bridge opens pi
with `--session-id <id>`. A run created after a DB reset (new id counter, reused run tree on the
host) can then never open or inherit another run's session context — and every session becomes
resumable by id.

This fixes session-context isolation only. A stale spool terminal from a previous life of the same
run id still settles a fresh run as cancelled; scoping terminal eventIds and validating callback
tokens is a separate change.

## Before you start

Locate the four touch points in your tree:

- the agent spawn block in the Bridge (search for `--session-id` in `agents/_runtime/bridge/bridge.mjs`)
- the run input record `RunInput` in `src/Events/Events/RunEvents.cs`
- the run creation site `CreateRunAndScheduleAsync` and the auto-retry site (`HandleRunFailedAsync`'s
  `CreateRunAsync` call) in `src/Orchestration/Orchestrator.cs`
- the container env export in `src/RunWorker/RunWorkerConsumer.cs` (`PI_RUN_ID=...`)

`RunInput` is serialized as jsonb into `orch_run.input` by `CreateRunAsync` — a new field persists
with no schema migration. Old rows deserialize with the field null; the worker falls back to the
legacy behavior for them.

## 1. Add `SessionId` to the run input

`src/Events/Events/RunEvents.cs` — add the field last (JSON options are camelCase; existing rows
without it deserialize as null):

```csharp
public sealed record RunInput(
    string Owner,
    string Repo,
    long ItemNumber,
    long? PrNumber,
    string Command,
    long PlanCommentId,
    string Profile,
    string? AgentBranch,
    string DefaultBranch,
    string? ReviewHeadSha,
    string? SessionId = null) : IEvent
```

The optional initializer keeps every existing construction site compiling; step 2 fills it.

## 2. Generate the id where the run row is created

`src/Orchestration/Orchestrator.cs` — `CreateRunAndScheduleAsync` is the only place fresh runs are
born; pass the input through a `with` clone carrying the new id:

```csharp
var input = new RunInput(workflow.Owner, workflow.Repo, workflow.ItemNumber,
    PrNumber: task.Type is TaskTypes.Review ? workflow.ItemNumber : null,
    Command: commandText ?? task.Type, PlanCommentId: 0,
    Profile: CommandRegistry.ProfileFor(task.Type),
    AgentBranch: task.Type is TaskTypes.Implement ? agentBranch : null,
    DefaultBranch: DefaultBranchFor(workflow.Owner, workflow.Repo),
    ReviewHeadSha: task.Type is TaskTypes.Review ? workflow.HeadSha : null);
input = input with { SessionId = Guid.NewGuid().ToString("N") };
var runId = await state.CreateRunAsync(taskId, input, uow, ct);
```

**Auto-retry must regenerate.** `HandleRunFailedAsync` re-schedules with `run.Input` copied
verbatim — a retry would inherit the failed run's session and, on a reused tree, resume its
context. Clone there too:

```csharp
var newRunId = await state.CreateRunAsync(task.Id, run.Input with { SessionId = Guid.NewGuid().ToString("N") }, uow, ct);
await log.AppendAsync(new RunScheduled(newRunId, task.Id, workflow.Id,
    run.Input with { SessionId = /* the same value passed to CreateRunAsync */ },
    SecretsFor(run.Input.Profile)), uow.Events, ct);
```

Generate the id once into a local and use it in both calls so the row and the event agree:

```csharp
var retryInput = run.Input with { SessionId = Guid.NewGuid().ToString("N") };
var newRunId = await state.CreateRunAsync(task.Id, retryInput, uow, ct);
await log.AppendAsync(new RunScheduled(newRunId, task.Id, workflow.Id, retryInput,
    SecretsFor(run.Input.Profile)), uow.Events, ct);
```

The session id also rides into `/run/input.json` (the Bridge renders the prompt from it) — the
agent can read its own session id from there.

## 3. Export it as container env

`src/RunWorker/RunWorkerConsumer.cs` — in the env list next to `PI_RUN_ID`, with a fallback that
keeps pre-existing runs (rows created before step 1) on the legacy behavior:

```csharp
var env = new List<string>
{
    $"PI_RUN_ID={scheduled.RunId}",
    $"PI_SESSION_ID={scheduled.Input.SessionId ?? scheduled.RunId.ToString()}",
    $"PI_RUN_TOKEN={token}",
    ...
```

## 4. Open pi with it

`agents/_runtime/bridge/bridge.mjs` — declare it with the other required env (fail-closed, like
`PI_RUN_ID`):

```js
const runId = env.PI_RUN_ID || fail("PI_RUN_ID is required");
const sessionId = env.PI_SESSION_ID || fail("PI_SESSION_ID is required");
```

and use it in the spawn block:

```js
const agent = spawn(agentBin, [
    ...agentArgs,
    "--session-dir", sessionDir,
    "--session-id", sessionId,
], { cwd: workspace, env, stdio: ["pipe", "pipe", "pipe"] });
```

`--session-id` means: use the current directory's existing session with this id, creating it
otherwise. Because the id is a GUID, "otherwise" (a fresh session) is the only branch a new run can
take — even inside a reused run tree. A same-run reprovision (worker restart) takes the other
branch and reopens its own session, which is the desired idempotent recovery.

## 5. Verify

1. Rebuild and start everything:

   ```bash
   task stack-rebuild && task stack && task build-agents
   ```

2. Admit a run (e.g. a `plan` command comment), then check the id traveled the whole chain:

   ```bash
   # run row + RunScheduled event carry it
   docker compose --env-file deploy/.env -f deploy/compose.yaml exec postgres \
     psql -U pi_agent -d pi_agent -c \
     "SELECT id, input->>'sessionId' FROM orch_run ORDER BY id DESC LIMIT 1;"
   # container got it
   docker exec pi-session-<runId> env | grep PI_SESSION_ID
   # pi opened a session file for it — the bridge boot log prints the discovered path
   docker logs pi-session-<runId> | grep "pi session file"
   ```

   The discovered path must reference the new session id; the Bridge reports the same path in
   terminal payloads (`sessionFilePath`).

## Restoring a session (future)

`pi --session <path|id>` reopens a session by file path or id. The terminal payloads already carry
`sessionFilePath`; persisting it on the run row when the terminal is acknowledged is all a restore
path needs on top of this change.

## Gotchas

- The Bridge validates env fail-closed (`PI_RUN_ID is required`): any container started from an
  old image without step 4 crashes at boot — rebuild agent images (`task build-agents`) whenever
  `agents/_runtime` changes.
- `--session-id` is scoped to the working directory the agent spawns in (`/workspace`). Uniqueness
  is only *required* per directory, but the GUID makes it global — that global uniqueness is what
  survives a DB reset plus tree reuse.
- `orch_run.input` rows written before this change deserialize `SessionId` as null; the step-3
  fallback keeps them bootable, but their sessions stay addressable by the legacy run-id-based id.

## 6. Instance-scoped callbacks

The session id above fixes which pi session a run opens; it does not stop a stale callback from
the *previous* life of a run id from settling the fresh run. Two lifetime-bound artifacts key on
the run id today: the Bridge's deterministic terminal eventIds (`evt-<runId>-cancelled` and
friends, spool filenames included) and the spool itself. If a DB is reset while a run tree on the
host still holds an undelivered spool terminal, the fresh run boots, and ~2 s later the old
terminal is redelivered — the run is settled as cancelled before the agent does any work (the
run-5 incident).

Three coordinated changes close that hole:

1. **Terminal ids are scoped by the session id.** Every terminal builder — `cancelledTerminal`
   (`agents/_runtime/bridge/stop.mjs`), `completedTerminal` (`agents/_runtime/bridge/terminal.mjs`),
   and the Bridge's failure terminal (`terminalBody` in `agents/_runtime/bridge/bridge.mjs`) —
   takes the pi session id instead of the run id: ids become `evt-<sessionId>-cancelled` /
   `-terminal` / `-failed`, and each terminal body now carries `sessionId`. A stale spool entry
   from a previous lifetime can therefore never collide with this run's terminal, and its body
   self-identifies as foreign. Non-terminal entries (tool callbacks, events, heartbeats) keep
   their existing run-id-scoped eventIds.

2. **The Bridge opens pi with the session id** (step 4) and declares `PI_SESSION_ID` fail-closed
   like `PI_RUN_ID`.

3. **The worker guards settlement on the session id — strict.** In `HandleCallbackAsync`
   (`src/RunWorker/RunWorkerConsumer.cs`), right after the registry lookup and above the route
   switch: parse `sessionId` from the callback body (tolerantly, like `ParseGitFacts`). If it is
   not exactly the in-flight run's `Input.SessionId` — a mismatch **or a missing one** — log
   `run {run}: callback from session {sid} skipped (current session {cur})` and return without
   appending anything. Sitting above the switch covers completed/failed/cancelled AND the tool
   route (a stale tool callback would otherwise post events on the item stream).

The guard rule: **a callback settles this run only if its body names the run's own session.**
There is no legacy tolerance: a session-less body (pre-fix spool entries, mixed-version bridges)
is not ours and is skipped. A mixed-version deploy (new app-host, old agent images) fails every
run visibly at settlement — rebuild both sides together (`task stack-rebuild && task build-agents`).

4. **Provisioning wipes the run tree's spool.** `RunTree.Ensure` deletes and recreates `spool/`
   (the `.terminal-delivered` marker lives inside it) on every provisioning. A reprovision means
   the previous attempt never settled, so its undelivered entries belong to a dead attempt — and
   pre-fix entries carry no sessionId, so the guard alone cannot attribute them. The wipe removes
   them at the source; the guard stays as the second line for anything that arrives after the
   wipe.
