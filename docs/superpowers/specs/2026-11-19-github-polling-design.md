# GitHub repo activity polling — design (small change)

**Intent.** Console monitoring of activity on configured GitHub repositories (start: `aliaksandrZh/04`), as groundwork for a future AI agent integration. Logs every discovered event and change to the console; persists cursors so later runs only log what is new.

**Goals / non-goals.** Track issues (created/updated/closed/reopened, assignments, labels, milestones, comments), PRs (created/updated/closed/merged, assignments, review requests, reviews, inline comments, commits), repo commits and repo events. Detect deltas between polls; paginate; survive rate limits, network failures, auth failures. **Not** implemented: AI processing, notifications, any modification of repo state, notifications API.

**Constraints.** Plain Node ESM `.mjs`, Node ≥22.9, zero new dependencies, follows repo conventions (env config with `--env-file-if-exists=.env`, `.env.example`, root `Taskfile.yml` task). New code lives under `github-polling/`.

## Approaches considered

1. **All calls through `gh api` subprocess** — rejected: a process spawn per request; `--paginate` output is not uniformly parseable JSON; rate-limit headers are not exposed for pagination/error handling.
2. **`gh` for credentials, REST via `fetch` (chosen)** — one `fetch` wrapper for every endpoint: `Link`-header pagination, `x-ratelimit-*` handling, typed errors. Token resolved once at startup: `gh auth token` (verified authed, `repo` scope) → `GITHUB_TOKEN` env fallback → anonymous. This satisfies "gh for auth when present/configured, REST otherwise" with a single code path.
3. **Octokit SDK** — rejected: a dependency for code this project already writes by hand.

## Sources per repo (one paged GET each, per poll)

| Endpoint | Purpose / cursor |
|---|---|
| `/repos/R/events` | repo-wide feed (reviews, labels, milestones, assignments, pushes…) — stop when seen id; **verified live: can be empty on quiet repos, so it is enrichment, not the primary source** |
| `/repos/R/issues/events` | repo-wide issue/PR events (assigned, labeled, milestoned, closed, reopened) — stop when seen id |
| `/repos/R/issues?state=all&sort=updated&direction=desc` | issues (+PR flag) — snapshot diff by number |
| `/repos/R/pulls?state=all&sort=updated&direction=desc` | PRs incl. `merged_at`, `requested_reviewers` — snapshot diff by number |
| `/repos/R/issues/comments?sort=created&direction=desc` | issue + PR conversation comments — numeric id cursor |
| `/repos/R/pulls/comments?sort=created&direction=desc` | inline review comments — numeric id cursor |
| `/repos/R/commits` | commits — stop when seen sha |

Verified live 2026-11-19 against `aliaksandrZh/04` (repo nearly empty; `-F per_page` on GET 404s — query params go in the URL; Link headers and rate-limit headers present; 5000 req/h).

**Per-PR detail endpoints (`pulls/N/reviews` etc.) skipped:** they need one call per PR; reviews and review requests already arrive via the events/`issues/events` feeds and PR snapshots.

## Change detection & state

`github-polling/state.json` (gitignored), per repo: `base:boolean`, `eventIds[]`, `issueEventIds[]`, `commitShas[]` (ring buffers, cap 5000), `maxIssueCommentId`, `maxPullCommentId`, `issues{}`, `pulls{}` snapshots keyed by number holding `{state, title, updated_at, merged_at, labels, assignees, milestone, draft, requested_reviewers}`. Deltas emit per-field change records (closed/reopened, assigned/unassigned, labeled/unlabeled, milestoned/demilestoned, merged, review-requested/-removed, title edits, draft/ready). First poll logs everything found as `[baseline]` plus a one-line per-source summary; afterwards only deltas.

## Polling, errors, output

Loop each `GITHUB_POLL_INTERVAL_SEC` (default 60) over `GITHUB_REPOS` (comma-separated, default `aliaksandrZh/04`). Pagination follows `rel="next"` with a page cap (default 5 × 100). 403 + `remaining=0` or `Retry-After`/429 → warn and sleep until reset; 401 → clear fatal exit; 404 → warn, skip repo this cycle; network errors → warn, retry next tick. One console line per item: `{event-time} [{kind}] owner/name #n (author) — what — url`, bodies flattened and truncated.

**Left to builder:** exact log phrasing, ring-buffer cap constant, internal function split — within the interfaces pinned in the plan.
