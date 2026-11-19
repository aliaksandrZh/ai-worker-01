// Write API for the installed GitHub App: issues, comments, PRs, reviews, contents, changes.
// Separate process from the read-only poller; same .env/credentials. CORS open for console.html.
// Spec: docs/superpowers/plans/2026-11-19-github-write-api.md
import { createServer } from "node:http";
import { pathToFileURL } from "node:url";
import { gh, cred, RateLimitError, NotFoundError, ApiError } from "./poll.mjs";

const PORT = Number(process.env.WRITE_API_PORT) || 3901;
const ROUTES =
  "routes: GET /health, GET /repos/:r/contents[/PATH]?ref=, GET /repos/:r/pulls/:n/diff, GET /repos/:r/pulls/:n/files, POST /repos/:r/issues {title}, PATCH /repos/:r/issues/:n {state,title,body,labels,assignees}, POST /repos/:r/issues/:n/comments {body}, POST /repos/:r/pulls {head,title}, POST /repos/:r/pulls/:n/reviews {event,body,comments}, POST /repos/:r/changes {branch,message,updates,pr?}, POST /repos/:r/graphql {query,variables} (Projects V2)";

const CORS = {
  "access-control-allow-origin": "*",
  "access-control-allow-methods": "GET, POST, PATCH, OPTIONS",
  "access-control-allow-headers": "content-type",
};

