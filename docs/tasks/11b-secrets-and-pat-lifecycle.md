# 11b: Secrets resolution — profile secret mounting + PAT lifecycle

**What to build:** The secrets plane between configuration and runtime. Resolution of secret references from repository/profile configuration and read-only mounting of a profile's required secrets into its agent containers at `/run/secrets/<name>` — least privilege (each profile receives only the secrets it needs: read-only sync PAT for the Poller; write PAT for the coder's MCP/Git; MCP tokens for planner/reviewer/test-designer), never via environment variables or image layers; values live only in host-managed files. The per-Run callback credential remains a separate mechanism (ticket 09). PAT lifecycle per the spec: rotation procedure (create replacement with equivalent rights → verify via GitHub API → switch the secret version → revoke the old token only after in-flight Runs finish), expiry tracking with a 14-day warning surfaced in logs/health status, compromise → immediate revocation even if it breaks active operations, and no automatic fallback to another PAT — revoked/expired tokens surface as clear errors (Poller sync errors; agent GitHub operations fail the Run with a clear error code).

**Spec:** `docs/pi-agent-architecture/02-data-model.md` §12 (secrets paragraphs); `01-architecture-and-flows.md` §2 (Poller authentication paragraph); `05-v1-decisions.md` decisions 50 (PAT split per role), 54 (secret mounting rules), 65 (rotation/compromise/expiry); `04-api-contracts.md` §4 (callback credential — separate mechanism).

**Blocked by:** 02, 09

**Status:** done-pending-review (branch `task-11b-secrets-pat-lifecycle`; automated verification 532/532 green via `dotnet test src/PiAgent.slnx` — Architecture 2, Platform 9, Infrastructure 47, Hosts 474 — plus `docker compose -f src/compose.yaml config --quiet` OK). **Scope ruling (owner, 2026-11-21): PAT-lifecycle automation skipped — no rotation runbook, no expiry tracking; decision 65 is honored as "revoked/expired PAT surfaces a proper, actionable error with no automatic fallback" only.** The secrets→container injection path (decision 54) is verified/pinned; mounting itself was already live-verified in ticket 09.

- [x] Each agent container receives only its profile's required secrets, mounted read-only at `/run/secrets/<name>` (planner/reviewer/test-designer MCP tokens; coder write PAT) — decision 54 mechanics exist since ticket 09 and are pinned by tests: `EmptySecretReferences_MountNothing`, `OnlyReferencedSecrets_AreMounted_LestPrivilege` (exactly the referenced file, `…:/run/secrets/<name>:ro` shape, name never in the container env), `MissingHostSecretFile_FailsStartClosed` (fail-closed), `DuplicateReferences_MountOnce`; per-profile names land with the agent tickets (12/18/19/20) — the mechanism is profile-version-driven, not name-hardcoded
- [x] No secret value ever reaches environment variables, image layers, or PostgreSQL — configuration carries names only (`environment_configuration = {"secret_references":[…]}`, `ConfigSyncService`); profile secret *values* only ever exist as host-managed files (`src/secrets/` gitignored, decision 54) mounted read-only; `OnlyReferencedSecrets_AreMounted_LestPrivilege` asserts the container env is free of the reference; image layers carry no secret files (profile Dockerfiles copy config-plane files only, `config/agents/<profile>/Dockerfile`)
- [x] PAT revocation/expired surfaces as a proper error with no automatic fallback (decision 65's execution clause; owner ruling: rotation/expiry tracking skipped) — Poller: `RevokedPat401_SyncFailsWithProperError_NoFallback` (Error-level line: "the polling PAT is revoked, expired, or invalid … no automatic fallback, decisions 50/65", no PAT bytes, cursor `last_error` recorded, other streams continue); Config Loader startup identity check: `GetUser_NonSuccess_ThrowsConfigExceptionWithStatus` (401 → "GitHub rejected the PAT (revoked, expired, or invalid) … no automatic fallback", startup aborts — the compose `--wait` gate shows it red); Outbox 401 → `dead_letter` (ticket 03, live-verified)
- [x] A compromised token is revoked immediately via GitHub UI (operator action, outside the system); the system's part — no automatic fallback and the clear errors above — so active operations fail visibly on the next GitHub call
- [x] Per-Run callback credential functionally independent of profile secrets — the callback credential (decision 60, ticket 09) is a separate mechanism (per-Run file, hashed in DB, scoped); a profile secret named `callback-token` would collide with its mount target and is now rejected at configuration sync AND at plan build: `ProfileSecret_ReservedName_Fails`, `ReservedCallbackName_FailsStartClosed`, grammar `Platform/Secrets/ProfileSecretName.cs`
- [-] Agent GitHub/MCP operations failing the Run with a clear error code — the Run-failure path is proven (Bridge `failed` callback → `error_code` on Run, ticket 09 live: `planner_stub_error`); the concrete GitHub-auth error code is applied when the first real GitHub-using agent lands (ticket 12/18) — deferred with owner agreement

## What has been done (branch `task-11b-secrets-pat-lifecycle`, 2026-11-21)

Scope ruling (owner, before implementation): the PAT *renew* logic is skipped — no rotation runbook and
no expiry tracking; decision 65 is implemented as its error clause only (revoked/expired PAT → proper,
actionable error; no automatic fallback), and the effort goes into verifying/pinning the
secrets→container injection path (decision 54). The mount mechanics themselves already existed from
tickets 02/09/10 (`environment_configuration = {"secret_references":[names]}`, per-profile read-only
`/run/secrets/<name>` mounts fail-closed at `/start`); ticket 11b closes its gaps:

- **`Platform/Secrets/ProfileSecretName.cs`** — the shared secret-reference grammar, enforced twice:
  `ConfigValidator` (startup, per-profile aggregated violations with file context) and the Runtime API's
  container-plan build over frozen `AgentProfileVersion.environment_configuration` rows (fail closed).
  Rules: non-empty single segment `[A-Za-z0-9][A-Za-z0-9._-]*` (no separators/traversal, no leading dot),
  duplicate references flagged, and the reserved name `callback-token` rejected — that name is the
  decision-60 per-Run callback credential's exclusive mount target, which is exactly what keeps the two
  credential planes functionally independent. (`StartRun.ParseSecretReferences` also dedupes, so one
  duplicate can never become a double mount.)
- **Poller proper-error clause (decision 65):** stream failures now carry the HTTP status through
  `SyncCursors.RecordFailureAsync` → `StreamResult.Status`; `PollerCycle` upgrades any 401 from the
  generic stream-failure warning to an Error-level line — "the polling PAT is revoked, expired, or
  invalid … synchronization stays broken until the host-managed PAT file is replaced (no automatic
  fallback, decisions 50/65)". Cursor `last_error` records the failure; other streams continue; PAT
  bytes never log (Redact discipline unchanged).
- **Config Loader proper-error clause:** a 401 on the `GET /user` identity check / repository lookups
  aborts startup with "GET /user failed: HTTP 401 — GitHub rejected the PAT (revoked, expired, or
  invalid); replace the host-managed PAT file (no automatic fallback, decision 65)" — the compose
  `--wait` gate shows it red, mirroring the ticket-02 failure-mode doctrine.
