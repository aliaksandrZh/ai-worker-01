// Write API tests — route passthrough/validation against a stubbed fetch (App auth layer lives in poll.mjs).
import { describe, test, beforeEach, afterEach } from "node:test";
import assert from "node:assert/strict";
import { setToken, installationToken } from "./poll.mjs";
import { route } from "./write-api.mjs";

const stub = (json, headers = {}, status = 200) => ({
  ok: status >= 200 && status < 300, status,
  headers: new Headers(headers),
  json: async () => json,
  text: async () => (typeof json === "string" ? json : JSON.stringify(json)),
});

const run = async (method, url, body, impl, headers = {}) => {
  const raw = typeof body === "string" || body == null ? body : JSON.stringify(body);
  const res = {
    code: null, raw: null,
    writeHead(code, h) { this.code = code; this.headers = h; return this; },
    end(x) { this.raw = x; },
  };
  await route({ method, url, headers }, res, impl, raw);
  let json = null;
  try { json = JSON.parse(res.raw); } catch { /* raw passthrough */ }
  return { status: res.code, json, raw: res.raw };
};

const recorder = () => {
  const calls = [];
  return {
    calls,
    impl: async (u, o) => (calls.push({ u: String(u), method: o.method ?? "GET", auth: o.headers.authorization, body: o.body, accept: o.headers.accept }), calls),
  };
};

describe("write-api validation", () => {
  beforeEach(() => setToken("t"));
  afterEach(() => setToken(null));

  test("400 on invalid JSON", async () => {
    const { status } = await run("POST", "/repos/own/r/issues", "{corrupt", async () => stub([1]));
    assert.equal(status, 400);
  });
  test("400 when issue title missing", async () => {
    const { status } = await run("POST", "/repos/own/r/issues", { body: "x" }, async () => stub(1));
    assert.equal(status, 400);
  });
  test("400 when comment body missing", async () => {
    const { status } = await run("POST", "/repos/own/r/issues/2/comments", {}, async () => stub(1));
    assert.equal(status, 400);
  });
  test("400 on invalid review event", async () => {
    const { status } = await run("POST", "/repos/own/r/pulls/3/reviews", { event: "LOOK" }, async () => stub(1));
    assert.equal(status, 400);
  });
  test("400 when changes lacks branch/message/updates", async () => {
    for (const bad of [{}, { branch: "b" }, { branch: "b", message: "m" }]) {
      const { status } = await run("POST", "/repos/own/r/changes", bad, async () => stub(1));
      assert.equal(status, 400);
    }
  });

  test("403 for foreign web origins (CSRF guard) — curl/no-origin passes", async () => {
    const { status: foreign } = await run("POST", "/repos/own/r/issues", { title: "t" }, async () => stub(1), { origin: "https://evil.example" });
    assert.equal(foreign, 403);
    const { status: okNoOrigin } = await run("POST", "/repos/own/r/issues", { title: "t" }, async () => stub({ number: 1 }, {}, 201), { origin: "null" });
    assert.equal(okNoOrigin, 201);
  });
  test("unknown route → 404 with route list", async () => {
    const { status, json } = await run("GET", "/nope", "", async () => stub(1));
    assert.equal(status, 404);
    assert.match(json.error, /^routes:/);
  });
  test("GET /health — route list", async () => {
    const { status, json } = await run("GET", "/health", "", async () => stub(1));
    assert.equal(status, 200);
    assert.match(json.routes, /^routes:/);
  });
});