export const route = async (req, res, fetchImpl = fetch, raw = "") => {
  const send = (code, obj) => res.writeHead(code, { ...CORS, "content-type": "application/json;charset=utf-8" }).end(JSON.stringify(obj));
  try {
    const url = new URL(req.url ?? "/", "http://localhost");
    const method = req.method ?? "GET";
    const path = url.pathname.replace(/\/+$/, "") || "/";
    // CSRF guard: browsers may drive this API with the user's App credentials — only null-origin (file://) or local pages may call
    const origin = req.headers?.origin ?? null;
    if (origin !== null && origin !== "null" && !/^https?:\/\/localhost(:\d+)?$/.test(origin))
      return send(403, { error: `origin ${origin} not allowed — call from localhost, file:// or curl` });
    if (method === "OPTIONS") return res.writeHead(204, CORS).end();
    if (method === "GET" && path === "/health") return send(200, { routes: ROUTES });

    const repo = path.match(/^\/repos\/([^/]+)\/([^/]+)(.*)$/);
    if (!repo) return send(404, { error: ROUTES });
    const r = `${repo[1]}/${repo[2]}`;
    const sub = repo[3] ?? "";
    let m;

    if (method === "GET" && (m = sub.match(/^\/contents(?:\/(.+))?$/))) {
      const p = {};
      if (url.searchParams.get("ref")) p.ref = url.searchParams.get("ref");
      const items = (await gh(`/repos/${r}/contents/${m[1] ?? ""}`, p, fetchImpl)).items;
      if (Array.isArray(items)) return send(200, items);
      return send(200, {
        ...items,
        text: items.encoding === "base64" ? Buffer.from(items.content ?? "", "base64").toString("utf8") : items.content ?? "",
      });
    }
    if (method === "GET" && (m = sub.match(/^\/pulls\/(\d+)\/diff$/)))
      return res.writeHead(200, { ...CORS, "content-type": "text/plain;charset=utf-8" }).end(
        (await gh(`/repos/${r}/pulls/${m[1]}`, {}, fetchImpl, { headers: { accept: "application/vnd.github.diff" } })).text,
      );
    if (method === "GET" && (m = sub.match(/^\/pulls\/(\d+)\/files$/)))
      return send(200, (await gh(`/repos/${r}/pulls/${m[1]}/files`, {}, fetchImpl)).items);

    if (method !== "POST" && method !== "PATCH") return send(404, { error: ROUTES });
    let b;
    try { b = raw ? JSON.parse(raw) : {}; } catch { return send(400, { error: "invalid JSON body" }); }

    if (method === "POST" && sub === "/issues") {
      if (typeof b.title !== "string" || !b.title.trim()) return send(400, { error: "title required" });
      return send(201, (await gh(`/repos/${r}/issues`, {}, fetchImpl, { method: "POST", body: b })).items);
    }
    if (method === "PATCH" && (m = sub.match(/^\/issues\/(\d+)$/))) {
      if (!Object.keys(b).length) return send(400, { error: "empty patch" });
      return send(200, (await gh(`/repos/${r}/issues/${m[1]}`, {}, fetchImpl, { method: "PATCH", body: b })).items);
    }
    if (method === "POST" && (m = sub.match(/^\/issues\/(\d+)\/comments$/))) {
      if (typeof b.body !== "string" || !b.body.trim()) return send(400, { error: "body required" });
      return send(201, (await gh(`/repos/${r}/issues/${m[1]}/comments`, {}, fetchImpl, { method: "POST", body: { body: b.body } })).items);
    }
    if (method === "POST" && sub === "/pulls") {
      if (typeof b.head !== "string" || !b.head.trim() || typeof b.title !== "string" || !b.title.trim())
        return send(400, { error: "head and title required" });
      return send(201, (await gh(`/repos/${r}/pulls`, {}, fetchImpl, { method: "POST", body: b })).items);
    }
    if (method === "POST" && (m = sub.match(/^\/pulls\/(\d+)\/reviews$/))) {
      if (!["APPROVE", "REQUEST_CHANGES", "COMMENT"].includes(b.event))
        return send(400, { error: "event must be APPROVE, REQUEST_CHANGES or COMMENT" });
      return send(200, (await gh(`/repos/${r}/pulls/${m[1]}/reviews`, {}, fetchImpl, { method: "POST", body: b })).items);
    }
    // Projects V2 (GraphQL-only): POST {query, variables} with the repo's installation token
    if (method === "POST" && sub === "/graphql") {
      if (typeof b.query !== "string" || !b.query.trim()) return send(400, { error: "query required" });
      // ladder decides: user tier → user token; app tier → mint an installation token
      const bearer = await cred(`/repos/${r}/graphql`, fetchImpl);
      return send(200, (await gh("/graphql", {}, fetchImpl, { method: "POST", bearer, body: { query: b.query, variables: b.variables ?? {} } })).items);
    }
    if (method === "POST" && sub === "/changes") {
      if (typeof b.branch !== "string" || !b.branch.trim()) return send(400, { error: "branch required" });
      if (!b.message) return send(400, { error: "message required" });
      if (!Array.isArray(b.updates) || !b.updates.length || b.updates.some((u) => typeof u?.path !== "string" || !u.path.trim() || typeof u?.content !== "string"))
        return send(400, { error: "updates must be [{path, content}]" });
      const def = (await gh(`/repos/${r}`, {}, fetchImpl)).items.default_branch;
      let base;
      try { base = (await gh(`/repos/${r}/git/ref/heads/${b.branch}`, {}, fetchImpl)).items.object.sha; } catch (e) { if (!(e instanceof NotFoundError)) throw e; }
      if (base === undefined) {
        const defSha = (await gh(`/repos/${r}/git/ref/heads/${def}`, {}, fetchImpl)).items.object.sha;
        await gh(`/repos/${r}/git/refs`, {}, fetchImpl, { method: "POST", body: { ref: `refs/heads/${b.branch}`, sha: defSha } });
      }
      const commits = [];
      for (const u of b.updates) {
        let fileSha;
        try { fileSha = (await gh(`/repos/${r}/contents/${u.path}`, { ref: b.branch }, fetchImpl)).items.sha; } catch (e) { if (!(e instanceof NotFoundError)) throw e; }
        const put = (await gh(`/repos/${r}/contents/${u.path}`, {}, fetchImpl, {
          method: "PUT",
          body: { message: b.message, content: Buffer.from(u.content, "utf8").toString("base64"), branch: b.branch, ...(fileSha ? { sha: fileSha } : {}) },
        })).items;
        commits.push(put.commit.sha);
      }
      let pr = null;
      if (b.pr) {
        if (!b.pr.title) return send(400, { error: "pr.title required" });
        pr = (await gh(`/repos/${r}/pulls`, {}, fetchImpl, {
          method: "POST",
          body: { head: b.branch, base: b.pr.base ?? def, title: b.pr.title, ...(b.pr.body ? { body: b.pr.body } : {}) },
        })).items;
      }
      return send(200, { branch: b.branch, commits, pr });
    }
    return send(404, { error: ROUTES });
  } catch (err) {
    if (err instanceof RateLimitError) return send(429, { error: err.message, resetAt: err.resetAt });
    if (err instanceof NotFoundError) return send(404, { error: err.message });
    if (err instanceof ApiError)
      return send(err.status < 500 ? err.status : 502, { error: err.message, ...(err.body ? { detail: err.body } : {}) });
    const msg = err?.message ?? String(err);
    console.warn(`write-api: ${msg}`);
    return send(502, { error: msg }); // AuthError / network / anything unexpected
  }
};

if (import.meta.url === pathToFileURL(process.argv[1] ?? "").href)
  createServer((req, res) => {
    let raw = "";
    req.on("data", (c) => (raw += c));
    req.on("end", () => route(req, res, fetch, raw));
  }).listen(PORT, () => console.log(`github write-api on http://localhost:${PORT} — ${ROUTES}`));
