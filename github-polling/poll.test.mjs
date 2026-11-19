import { describe, test, beforeEach, afterEach } from "node:test";
import assert from "node:assert/strict";
import { mkdtempSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { generateKeyPairSync, verify } from "node:crypto";
import {
  capRing, flatten, parseNextLink, snapshot, diffEntry, absorbFresh,
  eventLine, issueEventLine, commitLine, commentLine,
  readState, writeState,
  RateLimitError, AuthError, NotFoundError, ApiError,
  resolveToken, setToken, gh, paged,
  appJwt, installationToken,
} from "./poll.mjs";

describe("capRing", () => {
  test("appends and trims to cap", () => {
    assert.deepEqual(capRing(["a", "b"], ["c"], 3), ["a", "b", "c"]);
    assert.deepEqual(capRing(["a", "b", "c"], ["d"], 3), ["b", "c", "d"]);
    assert.deepEqual(capRing([], ["1", "2"], 2), ["1", "2"]);
  });
});

describe("absorbFresh", () => {
  test("repeated absorption never duplicates", () => {
    let a = absorbFresh([], ["x"], 5);
    a = absorbFresh(a, ["x"], 5);
    a = absorbFresh(a, ["x", "y"], 5);
    assert.deepEqual(a, ["x", "y"]);
  });
});

describe("flatten", () => {
  test("collapses whitespace", () => {
    assert.equal(flatten("a\nb\r\tc  d"), "a b c d");
    assert.equal(flatten(null), "");
  });
  test("truncates with ellipsis at max", () => {
    const out = flatten("x".repeat(50), 10);
    assert.equal(out.length, 11);
    assert.ok(out.endsWith("…"));
    assert.equal(flatten("hello", 10), "hello");
  });
});

describe("parseNextLink", () => {
  test("returns next url from GitHub Link header", () => {
    const h = '<https://api.github.com/repositories/1463708048/commits?per_page=1&page=2>; rel="next", <https://api.github.com/repositories/1463708048/commits?per_page=1&page=1>; rel="last"';
    assert.equal(parseNextLink(h), "https://api.github.com/repositories/1463708048/commits?per_page=1&page=2");
  });
  test("null on missing header or missing rel=next", () => {
    assert.equal(parseNextLink(null), null);
    assert.equal(parseNextLink('<https://x.com/a>; rel="last"'), null);
  });
});

describe("snapshot", () => {
  test("plain issue — PR-only fields null", () => {
    const s = snapshot({
      state: "open", title: "T", updated_at: "u1",
      labels: [{ name: "a" }], assignees: [{ login: "alice" }], milestone: { title: "v1" },
    });
    assert.deepEqual(s, {
      state: "open", title: "T", updated_at: "u1", merged_at: null,
      labels: ["a"], assignees: ["alice"], milestone: "v1",
      draft: false, requested_reviewers: [],
    });
  });
  test("PR entry (from issues list) — merged_at, requested_reviewers mapped", () => {
    const s = snapshot({
      state: "open", title: "P", updated_at: "u2", merged_at: "m1", draft: true,
      labels: [], assignees: [],
      requested_reviewers: [{ login: "bob" }, { login: "carl" }],
      pull_request: {},
    });
    assert.deepEqual(s, {
      state: "open", title: "P", updated_at: "u2", merged_at: "m1",
      labels: [], assignees: [], milestone: null,
      draft: true, requested_reviewers: ["bob", "carl"],
    });
  });
});

describe("diffEntry", () => {
  const prev = {
    state: "open", title: "T", updated_at: "u1", merged_at: null,
    labels: ["a"], assignees: ["alice"], milestone: null,
    draft: false, requested_reviewers: [],
  };
  test("emits all changed fields", () => {
    const next = {
      state: "closed", title: "New", updated_at: "u2", merged_at: "m1",
      labels: ["a", "bug"], assignees: [], milestone: "v1",
      draft: false, requested_reviewers: ["bob"],
    };
    const out = diffEntry(prev, next).sort();
    assert.deepEqual(out, [
      "assignee: -alice", "closed", "label: +bug", "merged",
      "milestone: v1", "review-requested: bob", "title: New",
    ].sort());
  });
  test("reopened, drafted, readied, unreview, label/assignee add, milestone cleared", () => {
    const closed = { ...prev, state: "closed" };
    const reopened = { ...prev, state: "open" };
    assert.deepEqual(diffEntry(closed, reopened), ["reopened"]);
    assert.deepEqual(diffEntry(prev, { ...prev, draft: true }), ["drafted"]);
    assert.deepEqual(diffEntry({ ...prev, draft: true }, prev), ["readied"]);
    assert.deepEqual(diffEntry({ ...prev, requested_reviewers: ["bob"] }, prev), ["review-removed: bob"]);
    assert.deepEqual(diffEntry(prev, { ...prev, labels: ["a", "wip"] }), ["label: +wip"]);
    assert.deepEqual(diffEntry({ ...prev, milestone: "v1" }, prev), ["milestone: (none)"]);
    assert.deepEqual(diffEntry({ ...prev, merged_at: "m1" }, { ...prev, merged_at: null }), ["unmerged"]);
  });
  test("equal snapshots — empty", () => {
    assert.deepEqual(diffEntry(prev, { ...prev }), []);
  });
});

describe("eventLine", () => {
  test("PullRequestEvent closed+merged — full line", () => {
    const line = eventLine({
      type: "PullRequestEvent", created_at: "2026-01-01T00:00:00Z",
      actor: { login: "ann" }, repo: { name: "own/r" },
      payload: { action: "closed", pull_request: { number: 5, merged: true, title: "T", html_url: "prurl" } },
    });
    assert.match(line, /PR #5/);
    assert.match(line, /own\/r/);
    assert.match(line, /\(ann\)/);
    assert.match(line, /merged/);
    assert.match(line, /prurl/);
  });
  test("IssueCommentEvent — flattened body + issue number + comment url", () => {
    const line = eventLine({
      type: "IssueCommentEvent", created_at: "2026-01-02T00:00:00Z",
      actor: { login: "ann" }, repo: { name: "own/r" },
      payload: { action: "created", issue: { number: 7, title: "Q", html_url: "iurl" },
        comment: { body: "hello  world", html_url: "curl" } },
    });
    assert.match(line, /issue #7/);
    assert.match(line, /hello world/);
    assert.match(line, /curl/);
  });
  test("PullRequestReviewEvent — review state", () => {
    const line = eventLine({
      type: "PullRequestReviewEvent", created_at: "2026-01-03T00:00:00Z",
      actor: { login: "rev" }, repo: { name: "own/r" },
      payload: { action: "submitted", review: { state: "changes_requested", body: "b" },
        pull_request: { number: 5, title: "T", html_url: "prurl" } },
    });
    assert.match(line, /changes_requested/);
    assert.match(line, /PR #5/);
  });
  test("PushEvent with commits — commit messages", () => {
    const line = eventLine({
      type: "PushEvent", created_at: "2026-01-04T00:00:00Z",
      actor: { login: "ann" }, repo: { name: "own/r" },
      payload: { ref: "refs/heads/main", commits: [{ message: "first", sha: "abc" }, { message: "second", sha: "def" }] },
    });
    assert.match(line, /push main/);
    assert.match(line, /first/);
    assert.match(line, /second/);
  });
  test("unknown type — catch-all contains type name", () => {
    const line = eventLine({
      type: "ReleaseEvent", created_at: "2026-01-05T00:00:00Z",
      actor: { login: "ann" }, repo: { name: "own/r" },
      payload: { action: "published", release: { tag_name: "v1", html_url: "rurl" } },
    });
    assert.match(line, /ReleaseEvent/);
    assert.match(line, /v1/);
  });
  test("PushEvent with zero commits — null", () => {
    assert.equal(eventLine({
      type: "PushEvent", created_at: "x", actor: { login: "a" }, repo: { name: "o/r" },
      payload: { ref: "refs/heads/x", commits: [] },
    }), null);
  });
});

describe("issueEventLine", () => {
  test("assigned — issue", () => {
    const line = issueEventLine({
      event: "assigned", actor: { login: "own" }, assignee: { login: "bob" },
      created_at: "2026-01-01T00:00:00Z", issue: { number: 3 },
    "url": "u3",
    });
    assert.match(line, /assigned/);
    assert.match(line, /issue #3/);
    assert.match(line, /bob/);
    assert.match(line, /u3/);
  });
  test("unlabeled — pull_request", () => {
    const line = issueEventLine({
      event: "unlabeled", actor: { login: "own" }, label: { name: "bug" },
      created_at: "2026-01-01T00:00:00Z", pull_request: { number: 9 }, url: "u9",
    });
    assert.match(line, /unlabeled: bug/);
    assert.match(line, /PR #9/);
  });
  test("unknown event — catch-all contains event name", () => {
    const line = issueEventLine({
      event: "locked", actor: { login: "own" },
      created_at: "2026-01-01T00:00:00Z", issue: { number: 2 }, url: "u2",
    });
    assert.match(line, /locked/);
    assert.match(line, /issue #2/);
  });
});

describe("commitLine", () => {
  test("sha, author, first message line, url", () => {
    const line = commitLine({
      sha: "abcdef1234567890", html_url: "curl",
      commit: { author: { name: "Zed", date: "2026-11-19T05:34:59Z" }, message: "fix bug\n\nbody text" },
    });
    assert.match(line, /abcdef1/);
    assert.match(line, /Zed/);
    assert.match(line, /fix bug/);
    assert.doesNotMatch(line, /body text/);
    assert.match(line, /curl/);
  });
});

describe("commentLine", () => {
  test("PR comment via issue_url shape + inline kind handled by caller", () => {
    const line = commentLine({
      id: 5, user: { login: "ann" }, body: "looks\n good",
      html_url: "curl", issue_url: "https://github.com/own/r/pull/9",
    });
    assert.match(line, /PR #9/);
    assert.match(line, /ann/);
    assert.match(line, /looks good/);
    assert.match(line, /curl/);
  });
  test("issue comment", () => {
    const line = commentLine({
      id: 6, user: { login: "bob" }, body: "hi",
      html_url: "curl2", issue_url: "https://github.com/own/r/issues/4",
    });
    assert.match(line, /issue #4/);
  });
});

describe("readState/writeState", () => {
  test("roundtrip, corrupt recovery, missing file", () => {
    const file = join(mkdtempSync(join(tmpdir(), "poll-test-")), "state.json");
    writeState(file, { repos: { "o/r": { base: true } } });
    assert.deepEqual(readState(file), { repos: { "o/r": { base: true } } });
    writeFileSync(file, "{corrupt");
    assert.deepEqual(readState(file), { repos: {} });
    assert.deepEqual(readState(join(file, "nope.json")), { repos: {} });
  });
});

describe("error classes", () => {
  test("carry their payloads", () => {
    assert.equal(new RateLimitError(123).resetAt, 123);
    assert.equal(new AuthError(401).status, 401);
    assert.equal(new NotFoundError(404).status, 404);
    assert.equal(new ApiError(422, "boom").body, "boom");
    assert.ok(new RateLimitError(1) instanceof Error);
  });
  test("resolveToken exists (live credential check pinned to Task 4 run)", () => {
    assert.equal(typeof resolveToken, "function");
  });
});

// -------- REST layer (Task 2) --------

const stub = (json, headers = {}, status = 200) => ({
  ok: status >= 200 && status < 300, status,
  headers: new Headers(headers),
  json: async () => json,
  text: async () => (typeof json === "string" ? json : JSON.stringify(json)),
});

describe("GitHub App auth", () => {
  const keys = generateKeyPairSync("rsa", { modulusLength: 2048 });
  const pub = keys.publicKey.export({ type: "pkcs1", format: "pem" }).toString();
  const keyFile = join(mkdtempSync(join(tmpdir(), "app-key-")), "key.pem");
  writeFileSync(keyFile, keys.privateKey.export({ type: "pkcs1", format: "pem" }));

  beforeEach(() => {
    setToken(null);
    process.env.GITHUB_APP_ID = "5828763";
    process.env.GITHUB_APP_KEY_FILE = keyFile;
  });
  afterEach(() => {
    delete process.env.GITHUB_APP_ID;
    delete process.env.GITHUB_APP_KEY_FILE;
    delete process.env.GITHUB_APP_INSTALLATION_ID;
    setToken(null);
  });

  test("appJwt — RS256, iss/exp payload, signature verifies against the public key", () => {
    const [h, b, s] = appJwt().split(".");
    assert.equal(JSON.parse(Buffer.from(h, "base64url")).alg, "RS256");
    const payload = JSON.parse(Buffer.from(b, "base64url"));
    assert.equal(payload.iss, 5828763);
    assert.equal(payload.exp - payload.iat, 600);
    assert.ok(verify("RSA-SHA256", Buffer.from(`${h}.${b}`), pub, Buffer.from(s, "base64url")));
  });

  test("installationToken — discovers installation, mints, then caches", async () => {
    const calls = [];
    const impl = async (u, o) => {
      calls.push({ u: String(u), method: o.method ?? "GET", auth: o.headers.authorization });
      if (calls.length === 1) return stub({ id: 42 });
      return stub({ token: "instok", expires_at: new Date(Date.now() + 9 * 60_000).toISOString() });
    };
    assert.equal(await installationToken("own/r1", impl), "instok");
    assert.equal(await installationToken("own/r1", impl), "instok"); // cache hit — no network
    assert.equal(calls.length, 2);
    assert.equal(calls[0].method, "GET");
    assert.match(calls[0].u, /\/repos\/own\/r1\/installation$/);
    assert.ok(calls[0].auth.startsWith("Bearer eyJ"));
    assert.equal(calls[1].method, "POST");
    assert.match(calls[1].u, /\/app\/installations\/42\/access_tokens$/);
  });

  test("stale token re-mints; explicit installation id skips discovery", async () => {
    process.env.GITHUB_APP_INSTALLATION_ID = "42";
    const calls = [];
    const impl = async (u, o) =>
      (calls.push({ method: o.method ?? "GET" }),
        stub({ token: `tok${calls.length}`, expires_at: new Date(Date.now() + 30_000).toISOString() }));
    assert.equal(await installationToken("own/r2", impl), "tok1");
    assert.equal(await installationToken("own/r2", impl), "tok2"); // expiring in 30s < 60s freshness window
    assert.equal(calls.length, 2);
    assert.ok(calls.every((c) => c.method === "POST"));
  });

  test("gh routes repo paths through the installation token, mint path via JWT", async () => {
    process.env.GITHUB_APP_INSTALLATION_ID = "42";
    const calls = [];
    const impl = async (u, o) => {
      calls.push({ method: o.method ?? "GET", auth: o.headers.authorization, u: String(u) });
      return calls.length === 1
        ? stub({ token: "instok2", expires_at: new Date(Date.now() + 9 * 60_000).toISOString() })
        : stub([1]);
    };
    const r = await gh("/repos/own/r3/issues", {}, impl);
    assert.equal(r.items[0], 1);
    assert.equal(calls[0].method, "POST");
    assert.match(calls[0].u, /access_tokens/);
    assert.equal(calls[1].method, "GET");
    assert.equal(calls[1].auth, "Bearer instok2");
  });
});

describe("gh", () => {
  test("builds url + auth headers", async () => {
    setToken("t123");
    let seen;
    const impl = async (u, opts) => ((seen = { u: String(u), o: opts }), stub([1]));
    const r = await gh("/repos/a/b", { per_page: 1 }, impl);
    assert.equal(r.items[0], 1);
    assert.equal(seen.u, "https://api.github.com/repos/a/b?per_page=1");
    assert.equal(seen.o.headers.authorization, "Bearer t123");
    assert.equal(seen.o.headers.accept, "application/vnd.github+json");
    assert.equal(seen.o.headers["x-github-api-version"], "2022-11-28");
    assert.equal(seen.o.headers["user-agent"], "github-polling");
  });
  test("403 + remaining 0 → RateLimitError with reset ms", async () => {
    const impl = async () => stub(null, { "x-ratelimit-remaining": "0", "x-ratelimit-reset": "1795070756" }, 403);
    await assert.rejects(() => gh("/x", {}, impl), (e) => e instanceof RateLimitError && e.resetAt === 1795070756000);
  });
  test("retry-after → RateLimitError in the future", async () => {
    const impl = async () => stub(null, { "retry-after": "60" }, 429);
    await assert.rejects(
      () => gh("/x", {}, impl),
      (e) => e instanceof RateLimitError && e.resetAt > Date.now() + 30_000,
    );
  });
  test("401 → AuthError, 404 → NotFoundError, plain 403 → ApiError", async () => {
    await assert.rejects(() => gh("/x", {}, async () => stub(null, {}, 401)), AuthError);
    await assert.rejects(() => gh("/x", {}, async () => stub(null, {}, 404)), NotFoundError);
    await assert.rejects(() => gh("/x", {}, async () => stub(null, {}, 403)), ApiError);
  });
});

describe("paged", () => {
  test("injects per_page=100 when caller omits it; preserves explicit per_page", async () => {
    const calls = [];
    const impl = async (u) => (calls.push(String(u)), stub([1]));
    await paged("/things", {}, { fetchImpl: impl });
    await paged("/things", { per_page: 7 }, { fetchImpl: impl });
    assert.match(calls[0], /per_page=100/);
    assert.match(calls[1], /per_page=7/);
  });
  test("single page, no next link — one call", async () => {
    const calls = [];
    const impl = async (u, o) => (calls.push(u), stub([1, 2]));
    assert.deepEqual(await paged("/things", { per_page: 1 }, { fetchImpl: impl }), [1, 2]);
    assert.equal(calls.length, 1);
  });
  test("follows rel=next; early-stops when stop matches (page still included)", async () => {
    const calls = [];
    const impl = async (u) => {
      calls.push(String(u));
      if (calls.length === 1) {
        return stub([1, 2], { link: '<https://api.github.com/things?page=2>; rel="next"' });
      }
      return stub([3, 4], { link: '<https://api.github.com/things?page=3>; rel="next"' });
    };
    const got = await paged("/things", {}, { stop: (x) => x === 4, fetchImpl: impl });
    assert.deepEqual(got, [1, 2, 3, 4]);
    assert.equal(calls.length, 2);
    assert.equal(new URL(calls[1]).search, "?page=2");
  });
  test("page cap reached — warns and returns what it has", async () => {
    process.env.GITHUB_PAGE_CAP = "1";
    const orig = console.warn;
    const warns = [];
    console.warn = (...a) => warns.push(a.join(" "));
    try {
      const impl = async () => stub([1], { link: '<https://api.github.com/things?page=2>; rel="next"' });
      assert.deepEqual(await paged("/things", {}, { fetchImpl: impl }), [1]);
    } finally {
      console.warn = orig;
      delete process.env.GITHUB_PAGE_CAP;
    }
    assert.equal(warns.length, 1);
    assert.match(warns[0], /page cap/);
  });
});
