// GitHub repo activity poller — console monitoring, stateful across runs.
// Auth: `gh auth token` when the gh CLI is installed+authed, else GITHUB_TOKEN, else anonymous.
// Spec: docs/superpowers/specs/2026-11-19-github-polling-design.md

import { readFileSync, writeFileSync } from "node:fs";
import { spawnSync } from "node:child_process";
import { join, dirname } from "node:path";
import { fileURLToPath, pathToFileURL } from "node:url";

// ---------------- state files ----------------

export class RateLimitError extends Error {
  constructor(resetAt) {
    super("GitHub rate limit reached");
    this.name = "RateLimitError";
    this.resetAt = resetAt;
  }
}
export class AuthError extends Error {
  constructor(status) {
    super(`GitHub auth failed (HTTP ${status})`);
    this.name = "AuthError";
    this.status = status;
  }
}
export class NotFoundError extends Error {
  constructor(status) {
    super(`GitHub API not found (HTTP ${status})`);
    this.name = "NotFoundError";
    this.status = status;
  }
}
export class ApiError extends Error {
  constructor(status, body) {
    super(`GitHub API error (HTTP ${status})`);
    this.name = "ApiError";
    this.status = status;
    this.body = body;
  }
}

export const readState = (file) => {
  try {
    const s = JSON.parse(readFileSync(file, "utf8"));
    return s && typeof s === "object" ? s : { repos: {} };
  } catch {
    return { repos: {} };
  }
};

export const writeState = (file, state) => writeFileSync(file, JSON.stringify(state));

// ---------------- pure helpers ----------------

export const capRing = (arr, items, cap) => [...arr, ...items].slice(-cap);

// store cursor ids without ever duplicating what is already stored
export const absorbFresh = (arr, ids, cap) => capRing(arr, ids.filter((id) => !arr.includes(id)), cap);

export const flatten = (text, max = 200) => {
  const s = String(text ?? "").replace(/\s+/g, " ").trim();
  return s.length > max ? `${s.slice(0, max)}…` : s;
};

export const parseNextLink = (header) => {
  if (!header) return null;
  for (const part of header.split(",")) {
    const [url, ...rels] = part.split(";");
    if (rels.some((r) => r.trim() === 'rel="next"'))
      return url.trim().replace(/^<|>$/g, "");
  }
  return null;
};

export const snapshot = (e) => ({
  state: e.state,
  title: e.title,
  updated_at: e.updated_at,
  merged_at: e.merged_at ?? null,
  labels: (e.labels ?? []).map((l) => l.name ?? l),
  assignees: (e.assignees ?? []).map((a) => a.login ?? a),
  milestone: e.milestone?.title ?? null,
  draft: e.draft ?? false,
  requested_reviewers: (e.requested_reviewers ?? []).map((r) => r.login ?? r),
});

export const diffEntry = (p, n) => {
  const out = [];
  if (p.state !== n.state) out.push(n.state === "closed" ? "closed" : "reopened");
  if (p.merged_at !== n.merged_at) out.push(n.merged_at ? "merged" : "unmerged");
  if (p.draft !== n.draft) out.push(n.draft ? "drafted" : "readied");
  if (p.title !== n.title) out.push(`title: ${n.title}`);
  if (p.milestone !== n.milestone)
    out.push(n.milestone ? `milestone: ${n.milestone}` : "milestone: (none)");
  const addRemove = (label, before, after) => {
    const b = new Set(before), a = new Set(after);
    for (const x of a) if (!b.has(x)) out.push(`${label}: +${x}`);
    for (const x of b) if (!a.has(x)) out.push(`${label}: -${x}`);
  };
  addRemove("label", p.labels, n.labels);
  addRemove("assignee", p.assignees, n.assignees);
  const b = new Set(p.requested_reviewers), a = new Set(n.requested_reviewers);
  for (const x of a) if (!b.has(x)) out.push(`review-requested: ${x}`);
  for (const x of b) if (!a.has(x)) out.push(`review-removed: ${x}`);
  return out;
};

// ---------------- REST layer ----------------

export const resolveToken = () => {
  try {
    const r = spawnSync("gh", ["auth", "token"], { encoding: "utf8" });
    if (r.status === 0 && r.stdout?.trim()) return r.stdout.trim();
  } catch { /* gh not installed */ }
  return process.env.GITHUB_TOKEN || "";
};

let _token = null; // lazily resolved once; tests inject via setToken
export const setToken = (t) => (_token = t);
const token = () => {
  if (_token === null) _token = resolveToken();
  return _token;
};

