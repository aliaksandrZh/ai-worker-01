# GitHub write API (App) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Small HTTP write-API managing issues/PRs/reviews/commits on configured repos through the installed GitHub App, as groundwork for AI-agent tooling.

**Architecture:** `github-polling/write-api.mjs` — plain Node `createServer`, imports the shared REST layer (`gh`, error types) from `poll.mjs`; same `.env`/credentials; separate process so the monitor stays read-only. Exported `route(req, res, fetchImpl)` for tests; the file only starts listening when run directly.

**Tech stack:** Node built-ins only; `node:test`.

**Spec:** approved chat design 2026-11-19 (endpoint table below is binding).

## Global Constraints

- Zero new dependencies; Node ≥22.9; ESM `.mjs`.
- Routes (exact):
  - `GET /repos/:r/contents/PATH?ref=` — file → `{...item, text}` (utf8-decode base64 when `encoding === "base64"`); dir → array
  - `GET /repos/:r/pulls/:n/diff` — GitHub diff via `Accept: application/vnd.github.diff`, text passthrough
  - `GET /repos/:r/pulls/:n/files` — JSON list
  - `POST /repos/:r/issues` `{title!(, body, labels, assignees)}`
  - `PATCH /repos/:r/issues/:n` `{state?, title?, body?, labels?, assignees?, milestone?}` (close/reopen/edit)
  - `POST /repos/:r/issues/:n/comments` `{body!}` (works for issue + PR threads)
  - `POST /repos/:r/pulls` `{head!(, base, title, body)}`
  - `POST /repos/:r/pulls/:n/reviews` `{event: APPROVE|REQUEST_CHANGES|COMMENT (, body, comments: [{path, line, body}])}`
  - `POST /repos/:r/changes` `{branch!, message!, updates!: [{path!, content!}], pr?: {base, title, body}}` → ensures branch (create from default when missing), commits each update via `PUT /repos/:r/contents/:path` (existing file sha via `GET /contents/:path?ref=branch` when present), opens PR when `pr` present; returns `{branch, commits: [sha…], pr?}`
  - `GET /health` → route list
- Error mapping: invalid JSON/missing required fields → 400; `NotFoundError`→404; `RateLimitError`→429×`{error, resetAt}`; `AuthError`→502 (message); other `ApiError`<500→original status; ≥500/anything→502. All bodies `{...}` JSON via `send(res, code, body)`.
- `poll.mjs` may gain: `gh` `opts.headers` override + non-JSON `text` in the return (existing `items` consumers untouched).

## Review Focus

- Diff endpoint must not JSON-parse a diff body (pinned: gh returns `text` when custom accept passed).
- `changes` must commit against the branch head created one update earlier (sequential ref updates; pinned via multi-file stub queue).
- Missing required fields must 400 before any network call (pinned).

---

### Task 1: route surface + tests

**Files:**
- Create: `github-polling/write-api.mjs`; Test: `github-polling/write-api.test.mjs`
- Modify: `github-polling/poll.mjs` (gh opts.headers + text return)

- [ ] **Step 1 (RED):** failing tests: all routes passthrough with `setToken("t")` stub; 400 on bad JSON / missing `title` / missing `body` / bad review `event`; contents file decodes text; diff returns raw text with diff accept; changes flow asserts call order (GET default_branch → GET refs → 404 → POST ref → 404 content → PUT content (+commit sha) → POST pulls → response `{branch, commits, pr}`); unknown route → 404 `{error}` with route list.
- [ ] **Step 2:** `node --test github-polling/write-api.test.mjs` → FAIL.
- [ ] **Step 3:** implement per Global Constraints.
- [ ] **Step 4:** `node --test` → PASS (and `task test` full suite still green).
- [ ] **Step 5:** Commit `feat(github-polling): write-api — issues, comments, PRs, reviews, contents, changes`.

### Task 2: wiring + live verification

**Files:**
- Modify: `Taskfile.yml` (task `github-api`, port 3901), `github-polling/.env.example` (`WRITE_API_PORT`)

- [ ] **Step 1:** config wiring.
- [ ] **Step 2:** live smoke via the App (permission bump must be accepted by user, else 403): create issue → comment → close → delete; `changes` → branch `write-api-smoke` + file commit + PR → fetch diff via API → close PR; cleanup branch+issue via `gh`; poller untouched.
- [ ] **Step 3:** Commit `feat(github-polling): write-api wiring — verified live via app`.
