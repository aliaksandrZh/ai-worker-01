# Orchestrator

> The component that drives the four flows ([flows/README.md](flows/README.md)): it
> consumes events, decides what work happens next, owns all orchestration state, and
> publishes agent output to GitHub. The current codebase (`src/Hosts/Orchestrator`) is
> the **reference**; this document describes the **simplified** target — divergences are
> listed in [Simplifications vs the current code](#simplifications-vs-the-current-code).
> Reference symbols are cited inline as `(ref: …)`.

## 1. Responsibility and invariants

The Orchestrator is the only writer of orchestration state and the only publisher to
GitHub. It owns:

- the event pipeline (admission → validation → schedule or park);
- the Workflow → Task → Run hierarchy and all its transitions;
- the Orchestrator policies (round caps, auto-retry, plan approval, manual acceptance);
- publication of agent artifacts and user-facing notifications.

Invariants:

1. **One active Task per Workflow** — the four unfinished Task states occupy the single
   unfinished slot (ref: `OrchestrationTask.cs`, partial unique index).
2. **Agents never write to GitHub** — publication goes through the Orchestrator
   (ADR-0001); the sole exception is the Coder's branch push.
3. **No Run without a Task** — every Run belongs to a Task; the Task decides what a
   Run's outcome means for the flow.
4. **Permissions travel in the event** — the Poller resolves the command author's
   permission at detection ([ADR-0006](adr/0006-github-sync.md)); the Orchestrator
   gates on the payload and never reads GitHub.
5. **Events are the only input** — the Orchestrator reacts to nothing else
   ([flows/events.md](flows/events.md)); it holds zero GitHub knowledge.

## 2. Event pipeline

Every event from [flows/events.md](flows/events.md) goes through the same pipeline:

```
event → admission → validation → schedule (run_scheduled) or park (waiting_for_human)
      → run worker dispatch → run settles (report_completion or not)
      → next transition (next Task · retry · park · workflow completion)
```

- **Admission** decides *is this event for us*: permission and label gates were
  resolved by the Poller and travel in the payload; the Orchestrator checks routing
  rules and the Workflow state gate (a command on a closed Workflow is rejected, not
  scheduled). Rejections are recorded and notified, never dropped silently
  (ref: `CommandAdmission.cs`).
- **Validation** decides *is it actionable now*: for commands — the item carries the
  right AI-scope label, no unfinished Task occupies the slot; for tool events — the Run
  that produced them still matches the active Task.
- **Scheduling** creates or reuses the Task for the mapped flow stage and puts its Run
  in the queue; the run worker claims it when capacity allows.
- **Parking** moves the active Task to `waiting_for_human` (escalations, decisions,
  manual acceptance); the matching user event returns it to the queue.

## 3. State machines

### Workflow — the work on one root item (issue)

```
active ──► cancelled        (user_cancel)
active ──► closed           (item_closed; reopening the root never resurrects it)
```

A Workflow holds the Tasks of one root item; while a Task parks in
`waiting_for_human`, the Workflow stays `active`.

### Task — one flow-stage execution

Task types = flow stages: `plan`, `implement`, `review`, `test_design`
(ref: `OrchestrationTaskTypes`).

```
queued ──► processing ──► succeeded        (report_completion received)
                 │────► failed             (Run failed without one, or all retries spent)
                 │────► cancelled          (user_cancel, claim-time recheck)
                 └────► waiting_for_human  (escalation, decision, manual acceptance)
waiting_for_human ──► queued               (@bot /resume / user_decision with fresh context)
failed ──► queued                          (auto-retry, new Run, attempt counter on the Task)
```

The flow-chain mapping: when the Task of one stage succeeds, the Orchestrator creates
the next Task per the fixed order (plan → implement → review → test_design); a fixing
round creates a new `implement` Task against the same PR.

### Run — one agent execution

```
pending ──► running ──► succeeded          (settled with report_completion)
                 │────► failed             (settled without it, or never started)
                 │────► cancelled          (stopping on user_cancel)
                 └────► timed_out          (Runtime-side watchdog)
```

Runs execute sequentially within a Task (ref: `Run.cs`). The run worker owns dispatch
and settlement; the Orchestrator consumes the terminal events (`run_completed`,
`run_failed`) and applies the Task transition (ref: `RunTerminalConsumer.cs`).

## 4. Policies

Applied while processing events, never as flow nodes (table mirrored in
[flows/README.md](flows/README.md)):

| Policy | Applied when |
|---|---|
| Round cap on review ↔ fixing cycles and implement iterations | a next Task would exceed the cap → Task `waiting_for_human` |
| Auto-retry of a failed Run | Orchestrator decides: new Run of the same Task vs park |
| Plan approval required? | the `plan_saved` event is processed |
| Manual acceptance of test results required? | a `test_design` Task succeeds; if required → park, route to the User |

## 5. Publication and notifications

- **Publication** (ADR-0001): the plan as an issue comment (`plan_saved`), review
  comments on the PR (`review_comments_submitted`), bug reports on the issue
  (`bug_reported`), and PR creation/update after an `implement` Run succeeds.
- **Notifications** go out through the durable Outbox with retry and dead-letter
  handling (ref: `OutboxAction`): task failures, command rejections, cancellations,
  cap exhaustion. A notification is output, never an input event.

## 6. Failures and cancellation

- **Run fails** → policy: a new Run of the same Task, or the Task parks in
  `waiting_for_human` with a user notification.
- **Task fails** (retries spent) → the single slot frees; the Workflow stays `active`
  and the failure is notified. A new command starts fresh work; a parked task
  re-enters the queue only on `@bot /resume` — a plain comment never does (a bot's own
  failure notification must not wake the parked Task).
- **`user_cancel`** → the running Run is stopped (`running → stopping → cancelled`),
  the Task turns `cancelled`, the Workflow turns `cancelled`; all related scheduled
  work is dropped (ref: `StopCommandHandler.cs`).
- **`item_closed`** → the Workflow closes; database history is retained, execution
  storage is delegated to Runtime API (ref: `RootClosureCycle.cs`). In-flight runs
  stop, as on cancel.

## 7. Boundaries

All consumers (Poller, Orchestration, Run Worker, Publisher, Build Worker) are
in-process workers of one app host ([ADR-0004](adr/0004-one-app-host.md)); the table
below is about module seams, not processes.

| Neighbor | Direction | Contract |
|---|---|---|
| **Poller** | in | parsed trigger events (`user_command` with author permission) + sync events; it owns the command grammar and every GitHub read |
| **Run Worker** | in/out | consumes `run_scheduled` / `run_stop_requested`, emits terminal events; it owns container execution, sessions, and the run watchdog ([run-worker.md](run-worker.md)) |
| **Outbox** | out | durable user-facing notifications |
| **Build Worker** | out | durable build jobs for profile versions; Orchestrator validates the result and flips the version `ready` / `build_failed` |
| **Config plane** | in | YAML (system, repositories, routing, projects) loaded at startup; desired configuration lives in files, never in the DB |

## Simplifications vs the current code

- **`waiting_for_human` moves Workflow → Task** (in code it is a Workflow status): the
  parked thing is the flow-stage execution; the Workflow stays `active`.
- **Task states collapse**: `claimed` and `starting` fold into `processing`; `blocked`
  folds into `waiting_for_human`.
- **Workflow states collapse** to `active / cancelled / closed`; the code's `failed`
  Workflow state is expressed at Task level (a failed Task frees the slot).
- **The scanner cycles become event handlers**: `LabelTriggerCycle`,
  `ReviewTriggerCycle`, `ProjectEventLogCycle`, `BoardStatusOutbox`, `RootClosureCycle`,
  `RetryCommandHandler`, `StopCommandHandler`, `ClaimCancellationNotices` are all
  reactions to events from [flows/events.md](flows/events.md) inside the single
  pipeline — not parallel schedulers.
- **The GitHub mirror dies** ([ADR-0006](adr/0006-github-sync.md)): normalized GitHub
  entities, their mappers and the claim-time recheck are deletions; the Orchestrator
  reads no GitHub state.
- **ContextAssembler and the RunContextSnapshot are deleted**: agents read their
  context from GitHub through their read tools; the Run input is pointers (item, PR,
  plan, command); byte-exact snapshots and token budgets (OBS-6/OBS-8) go with it.