describe("write-api passthrough routes", () => {
  beforeEach(() => setToken("t"));
  afterEach(() => setToken(null));

  test("POST issue → 201 passthrough", async () => {
    const { calls } = recorder();
    const { status, json } = await run("POST", "/repos/own/r/issues", { title: "T", body: "B" }, async (u, o) => {
      calls.push({ u: String(u), o });
      return stub({ number: 9 }, {}, 201);
    });
    assert.equal(status, 201);
    assert.equal(json.number, 9);
    assert.match(calls[0].u, /\/repos\/own\/r\/issues$/);
    assert.equal(JSON.parse(calls[0].o.body).title, "T");
  });
  test("PATCH issue → close", async () => {
    let seen;
    const { status, json } = await run("PATCH", "/repos/own/r/issues/3", { state: "closed" }, async (u, o) => {
      seen = { u: String(u), o }; return stub({ number: 3, state: "closed" });
    });
    assert.equal(status, 200);
    assert.equal(json.state, "closed");
    assert.equal(seen.o.method, "PATCH");
  });
  test("POST comment (issue or PR thread)", async () => {
    let seen;
    const { status, json } = await run("POST", "/repos/own/r/issues/2/comments", { body: "hi" }, async (u, o) => {
      seen = { u: String(u), o }; return stub({ id: 1 }, {}, 201);
    });
    assert.equal(status, 201);
    assert.equal(json.id, 1);
    assert.match(seen.u, /\/issues\/2\/comments$/);
  });
  test("POST pull request", async () => {
    let seen;
    const { status, json } = await run("POST", "/repos/own/r/pulls", { head: "feat", title: "PR" }, async (u, o) => {
      seen = { u: String(u), o }; return stub({ number: 5 }, {}, 201);
    });
    assert.equal(status, 201);
    assert.equal(json.number, 5);
    assert.match(seen.u, /\/repos\/own\/r\/pulls$/);
  });
  test("POST review with inline comments", async () => {
    let seen;
    const { status } = await run("POST", "/repos/own/r/pulls/5/reviews",
      { event: "REQUEST_CHANGES", body: "b", comments: [{ path: "a.js", line: 3, body: "x" }] },
      async (u, o) => (seen = { u: String(u), o }, stub({ id: 7 }, {}, 200)));
    assert.equal(status, 200);
    assert.match(seen.u, /\/pulls\/5\/reviews$/);
    assert.equal(JSON.parse(seen.o.body).event, "REQUEST_CHANGES");
  });

  test("GET contents file — text decoded", async () => {
    const { status, json } = await run("GET", "/repos/own/r/contents/src/a.txt", "",
      async () => stub({ type: "file", encoding: "base64", content: Buffer.from("héllo").toString("base64") }));
    assert.equal(status, 200);
    assert.equal(json.text, "héllo");
  });
  test("GET contents dir — array passthrough", async () => {
    const { status, json } = await run("GET", "/repos/own/r/contents/src", "", async () => stub([{ type: "dir" }, { type: "file" }]));
    assert.equal(status, 200);
    assert.equal(json.length, 2);
  });
  test("GET PR diff — raw text, diff accept header", async () => {
    let seen;
    const { status, raw } = await run("GET", "/repos/own/r/pulls/5/diff", "", async (u, o) => {
      seen = o; return stub("diff --git a/x b/x");
    });
    assert.equal(status, 200);
    assert.equal(raw, "diff --git a/x b/x");
    assert.equal(seen.headers.accept, "application/vnd.github.diff");
  });
  test("GET PR files", async () => {
    const { status, json } = await run("GET", "/repos/own/r/pulls/5/files", "", async () => stub([{ filename: "x" }]));
    assert.equal(status, 200);
    assert.equal(json[0].filename, "x");
  });
});

