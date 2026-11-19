# GitHub repo activity polling Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Standalone console service under `github-polling/` that polls configured GitHub repos on an interval and logs new activity (issues, PRs, comments, reviews, commits, repo events) once per item.

**Architecture:** One Node ESM script (`poll.mjs`, zero dependencies) with pure logging/state logic, a fetch-based REST layer (token from `gh auth token` → `GITHUB_TOKEN` → anonymous), and a poll loop over 7 paged GETs per repo; cursors and snapshots persist in `github-polling/state.json`.

**Tech stack:** Node ≥22.9 built-ins only (`fetch`, `node:child_process.spawnSync`, `node:fs`), `node:test`.

**Spec:** `docs/superpowers/specs/2026-11-19-github-polling-design.md`

## Global Constraints

- Plain `.mjs` ESM, Node ≥22.9, zero new dependencies.
- No AI processing, no notifications, no writes to GitHub except reading.
- Env config: `GITHUB_REPOS` (comma-separated, default `aliaksandrZh/04`), `GITHUB_POLL_INTERVAL_SEC` (default 60), `GITHUB_PAGE_CAP` (default 5), `GITHUB_TOKEN` (fallback). `github-polling/.env.example` documents them.
- State file `github-polling/state.json`, gitignored, written after every cycle.
- Log line format (exact): `{created_at as ISO} [{kind}] {owner/name}{ #n} ({author}) — {what} — {url}`; first-cycle lines prefixed `[baseline]`.
- Ring buffer cap 5000 for `eventIds` / `issueEventIds` / `commitShas`.
- Per-repo sources (paged, per design table): events, issues/events, issues, pulls, issues/comments, pulls/comments, commits.

## Review Focus

- Events feed can be empty on quiet repos (verified live) — poller must still see commits/issues via list endpoints. Test: live run in Task 4 sees the initial commit while `/events` is empty.
- Rate limit handling must sleep, not crash or hammer. Test: `gh()` on 403 with `x-ratelimit-remaining: 0` throws `RateLimitError` carrying reset time (Task 2).
- Auth failure must exit with a clear message, not loop. Test: `gh()` on 401 throws `AuthError` (Task 2); Task 4 verifies logged message.
- Pagination must stop at cursor to avoid re-logging old pages. Test: `paged()` early-stops when `stop()` matches (Task 2).
- `state.json` corruption must not brick the service. Test: `readState` returns fresh state on bad JSON (Task 1).

---

### Task 1: Pure logic — state helpers, snapshots, diffs, log lines

**Files:**
- Create: `github-polling/poll.mjs` (pure part only — no network, no loop)
- Test: `github-polling/poll.test.mjs`

**Interfaces (produced, consumed by later tasks):**
- `capRing(arr, items, cap) → string[]` — returns `[...arr, ...items].slice(-cap)`
- `flatten(text, max = 200) → string` — collapse `\s+` to single spaces, `…`-truncate to `max`
- `parseNextLink(header) → url|null` — parses `Link:` value, returns `rel="next"` URL
- `snapshot(entry) → {state, title, updated_at, merged_at, labels: string[], assignees: string[], milestone: string|null, draft: boolean, requested_reviewers: string[]}` (labels/assignees `.name`/`.login` mapped; PR-only fields default from PR items, nulls for issues)
- `diffEntry(prev, next) → string[]` of change records: `closed`, `reopened`, `merged`, `unmerged` (never expected, included for correctness), `drafted`, `readied` (draft flag flips), `label: +x`, `label: -x`, `assignee: +x`, `assignee: -x`, `milestone: <t>`, `milestone: (none)`, `review-requested: x`, `review-removed: x`, `title: <new>`. Compare against prev snapshot fields.
- `eventLine(ev) → string|null` — feed event to line body (what+kind), catch-all for unknown types; `null` if nothing meaningful (empty payload)
- `issueEventLine(ie) → string|null` — row of `/issues/events`: `event` field with label/milestone/assignee details
- `commitLine(c) → string-body` and `commentLine(c) → string-body` — bodies for the shared line format
- `readState(file) → state` — JSON parse, `try/catch` → `{ repos: {} }` on missing/corrupt file
- `writeState(file, state)` — `JSON.stringify` compact
- Types: `RateLimitError(resetAtMs)`, `AuthError(status)`, `NotFoundError(status)`, `ApiError(status, body)` classes (constructed here, used in Task 2)

