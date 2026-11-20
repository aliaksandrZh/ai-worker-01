# 06: Command parsing + authorization

**What to build:** The command-grammar parser and authorization gate that turn synchronized comments into persisted `AgentCommand` rows. Grammar per the spec: mention + recognized slash command on the first non-empty line, exact `github.bot_login`, blockquote/code-block directives ignored, exactly one directive per comment, only the original (unedited when first seen) version parsed, bot-authored comments never trigger. A mention-only comment (bot mention, no slash directive) is context only — no `AgentCommand` row, no rejection, no notification (`01 §8`, decision 45); recognition/rejection applies only to comments containing an actual directive. `source_body_snapshot` frozen; statuses `received → authorized/rejected` (+ `queued` while execution capacity is busy, `processed`/`failed` for consumers per `02 §11`/`03 §7`); every rejection persisted with a reason. Authorization: `GET /repos/{owner}/{repo}/collaborators/{username}/permission` (accept `write` + `admin`; `maintain`→write, `triage`→read), ≤60 s cache with claim-time recheck, GitHub 404 → reject, transient errors leave the command `received` with the check deferred; per-repository allowlist and permitted commands/WorkItem types from `repositories.yaml`; PAT lacking scope is a configuration error.

**Spec:** `docs/pi-agent-architecture/06-command-reference.md` §1 (recognition rules); `02-data-model.md` §11 (AgentCommand), §21 (authorization mechanics); `05-v1-decisions.md` decisions 6, 7, 45, 49, 66, 79 (arguments `{}`); `01-architecture-and-flows.md` §8 (mention-only stays context).

**Blocked by:** 04

**Status:** done-pending-review (plan: `docs/superpowers/plans/2026-11-20-command-parsing.md`, design: `docs/superpowers/specs/2026-11-20-command-parsing-design.md` — six owner decisions Q1–Q6 recorded there; branch `task-6-command-parsing`)

- [x] Valid command → `authorized` AgentCommand with `source_body_snapshot`; duplicate comment ID never re-processed
      (`ValidCommand_Authorized_WithSnapshotAndArguments` — arguments `"{}"`, parser_version `"1"`, snapshot = body; `ReplaySameEvent_DoesNotDuplicateRowOrReply` + unique `github_comment_id`)
- [x] Malformed, unrecognized (an actual directive that is not a known command), ambiguous (multiple directives), quoted/code-blocked comments → rejected with persisted reason
      (`UnknownCommand_Rejected_WithReplyEnqueued`, `TwoDirectives_Rejected_Ambiguous`, `QuotedDirective_Rejected`; parser pins in `CommandParserTests.*Malformed*`, `*Ambiguous*`, `*UnknownCommand*`, `*Rejected*`)
- [x] Mention without a slash command → context only: no `AgentCommand` row, no rejection, no Outbox reply (decision 45, `01 §8`)
      (`MentionOnlyComment_NoRow_NoOutbox`; parser: `SlashTokenWithoutMention_NoDirective`, `NonAdjacentMentionAndCommand_ContextOnly`, `DifferentUserMention_ContextOnly`, `WrongCaseBotLogin_ContextOnly`)
- [x] Bot-authored comment never triggers; inline review comments and thread replies are context only
      (`BotAuthoredDirective_Ignored_NoRow`; `ReviewCommentEventsNeverClaimed` — review-comment events are never claimed at all)
- [x] Comment already edited when first seen → not accepted as a command; edits after processing never retrigger
      (`AlreadyEditedWhenFirstSeen_Rejected_CommandEdited` — reason `command_edited` + reply telling the user to post a new comment; edits-after are never re-parsed and never re-read: `EditAfterCommandReceived_NeverReprocessedOrReinterpreted` — deferred authorization on an edited comment keeps the original parse/snapshot and stays `authorized`)
- [x] Non-allowlisted user → rejected; GitHub 404 → rejected; transient error → stays `received`, check deferred
      (`Allowlist_NonMemberRejected` (case-insensitive: author "Alice" vs allowlist "bob" denied), `PermissionLookup404_Rejected_UserNotCollaborator`, `TransientError_CommandStaysReceived_EventRetries` (500 → received + event released, retry succeeds); cache ≤60 s: `CacheWithinTtl_SecondCheckSkipsHttp`, `CacheExpired_Rechecks`, `TransientOutcomes_AreNotCached`)
