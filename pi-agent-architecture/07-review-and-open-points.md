# Documentation Review and Remaining Decisions

This review was performed against the decisions and flows discussed in the project conversation. The current Markdown set is an architecture draft, not an implementation-ready SQL schema or a final OpenAPI specification.

## Corrections made during the review

1. Added a full command reference with purpose, target WorkItem type, Agent Profile, preconditions, state effects, artifacts, and GitHub output for every proposed command.
2. Standardized the proposed V1 syntax as `@<bot-account> /<command>`. The bot mention and slash command must be in the same comment. `/agent <command>` is only a possible future alias.
3. Defined `/status`, `/stop`, and `/help` as non-agent control commands; they do not create Tasks, Pi sessions, or containers.
4. Defined `/proceed` as human approval to execute a previously prepared plan; by default it requires a successful plan Artifact.
5. Defined `/review` for manual re-review of the current PR Head SHA; the first review is automatic when the bot is requested as reviewer. A new commit alone does not trigger a review.
6. Defined `/test-cases` as test design only (no test implementation/execution by default), and `/debug` as diagnostics/read-only by default.
7. Clarified global sequential execution and behavior for commands waiting while the execution slot is occupied.
8. Corrected the conceptual ERD to show Task → AgentSession, Task → Workspace, review records, task-artifact inputs, profile routing, and links between an Issue and its related PR.
9. Clarified that an Issue and related PR are separate root WorkItems/Workflows; closing one does not silently close the other.
10. Clarified container stop versus cleanup: stop after Task completion, retain session files and workspace volume, and remove them when the root WorkItem closes.
11. Added separate runtime heartbeat, session/request/process activity, and idle-timeout checks. A 60-minute timeout remains configurable and requires reconciliation before destructive cleanup.
12. Explicitly treated the current repository as a proof of concept; its code, API, language, framework, and persistence implementation may all be replaced.
13. Added the convention that each specialized agent has its own repository directory/build context.
14. Documented that agent profiles may use GitHub MCP, while orchestration-owned notifications/writes use the durable Outbox.

## Requirements coverage

| Discussed requirement | Where covered |
|---|---|
| Poll GitHub Issues, PRs, comments, reviews, commits | `01`, `02` |
| PostgreSQL replaces poller JSON state | `01`, `02` |
| Per-stream polling cursors and idempotency | `02`, `04` |
| Commands, assignment-independent Issue triggering | `01`, `05`, `06` |
| Write permission and optional allowlist | `02`, `05`, `06` |
| One Workflow with sequential Tasks | `01`, `02`, `03` |
| New Pi session per Task | `01`, `02`, `03`, `05` |
| Different Pi profiles/images/skills/MCP | `01`, `02`, `05` |
| Per-agent repository directories | `01`, `02` |
| Task workspace volume persists while container is stopped | `01`, `02`, `03`, `05` |
| Reviewer gets a fresh workspace | `01`, `02`, `06` |
| Root closure removes containers/volumes/session files; DB remains | `01`, `02`, `03`, `05` |
| PR first review automatic on reviewer request | `01`, `05`, `06` |
| Further PR reviews only on explicit request | `01`, `05`, `06` |
| Runtime API, Komodo, callbacks | `01`, `04` |
| Reconciliation and one-hour idle timeout proposal | `01`, `03`, `05` |
| Split Compose/deploy across hosts possible | `01` |
| No language/framework chosen | `01`, `05`, README |

## Remaining product/implementation decisions

These are not omissions from the draft; they are genuinely unresolved or should be finalized before implementation:

1. **Free-form bot mentions:** Should `@bot please explain this` trigger a response without `/command`? Current V1 proposal says no; a recognized slash command is required. If direct conversational replies are a core requirement, add an explicit `/respond` or `/ask` behavior or define mention-only dispatch.
2. **PR discussion replies:** `/review` receives prior PR conversation/review threads and can reply in relevant threads. Decide whether a separate comment-reply command is required for arbitrary PR comments.
3. **Coder creates PR:** The agreed baseline is commit + push branch + report to Issue. Automatic PR creation is deliberately not assumed; decide whether to add `/create-pr` or a repository setting.
4. **Test-case targets:** V1 command reference supports `/test-cases` on Issues. Decide whether to allow it on PRs.
5. **Authorization policy:** Write permission is baseline; decide whether allowlist is enabled by default and whether it is mandatory in selected repositories.
6. **GitHub authentication:** GitHub App versus PAT, installation permissions, and token refresh are not chosen.
7. **PR↔Issue linking:** Decide whether to detect relationships using closing keywords, branch metadata, an agent-created PR action, or manual metadata.
8. **Retry/session recovery:** Decide exact retry count/backoff and whether a failed Task always resumes the same Pi session or is replaced when the session is not recoverable.
9. **Storage operations:** Choose concrete Docker volume naming, backup, permissions, and cleanup implementation. External file/object storage remains future work.
10. **Polling details:** Choose exact API endpoints, rate-limit strategy, page sizes, overlap windows, and stream-specific cursor strategy during implementation.
11. **Idle detection:** Choose concrete heartbeat/event cadence and the definition of observable process activity. The 60-minute threshold is only an initial setting.
12. **Agent profile format:** Choose configuration representation, build pipeline, and secret injection mechanism. No programming language or framework is selected.

## Assessment

After the corrections, the documents represent the major architectural decisions discussed so far. They are coherent enough to review and use as the basis for the next design step. They are not yet a final implementation specification: unresolved choices above need decisions, and the ER model still needs to be translated into concrete DDL with FK behavior, constraints, and indexes once the storage/runtime details are selected.
