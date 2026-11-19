# 11: Context Assembler + RunContextSnapshot

**What to build:** The Orchestrator-side Context Assembler producing each Run's input within the size limits and freezing it as an immutable `RunContextSnapshot` (content_hash, included_sources, excluded_sources with per-element pruning reason). Assembly per task type: instructions + profile config, user command (+ trailing comment text as context), WorkItem (title/description/state/labels), comments chronologically with author+timestamp (deleted excluded), previous Tasks' Artifacts of the Workflow, repository/Git data. Reviewer-specific: `target_head_sha`/`target_base_sha`, unresolved review threads first, then remaining threads with state/answers/file-line bindings. Limits: the profile's mandatory `max_context_tokens` plus 256 KiB per artifact / 512 KiB artifacts total / 512 KiB serialized input; pruning priority — never drop the command, mandatory inputs, or the last approved plan; keep recent user answers and unfinished discussions; drop older/less-significant comments. Mandatory context that cannot fit → Run not started, Task fails with `context_too_large`. No silent truncation anywhere.

**Spec:** `docs/pi-agent-architecture/02-data-model.md` §14a (RunContextSnapshot), §17 (artifact limits); `06-command-reference.md` §3–§6 (per-command input), §10 (PR context); `04-api-contracts.md` §3 (input delivery), §5 (reviewer input); `05-v1-decisions.md` decision 63.

**Blocked by:** 07, 04

**Status:** ready-for-agent

- [ ] Plan-Task context contains Issue body + all comments (including earlier plan answers) chronologically, tombstones excluded
- [ ] Reviewer context puts unresolved review threads first and includes the pinned Head/Base revisions for the runtime diff
- [ ] `RunContextSnapshot` rows are immutable, hashed, and record included/excluded sources
- [ ] Exceeding `max_context_tokens` or the serialized-input limit → Task fails `context_too_large`, Run never starts
- [ ] The command, mandatory inputs, and the last approved plan are never pruned; every exclusion lands in the snapshot
- [ ] Coder context includes the last approved plan Artifact and current agent-branch info