- [x] `arguments` is always `{}`; trailing text stored as user context for the Context Assembler
      (`ValidCommand_Authorized_WithSnapshotAndArguments` — `arguments` = `"{}"`, trailing context inside `source_body_snapshot`; parser returns it: `Valid_FirstLineMentionCommand_WithTrailingContext`)
- [x] Allowlist/permission removal never interrupts an already-running Task; only later commands from that user are rejected (decision 8)
      (structural: authorization is evaluated only at command-arrival/retry; nothing re-checks running Tasks — the Task/Run path does not exist yet, ticket 07 owns it; permission-API results cached ≤60 s and re-checked at claim by ticket 07 through the same gate)
- [x] Command on a paused repository (`is_enabled = false`) → rejected with a recorded reason, no Task (decision 75)
      (`PausedRepository_PlanRejected_RepositoryPaused` for task-producing/`/retry`; `/status` passes: `PausedRepository_StatusAuthorized`)
- [x] Command with a wrong target type (e.g. `/plan` on a PR, `/review` on an Issue) → rejected with a recorded reason per the command table (`06 §1` rule 6)
      (`PlanOnPullRequest_Rejected_WrongTargetType`, `ReviewOnIssue_Rejected_WrongTargetType`; plus repo-config filter `WorkItemTypeNotPermitted_Rejected`, `NotPermittedCommand_Rejected`)
- [x] Revoked/expired PAT during authorization → surfaced as a system configuration error, no automatic fallback to another PAT (decisions 65, 66)
      (`RevokedPat401_SystemConfigError_StaysReceived` — command stays `received`, no rejection row, no Outbox reply, `authorizationSystemErrorsTotal` +1; 401/non-rate-limit-403 → SystemConfigError via `Unauthorized401_SystemConfigError`, `Regular403_SystemConfigError`; rate-limits/5xx/network → transient with Retry-After honored (`PrimaryRateLimit403_Transient_WithRetryAfter`, `Secondary429_Transient`, `ServerError500_Transient`, `NetworkFailure_Transient`))
- [x] Every rejected command → Outbox notification enqueued → concise reply comment with the reason on GitHub (`06 §1` rule 10, `§13` table, `04 §2`) — wired end-to-end with ticket 03
      (rejected rows enqueue exactly one `outbox_action` in the same transaction (idempotent replay: `ReplaySameEvent_DoesNotDuplicateRowOrReply`); reply text per reason in `AgentCommandRejectionReplies`; the ticket-03 dispatcher already delivers them — live GitHub delivery listed unverified below)
- [x] Acceptance scenarios "Command parser" and "Authorization" (incl. revoked-PAT handling, `05` matrix) pass
      (28 parser + 23 gate + 26 processor tests; suite totals below)

## What was done (branch `task-6-command-parsing`, 2026-11-20)

