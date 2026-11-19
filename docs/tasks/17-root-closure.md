# 17: Root closure, merged PR, reopen policy

**What to build:** The root WorkItem closure flow: close/merge of the root Issue or PR → workflow marked `closed` and new commands rejected; active Tasks/Runs cancelled; containers stopped and removed (Komodo deployments); the Workflow storage tree (task workspaces, per-Run `pi-session/` and `logs/`) deleted after containers stop; DB history retained with cleanup/deletion flags (PostgreSQL records never auto-deleted). Merged PR treated as a closed WorkItem (no separate merged event). Reopen policy: a reopened Issue/PR leaves the Workflow `closed`, no second Workflow is created (`UNIQUE(work_item_id)`), commands for the reopened item rejected — root-closure events and reopen detection from Poller sync (including `work_item_closed`/`close_reason = merged` events). Issue and its related PR remain separate roots — closing one touches nothing of the other.

**Spec:** `docs/pi-agent-architecture/01-architecture-and-flows.md` §11 (closure + reopen); `02-data-model.md` §9 (workflow), §22 (root closure + retention); `03-state-machines.md` §10; `05-v1-decisions.md` decisions 20, 21, 40, 73 (reopen), 76 (retention).

**Blocked by:** 12, 05

**Status:** ready-for-agent

- [ ] Closing the root Issue → Workflow `closed`; commands rejected; containers removed; storage tree deleted; DB rows retain history with flags
- [ ] Closing the related Issue does not close/clean the PR Workflow (and vice versa) — separate roots
- [ ] Merged PR → cleanup identical to closure; DB keeps `merged_at`
- [ ] Reopen → Workflow stays closed, no second Workflow, commands rejected with a recorded reason
- [ ] Cleanup is silent (no GitHub notifications per decision 29)
- [ ] Acceptance scenario "Cleanup" (`05` matrix) passes
