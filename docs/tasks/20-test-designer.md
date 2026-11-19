# 20: /test-cases + test-designer profile

**What to build:** The test-designer phase: `/test-cases` on an open Issue → `test_design` Task on the test-designer profile → new Pi session + workspace → structured test scenarios/cases (including assumptions and uncovered requirements) saved as a `test_cases` Artifact and published as a readable summary comment via GitHub MCP (requiredPublications correlation applies). PR targets explicitly rejected with a clear explanation. The command designs tests; it never implements or runs them (profile behavior instructions).

**Spec:** `docs/pi-agent-architecture/06-command-reference.md` §6 (/test-cases); `05-v1-decisions.md` decisions 36 (phase gating), 48 (Issue-only); `02-data-model.md` §10 (task type `test_design`), §17 (artifact type `test_cases`).

**Blocked by:** 12, 11, 10

**Status:** ready-for-agent

- [ ] `/test-cases` on an open Issue → `test_cases` Artifact + summary comment published (correlated before completion)
- [ ] `/test-cases` targeting a PR → rejected with a clear explanation (notification via Outbox)
- [ ] The test-designer profile never implements or runs tests (profile instructions)
- [ ] Task Succeeded → Workflow `waiting_for_human`; storage retained per standard lifecycle