- **Schema (migration `AddAgentCommands`):** `agent_command` per 02 §11 — unique `github_comment_id` (bare external ID, no DB FK: the comment unique index is composite `(kind, github_comment_id)`; same house pattern as `github_comment.github_review_id`), index `(status, created_at)`, jsonb `arguments`, FK `work_item_id` Restrict. Statuses/name constants: `AgentCommandStatuses` (`received/authorized/queued/processed/rejected/failed`), `AgentCommandNames` (`plan/code/review/test_cases/status/stop/help/retry`).
- **Pure parser** (`Hosts/Orchestrator/Commands/CommandParser.cs`): line state machine stripping fenced (` ``` `/`~~~`), indented-code (tab or ≥4 spaces), blockquote (leading `>`), inline-code spans; directive = `@{exact-case bot_login}` + whitespace + `/token` (adjacency required, Q4). First non-empty line + only leading whitespace → parse; >1 usable directive → `ambiguous_command`; token not in the V1 table → `unknown_command` (token case-insensitive, Q3). Stripped directives are not usable directives; bot-addressed comments (login word-bounded anywhere in the body) carrying one → `command_in_quote_or_code_block` + reply (Q1); without the login they stay context (no-mention prose is never rejected). `@login2`/`@JSXHUZ` never match login `jsxhuz` (Q5); `@bot/plan` glued is not a directive; trailing text after the token is user context.
- **Processor** (`CommentCommandProcessor`, the first Orchestrator `IWorkerCycle`; gets its DbContext via `IServiceScopeFactory` like `OutboxDispatcher`): claims **only** `issue_comment_created` events (edits/review comments are never claimed), FIFO by `received_at`, batch 32; cold-start gate defers until config sync (`Q6`); orphan `processing` events reset each tick (single-instance V1 — the lease machinery is ticket 07's); every event processes in its own transaction. First sight: edited-check (`UpdatedAtGithub != CreatedAtGithub` → `command_edited`, never re-run on retries) → parse → repo policy in order: paused (task-producing + `/retry` only; `/status`/`/help`/`/stop` pass, decision 75) → `permitted_commands` → command-table target type → `permitted_work_item_types` → then authorization: allowlist (from `Repository.Configuration` jsonb, case-insensitive, emptied = write baseline; malformed config denies) → `CollaboratorPermissionGate` (≤60 s in-memory cache, `role_name`/`permission` ranking none<read<triage<write<maintain<admin vs `minimum_permission` default write; 404 → `user_not_collaborator`; transient → command stays `received`, event released to `pending`, `Retry-After` pause ≤1 h; 401/regular 403 → system configuration error: log + metric + deferral, never a per-user rejection, no fallback PAT). Every rejection persists its reason and enqueues one Outbox reply in the same transaction (random idempotency key — the unique command row makes replays no-ops).
- **Wiring:** bot login rides the cold-start gate (`ConfigurationState.MarkSynchronized(botLogin)`; startup-only sync); the read-only PAT (`decision 65`) is read once at host start (`SyncPat`, extracted from the loader) and feeds the gate's `HttpClient`; orchestrator metrics gained `commandsReceivedTotal/commandsAuthorizedTotal/commandsRejectedTotal (+ by reason)/authorizationSystemErrorsTotal`; `GitHubEventStatuses` gained `processing` (03 §7).
- **Shared plumbing:** `GitHubExceptions` (`GitHubRateLimitedException`/`GitHubReadException`) moved from the Poller to `Platform/Http` — one source of the 403-remaining-0/429 classification for both the Poller and the gate; poller behavior unchanged (34 DB-free poller tests green; DB-backed poller suites re-verified in the full run).
- Deliberately out (ticket 07/08/15/11): Workflow/Task/Run creation, admission, `queued/processed/failed` transitions, routing/`agent_profile_not_configured`, claim-time recheck wiring, `/status`-`/stop`-`/help`-`/retry` replies, closed-WorkItem admission (decision 73), `issue_comment_deleted` of commanded comments (rows are audit history, tombstones exclude comments from future context).

## How to manually test (verification levels marked per Implementation Instructions)

Verified during development (commands + observed outputs from real runs):

```sh
dotnet build src/PiAgent.slnx --nologo
#   → "0 Warning(s)" / "0 Error(s)"
dotnet test src/PiAgent.slnx --nologo          # requires Docker (Testcontainers postgres)
#   → Passed! 2 (Architecture) + 9 (Platform) + 28 (Infrastructure) + 305 (Hosts.Tests)
#   → total 344, Failed: 0
docker compose -f src/compose.yaml config --quiet   # "compose config OK" (compose wiring unchanged)
docker ps                                      # after the run: 0 containers (testcontainers torn down)
```

Behavior can be exercised without GitHub by replaying the seeded scenarios — the automated proof lives in
`src/tests/Hosts.Tests/Commands/` (`CommentCommandProcessorTests`, `CommandParserTests`,
`CollaboratorPermissionGateTests`), each named per acceptance item above.

NOT verified (needs a real fine-grained read-only PAT + the disposable GitHub repository + the live stack — run when the stack is exercised end-to-end, e.g. with ticket 07's consumers):

```sh
printf '<read-only-pat>' > src/secrets/github-sync-pat.txt
task stack-rebuild && task stack
# in the disposable repo, a collaborator comments "@<bot_login> /plan" and
# "@<bot_login> /frobnicate" (unknown → rejection reply via Outbox), and a
# non-collaborator comments a valid command (rejection reply)
docker compose --env-file src/.env -f src/compose.yaml exec postgres \
  psql -U pi_agent -c 'select command_name, status, rejection_reason from agent_command order by created_at;'
# expect: /plan → authorized (null reason), /frobnicate → rejected (unknown_command),
#         non-collaborator → rejected (user_not_collaborator); rejection replies delivered as comments
```

Live-path prerequisites: `github.bot_login` in `src/config/system.yaml` matches the PAT identity (enforced at Orchestrator startup), the repository registered in `src/config/repositories.yaml` (allowlist defaults seeded with the owner), `github-sync-pat.txt` with the read-only PAT. Everything above unverified-live is automation-backed but not yet observed against real GitHub; nothing else remains unverified.
