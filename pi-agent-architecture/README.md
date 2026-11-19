# Pi GitHub Agent — Architecture Draft

This documentation describes the target architecture for a GitHub-native multi-agent system using Pi agents and containerized execution managed by Komodo.

The current repository is a proof of concept only. Existing code, API shape, runtime implementation, polling implementation, and language are not architectural constraints and may be replaced.

## Documents

- [Architecture and flows](01-architecture-and-flows.md)
- [Data model / ERD](02-data-model.md)
- [State machines](03-state-machines.md)
- [API contracts](04-api-contracts.md)
- [V1 scope and decisions](05-v1-decisions.md)
- [Command reference](06-command-reference.md)
- [Open product decisions](10-open-product-decisions.md)

`07-review-and-open-points.md` was retired (documentation-review history); its still-open decisions were moved here and into 09.

## Main design principles

1. PostgreSQL is the source of truth for orchestration state.
2. GitHub is the source of truth for issues, pull requests, comments, reviews, and repository content.
3. Polling synchronizes GitHub data; it does not directly run agents.
4. The Orchestrator makes business decisions and schedules tasks.
5. Agent Runtime manages execution mechanics: sessions, containers, RPC, callbacks, and workspace mounting.
6. Each task gets a new Pi session and task-specific workspace.
7. Agent profiles are independently configurable, including image, skills, MCP servers, prompts, tools, and runtime settings.
8. A Workflow holds at most one unfinished Task at any time; a further command for that Workflow is rejected at admission. Parallel Runs of different Workflows are bounded by a global cap `max_concurrent_runs` (default 10, Orchestrator configuration).
9. Task/session metadata remains in PostgreSQL after cleanup; session files and task workspace volumes are deleted when that root GitHub item is closed.
10. Issue assignment is not a V1 trigger. Issue commands require a bot mention and recognized slash command; PR's first review is triggered when the bot is requested as reviewer.
11. A new PR commit does not automatically trigger another review; subsequent reviews are command-driven.
12. Architecture is language- and framework-neutral.

## Terminology

- **Root Work Item**: GitHub Issue or Pull Request around which a workflow is organized.
- **Workflow**: Orchestration lifecycle for a root work item.
- **Task**: A discrete operation such as planning, implementation, test design, or review.
- **Agent Profile**: Runtime and behavioral configuration for a specialized agent.
- **Agent Session**: Pi conversation/session created for a Task.
- **Run**: One execution attempt of a Task.
- **Workspace**: Task-specific working copy and persistent files.
- **Container Instance**: Runtime container managed through Komodo.
- **Artifact**: Structured or textual output that can be handed from one task to another.

## Open decisions (remaining product/implementation choices)

Detailed questions are tracked in [10-open-product-decisions.md](10-open-product-decisions.md):

- Free-form `@bot` replies without a recognized command.
- A separate command for replying to arbitrary PR threads beyond `/review` context.
- Whether coder auto-creates a PR (currently not assumed; coder is a later phase).
- Whether `/test-cases` should support PR targets.
- Whether the allowlist is enabled by default or only when configured.
- GitHub App vs PAT for the overall installation/sync model.
- How Issue↔PR relations are detected.
- Docker volume naming, backup, and cleanup for session/workspace storage.
- Polling implementation details (endpoints, rate limits, page sizes, overlap windows, cursors).
- Agent profile configuration file format and secret injection mechanism.
