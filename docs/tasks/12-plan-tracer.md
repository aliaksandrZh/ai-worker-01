# 12: /plan tracer bullet — real planner end to end

**What to build:** The first real agent flow, replacing the stub with Pi: authorized `/plan` on an open Issue → Context Assembler builds the snapshot → Runtime API starts a Run with the planner profile image (Build Worker ready) → Bridge launches Pi in `--mode rpc` with `--session-dir` + `--session-id` pointed at the per-Run mount, `PI_CODING_AGENT_DIR` baked → task input delivered as one RPC `prompt` (structured Markdown from the snapshot) → real pi→RunEvent mapping (agent_started … tool events … agent_settled) → the planner analyzes, publishes plan/questions via GitHub MCP, writes `result.json` with artifacts and `requiredPublications` → Bridge validates in-place, derives Git facts, correlates publications with tool events → `completed` → plan Artifact persisted → Task Succeeded, Workflow `waiting_for_human`. Planner publication verified per decision 42/59; failure → `github_publication_failed` + Outbox notification, artifacts kept, `/retry` recovery. Profile prompt instructs MCP posting + manifest writing; planner's MCP allow-list pinned in the profile version.

**Spec:** `docs/pi-agent-architecture/06-command-reference.md` §3 (/plan), §13 (system notifications + required publications); `04-api-contracts.md` §3–§4; `03-state-machines.md` §2–§4; `05-v1-decisions.md` decisions 11, 12, 26, 33, 34, 36 (phase 1), 42, 57, 58, 59, 63; `01-architecture-and-flows.md` §6 (plan flow diagram), §15 (Pi mapping).

**Blocked by:** 09, 10, 11, 11b, 08

**Status:** ready-for-agent

- [ ] `@bot /plan` on an open Issue → real Pi run in the planner image → plan/gaps/questions comment published via GitHub MCP + plan Artifact row (inline, ≤256 KiB)
- [ ] requiredPublications correlated before `completed`; unconfirmed publication → Run/Task fail `github_publication_failed`, notification posted, artifacts kept
- [ ] `/status` during the run shows the current phase from RunEvents with the progress rules (heartbeat never progress)
- [ ] Task Succeeded → Workflow `waiting_for_human`; a later `/plan` creates a new Task + new per-Run session at a new per-Run directory
- [ ] Real event mapping persisted (agent_started/tool_*/agent_settled) with text deltas aggregated
- [ ] Acceptance scenario "Planning" (`05` matrix) passes end to end
