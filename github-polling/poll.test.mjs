import { describe, test } from "node:test";
import assert from "node:assert/strict";
import { mkdtempSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import {
  capRing, flatten, parseNextLink, snapshot, diffEntry,
  eventLine, issueEventLine, commitLine, commentLine,
  readState, writeState,
  RateLimitError, AuthError, NotFoundError, ApiError,
} from "./poll.mjs";

describe("capRing", () => {
  test("appends and trims to cap", () => {
    assert.deepEqual(capRing(["a", "b"], ["c"], 3), ["a", "b", "c"]);
    assert.deepEqual(capRing(["a", "b", "c"], ["d"], 3), ["b", "c", "d"]);
    assert.deepEqual(capRing([], ["1", "2"], 2), ["1", "2"]);
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
});
