# Flows

> The four agent flows of the system: their diagrams, their tools
> ([tools.md](tools.md)), and the events that drive them ([events.md](events.md)).
> Flow order is fixed: **plan → development → review → testing**; review and fixing may
> cycle — a round cap exists, the Orchestrator owns it, and it is not modeled in the
> diagrams. The component that drives all four flows is described in
> [../orchestrator.md](../orchestrator.md).

| # | Flow | Agent | Doc | Entry | Exit signal |
|---|---|---|---|---|---|
| 1 | Plan | Planner | [plan.md](plan.md) | `user_command` (task created) | `save_plan` + `report_completion` |
| 2 | Development | Coder | [development.md](development.md) | plan approved, or a fixing round assigned | `report_completion` (PR published by the Orchestrator) |
| 3 | Code review | Review Agent | [review.md](review.md) | PR created / updated (`pr_updated`) | `submit_review_comments` or pass + `report_completion` |
| 4 | Testing | Testing Agent | [test.md](test.md) | review passed | `report_tests_passed` + `report_completion` |

## Rules that hold across all flows

1. **Publication** — agents never write to GitHub; the Orchestrator publishes plans,
   review comments, bug reports, and the PR. The single exception is code delivery:
   the Coder pushes its working branch via `git push` (see ADR-0001).
2. **Completion** — every agent Run ends with `report_completion()` (no parameters); a
   Run that ends without it has failed.
3. **Resume** — every "Waiting for Human" state is released by exactly one user event
   ([events.md](events.md)); the flow continues as a new Run.
4. **Escalation** — four agent-specific escalation tools share one shape (what happened,
   context, options → resume on a user event); see [tools.md](tools.md).

## Orchestrator policies (not flow nodes)

Decisions the flows deliberately leave to the Orchestrator; they are applied while it
processes events, never drawn as diagram nodes:

| Policy | Applied when |
|---|---|
| Round cap on review ↔ fixing cycles and Coder iterations | a next round would exceed the cap → `waiting_for_human` |
| Auto-retry of a failed Run (`run_failed`) | Orchestrator decides retry vs `waiting_for_human` |
| Plan approval required? | the `plan_saved` event is processed |
| Manual acceptance of test results required? | a passing Run settles; if required, results are routed to the User |

## User actions

| Action | Where | Event |
|---|---|---|
| Provide clarification | plan, development, testing | `user_command` (`@bot /resume`) |
| Approve / request changes (plan) | plan | `user_decision` |
| Respond to a blocker | development | `user_command` (`@bot /resume`) |
| Decide a review escalation (fixes / change scope / accept risk) | review | `user_decision` |
| Restore environment / access | testing | `user_command` (`@bot /resume`) |
| Clarify expected behavior | testing → planner | `user_command` (`@bot /resume`) |
| Accept / reject test results | testing | `user_decision` |
| Cancel the workflow | any waiting state | `user_cancel` |
