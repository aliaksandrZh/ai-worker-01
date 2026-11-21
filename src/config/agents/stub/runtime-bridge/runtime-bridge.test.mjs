/**
 * Node test suite for the ticket-09 Runtime Bridge + stub agent. Runs with:
 *   node --test config/agents/stub/runtime-bridge/
 * (no npm dependencies)
 */
import test from "node:test";
import assert from "node:assert/strict";
import { spawnSync, execFileSync } from "node:child_process";
import { existsSync, mkdtempSync, readFileSync, rmSync, writeFileSync, mkdirSync, statSync } from "node:fs";
import { join } from "node:path";
import { tmpdir } from "node:os";
import { fileURLToPath } from "node:url";
import { EventMapper } from "./events.mjs";
import { openSpool } from "./spool.mjs";
import { Trio } from "./logs.mjs";
import { loadManifest, validateManifest, terminalFromManifest, gitFacts, renderPrompt } from "./manifest.mjs";

const AGENT = fileURLToPath(new URL("../stub-agent/pi-stub.mjs", import.meta.url));

function tempDir(prefix) {
  return mkdtempSync(join(tmpdir(), prefix));
}

// ---- EventMapper: pi→RunEvent mapping (04 §4) ----

test("EventMapper maps the prompt event sequence one to one", () => {
  const emitted = [];
  const mapper = new EventMapper((entry) => emitted.push(entry.body), 5);
  for (const line of [
    JSON.stringify({ id: "1", type: "agent_start", data: { sessionId: "s" } }),
    JSON.stringify({ id: "2", type: "turn_start" }),
    JSON.stringify({ id: "3", type: "message_update", data: { delta: "Analyzing " } }),
    JSON.stringify({ id: "4", type: "tool_execution_start", data: { toolCallId: "c1", toolName: "t" } }),
    JSON.stringify({ id: "5", type: "tool_execution_update", data: { toolCallId: "c1" } }),
    JSON.stringify({ id: "6", type: "tool_execution_end", data: { toolCallId: "c1", status: "completed" } }),
    JSON.stringify({ id: "7", type: "turn_end" }),
    JSON.stringify({ id: "8", type: "agent_settled", data: {} }),
  ]) mapper.onLine(line);

  assert.deepEqual(emitted.map((e) => e.type), [
    "agent_started", "turn_started", "assistant_output", "tool_started", "tool_progress", "tool_completed", "turn_completed", "agent_settled",
  ]);
  assert.equal(mapper.settled, true);
  const toolCompleted = emitted.find((e) => e.type === "tool_completed");
  assert.deepEqual(toolCompleted.payload, { toolCallId: "c1", status: "completed" });
  assert.equal(emitted.every((e) => typeof e.eventId === "string" && e.eventId.length > 0), true);
});

test("EventMapper aggregates text deltas (no per-token events) and flushes on settle", () => {
  const emitted = [];
  const mapper = new EventMapper((entry) => emitted.push(entry.body), 5);
  for (const line of [
    JSON.stringify({ type: "message_update", data: { delta: "one " } }),
    JSON.stringify({ type: "message_update", data: { delta: "two" } }),
    JSON.stringify({ type: "agent_settled", data: {} }),
  ]) mapper.onLine(line);
  const assistant = emitted.filter((e) => e.type === "assistant_output");
  assert.equal(assistant.length, 1); // aggregated BEFORE persistence (decision 58)
  assert.deepEqual(assistant[0].payload, { text: "one two" });
});

test("EventMapper: thinking deltas, queue updates and empty text are not events", () => {
  const emitted = [];
  const mapper = new EventMapper((entry) => emitted.push(entry.body), 5);
  for (const line of [
    JSON.stringify({ type: "message_update", data: { delta: "thinking…", reasoning: true } }),
    JSON.stringify({ type: "message_update", data: { delta: "" } }),
    JSON.stringify({ type: "queue_update", data: { queued: 1 } }),
    JSON.stringify({ type: "unknown_stream_type", data: {} }),
  ]) mapper.onLine(line);
  assert.equal(emitted.length, 0);
});

// ---- manifest validation + terminal shape ----

test("validateManifest accepts a valid in-workspace manifest", () => {
  const workspace = tempDir("ws-");
  mkdirSync(join(workspace, "artifacts"), { recursive: true });
  writeFileSync(join(workspace, "artifacts", "summary.md"), "x".repeat(100));
  const manifest = {
    schemaVersion: 1,
    summary: "done",
    artifacts: [{ type: "implementation_summary", format: "markdown", path: "artifacts/summary.md" }],
    requiredPublications: [{ toolCallId: "call-1", kind: "issue_comment", externalId: "p1" }],
  };
  assert.equal(validateManifest(manifest, workspace, new Set(["call-1"])), null);
  rmSync(workspace, { recursive: true, force: true });
});

