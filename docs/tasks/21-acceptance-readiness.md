# 21: Acceptance suite + v1 readiness gate

**What to build:** The end-to-end acceptance suite defined by the spec's acceptance matrix, assembled into one automated suite running against a dedicated disposable GitHub repository (never production): repository onboarding, polling, command parser, authorization, planning, approval, coding, reviewing, queue (cross-Workflow concurrency under the cap + same-Workflow exclusion), stop, retry, timeouts, callbacks (duplicates, API outage, spool recovery), outbox (retries, duplicate prevention, dead-letter), restart recovery, cleanup. Each test records preconditions → action → expected PostgreSQL states → expected GitHub side effects → expected container/storage side effects. The suite runs in CI on the release build; it is the v1 readiness gate. Phase-1 configuration (planner-only rollout) verified: profile-gated commands rejected with `agent_profile_not_configured`.

**Spec:** `docs/pi-agent-architecture/05-v1-decisions.md` — "V1 acceptance criteria" (matrix + readiness criterion) and rollout phases (decision 36); `06-command-reference.md` §13 (notification templates).

**Blocked by:** 12, 13, 14, 15, 16, 17, 18, 19, 20

**Status:** ready-for-agent

- [ ] All 16 acceptance scenarios automated on the disposable GitHub repository
- [ ] Repository pause behavior (decision 75) and revoked-PAT handling (decision 65) verified inside the Authorization / Queue / Command parser scenarios
- [ ] The cross-cutting scenarios hold: cross-Workflow concurrency, restart recovery, callback outage + spool, Outbox duplicate control, cleanup
- [ ] The suite runs green in CI on the release build
- [ ] Phase-1 rollout config ships the planner profile only; profile-gated commands rejected via `agent_profile_not_configured`
- [ ] System notification templates match the spec (command, agent, attempt `#N`, stage, diagnostic id — no secrets)
- [ ] V1 readiness criterion met: all main happy paths and error paths pass automatically
