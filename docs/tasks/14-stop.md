# 14: /stop — graceful cancellation

**What to build:** The `/stop` control path: Orchestrator → Runtime API → Bridge with the graceful-stop sequence (RPC `clear_queue` first, then `abort`/`abort_bash`, wait up to ~30 s for `agent_settled`, close Pi stdin, SIGTERM then SIGKILL if needed, verify child processes stopped, post the terminal `cancelled` callback); Run + Task marked `cancelled` after runtime confirmation or reconciliation; ALL queued Tasks of the same Workflow cancelled with their Jobs; system notification via the Outbox (one comment may cover active+queued cancellations); Workflow → `waiting_for_human`; session/log/workspace storage retained; "nothing to stop" reply when there is no work; other Workflows never touched; the WorkItem itself never closed.

**Spec:** `docs/pi-agent-architecture/06-command-reference.md` §8; `03-state-machines.md` §2, §5; `04-api-contracts.md` §4 (graceful stop sequence); `05-v1-decisions.md` decisions 29, 38, 61; `01-architecture-and-flows.md` §10 (retained storage).

**Blocked by:** 12

**Status:** ready-for-agent

- [ ] `/stop` during a running planner Task → Bridge executes the full stop sequence; Run + Task `cancelled`; confirmed by terminal callback or reconciliation
- [ ] Queued Tasks of the Workflow (with Jobs) are cancelled too
- [ ] Cancellation notification published via the Outbox (exactly once for active+queued)
- [ ] Storage (session files, logs, workspace) retained after stop
- [ ] `/stop` with nothing running → "nothing to stop" reply; Workflow and storage untouched
- [ ] Other Workflows unaffected; the GitHub Issue/PR is not closed
- [ ] Acceptance scenario "Stop" (`05` matrix) passes