test("validateManifest rejects oversize artifacts without truncation", () => {
  const workspace = tempDir("ws-");
  mkdirSync(join(workspace, "artifacts"), { recursive: true });
  writeFileSync(join(workspace, "artifacts", "big.md"), "x".repeat(256 * 1024 + 1));
  const manifest = {
    schemaVersion: 1, summary: "done",
    artifacts: [{ type: "implementation_summary", format: "markdown", path: "artifacts/big.md" }],
  };
  const reason = validateManifest(manifest, workspace, new Set());
  assert.match(reason, /artifact_too_large/);
  assert.ok(statSync(join(workspace, "artifacts", "big.md")).size > 256 * 1024); // file intact
  rmSync(workspace, { recursive: true, force: true });
});

test("validateManifest rejects paths escaping the workspace", () => {
  const workspace = tempDir("ws-");
  const outside = join(tempDir("out-"), "secret.md");
  writeFileSync(outside, "secret");
  const manifest = {
    schemaVersion: 1, summary: "done",
    artifacts: [{ type: "implementation_summary", format: "markdown", path: outside }],
  };
  assert.match(validateManifest(manifest, workspace, new Set()) ?? "", /escapes the workspace/);
  rmSync(workspace, { recursive: true, force: true });
  rmSync(outside, { force: true });
});

test("validateManifest enforces publication correlation (decision 42)", () => {
  const workspace = tempDir("ws-");
  const manifest = {
    schemaVersion: 1, summary: "done", artifacts: [],
    requiredPublications: [{ toolCallId: "call-1", kind: "issue_comment", externalId: "p1" }],
  };
  assert.match(validateManifest(manifest, workspace, new Set()) ?? "", /github_publication_failed/);
  assert.equal(validateManifest(manifest, workspace, new Set(["call-1"])), null);
  rmSync(workspace, { recursive: true, force: true });
});

test("terminalFromManifest carries outcome + git facts", () => {
  const workspace = tempDir("ws-");
  execFileSync("git", ["init", "-q", "-b", "agent/pipe-stub", workspace]);
  execFileSync("git", ["-C", workspace, "-c", "user.name=t", "-c", "user.email=t@t", "commit", "--allow-empty", "-q", "-m", "k"]);
  process.env.PI_WORKSPACE = workspace;
  const terminal = terminalFromManifest({
    schemaVersion: 1, summary: "s", outcome: "needs_human_input", artifacts: [],
  }, "run-1");
  assert.equal(terminal.route, "completed");
  assert.equal(terminal.body.outcome, "needs_human_input");
  assert.equal(terminal.id, "evt-run-1-terminal");
  assert.equal(terminal.body.eventId, "evt-run-1-terminal");
  assert.equal(terminal.body.result.branch, "agent/pipe-stub");
  assert.match(terminal.body.result.commitSha, /^[0-9a-f]{40}$/);
  delete process.env.PI_WORKSPACE;
  rmSync(workspace, { recursive: true, force: true });
});

test("gitFacts return nulls when there is no repository", () => {
  const workspace = tempDir("ws-plain");
  const facts = gitFacts(workspace);
  assert.equal(facts.branch, null);
  assert.equal(facts.commitSha, null);
  rmSync(workspace, { recursive: true, force: true });
});

