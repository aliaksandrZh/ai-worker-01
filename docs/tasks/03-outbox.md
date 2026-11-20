# 03: Outbox dispatcher — system notification comments

**What to build:** The `OutboxAction` store and the Outbox Dispatcher worker that publishes orchestration-owned system notification comments to GitHub (`action_type = comment` only — agent GitHub writes never go through the Outbox). Delivery: exponential backoff for transient failures (network, 5xx, 429, rate-limit 403) with max interval 1 h and total window 24 h → retained `dead_letter`; permanent errors (401, regular 403, 404, 422) → `dead_letter` immediately, surfaced in host logs and health status. Duplicate control: an invisible HTML-comment marker carrying `idempotency_key` is embedded in the delivered comment and checked before re-sending after an ambiguous timeout; one dispatcher owns one action at a time via a DB lease. Authentication: repository-scoped write PATs via config-plane secret references.

**Spec:** `docs/pi-agent-architecture/02-data-model.md` §20; `03-state-machines.md` §8 (incl. `dead_letter`); `05-v1-decisions.md` decision 67; `01-architecture-and-flows.md` §2 (Outbox Dispatcher); `04-api-contracts.md` §8 (idempotency).

**Blocked by:** 02

**Status:** done

- [x] Enqueued action → comment published on the target work item with the invisible idempotency marker — verified live 2026-11-20: INSERT into `outbox_action` (idempotency_key `e2e-task03-marker-0001`) → worker log `outbox comment delivered … to aliaksandrzh/04/10 (attempts 1)`, comment `7039600376` on issue 10 with first body line `<!-- pi-agent:outbox:e2e-task03-marker-0001 -->` and visible text intact (see manual test 1); pinned by `OutboxDispatcherTests.Success_CompletesWithMarker_VisibleTextUntouched`
- [x] Injected network/5xx failures → retries with backoff, then `dead_letter` with payload/attempts/last error retained — automated: `ServerError_BackoffDoubles` (8 s → 16 s), `RateLimit403_TransientWithRetryAfter` (Retry-After honored), `AmbiguousError_MarkerCheckSuppressesRepost` (network failure → `ambiguous:` last_error), `RetryWindowExhausted_DeadLetter_NoPost` (age ≥ window → dead_letter, no POST), `MarkTransientFailure_SetsBackoff`/`MarkDeadLetter_RetainsPayloadAttemptsError`; live: network failure covered by the lease-wedge recovery below (see manual test 4); live 5xx/429 injection has no GitHub-side hook and is test-verified only
- [x] 401/403/404/422 → straight to `dead_letter` — verified live: garbage write PAT → fresh action `e2e-task03-marker-0003` dead_lettered at attempts 1 on the first POST (`HTTP 401 … Bad credentials`, token bytes absent from logs and `last_error`), `outboxDeadLetter` 1→2 on worker `/metrics`; regular-403 dead-letter + rate-limit-403 transient pinned by `Regular403_DeadLetter` / `RateLimit403_TransientWithRetryAfter`; 404/422 pinned by `PermanentStatuses_DeadLetterImmediately` (test-only)
- [x] Two concurrent dispatchers never double-deliver one action (DB lease) — `OutboxActionStoreTests.ClaimNext_ConcurrentClaims_DoNotOverlap` over real postgres (`FOR UPDATE SKIP LOCKED` claim; one winner on a single row, distinct rows across two; expired lease reclaimable)
- [x] Re-delivery after an ambiguous timeout does not create a duplicate comment (marker check) — verified live: the SAME action re-armed (`status='pending'`, `attempts=1`) → worker log `outbox duplicate suppressed e2e-task03-marker-0001`, GitHub comment count unchanged (2) (manual test 2); pinned by `AmbiguousError_MarkerCheckSuppressesRepost` / `ServerError_MarkerCheckSuppressesRepost`
- [x] Acceptance scenario "Outbox" (`05` matrix) passes — delivery retries, duplicate prevention, dead-letter handling all executed live against `aliaksandrZh/04` (manual tests 1–4) plus 137 Hosts.Tests / 157 total automated tests green (`dotnet test` per project 2026-11-20: Platform 9, Infrastructure 11, Architecture 2, Hosts 137)

## Done (branch `task-3-outbox`, 2026-11-20)