- **Deliberately not changed:** compose/mounts (runtime-api already reads the secrets dir read-only,
  decision 54). DB schema (secrets stay names-only — no migration), the Outbox Worker (401 →
  `dead_letter` was verified live in ticket 03), the agent path's concrete error code (no real
  GitHub-using agent exists yet; the Bridge `failed` → `error_code` → Run path is ticket-09-proven).
- **Review round (same session, owner-approved):** mount-source hygiene, probed against the in-container
  managed view (`RuntimeStorage.SecretHostPath` now returns `(hostPath, error)`): files only (a
  directory named like a secret no longer mounts "successfully"), and empty files fail closed with a
  naming error instead of mounting a broken secret. Plus `ParseSecretReferences` fails closed on a
  wrong-shaped frozen `environment_configuration` row (non-object root / non-array `secret_references`)
  instead of silently running the agent without its secrets — `{"secret_references":null}` (the Config
  Loader's no-secrets shape) still means "none".

## How to manually test

> **Verification state:** all commands below were executed during development on branch
> `task-11b-secrets-pat-lifecycle` with the outputs shown. The **live-stack walkthrough section at the
> end has NOT been executed in this session** — it needs Komodo + the stack running (owner go-ahead).

Automated verification (executed, real outputs):

```sh
dotnet build src/PiAgent.slnx --nologo
#   → 0 Warning(s) / 0 Error(s)
dotnet test src/PiAgent.slnx --nologo          # requires Docker (Testcontainers postgres)
#   → Passed! 2 (Architecture) + 9 (Platform) + 47 (Infrastructure) + 474 (Hosts.Tests)
#   →   total 532, Failed: 0
docker compose --env-file src/.env -f src/compose.yaml config --quiet
#   → (silent exit 0 — wiring unchanged; no new env/mounts needed)
dotnet test src/PiAgent.slnx --nologo --filter "FullyQualifiedName~StartContainerPlanTests"
#   → 11 passed (the decision-54 pinning set)
```

New/changed tests and what each pins (all green in the 528 run):

| test | pins |
|---|---|
| `ConfigValidatorTests.ProfileSecret_ReservedName_Fails` | `callback-token` rejected at config sync ("reserved for the per-Run callback credential") |
| `ConfigValidatorTests.ProfileSecret_TraversalName_Fails` | `../github-sync-pat.txt` rejected (single-segment grammar) |
| `ConfigValidatorTests.ProfileSecret_EmptyName_Fails` | blank references rejected |
| `ConfigValidatorTests.ProfileSecret_DuplicateReference_Fails` | duplicate references flagged |
| `ConfigValidatorTests.ProfileSecret_ValidNames_Pass` | `github_agent_pat` / `mcp-github.token-v2` pass, valid sample still clean |
| `StartContainerPlanTests.ReservedCallbackName_FailsStartClosed` | frozen version rows with the reserved name fail `/start` closed, even if the file exists |
| `StartContainerPlanTests.TraversalName_FailsStartClosed` | frozen rows fail closed on traversal names |
| `StartContainerPlanTests.DuplicateReferences_MountOnce` | duplicated reference → exactly one mount |
| `StartContainerPlanTests.OnlyReferencedSecrets_AreMounted_LestPrivilege` | second existing secret file NOT mounted for this profile; `/etc/pi-agent/secrets/<name>:/run/secrets/<name>:ro` shape; the name never enters the container environment |
| `PollerCycleTests.RevokedPat401_SyncFailsWithProperError_NoFallback` | 401 → single Error-level decision-65 line, no PAT bytes anywhere, cursor `last_error` set, other streams continue |
| `GitHubClientTests.GetUser_NonSuccess_ThrowsConfigExceptionWithStatus` | startup 401 message names "revoked, expired, or invalid" + "no automatic fallback"; PAT never in the message |
| `StartContainerPlanTests.SecretFile_IsEmpty_FailsStartClosed` | an empty secret file rejects `/start` ("exists but is empty") instead of mounting broken |
| `StartContainerPlanTests.DirectoryNamedLikeSecret_FailsStartClosed` | a directory in the secrets dir is not a mountable secret (files-only probe) |
| `StartContainerPlanTests.MalformedEnvironmentConfiguration_FailsStartClosed` | frozen row `[]` / non-array `secret_references` → fail closed, not silent-no-secrets |
| `StartContainerPlanTests.NullReferencesArray_MountsNothing` | `{"secret_references":null}` (Config-Loader no-secrets shape) stays "none" |

### Live walkthrough — EXECUTED 2026-11-21 (owner-approved; evidence below is real output)

Environment: branch binaries (stack-rebuild), Komodo up, stack `--wait` green (`readyz: postgres ok, komodo ok`).

**Demo 1 — profile-secret mount (least privilege, decision 54).** Created `src/secrets/demo-token.txt`
(`dummy-value-not-a-token`; deleted after), stub profile `secrets: ['demo-token.txt']` → orchestrator
restart → config sync created **stub v5** (`environment_configuration = {"secret_references":
["demo-token.txt"]}` — names only in PostgreSQL) → the ticket-10 build flow built and validated
`agent-stub:v5` automatically → `/plan` on issue #10 via the internal commands hook (pr #13 correctly
rejected `wrong_target_type` first) → Task pinned to v5 → Run `bddfc675-…` → container
`pi-session-bddfc675-…` up. Evidence: `docker inspect` binds = workspace/session/logs/spool /
run-tree `callback-token` / `src/secrets/demo-token.txt:/run/secrets/demo-token.txt:ro` — and nothing
else from `src/secrets` (the sync/write PATs are never in an agent container); `docker exec ls -l
/run/secrets` → exactly `callback-token` + `demo-token.txt`; `cat` the file → `dummy-value-not-a-token`
(value travels by mount only); `env | grep -iE 'demo|dummy|token|secret'` → only
`PI_CALLBACK_TOKEN_FILE=/run/secrets/callback-token` (the decision-60 path pointer, by design — no
secret value). Deterministic inspection window: one reversible lever flip
(`jsonb_set(bridge_configuration,'{behavior}','"hang"')` on v5, ticket-09-walkthrough precedent) →
reverted to `succeed` after; run terminated via Runtime API `/stop`.