export const gh = async (path, params = {}, fetchImpl = fetch) => {
  const url = new URL(path, "https://api.github.com");
  for (const [k, v] of Object.entries(params)) url.searchParams.set(k, String(v));
  const t = token();
  const res = await fetchImpl(url, {
    headers: {
      accept: "application/vnd.github+json",
      "x-github-api-version": "2022-11-28",
      "user-agent": "github-polling",
      ...(t ? { authorization: `Bearer ${t}` } : {}),
    },
  });
  const retryAfter = Number(res.headers.get("retry-after")) || 0;
  if (res.headers.get("x-ratelimit-remaining") === "0" || res.status === 429 || retryAfter)
    throw new RateLimitError(
      retryAfter ? Date.now() + retryAfter * 1000 : Number(res.headers.get("x-ratelimit-reset")) * 1000,
    );
  if (res.status === 401) throw new AuthError(401);
  if (res.status === 404) throw new NotFoundError(404);
  if (!res.ok) throw new ApiError(res.status, await res.text().catch(() => ""));
  return { items: res.status === 204 ? [] : await res.json(), headers: res.headers, status: res.status };
};

export const paged = async (path, params = {}, { stop, fetchImpl = fetch } = {}) => {
  const pageCap = Number(process.env.GITHUB_PAGE_CAP) || 5;
  const items = [];
  let url = path;
  let q = { per_page: 100, ...params };
  for (let page = 1; ; page++) {
    const res = await gh(url, q, fetchImpl);
    if (!Array.isArray(res.items)) return [res.items]; // single-object endpoint
    items.push(...res.items);
    const next = parseNextLink(res.headers.get("link"));
    if (!next) return items;
    if (res.items.some(stop ?? (() => false))) return items; // include page, caller dedups
    if (page >= pageCap) {
      console.warn(`github-polling: page cap ${pageCap} reached for ${path} — results may be truncated`);
      return items;
    }
    url = next;
    q = {};
  }
};

// ---------------- poll cycle ----------------

const RING_CAP = 5000; // shortcut: keeps state.json bounded; a repo with >5000 events between polls loses the tail
const STATE_FILE = process.env.GITHUB_STATE_FILE || join(dirname(fileURLToPath(import.meta.url)), "state.json");

export const newRepoState = () => ({
  base: false,
  eventIds: [],
  issueEventIds: [],
  commitShas: [],
  maxIssueCommentId: 0,
  maxPullCommentId: 0,
  issues: {},
  pulls: {},
});

export const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

const snapExtra = (s) =>
  [
    s.labels.length ? `labels: ${s.labels.join(", ")}` : "",
    s.assignees.length ? `assigned: ${s.assignees.join(", ")}` : "",
    s.milestone ? `milestone: ${s.milestone}` : "",
    s.requested_reviewers.length ? `review requested: ${s.requested_reviewers.join(", ")}` : "",
    s.merged_at ? "merged" : "",
    s.draft ? "draft" : "",
  ]
    .filter(Boolean)
    .join("; ");

