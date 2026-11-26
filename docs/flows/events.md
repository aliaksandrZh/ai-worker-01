# Events

> Events are delivered through the Postgres Event Log ([../eventlog.md](../eventlog.md),
> [ADR-0002](../adr/0002-event-log.md)) — this document is their vocabulary, not their
> transport.
>
> An **event** is anything the Orchestrator must react to: a user action, a GitHub state
> change, a board/label transition, or an agent's tool call. The Orchestrator consumes
> events, updates Workflow state, and starts or restarts the mapped flow — a restarted flow
> is always a **new Run** with freshly assembled context. There is no "receive" tool: this
> list is the complete input surface of the flows. Every "Waiting for Human" state in the
> flow diagrams is released by exactly one user event from this list.
>
> Every accepted event goes through the Orchestrator, and exactly one of two things
> happens: it schedules a Run (`run_scheduled` — the only way an agent Run ever starts)
> or it only changes flow state without one (`waiting_for_human`, cancel, close).

## User events

| Event | Produced by | Effect |
|---|---|---|
| `user_command` | the Poller parses a slash command on an issue or PR (grammar, author permission and resolved target travel in the payload — the trigger set is defined separately) | starts the mapped flow (→ `run_scheduled`) |
| `user_reply` | the User answers a clarification, blocker, or env-issue request | resumes the paused flow: new Run of the waiting agent with the response |
| `user_decision` | plan approved / changes requested; review escalation resolved (fixes / change scope / accept risk); test results accepted / bug found / does not meet requirements | routes the decision to the flow stage that asked for it |
| `user_cancel` | the User cancels the workflow | all related events, tasks, and runs stop: the running container is stopped immediately, the Run is marked failed-by-cancel (no retry), the Workflow → cancelled |

## Sync events (Poller → EventLog)

Raw normalized GitHub state changes — the only GitHub facts any other module consumes
([../github-sync.md](../github-sync.md)); the Poller derives `user_command` from parsed
comments. Stream key: the root item, else the repo.

| Event | Carries |
|---|---|
| `item_changed` | issue/PR: title, body, state, labels — the item's whole visible state; the eventId fingerprints all of it |
| `item_closed` | explicit terminal beat when the root item closes — closes the Workflow; in-flight runs stop, as on `user_cancel` |
| `comment_changed` | added / edited / **deleted** (tombstone flag) |
| `review_added` | submitted review: verdict + inline comments |
| `pr_updated` | new commits on the PR — starts the next review round |
| `project_item_changed` | board card: column, link to its item |

## Agent tool events

Every communication tool call inside a Run produces exactly one event — the tool call is
the mechanism, the event is what the Orchestrator consumes. Escalation events park the
flow in `waiting_for_human`; the matching user event (`user_reply` / `user_decision`)
releases it.

| Event | Produced by tool | Effect |
|---|---|---|
| `plan_saved` | `save_plan` | publishes the plan as a comment in the issue, runs the approval step |
| `clarification_requested` | `request_clarification` | `waiting_for_human`, routes the answer back to the Planner |
| `blocker_reported` | `report_blocker` | `waiting_for_human` |
| `review_comments_submitted` | `submit_review_comments` | publishes the comments to the PR, starts a fixing Coder Run |
| `review_decision_requested` | `request_decision` | `waiting_for_human` |
| `bug_reported` | `report_bug` | publishes the report as a comment in the issue, assigns the fix to the Coder |
| `env_issue_reported` | `report_env_issue` | `waiting_for_human` |
| `criteria_clarification_requested` | `request_criteria_clarification` | `waiting_for_human`, routes the answer to the Planner |
| `tests_passed` | `report_tests_passed` | manual acceptance policy check |

## Publication events (Orchestrator → Publisher)

The Orchestrator's output channel to the Publisher consumer
([../publisher.md](../publisher.md)): one event per artifact, deterministic idempotency
key, riding the root item's stream.

| Event | Covers |
|---|---|
| `publish_comment` | plan, bug report, notification — an issue/PR comment |
| `publish_pr` | PR create/update |
| `publish_review` | review outcome: approve / request changes |
| `publish_board_move` | Projects v2 card status |

Labels are never written — AI-scope labels are read-only triggers.

## Run lifecycle events

Every accepted event that needs agent work — a `user_command`, a resuming
`user_reply` / `user_decision`, `pr_updated`, a converging board/label trigger, a chaining
agent-tool event (`review_comments_submitted`, `bug_reported`), or a settled predecessor
Run — is validated by the Orchestrator and turned into `run_scheduled`. The run worker
owns the rest of the lifecycle: it claims a scheduled Run when capacity allows; if the Run
cannot start (no free capacity, missing image, unavailable environment), the worker
refuses and emits `run_failed` — the Orchestrator then pings the User. Whether to
auto-retry, apply round caps, require plan approval or manual acceptance — all of these
are Orchestrator policy decisions made while processing events, not flow nodes.

| Event | Produced by | Effect |
|---|---|---|
| `run_scheduled` | Orchestrator, after admitting any event | the Run is queued; the run worker claims it when capacity allows |
| `run_processing` | run worker, on claiming the Run | the agent Run starts |
| `run_stop_requested` | Orchestrator, on the cancel path | the run worker stops the container (see [../run-worker.md](../run-worker.md)) |
| `run_completed` | run worker, on a Run settled **with** `report_completion` | starts the next flow stage per the fixed order (development → review → testing), or a `waiting_for_human` state |
| `run_failed` | run worker — when the Run cannot be started, settles **without** `report_completion`, crashes, or hits the watchdog timeout | auto-retry or `waiting_for_human` — the Orchestrator decides; on giving up it notifies the User |
| `run_cancelled` | run worker, on stopping for `run_stop_requested` | the Run is terminal; the Task → cancelled |

## Not events

- **Publication** (plan comment, review comments on the PR, PR creation) is an
  Orchestrator action, not an event — see the publication rule in [tools.md](tools.md).
- **User-facing notifications** (task failure, rejections, cancellations) are Orchestrator
  output, not input events.
