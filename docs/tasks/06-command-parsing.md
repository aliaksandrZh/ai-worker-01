# 06: Command parsing + authorization

**What to build:** The command-grammar parser and authorization gate that turn synchronized comments into persisted `AgentCommand` rows. Grammar per the spec: mention + recognized slash command on the first non-empty line, exact `github.bot_login`, blockquote/code-block directives ignored, exactly one directive per comment, only the original (unedited when first seen) version parsed, bot-authored comments never trigger. `source_body_snapshot` frozen; statuses `received → authorized/rejected` (+ `processed`/`failed` for consumers); every rejection persisted with a reason. Authorization: `GET /repos/{owner}/{repo}/collaborators/{username}/permission` (accept `write` + `admin`; `maintain`→write, `triage`→read), ≤60 s cache with claim-time recheck, GitHub 404 → reject, transient errors leave the command `received` with the check deferred; per-repository allowlist and permitted commands/WorkItem types from `repositories.yaml`; PAT lacking scope is a configuration error.

**Spec:** `docs/pi-agent-architecture/06-command-reference.md` §1 (recognition rules); `02-data-model.md` §11 (AgentCommand), §21 (authorization mechanics); `05-v1-decisions.md` decisions 6, 7, 49, 66, 79 (arguments `{}`).

**Blocked by:** 04

**Status:** ready-for-agent

- [ ] Valid command → `authorized` AgentCommand with `source_body_snapshot`; duplicate comment ID never re-processed
- [ ] Malformed, ambiguous (multiple directives), quoted/code-blocked comments → rejected with persisted reason
- [ ] Bot-authored comment never triggers; inline review comments and thread replies are context only
- [ ] Comment already edited when first seen → not accepted as a command; edits after processing never retrigger
- [ ] Non-allowlisted user → rejected; GitHub 404 → rejected; transient error → stays `received`, check deferred
- [ ] `arguments` is always `{}`; trailing text stored as user context for the Context Assembler
- [ ] Command on a paused repository (`is_enabled = false`) → rejected with a recorded reason, no Task (decision 75)
- [ ] Command with a wrong target type (e.g. `/plan` on a PR, `/review` on an Issue) → rejected with a recorded reason per the command table (`06 §1` rule 6)
- [ ] Revoked/expired PAT during authorization → surfaced as a system configuration error, no automatic fallback to another PAT (decisions 65, 66)
- [ ] Every rejected command → Outbox notification enqueued → concise reply comment with the reason on GitHub (`06 §1` rule 10, `§13` table, `04 §2`) — wired end-to-end with ticket 03
- [ ] Acceptance scenarios "Command parser" and "Authorization" (incl. revoked-PAT handling, `05` matrix) pass