// logs each of the 7 sources; every line is the shared format, first cycle gets a [baseline] prefix.
export const pollRepo = async (repo, st) => {
  const bw = st.base ? "" : "[baseline] ";
  const seen = (ids, items) => ids.includes(items.id);
  const counts = [];
  const bump = (what, n) => n && counts.push(`+${n} ${what}`);

  // 1. repo event feed (empty on quiet repos — commits/issues lists below are the primary sources)
  const evs = await paged(`/repos/${repo}/events`, {}, {});
  const newEvs = evs.filter((e) => !seen(st.eventIds, e));
  st.eventIds = absorbFresh(st.eventIds, newEvs.map((e) => e.id), RING_CAP);
  for (const e of newEvs) {
    const line = eventLine(e);
    if (line) console.log(bw + line);
  }
  bump("events", newEvs.length);

  // 2. repo-wide issue/PR events (assigned, labeled, milestoned, closed, reopened…)
  const ies = await paged(`/repos/${repo}/issues/events`, {}, {});
  const newIes = ies.filter((e) => !seen(st.issueEventIds, e));
  st.issueEventIds = absorbFresh(st.issueEventIds, newIes.map((e) => e.id), RING_CAP);
  for (const ie of newIes) {
    const line = issueEventLine(ie);
    if (line) console.log(bw + line);
  }
  bump("issue-events", newIes.length);

  // 3+4. issues & PRs — snapshot diff by number
  const itemLine = (kind, e, body, prefix) =>
    console.log(`${prefix}[${kind}] ${repo} #${e.number} (${e.user?.login ?? "?"}) — ${body} — ${e.html_url}`);
  for (const [path, kind, bucket, isPR] of [
    ["issues", "issue", "issues", false],
    ["pulls", "pull", "pulls", true],
  ]) {
    const entries = await paged(`/repos/${repo}/${path}?state=all`, { sort: "updated", direction: "desc" });
    let changed = 0;
    for (const e of entries) {
      if (!isPR && e.pull_request != null) continue; // issues list also returns PRs; pulls list — only PRs
      const snap = snapshot(e);
      const prev = st[bucket][e.number] ?? null;
      if (prev === null) {
        if (st.base)
          itemLine(kind, e, `opened: ${flatten(e.title)}`, bw);
        else
          itemLine(kind, e, `${snap.state}${snap.merged_at ? ", merged" : ""}: ${flatten(e.title)}${snapExtra(snap) ? ` — ${snapExtra(snap)}` : ""}`, bw);
        changed++;
      } else {
        const records = diffEntry(prev, snap);
        if (records.length) {
          itemLine(kind, e, `${records.join(", ")} — ${flatten(e.title)}`, bw);
          changed++;
        }
      }
      st[bucket][e.number] = snap;
    }
    bump(kind, changed);
  }

  // 5+6. comments — conversation (issues incl. PR threads) and inline review comments
  for (const [path, cursor] of [
    ["issues/comments", "maxIssueCommentId"],
    ["pulls/comments", "maxPullCommentId"],
  ]) {
    const cs = await paged(`/repos/${repo}/${path}?sort=created`, { direction: "desc" });
    const fresh = cs.filter((c) => c.id > st[cursor]);
    for (const c of fresh) {
      const line = commentLine(c);
      if (line) console.log(bw + line);
    }
    st[cursor] = Math.max(st[cursor], ...cs.map((c) => c.id), 0);
    bump(path === "issues/comments" ? "comments" : "inline", fresh.length);
  }

  // 7. commits
  const commits = await paged(`/repos/${repo}/commits`, {}, {});
  const newCommits = commits.filter((c) => !st.commitShas.includes(c.sha));
  st.commitShas = absorbFresh(st.commitShas, newCommits.map((c) => c.sha), RING_CAP);
  for (const c of newCommits) console.log(bw + commitLine(c));
  bump("commits", newCommits.length);

  if (!st.base) {
    st.base = true;
    console.log(`[baseline] ${repo} done: ${counts.join(", ") || "nothing"}`);
  }
};

export const pollOnce = async (repos, state) => {
  for (const repo of repos) {
    state.repos[repo] ??= newRepoState();
    try {
      await pollRepo(repo, state.repos[repo]);
      writeState(STATE_FILE, state); // per-repo save: a crash keeps cursor progress up to the last good repo
    } catch (err) {
      if (err instanceof AuthError) {
        console.error(`github-polling: ${err.message} — check \`gh auth status\` or set GITHUB_TOKEN`);
        process.exit(1);
      }
      if (err instanceof RateLimitError) {
        console.warn(`github-polling: rate limit hit at ${repo} — resuming after ${new Date(err.resetAt).toISOString()}`);
        throw err;
      }
      if (err instanceof NotFoundError)
        console.warn(`github-polling: ${repo} not found — check GITHUB_REPOS entry and token scopes`);
      else
        console.warn(`github-polling: ${repo}: ${err?.message ?? err}`);
    }
  }
};

export const main = async () => {
  const repos = (process.env.GITHUB_REPOS || "aliaksandrZh/04")
    .split(",")
    .map((s) => s.trim())
    .filter(Boolean);
  const intervalMs = (Number(process.env.GITHUB_POLL_INTERVAL_SEC) || 60) * 1000;
  const state = readState(STATE_FILE);
  console.log(
    `github-polling: ${repos.join(", ")} — every ${intervalMs / 1000}s — ${token() ? "authed via gh/GITHUB_TOKEN" : "anonymous (60 req/h)"}`,
  );
  while (true) {
    const t0 = Date.now();
    try {
      await pollOnce(repos, state);
    } catch (err) {
      if (err instanceof RateLimitError) {
        await sleep(Math.max(0, err.resetAt - Date.now()));
        continue; // fresh cycle immediately; already-polled repos just dedup
      }
      throw err;
    }
    await sleep(Math.max(0, intervalMs - (Date.now() - t0)));
  }
};

// {at} [{kind}] {repo}{ #n} ({author}) — {body} — {url}
const numKind = (item) => (item.pull_request ? "PR" : "issue"); // issues-list / payload rows