- [ ] **Step 1: Write failing tests** in `github-polling/poll.test.mjs` (`node:test`, one `describe` per function). Pin: `capRing` trims to cap; `flatten` kills newlines and truncates; `parseNextLink` on real GitHub Link header → next URL, `null` on missing; `snapshot` on an issues-list PR entry (has `pull_request`, `merged_by`) and a plain issue (PR-only fields null); `diffEntry` old→new emit `closed`, `label: +bug`, `assignee: -alice`, `review-requested: bob`, `milestone: v1`, `title: New`; snapshot-equal → `[]`; `eventLine` on `PullRequestEvent` action `closed` with `pr.merged` → contains `merged`; `IssueCommentEvent` → contains flattened body and `issue #<n>`; unknown type → contains type name; `readState` on bad JSON → `{ repos: {} }`.
- [ ] **Step 2:** `node --test github-polling/poll.test.mjs` → FAIL (module has no exports).
- [ ] **Step 3:** Implement the exports above in `github-polling/poll.mjs`.
- [ ] **Step 4:** `node --test github-polling/poll.test.mjs` → PASS.
- [ ] **Step 5:** Commit `feat(github-polling): pure poll logic — snapshots, diffs, log lines`.

### Task 2: REST layer — `gh()` fetch wrapper + `paged()`

**Files:**
- Modify: `github-polling/poll.mjs` (append)
- Test: `github-polling/poll.test.mjs` (append)

**Interfaces:**
- `resolveToken() → string` — tries `spawnSync("gh", ["auth", "token"])` (status 0, non-empty stdout), then `process.env.GITHUB_TOKEN`, then `""`
- `gh(path, params, fetchImpl = fetch) → { items, headers, status }` — GET `https://api.github.com` + path + params (URL query, never `-F`-style), headers `accept: application/vnd.github+json`, `x-github-api-version: 2022-11-28`, `authorization: Bearer <token>` when token non-empty, `user-agent: github-polling`. Errors: 403 with `x-ratelimit-remaining: "0"` or 429 or `retry-after` → `RateLimitError(reset)` (reset = `x-ratelimit-reset`×1000 or now+retry-after); 401 → `AuthError`; 404 → `NotFoundError`; other !ok → `ApiError`. Network rejection propagates (caller catches).
- `paged(path, params, opts = { stop, fetchImpl }) → item[]` — `per_page` defaults 100; follows `parseNextLink` until no next link or page ≤ `GITHUB_PAGE_CAP`; when any item matches `stop(item)` it includes that page's items and stops (callers dedup). If page cap hit, `console.warn` once with path.

- [ ] **Step 1: Failing tests** — stub `fetchImpl` returning `{ ok, status, headers: new Headers({...}), json }` objects: 403+remaining-0 → rejects `RateLimitError` with reset; 401 → rejects `AuthError`; 200 no link → returns page, no follow-up; 200 two pages via `Link: rel="next"` → 4 items total, stops early when second page has a `stop` match; page cap 1 → warns and returns first page only.
- [ ] **Step 2:** run → FAIL.
- [ ] **Step 3:** implement `gh`, `paged`, `resolveToken`.
- [ ] **Step 4:** run → PASS.
- [ ] **Step 5:** Commit `feat(github-polling): REST layer — gh() with rate-limit/auth errors, paged() with cursor early-stop`.

### Task 3: Poll cycle — sources, baseline, loop, error handling

**Files:**
- Modify: `github-polling/poll.mjs` (append)
- Test: existing suite still passes; live check in Task 4

**Interfaces:**
- `pollRepo(repo, st) → Promise<void>` — `st` is the per-repo state object, mutated; runs the 7 sources via `paged` + `gh`:
  1. events → unseen ids → `log(kind "event", ev)` via `eventLine`, update `eventIds`
  2. issues/events → unseen → `issueEventLine`, update `issueEventIds`
  3. issues (skip entries `has("pull_request")`) / 4. pulls → per number: `prev ?? null`; `prev==null && !st.base` → `[baseline]` line with current state; `prev==null && st.base` → `opened`/`PR opened` line + diff-free; else `diffEntry` records logged under kind `issue`/`pull`; store `snapshot(entry)`; first cycle sets `st.base = true` at the end
  5. issues/comments → keep items with `id > st.maxIssueCommentId`, log via `commentLine` (kind from `issue_url` containing `/pull/` → `pr-comment` else `comment`), update cursor to max seen id (even when nothing new: set to current max of fetched page)
  6. pulls/comments → same with `maxPullCommentId`, kind `inline`
  7. commits → unseen shas → `commitLine`, kind `commit`, update `commitShas`
  - Every logged line: `${created_at (or commit author date)} [{kind}] ${repo}{ ${'#'+n}} (${author}) — ${body} — ${url}`; `console.log`.
