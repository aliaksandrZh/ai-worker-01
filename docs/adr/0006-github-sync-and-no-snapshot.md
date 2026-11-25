# Agents read GitHub themselves; the Poller detects triggers

Today the Poller mirrors GitHub into normalized Postgres tables (issues, comments,
reviews, timeline, projects, links — 8+ entity tables, mappers, per-stream content
sync), the ContextAssembler re-reads them to freeze a byte-exact RunContextSnapshot
under token budgets, and the Orchestrator parses commands and rechecks permissions at
claim time. All three move aside:

- **The GitHub mirror is deleted.** Postgres keeps only the EventLog, orchestration
  state (Workflow/Task/Run), cursors, dead-letter and Publisher markers. The single
  source of truth for GitHub content is GitHub itself.
- **ContextAssembler is deleted.** Agents read their context from GitHub through their
  read tools (the plan is an issue comment); the Run input shrinks to pointers — item,
  PR, plan, command. We consciously give up the byte-exact snapshot and token-budget
  enforcement (OBS-6/OBS-8 machinery dies with it): reproducibility of "what the agent
  saw" rests on logs and the EventLog, not on a frozen copy.
- **Parsing moves to the edge.** The Poller owns the command grammar and trigger
  detection (labels, board columns, item state), resolves the command author's
  permission at detection time and puts it into the `user_command` payload. The
  Orchestrator becomes a pure event reactor with zero GitHub knowledge — it gates on
  the payload; the claim-time permission recheck is deleted as redundant.

Why: the mirror + assembler were the largest mass of shallow mapping code in the
system, and their products duplicated what the agents can fetch live. The trade-offs —
no frozen input, no budget gate, permission freshness bounded by the poll — are
accepted pre-production simplifications.
