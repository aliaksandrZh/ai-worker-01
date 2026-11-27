/**
 * Node test suite for the communication-channel server (the agent's one channel to the
 * Orchestrator/User — docs/flows/tools.md). Runs with:
 *   npm test        (node --test test/)
 * in agents/_runtime/communication-channel/ (devDependencies: zod, esbuild only).
 *
 * The server is exercised as a process over stdio (JSON-RPC lines), both from src and from
 * the built self-contained bundle, mirroring how the agent runtime will talk to it.
 */
import test from "node:test";
import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { existsSync, mkdtempSync, readdirSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { join } from "node:path";
import { tmpdir } from "node:os";
import { fileURLToPath } from "node:url";
import { generateMcpJson } from "../src/mcpjson.mjs";
import { VOCABULARY, ESCALATION_TOOLS } from "../src/spec.mjs";

const ROOT = fileURLToPath(new URL("..", import.meta.url));
const SRC_SERVER = join(ROOT, "src", "server.mjs");
const DIST_SERVER = join(ROOT, "dist", "server.mjs");

function tempDir(prefix) {
  return mkdtempSync(join(tmpdir(), prefix));
}

function writeManifest(dir, tools) {
  const path = join(dir, "agent.json");
  writeFileSync(path, JSON.stringify({ name: "tester", tools }));
  return path;
}

/** Run the server as a process: one JSON-RPC request line per request, replies in order. */
function rpc(serverPath, manifestPath, requests) {
  const input = requests.map((r) => JSON.stringify(r)).join("\n") + "\n";
  const res = spawnSync(process.execPath, [serverPath, manifestPath], { input, encoding: "utf8" });
  assert.equal(res.status, 0, `server exited ${res.status}: ${res.stderr}`);
  return res.stdout
    .split("\n")
    .filter((l) => l.trim())
    .map((l) => JSON.parse(l));
}

function initialize(serverPath, manifestPath) {
  return rpc(serverPath, manifestPath, [{ jsonrpc: "2.0", id: 1, method: "initialize", params: {} }]);
}

const listRequest = { jsonrpc: "2.0", id: 2, method: "tools/list" };
const call = (name, args) => ({ jsonrpc: "2.0", id: 3, method: "tools/call", params: { name, arguments: args } });

// ---- manifest-driven registration ----

test("registers only the manifest's communication tools; unlisted and unknown names are not registered", () => {
  const dir = tempDir("comm-");
  try {
    // coder profile: context tools + 3 of the 11; save_plan/request_decision are NOT listed,
    // "stale_tool" is not in the vocabulary at all.
    const manifest = writeManifest(dir, [
      "issue_read", "add_issue_comment",          // context (GitHub MCP server's)
      "report_completion", "submit_pr_description", "report_blocker",
      "stale_tool",                               // not in the vocabulary — ignored
    ]);
    const replies = rpc(SRC_SERVER, manifest, [listRequest]);
    const names = replies[0].result.tools.map((t) => t.name);
    assert.deepEqual(names, ["report_completion", "submit_pr_description", "report_blocker"]);
    // every registered tool carries description + JSON Schema inputSchema, no internals
    for (const t of replies[0].result.tools) {
      assert.equal(typeof t.description, "string");
      assert.equal(t.inputSchema?.type, "object");
      assert.equal(t.schema, undefined);
    }
    // calling an unlisted communication tool is rejected
    const [denied] = rpc(SRC_SERVER, manifest, [call("save_plan", { goals: "g", scope: "s", acceptanceCriteria: "a", steps: [] })]);
    assert.equal(denied.error.code, -32602);
    assert.match(denied.error.message, /not registered/);
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

test("initialize answers with the communication-channel server info; notifications get no reply", () => {
  const dir = tempDir("comm-");
  try {
    const manifest = writeManifest(dir, ["report_completion"]);
    const replies = rpc(SRC_SERVER, manifest, [
      { jsonrpc: "2.0", method: "notifications/initialized" }, // no id — must not produce output
      { jsonrpc: "2.0", id: 1, method: "initialize", params: {} },
    ]);
    assert.equal(replies.length, 1);
    assert.equal(replies[0].result.serverInfo.name, "communication-channel");
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

// ---- payload validation: valid -> success + hint; invalid -> JSON-RPC error naming the field ----

test("valid payload succeeds with the acknowledgment hint (save_plan)", () => {
  const dir = tempDir("comm-");
  try {
    const manifest = writeManifest(dir, ["save_plan"]);
    const [reply] = rpc(SRC_SERVER, manifest, [
      call("save_plan", { goals: "g", scope: "s", acceptanceCriteria: "a", steps: ["one", "two"] }),
    ]);
    assert.equal(reply.error, undefined);
    assert.equal(reply.result.content[0].type, "text");
    assert.match(reply.result.content[0].text, /Recorded: save_plan/);
    assert.match(reply.result.content[0].text, /published nothing to GitHub/);
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

test("invalid payload is a JSON-RPC error naming the offending field", () => {
  const dir = tempDir("comm-");
  try {
    const manifest = writeManifest(dir, ["save_plan", "report_completion", "submit_review_comments"]);
    const replies = rpc(SRC_SERVER, manifest, [
      call("save_plan", { goals: "g", scope: "s", acceptanceCriteria: "a" }), // steps missing
      call("save_plan", { goals: "g", scope: "s", acceptanceCriteria: "a", steps: "one" }), // wrong type
      call("report_completion", { summary: "sneaky payload" }), // parameterless: no arguments accepted
      call("submit_review_comments", { comments: [{ file: "a.ts", line: 1, severity: "major" }] }), // message missing
    ]);
    const expectations = [
      /invalid 'steps'/,        // required field missing
      /invalid 'steps'/,        // wrong type (string instead of string[])
      /'summary'/,              // parameterless: no arguments accepted
      /'comments\.0\.message'/, // nested required field missing (path includes the index)
    ];
    assert.equal(replies.length, expectations.length);
    expectations.forEach((fragment, i) => {
      assert.equal(replies[i].error?.code, -32602, JSON.stringify(replies[i]));
      assert.match(replies[i].error.message, fragment, `case ${i}`);
      assert.match(replies[i].error.message, /^save_plan|^report_completion|^submit_review_comments/);
    });
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

// ---- hints: escalations park the flow; completion settles; others acknowledge ----

test("escalation tools hint: parked until a user event resumes a NEW Run; still end with report_completion", () => {
  const dir = tempDir("comm-");
  try {
    const manifest = writeManifest(dir, [...ESCALATION_TOOLS, "report_completion"]);
    const argsByTool = {
      request_clarification: { question: "q" },
      report_blocker: { description: "d", cause: "c", options: ["o"] },
      request_decision: { issue: "i", context: "c", options: ["o"] },
      report_env_issue: { description: "d", whatIsNeeded: "n" },
      request_criteria_clarification: { question: "q", context: "c" },
    };
    const requests = [...ESCALATION_TOOLS].map((name) => call(name, argsByTool[name]));
    const replies = rpc(SRC_SERVER, manifest, requests);
    for (const reply of replies) {
      assert.equal(reply.error, undefined, JSON.stringify(reply));
      const text = reply.result.content[0].text;
      assert.match(text, /NEW Run/, `hint says the flow resumes as a NEW Run: ${text}`);
      assert.match(text, /report_completion/, `hint reminds to end the Run with report_completion: ${text}`);
    }
    // and report_completion itself says the run can settle
    const [completion] = rpc(SRC_SERVER, manifest, [call("report_completion", {})]);
    assert.match(completion.result.content[0].text, /run can settle/);
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

test("every agent manifest lists only tools declared in the catalog (agents/_runtime/tools.json)", () => {
  const catalog = JSON.parse(readFileSync(new URL("../../tools.json", import.meta.url), "utf8"));
  const repoRoot = fileURLToPath(new URL("../../../..", import.meta.url));
  const manifests = readdirSync(join(repoRoot, "agents"), { withFileTypes: true })
    .filter((d) => d.isDirectory() && !d.name.startsWith("_"))
    .map((d) => join(repoRoot, "agents", d.name, "agent.json"))
    .filter(existsSync);
  assert.ok(manifests.length >= 4); // planner, coder, reviewer, stub
  for (const path of manifests) {
    const manifest = JSON.parse(readFileSync(path, "utf8"));
    for (const name of manifest.tools ?? [])
      assert.ok(catalog.tools[name], `${path}: '${name}' is not declared in the catalog`);
  }
});

test("the vocabulary is exactly the 11 bridge-route names (contract with docs/run-worker.md)", () => {
  assert.deepEqual(
    [...VOCABULARY].sort(),
    [
      "report_completion", "save_plan", "request_clarification", "report_blocker",
      "submit_review_comments", "request_decision", "report_bug", "report_env_issue",
      "request_criteria_clarification", "report_tests_passed", "submit_pr_description",
    ].sort(),
  );
  assert.equal(ESCALATION_TOOLS.size, 5);
});

// ---- mcp.json generator ----

test("generator: GITHUB_TOOLS = manifest context tools; communication entry runs the bundle with the manifest", () => {
  const json = generateMcpJson({
    name: "coder",
    tools: ["issue_read", "pull_request_read", "report_completion", "submit_pr_description", "report_blocker"],
  }, { bundle: "/opt/x/dist/server.mjs", manifestArg: "/opt/x/agent.json" });
  assert.equal(json.mcpServers.github.env.GITHUB_TOOLS, "issue_read,pull_request_read");
  assert.deepEqual(json.mcpServers.communication.args, ["/opt/x/dist/server.mjs", "/opt/x/agent.json"]);
  assert.equal(json.mcpServers.communication.command, "node");
});

test("generator: token-file path pointer is per-agent (planner/reviewer read-only PAT, ticket 08)", () => {
  const json = generateMcpJson({ name: "planner", tools: ["issue_read", "save_plan"] });
  assert.equal(json.mcpServers.github.env.GITHUB_PERSONAL_ACCESS_TOKEN, "!cat /run/secrets/github-write-pat.txt"); // coder default
  const ro = generateMcpJson(
    { name: "planner", tools: ["issue_read", "save_plan"] },
    { tokenFile: "/run/secrets/github-read-pat.txt" });
  assert.equal(ro.mcpServers.github.env.GITHUB_PERSONAL_ACCESS_TOKEN, "!cat /run/secrets/github-read-pat.txt");
  assert.equal(ro.mcpServers.github.env.GITHUB_TOOLS, "issue_read");
});

test("generator CLI writes the file with -o", () => {
  const dir = tempDir("comm-");
  try {
    const manifest = writeManifest(dir, ["issue_read", "add_issue_comment", "report_completion"]);
    const out = join(dir, "mcp.json");
    const res = spawnSync(process.execPath, [join(ROOT, "src", "generate-mcpjson.mjs"), manifest, "-o", out], { encoding: "utf8" });
    assert.equal(res.status, 0, res.stderr);
    const json = JSON.parse(readFileSync(out, "utf8"));
    assert.equal(json.mcpServers.github.env.GITHUB_TOOLS, "issue_read,add_issue_comment");
    assert.ok(json.mcpServers.communication);
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

// ---- the built bundle: self-contained, no runtime dependencies ----

test("npm run build produces dist/server.mjs with no external (non-node) imports", () => {
  assert.equal(existsSync(join(ROOT, "package-lock.json")), true);
  const build = spawnSync("npm", ["run", "build"], { cwd: ROOT, encoding: "utf8" });
  assert.equal(build.status, 0, build.stderr);
  assert.equal(existsSync(DIST_SERVER), true);
  const bundle = readFileSync(DIST_SERVER, "utf8");
  const external = [...bundle.matchAll(/^import\s+[^;]*from\s+["']([^"']+)["']/gm)]
    .map((m) => m[1])
    .filter((spec) => !spec.startsWith("node:"));
  assert.deepEqual(external, [], "bundle must inline zod — only node builtins stay external");
});

test("the bundle serves the full conversation end to end (no node_modules needed at runtime)", () => {
  const dir = tempDir("comm-");
  try {
    const manifest = writeManifest(dir, ["report_tests_passed", "issue_read"]);
    const replies = rpc(DIST_SERVER, manifest, [
      { jsonrpc: "2.0", id: 1, method: "initialize", params: {} },
      listRequest,
      call("report_tests_passed", { summary: "42 green" }),
      call("report_tests_passed", {}),
    ]);
    assert.equal(replies.length, 4);
    assert.equal(replies[0].result.serverInfo.name, "communication-channel");
    assert.deepEqual(replies[1].result.tools.map((t) => t.name), ["report_tests_passed"]);
    assert.match(replies[2].result.content[0].text, /Recorded: report_tests_passed/);
    assert.equal(replies[3].error.code, -32602);
    assert.match(replies[3].error.message, /'summary'/);
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

// ---- the workspace delivery guard (CC_COMPLETION_GUARD) ----

import { workspaceGuard } from "../src/server.mjs";
import { execFileSync } from "node:child_process";

/** A minimal git repo with an origin (a bare sibling): one commit on a pushed branch. */
function gitWorkspace(prefix, { branch, push = true, detached = false } = {}) {
  const dir = tempDir(prefix);
  const run = (...args) => execFileSync("git", ["-C", dir, ...args], { encoding: "utf8" });
  run("init", "--initial-branch=main");
  run("config", "user.email", "t@t");
  run("config", "user.name", "t");
  run("commit", "--allow-empty", "-m", "init");
  const origin = tempDir(prefix + "-origin-");
  execFileSync("git", ["init", "--bare", origin], { encoding: "utf8" });
  run("remote", "add", "origin", origin);
  if (branch && branch !== "main") run("checkout", "-b", branch);
  if (push) run("push", "-u", "origin", branch ?? "main");
  if (detached) run("checkout", "--detach", "HEAD");
  return dir;
}

const withEnv = (env) => (requests, manifest) => {
  const res = spawnSync(process.execPath, [SRC_SERVER, manifest], {
    input: requests.map((r) => JSON.stringify(r)).join("\n") + "\n",
    encoding: "utf8",
    env: { ...process.env, ...env },
  });
  assert.equal(res.status, 0, `server exited ${res.status}: ${res.stderr}`);
  return res.stdout.split("\n").filter((l) => l.trim()).map((l) => JSON.parse(l));
};

test("without CC_COMPLETION_GUARD the delivery tools pass regardless of git state", () => {
  const dir = tempDir("comm-");
  const ws = gitWorkspace("guard-", { branch: "main" }); // default branch — would fail guarded
  try {
    const manifest = writeManifest(dir, ["report_completion"]);
    const replies = rpc(SRC_SERVER, manifest, [
      listRequest,
      call("report_completion", {}),
    ]);
    assert.match(replies[1].result.content[0].text, /Recorded: report_completion/);
  } finally {
    rmSync(dir, { recursive: true, force: true });
    rmSync(ws, { recursive: true, force: true });
  }
});

test("the guard blocks report_completion on the default branch and on an unpushed branch", () => {
  const dir = tempDir("comm-");
  const ws = gitWorkspace("guard-", { branch: "main" });
  try {
    const manifest = writeManifest(dir, ["report_completion"]);
    const send = withEnv({ CC_COMPLETION_GUARD: ws, GIT_DEFAULT_BRANCH: "main" });
    const replies = send([listRequest, call("report_completion", {})], manifest);
    assert.equal(replies[1].error.code, -32602);
    assert.match(replies[1].error.message, /invalid 'workspace'.*default branch 'main'/);
  } finally {
    rmSync(dir, { recursive: true, force: true });
    rmSync(ws, { recursive: true, force: true });
  }
});

test("the guard passes a pushed agent branch and still validates the payload after it", () => {
  const dir = tempDir("comm-");
  const ws = gitWorkspace("guard-", { branch: "agent/1-fix" });
  try {
    const manifest = writeManifest(dir, ["submit_pr_description", "report_completion"]);
    const send = withEnv({ CC_COMPLETION_GUARD: ws, GIT_DEFAULT_BRANCH: "main" });
    const replies = send([
      call("submit_pr_description", { title: "t", body: "b" }),
      call("report_completion", {}),
    ], manifest);
    assert.match(replies[0].result.content[0].text, /Recorded: submit_pr_description/);
    assert.match(replies[1].result.content[0].text, /Recorded: report_completion/);
  } finally {
    rmSync(dir, { recursive: true, force: true });
    rmSync(ws, { recursive: true, force: true });
  }
});

test("the guard fails closed when the workspace has no repository", () => {
  const dir = tempDir("comm-");
  const ws = tempDir("guard-empty-"); // no git init
  try {
    const manifest = writeManifest(dir, ["report_completion"]);
    const send = withEnv({ CC_COMPLETION_GUARD: ws, GIT_DEFAULT_BRANCH: "main" });
    const replies = send([call("report_completion", {})], manifest);
    assert.equal(replies[0].error.code, -32602);
    assert.match(replies[0].error.message, /no git repository state/);
  } finally {
    rmSync(dir, { recursive: true, force: true });
    rmSync(ws, { recursive: true, force: true });
  }
});