- `pollOnce(repos, state) → Promise<void>` — per repo `try/catch`: `AuthError` → `console.error` + `process.exitCode = 1` (auth cannot self-heal) — actually exit the process (clear fatal); `RateLimitError` → warn once, throw to abort cycle (loop sleeps till reset); `NotFoundError` → warn, skip; other (network/runtime) → warn with message. Saves state after all repos.
- `sleep(ms)`, `main()` — load state via `readState`, resolve interval/repos from env, `while (true) { t0; try pollOnce (catch RateLimitError → sleep till reset) ; sleep(INTERVAL_MS - elapsed) }`
- `isMain` guard: only when `import.meta.url === pathToFileURL(process.argv[1] ?? "").href` call `main()`
- Repo state init: `{ base: false, eventIds: [], issueEventIds: [], commitShas: [], maxIssueCommentId: 0, maxPullCommentId: 0, issues: {}, pulls: {} }`; baseline summary line per repo after first cycle: `[baseline] {repo} done: {counts per source}`.

- [ ] **Step 1:** implement `pollRepo`, `pollOnce`, `main`, guard (signature above; loop shape per design doc "Polling, errors, output").
- [ ] **Step 2:** `node --test github-polling/poll.test.mjs` → still PASS (no regressions); `node github-polling/poll.mjs` starts and polls without crash (Ctrl-C after first cycle).
- [ ] **Step 3:** Commit `feat(github-polling): poll cycle with baseline, cursors, error handling`.

### Task 4: Config wiring + live verification

**Files:**
- Create: `github-polling/.env.example` — `GITHUB_REPOS=aliaksandrZh/04`, commented `GITHUB_POLL_INTERVAL_SEC=60`, `GITHUB_TOKEN`, `GITHUB_PAGE_CAP=5`
- Modify: `Taskfile.yml` — new task `github-polling` (`dir: github-polling`, `node --env-file-if-exists=.env poll.mjs`); `test` task gains `node --test github-polling/poll.test.mjs`
- Modify: `.gitignore` — add `github-polling/.env` and `github-polling/state.json`

- [ ] **Step 1:** write the three config changes.
- [ ] **Step 2:** Live baseline check: `timeout 10 node --env-file-if-exists=.env github-polling/poll.mjs` with interval 5 — expect `[baseline]` commit line for `f916007 Initial commit` (events feed is empty — validates Review Focus #1), baseline summary, then a quiet second cycle (no repeats). Then `git status` — `state.json` must NOT appear.
- [ ] **Step 3:** Delta check: create throwaway issue in `aliaksandrZh/04` (`gh issue create -t "poll-test (delete me)" -b "verifies github-polling delta path"`), add a comment, wait one cycle — expect `[issue] … opened`, `[comment] …` lines with number/author/time/url. Then `gh issue close` + delete via `gh api -X DELETE repos/aliaksandrZh/04/issues/N`, restart with fresh state.json deleted, confirm baseline is quiet about it.
- [ ] **Step 4:** `npm task test` equivalent: `node --test github-polling/poll.test.mjs` + `node --test api/rpc-session.test.mjs` → PASS.
- [ ] **Step 5:** Commit `feat(github-polling): task wiring, env config — verified live baseline + delta`.

## Self-review notes

- Spec coverage: all requirement bullets map to Task 1 (logging format), Task 2 (pagination/rate-limit/auth), Task 3 (sources, cursors, deltas, lifecycle), Task 4 (config, repo list, run instructions). Repo events = source 1–2; PR reviews/review-requests/assignments via events + `issues/events` + pulls snapshots (design decision recorded).
- Type check: `diffEntry` record strings reused verbatim by Task 3 logging; error classes from Task 1 caught in Task 3.
- Proportion: plan ≈ spec size; no bodies given beyond signatures + values pinned by tests.