describe("write-api graphql passthrough (Projects V2)", () => {
  beforeEach(() => setToken("t"));
  afterEach(() => setToken(null));

  test("400 when query missing", async () => {
    const { status } = await run("POST", "/repos/own/r/graphql", { variables: {} }, async () => stub(1));
    assert.equal(status, 400);
  });
  test("mints installation token and posts to /graphql as bearer", async () => {
    process.env.GITHUB_APP_INSTALLATION_ID = "42";
    try {
      const calls = [];
      const impl = async (u, o) => {
        calls.push({ u: String(u), method: o.method ?? "GET", auth: o.headers.authorization, body: o.body });
        if (calls.length === 1) return stub({ token: "instok", expires_at: new Date(Date.now() + 9 * 60_000).toISOString() });
        return stub({ data: { repository: { issue: { id: "NODE1" } } } });
      };
      const { status, json } = await run("POST", "/repos/own/r/graphql",
        { query: "query { repository { issue { id } } }", variables: { a: 1 } }, impl);
      assert.equal(status, 200);
      assert.deepEqual(json, { data: { repository: { issue: { id: "NODE1" } } } });
      assert.equal(calls[0].method, "POST");
      assert.match(calls[0].u, /access_tokens$/);
      assert.equal(calls[1].method, "POST");
      assert.match(calls[1].u, /api\.github\.com\/graphql$/);
      assert.equal(calls[1].auth, "Bearer instok");
      assert.equal(JSON.parse(calls[1].body).variables.a, 1);
    } finally {
      delete process.env.GITHUB_APP_INSTALLATION_ID;
    }
  });
});

describe("write-api changes flow", () => {
  beforeEach(() => setToken("t"));
  afterEach(() => setToken(null));

  test("branch missing → create from default, commit updates, open PR, ordered calls", async () => {
    const calls = [];
    const q = [
      [/\/repos\/own\/r$/, stub({ default_branch: "main" })],
      [/\/git\/ref\/heads\/feat$/, stub({}, {}, 404)],
      [/\/git\/ref\/heads\/main$/, stub({ object: { sha: "mainsha" } })],
      [/\/git\/refs$/, stub({ ref: "refs/heads/feat" }, {}, 201)],
      [/\/contents\/a\.txt\?ref=feat$/, stub("nf", {}, 404)],
      [/\/contents\/a\.txt$/, stub({ commit: { sha: "c1" } }, {}, 201)],
      [/\/contents\/b\.txt\?ref=feat$/, stub("nf", {}, 404)],
      [/\/contents\/b\.txt$/, stub({ commit: { sha: "c2" } }, {}, 201)],
      [/\/pulls$/, stub({ number: 5 }, {}, 201)],
    ];
    const impl = async (u, o) => {
      const [pattern, resp] = q.shift() ?? [/(?!)/, stub(null)];
      assert.match(String(u), pattern); // order pinned: wrong step = wrong URL = fail
      calls.push({ u: String(u), method: o.method ?? "GET" });
      return resp;
    };
    const { status, json } = await run("POST", "/repos/own/r/changes",
      { branch: "feat", message: "msg", updates: [{ path: "a.txt", content: "one" }, { path: "b.txt", content: "two" }], pr: { title: "T" } },
      impl);
    assert.equal(status, 200);
    assert.deepEqual(json, { branch: "feat", commits: ["c1", "c2"], pr: { number: 5 } });
    assert.deepEqual(calls.map((c) => c.method), ["GET", "GET", "GET", "POST", "GET", "PUT", "GET", "PUT", "POST"]);
  });

  test("existing branch reused (no ref creation), existing file shas sent", async () => {
    let putBody;
    const impl = async (u, o) => {
      const url = String(u);
      if (/\/repos\/own\/r$/.test(url)) return stub({ default_branch: "main" });
      if (/\/git\/ref\/heads\/feat$/.test(url)) return stub({ object: { sha: "basesha" } });
      if (/\/contents\/a\.txt\?ref=feat$/.test(url)) return stub({ sha: "filesha" });
      if (/\/contents\/a\.txt$/.test(url)) { putBody = JSON.parse(o.body); return stub({ commit: { sha: "c1" } }, {}, 201); }
      return stub(1, {}, 404); // pulls optional — pr absent
    };
    const { status, json } = await run("POST", "/repos/own/r/changes",
      { branch: "feat", message: "m", updates: [{ path: "a.txt", content: "one" }] }, impl);
    assert.equal(status, 200);
    assert.deepEqual(json, { branch: "feat", commits: ["c1"], pr: null });
    assert.deepEqual(putBody, { message: "m", content: Buffer.from("one").toString("base64"), branch: "feat", sha: "filesha" });
  });
});