**Demo 2 — revoked-PAT proper error (decision 65, no fallback).** `src/secrets/github-sync-pat.txt` →
garbage → poller restart → within one cycle:

```
{"level":"Error","service":"poller","message":"poller sync failed: aliaksandrzh/04/issues: repos/…
/issues: HTTP 401: {"message": "Bad credentials", …} — the polling PAT is revoked, expired, or
invalid: system configuration error; synchronization stays broken until the host-managed PAT file is
replaced (no automatic fallback, decisions 50/65)"}
```

`level: Error` for every 401 stream (issues/pulls/pr_reviews/issue_comments/pr_review_comments),
`sync_cursor.last_error` recorded per stream, garbage token bytes: **0 occurrences** in poller logs.
Restore original PAT → poller restart (13:58:15Z) → last Error line predates the restart (13:57:54Z);
next cycle clean — all 7 cursors `last_error IS NULL` with fresh `last_success_at` (13:59:22Z,
sweep 14:38:43Z).

Dev-DB state after the demos (disclosed, nothing dropped/wiped; pgdata volume retained):
run `bddfc675` row stays `starting` with container `observed=failed` — the known pre-ticket-13 gap
(ticket-09 walkthrough's documented stand-in SQL covers these; left as-is deliberately); stub v5
remains `ready`/`agent-stub:v5` referencing the since-deleted secret — fail-closed if ever dispatched;
the next `task stack` startup syncs the reverted `profile.yaml` (no `secrets`) and automatically builds
`agent-stub:v6` + repoints routing. Config tree, compose, and the branch's tracked files are back to
their original state.