export const eventLine = (ev) => {
  const repo = ev.repo?.name ?? "";
  const who = ev.actor?.login ?? "?";
  const at = ev.created_at;
  const p = ev.payload ?? {};
  const ghUrl = `https://github.com/${repo}`;
  const num = (x) => `${numKind(x)} #${x.number}`;
  const first = (objs, keys) => {
    for (const o of objs)
      for (const k of keys)
        if (o?.[k] != null) return String(o[k]);
    return "";
  };
  const kind = ev.type?.replace(/Event$/, "").toLowerCase() ?? "event";
  let body = null;
  let url = ghUrl;
  switch (ev.type) {
    case "IssuesEvent":
    case "PullRequestEvent": {
      const x = ev.type === "IssuesEvent" ? p.issue : p.pull_request;
      if (!x) return null;
      const merged = ev.type === "PullRequestEvent" && p.action === "closed" && x.merged;
      body = ev.type === "PullRequestEvent"
        ? `PR #${x.number} ${p.action}${merged ? ", merged" : ""}: ${flatten(x.title)}`
        : `${num(x)} ${p.action}: ${flatten(x.title)}`;
      url = x.html_url;
      break;
    }
    case "IssueCommentEvent": {
      if (!p.comment || !p.issue) return null;
      body = `${num(p.issue)} comment (${p.action}): ${flatten(p.comment.body)}`;
      url = p.comment.html_url;
      break;
    }
    case "PullRequestReviewEvent": {
      if (!p.review || !p.pull_request) return null;
      body = `PR #${p.pull_request.number} review ${p.review.state}: ${flatten(p.review.body)}`;
      url = p.review.html_url ?? p.pull_request.html_url;
      break;
    }
    case "PullRequestReviewCommentEvent": {
      if (!p.comment || !p.pull_request) return null;
      body = `PR #${p.pull_request.number} inline${p.comment.path ? ` on ${p.comment.path}` : ""}: ${flatten(p.comment.body)}`;
      url = p.comment.html_url;
      break;
    }
    case "PushEvent": {
      const commits = p.commits ?? [];
      if (commits.length === 0) return null;
      body = `push ${flatten((p.ref ?? "").replace(/^refs\/(?:heads|tags)\//, ""), 80)}: ${commits
        .map((c) => flatten((c.message ?? "").split("\n")[0], 60))
        .join(" | ")}`;
      url = p.compare ?? ghUrl;
      break;
    }
    case "WatchEvent":
      body = "starred the repository";
      break;
    case "ForkEvent":
    case "CreateEvent":
    case "DeleteEvent":
    case "MilestoneEvent":
    case "ReleaseEvent":
    default: {
      const named = first([p.release, p.forkee, p.ref, p.milestone, p.label], [
        "name", "full_name", "tag_name", "title",
      ]);
      body = `${ev.type}${p.action ? ` (${p.action})` : ""}${named ? `: ${flatten(named, 120)}` : ""}`;
      url = p.release?.html_url ?? ghUrl;
    }
  }
  return `${at} [${kind}]${repo ? ` ${repo}` : ""} (${who}) — ${body} — ${url}`;
};

export const issueEventLine = (ie) => {
  const n = ie.issue?.number ?? ie.pull_request?.number;
  if (n == null) return null;
  const isPR = ie.pull_request != null;
  const first = [
    ie.assignee?.login, ie.label?.name, ie.milestone?.title,
    ie.requested_reviewer?.login, ie.review_requester?.login,
  ].find((v) => v != null);
  return `${ie.created_at} [${ie.event}] (${ie.actor?.login ?? "?"}) — ${
    isPR ? "PR" : "issue"
  } #${n} ${ie.event}${first != null ? `: ${first}` : ""} — ${ie.url ?? ie.issue?.html_url ?? ""}`;
};

export const commitLine = (c) =>
  `${c.commit?.author?.date ?? ""} [commit] ${c.sha?.slice(0, 7)} (${
    c.commit?.author?.name ?? "?"
  }) — ${flatten((c.commit?.message ?? "").split("\n")[0], 160)} — ${c.html_url ?? ""}`;

export const commentLine = (c) => {
  const n = c.issue_url?.match(/\/(?:issues|pull)\/(\d+)/)?.[1];
  const isPR = c.issue_url?.includes("/pull/");
  const kind = c.path ? "inline" : "comment";
  const body = c.path
    ? `${isPR ? "PR" : "issue"} ${n ? `#${n} ` : ""}on ${c.path}${c.line != null ? `:${c.line}` : ""}: ${flatten(c.body)}`
    : `${isPR ? "PR" : "issue"} #${n} comment: ${flatten(c.body)}`;
  const repoUrl = c.repository_url?.match(/\/repos\/([^/]+\/[^/]+)$/)?.[1] ?? "";
  return `${c.created_at} [${kind}]${repoUrl ? ` ${repoUrl}` : ""} (${c.user?.login ?? "?"}) — ${body} — ${
    c.html_url ?? ""
  }`;
};

if (import.meta.url === pathToFileURL(process.argv[1] ?? "").href) main();
