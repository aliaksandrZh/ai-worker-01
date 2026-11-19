# 03: Outbox dispatcher — system notification comments

**What to build:** The `OutboxAction` store and the Outbox Dispatcher worker that publishes orchestration-owned system notification comments to GitHub (`action_type = comment` only — agent GitHub writes never go through the Outbox). Delivery: exponential backoff for transient failures (network, 5xx, 429, rate-limit 403) with max interval 1 h and total window 24 h → retained `dead_letter`; permanent errors (401, regular 403, 404, 422) → `dead_letter` immediately, surfaced in host logs and health status. Duplicate control: an invisible HTML-comment marker carrying `idempotency_key` is embedded in the delivered comment and checked before re-sending after an ambiguous timeout; one dispatcher owns one action at a time via a DB lease. Authentication: repository-scoped write PATs via config-plane secret references.

**Spec:** `docs/pi-agent-architecture/02-data-model.md` §20; `03-state-machines.md` §8 (incl. `dead_letter`); `05-v1-decisions.md` decision 67; `01-architecture-and-flows.md` §2 (Outbox Dispatcher); `04-api-contracts.md` §8 (idempotency).

**Blocked by:** 02

**Status:** ready-for-agent

- [ ] Enqueued action → comment published on the target work item with the invisible idempotency marker
- [ ] Injected network/5xx failures → retries with backoff, then `dead_letter` with payload/attempts/last error retained
- [ ] 401/403/404/422 → straight to `dead_letter`
- [ ] Two concurrent dispatchers never double-deliver one action (DB lease)
- [ ] Re-delivery after an ambiguous timeout does not create a duplicate comment (marker check)
- [ ] Acceptance scenario "Outbox" (`05` matrix) passes