test("renderPrompt makes one structured markdown message", () => {
  const prompt = renderPrompt({
    workItem: { repository: "o/r", number: 15, title: "T" },
    command: "plan",
    instructions: "Do the work.",
    artifactReferences: [],
  });
  assert.match(prompt, /# Task/);
  assert.match(prompt, /o\/r/);
  assert.match(prompt, /## Instructions/);
  assert.match(prompt, /Do the work\./);
});

// ---- spool: durable append, coalescing, redelivery ----

test("spool appends durably, delivers in order, removes on completion", async () => {
  const spoolDir = tempDir("spool-");
  const marker = [];
  const spool = openSpool(spoolDir, { onTerminalDelivered: () => marker.push("t") });
  spool.append({ id: "e1", route: "events", body: { eventId: "e1", type: "tool_started" } });
  spool.append({ id: "e2", route: "events", body: { eventId: "e2", type: "tool_completed" } });
  spool.append({ id: "t1", route: "completed", body: { eventId: "t1" } });

  assert.equal(spool.count(), 3);
  const first = await spool.next();
  assert.equal(first.id, "e1");
  spool.remove(first);
  const second = await spool.next();
  assert.equal(second.id, "e2");
  spool.remove(second);
  const third = await spool.next();
  assert.equal(third.id, "t1");
  spool.remove(third);
  assert.equal(spool.count(), 0);
  assert.equal(marker.length, 1);
  rmSync(spoolDir, { recursive: true, force: true });
});

test("spool coalesces heartbeats to the latest beat, delivered once", async () => {
  const spoolDir = tempDir("spool-hb-");
  const spool = openSpool(spoolDir);
  const beats = spool.coalescingHeartbeats("hb-1");
  beats({ occurredAt: "t1" });
  beats({ occurredAt: "t2" });
  assert.equal(spool.count(), 1); // single slot
  const beat = await spool.next();
  assert.equal(beat.id, "hb-1");
  assert.equal(beat.body.body?.occurredAt ?? beat.body.occurredAt, "t2"); // latest value
  spool.remove(beat);
  assert.equal(spool.count(), 0);
  rmSync(spoolDir, { recursive: true, force: true });
});

test("spool redelivers the same eventIds after a bridge restart", async () => {
  const spoolDir = tempDir("spool-restart-");
  const first = openSpool(spoolDir);
  first.append({ id: "e1", route: "events", body: { eventId: "e1", type: "tool_started" } });
  first.append({ id: "t1", route: "failed", body: { eventId: "t1", error: { code: "c" } } });

  const second = openSpool(spoolDir);
  const redelivered = [];
  for (let i = 0; i < 2; i++) {
    const entry = await second.next();
    redelivered.push(entry.id);
    second.remove(entry);
  }
  assert.deepEqual(redelivered, ["e1", "t1"]);
  rmSync(spoolDir, { recursive: true, force: true });
});

// ---- log trio with the shared 50 MiB budget (decision 43) ----

test("log trio rotates under the shared budget, oldest segments first", () => {
  const logsDir = tempDir("logs-");
  const trio = new Trio(logsDir, 2048);
  trio.write("stdout.log", "x".repeat(700));
  trio.write("stdout.log", "x".repeat(700)); // crosses the per-file share → rotated
  assert.ok(existsSync(join(logsDir, "stdout.log.1")));
  trio.write("stderr.log", "y".repeat(700)); // pushes the total over → oldest segment dropped
  assert.equal(existsSync(join(logsDir, "stdout.log.1")), false);
  assert.ok(trio.usedBytes() <= 2048 + 1024); // bounded: cap + one share
  rmSync(logsDir, { recursive: true, force: true });
});

// ---- stub CLI end-to-end over the RPC-shaped protocol ----

test("stub agent answers one prompt with the mapped event set and settles", () => {
  const workspace = tempDir("ws-stub-");
  const sessionDir = tempDir("session-");
  const child = spawnSync(process.execPath, [AGENT, "--session-dir", sessionDir,
    "--session-id", "run-1", "--workspace", workspace], {
    env: { ...process.env, STUB_BEHAVIOR: "succeed" },
    input: '{"type":"prompt","message":"do a thing"}\n',
    encoding: "utf8",
  });
  assert.equal(child.status, 0, child.stderr);
  const types = child.stdout.split("\n").filter(Boolean).map((line) => JSON.parse(line).type);
  assert.deepEqual(types, [
    "agent_start", "turn_start", "message_update", "tool_execution_start",
    "tool_execution_update", "tool_execution_end", "turn_end", "agent_settled",
  ]);
  assert.ok(existsSync(join(workspace, "result.json")));
  const manifest = JSON.parse(readFileSync(join(workspace, "result.json"), "utf8"));
  assert.equal(manifest.schemaVersion, 1);
  assert.equal(manifest.outcome, undefined);
  assert.ok(existsSync(join(sessionDir, "run-1.jsonl"))); // session history on the pinned path
  rmSync(workspace, { recursive: true, force: true });
  rmSync(sessionDir, { recursive: true, force: true });
});

test("stub agent crash behavior: fail reports an error line and exits non-zero", () => {
  const workspace = tempDir("ws-fail-");
  const child = spawnSync(process.execPath, [AGENT, "--session-dir", tempDir("s-"),
    "--session-id", "r", "--workspace", workspace], {
    env: { ...process.env, STUB_BEHAVIOR: "fail" },
    input: '{"type":"prompt","message":"do a thing"}\n',
    encoding: "utf8",
  });
  assert.equal(child.status, 3, child.stderr);
  const errorLine = child.stdout.split("\n").map((l) => { try { return JSON.parse(l); } catch { return null; } })
    .find((e) => e?.type === "error");
  assert.equal(errorLine.data.code, "planner_stub_error");
  assert.equal(existsSync(join(workspace, "result.json")), false); // no manifest on the failure path
  rmSync(workspace, { recursive: true, force: true });
});

test("stub agent needs_human behavior writes the outcome flag", async () => {
  const workspace = tempDir("ws-human-");
  const child = spawnSync(process.execPath, [AGENT, "--session-dir", tempDir("s-"),
    "--session-id", "r", "--workspace", workspace], {
    env: { ...process.env, STUB_BEHAVIOR: "needs_human" },
    input: '{"type":"prompt","message":"q"}\n',
    encoding: "utf8",
  });
  assert.equal(child.status, 0);
  const manifest = JSON.parse(readFileSync(join(workspace, "result.json"), "utf8"));
  assert.equal(manifest.outcome, "needs_human_input");
  rmSync(workspace, { recursive: true, force: true });
});