- Schema: `outbox_action` — unique `idempotency_key`, `(status, available_at)`, jsonb `payload`, Restrict FK to `repository`,
  decision-67 lease columns `locked_by`/`locked_until` (§20's field list omits them — owner ruling 1); `work_item_id`/`task_id`
  are plain Guid columns, FKs land with tickets 04/07. Migration `AddOutbox`.
- `OutboxActionStore` (`Infrastructure/Persistence`): validating idempotent enqueue (duplicate key → existing row), atomic claim
  (`SELECT … FOR UPDATE SKIP LOCKED` + lease + retry-window exhaustion inside the claim transaction), owner-guarded
  complete/transient-failure/dead-letter transitions, cancel-pending-by-work-item. Claim reclaims `processing` rows whose lease
  expired (03 §8) — plan-ruled extension of the brief SQL.
- Outbox Worker (`src/Hosts/OutboxWorker/`): `OutboxDispatcher` (one action per 5 s tick) — marker
  `<!-- pi-agent:outbox:{idempotency_key} -->` prepended at dispatch; marker check before every re-send (attempts ≥ 2 —
  post-claim counting, live-found ruling); error classification permanent vs transient per decision 67 with `Retry-After` honored
  (capped at 1 h); backoff 8 s·2ⁿ⁻¹ capped 1 h; §14 warning on each dead_letter; `/metrics` now carries real
  `outboxPending/outboxFailed/outboxDeadLetter` (`failed` reserved, stays 0 per ruling).
- Plumbing: `GitHubWriteClient` (GitHub JSON/Bearer/UA discipline mirroring `GitHubClientFactory`; rate-limit + Retry-After header
  capture; PAT redacted even from hostile response echoes), `PatFile` (same host-managed-file pattern as the sync PAT — env
  `PI_AGENT_GITHUB_WRITE_PAT_FILE`, read once at startup, missing/empty = startup config error; one bot-owned write PAT serves all
  repositories, fine-grained scoping is GitHub-side per decision 50), `OutboxWorkerConfig` (`outbox_retry_window` from
  `system.yaml`, default 24 h, malformed value fails startup).
- Compose: `outbox-worker` mounts `config/` + `secrets/` read-only with the write-PAT env; `.env.example`/README updated.
- Live-walkthrough fixes (branch evidence in `docs/superpowers/…2026-11-20-task03-outbox.md` ledger): claim SQL reclaim of
  expired-lease rows, and the `attempts > 1` marker trigger + GET failures classified like writes — found during step 4 of the
  walkthrough (garbage PAT had wedged a row in `processing`); TDD-pinned by `FirstAttempt_DoesNotProbeGitHub` and
  `GetFailure_ClassifiesLikeWrite`.
- 157 automated tests green; 4 walkthrough ruling entries recorded in the plan ledger.

### Manual test (owner)

> **Verified line-by-line on the owner's stack 2026-11-20** — every command below ran in this shape
> during development (expected outputs are real ones from those runs; ids/keys are generalized with
> the live-run values annotated in comments).

Prerequisites:
- After any `src/` change run `task stack-rebuild` once — it is the only step that rebuilds images (`task stack` alone reuses them).
- Two PAT files present: `src/secrets/github-sync-pat.txt` (identity check) and `src/secrets/github-write-pat.txt` (Issues write
  on the registered repo). Missing/empty write PAT fails `outbox-worker` startup — visible red in the compose `--wait` gate.
- One issue on the registered repository to receive comments (live run used issue **10** on `aliaksandrZh/04`).

1. **Happy path** (`task stack` first; outbox-worker metrics via the runtime API only exposes worker baselines — the outbox keys
   live on the worker itself):
   ```sh
   task stack
   docker compose --env-file src/.env -f src/compose.yaml exec -T outbox-worker curl -s localhost:8080/metrics
   #   → "outboxPending":0,"outboxFailed":0,"outboxDeadLetter":0
   docker compose --env-file src/.env -f src/compose.yaml exec -T postgres psql -U pi_agent -d pi_agent -t -c \
     "SELECT id FROM repository;"        # live: 3c701326-3d6f-48c9-97a1-28a3ff4c5334
   docker compose --env-file src/.env -f src/compose.yaml exec -T postgres psql -U pi_agent -d pi_agent -c \
     "INSERT INTO outbox_action (id, repository_id, work_item_id, task_id, action_type, payload, status, attempts, available_at, idempotency_key, created_at) \
      VALUES (gen_random_uuid(), '<repository_id>', '11111111-2222-3333-4444-555555555555', NULL, 'comment', \
              '{\"issue_number\": 10, \"text\": \"<notice text>\"}'::jsonb, 'pending', 0, now(), 'e2e-marker-1', now());"
   sleep 8
   docker compose --env-file src/.env -f src/compose.yaml logs outbox-worker --since 2m | grep delivered
   #   → "outbox comment delivered <action_id> to aliaksandrzh/04/10 (attempts 1)"
   PAT=$(cat src/secrets/github-sync-pat.txt)
   curl -s -H "Authorization: Bearer $PAT" https://api.github.com/repos/<owner>/<repo>/issues/10/comments | head -c 400
   #   → newest comment body starts with <!-- pi-agent:outbox:e2e-marker-1 --> followed by the text
   ```
2. **Duplicate suppression** — re-arm the SAME action so the marker check runs:
   ```sh
   docker compose --env-file src/.env -f src/compose.yaml exec -T postgres psql -U pi_agent -d pi_agent -c \
     "UPDATE outbox_action SET status='pending', attempts=1, locked_by=NULL, locked_until=NULL, available_at=now() WHERE idempotency_key='e2e-marker-1';"
   sleep 10
   docker compose --env-file src/.env -f src/compose.yaml logs outbox-worker --since 1m | grep duplicate
   #   → "outbox duplicate suppressed e2e-marker-1 (action <id>)"; GitHub comment count unchanged
   ```
3. **Dead-letter (401)** — temporarily replace the write PAT with garbage and restart the worker (restart re-reads the file;
   `task stack-rebuild && task stack` works the same but is only needed for source changes):
   ```sh
   cp src/secrets/github-write-pat.txt /tmp/write-pat.bak   # restore manually afterwards
   printf 'ghp_garbage-token-not-a-real-pat\n' > src/secrets/github-write-pat.txt
   docker compose --env-file src/.env -f src/compose.yaml restart outbox-worker
   # INSERT as in step 1 with idempotency_key 'e2e-marker-2'
   sleep 12
   docker compose --env-file src/.env -f src/compose.yaml exec -T postgres psql -U pi_agent -d pi_agent -c \
     "SELECT status, attempts, last_error FROM outbox_action WHERE idempotency_key='e2e-marker-2';"
   #   → dead_letter | 1 | repos/.../comments: HTTP 401: {"message": "Bad credentials", ...}; PAT bytes absent
   docker compose --env-file src/.env -f src/compose.yaml logs outbox-worker --since 2m | grep dead_letter   # warning, no PAT
   docker compose --env-file src/.env -f src/compose.yaml exec -T outbox-worker curl -s localhost:8080/metrics
   #   → "outboxDeadLetter":1
   cp /tmp/write-pat.bak src/secrets/github-write-pat.txt && docker compose --env-file src/.env -f src/compose.yaml restart outbox-worker
   ```
   Live-run precision: the attempts-1 clean dead-letter shown here is the fresh insert (real key
   `e2e-task03-marker-0003`); the key `e2e-task03-marker-0002` from the same run first wedged in
   `processing` — that was the live bug discovery (see Done) and it is step 4's lease-wedge case.
   The live "PAT bytes absent" check was a spot check; the never-log guarantee is pinned by the
   automated `TokenNeverLogged` test (hostile response echoing the PAT must stay redacted).
4. **Lease-wedge recovery** (bonus, exercised live): if a dispatcher dies mid-delivery, an expired lease is reclaimed and the
   row then follows normal classification — live it ran: wedged row → attempts 2, 3 → GET 401 → `dead_letter`, `outboxDeadLetter`
   counts up. `docker compose ... logs outbox-worker | grep dead_letter`.
5. **Teardown**:
   ```sh
   task stack-down
   docker compose --env-file src/.env -f src/compose.yaml ps     # header only
   docker ps --filter name=outbox --format '{{.Names}}'          # empty
   ```

**Only test-verified (no live GitHub injection point):** real 5xx/429 retry chain with `Retry-After`/backoff values
(`ServerError_BackoffDoubles`, `RateLimit403_TransientWithRetryAfter`), 404/422 immediate dead-letter
(`PermanentStatuses_DeadLetterImmediately`), full 24 h window exhaustion (`ClaimNext_RetryWindowExhausted_DeadLetters`).
Walkthrough `dead_letter` rows were left in the dev DB (retention per decision 67); remove with
`DELETE FROM outbox_action WHERE idempotency_key LIKE 'e2e-task03-marker-%';` if unwanted.
