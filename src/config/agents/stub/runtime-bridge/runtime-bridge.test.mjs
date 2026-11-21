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
import { EventMapper, isSuccessfulToolEnd } from "./events.mjs";
import { openSpool } from "./spool.mjs";
import { RpcCorrelator, promptDisposition } from "./rpc.mjs";
import { openLogs } from "./logs.mjs";
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
    JSON.stringify({ id: "6", type: "tool_execution_end", data: { toolCallId: "c1", toolName: "t", status: "completed" } }),
    JSON.stringify({ id: "7", type: "turn_end" }),
    JSON.stringify({ id: "8", type: "agent_settled", data: {} }),
    JSON.stringify({ id: "c1", type: "response", command: "prompt", success: true, data: { disposition: "started" } }),
  ]) mapper.onLine(line);

  assert.deepEqual(emitted.map((e) => e.type), [
    "agent_started", "turn_started", "assistant_output", "tool_started", "tool_progress", "tool_completed", "turn_completed", "agent_settled",
  ]);
  assert.equal(mapper.settled, true);
  const toolCompleted = emitted.find((e) => e.type === "tool_completed");
  assert.deepEqual(toolCompleted.payload, { toolCallId: "c1", toolName: "t", status: "completed" });
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
    // real pi thinking stream events are typed and never become RunEvents (decision 58)
    JSON.stringify({ type: "message_update", assistantMessageEvent: { type: "thinking_delta", contentIndex: 1, delta: "hmm" } }),
    JSON.stringify({ type: "message_update", data: { delta: "" } }),
    JSON.stringify({ type: "queue_update", data: { queued: 1 } }),
    JSON.stringify({ type: "unknown_stream_type", data: {} }),
    JSON.stringify({ type: "response", command: "prompt", success: true, id: "r1" }),
  ]) mapper.onLine(line);
  assert.equal(emitted.length, 0);
});

// ---- real pi record shapes (pi docs json.md; ticket 12) ----

test("EventMapper maps real pi shapes: nested text deltas, top-level tool events", () => {
  const emitted = [];
  const mapper = new EventMapper((entry) => emitted.push(entry.body), 5);
  for (const line of [
    JSON.stringify({ type: "agent_start" }),
    JSON.stringify({ type: "turn_start" }),
    JSON.stringify({ type: "message_update", usage: {},
      assistantMessageEvent: { type: "text_delta", contentIndex: 0, delta: "Analyzing the issue." } }),
    JSON.stringify({ type: "message_update", usage: {},
      assistantMessageEvent: { type: "thinking_delta", contentIndex: 1, delta: "hmm" } }), // never progress
    JSON.stringify({ type: "message_update", usage: {},
      assistantMessageEvent: { type: "toolcall_start", id: "c9", toolName: "mcp__github__add_issue_comment" } }),
    JSON.stringify({ type: "tool_execution_start", toolCallId: "c9", toolName: "mcp__github__add_issue_comment", args: {} }),
    JSON.stringify({ type: "tool_execution_end", toolCallId: "c9", toolName: "mcp__github__add_issue_comment",
      result: {}, isError: false, durationMs: 12 }),
    JSON.stringify({ type: "turn_end", message: {}, toolResults: [] }),
    JSON.stringify({ type: "agent_settled", aborted: false }),
  ]) mapper.onLine(line);

  assert.deepEqual(emitted.map((e) => e.type), [
    "agent_started", "turn_started", "assistant_output", "tool_started", "tool_completed", "turn_completed", "agent_settled",
  ]);
  assert.deepEqual(emitted.find((e) => e.type === "assistant_output").payload, { text: "Analyzing the issue." });
  const completed = emitted.find((e) => e.type === "tool_completed");
  assert.deepEqual(completed.payload, { toolCallId: "c9", toolName: "mcp__github__add_issue_comment", isError: false });
});

/** Mirrors the Bridge's decision-59 correlation set building (only error-free completed ends count). */
function completedToolCallsFrom(records) {
  const set = new Set();
  const mapper = new EventMapper((entry) => {
    if (entry.body.type === "tool_completed" && isSuccessfulToolEnd(entry.body.payload))
      set.add(entry.body.payload.toolCallId);
  }, 5);
  for (const line of records) mapper.onLine(line);
  return set;
}

test("isSuccessfulToolEnd: pi isError and stub status determine the correlation set", () => {
  const set = completedToolCallsFrom([
    JSON.stringify({ type: "tool_execution_end", toolCallId: "ok", toolName: "add_issue_comment", isError: false }),
    JSON.stringify({ type: "tool_execution_end", toolCallId: "bad", toolName: "add_issue_comment", isError: true }),
    JSON.stringify({ type: "tool_execution_end", toolCallId: "stub", status: "completed" }),
  ]);
  assert.deepEqual([...set].sort(), ["ok", "stub"]);
  assert.equal(isSuccessfulToolEnd({ toolCallId: "ok", isError: false }), true);
  assert.equal(isSuccessfulToolEnd({ toolCallId: "bad", isError: true }), false);
  assert.equal(isSuccessfulToolEnd({ toolCallId: "x", status: "failed" }), false);
  assert.equal(isSuccessfulToolEnd(null), false);
});

// ---- rpc response correlation (pi docs rpc.md; ticket 12) ----

test("RpcCorrelator correlates command ids and resolves the matching response", async () => {
  const sent = [];
  const rpc = new RpcCorrelator((line) => sent.push(JSON.parse(line)), 5_000);
  const pending = rpc.command("get_state");
  assert.deepEqual(sent, [{ id: "req-1", type: "get_state" }]);
  assert.equal(rpc.onRecord({ type: "agent_start" }), false); // event lines pass through
  assert.equal(rpc.onRecord({ type: "response", command: "get_state", success: true, id: "req-1",
    data: { sessionFile: "/session/2026-11-21T15-31-18-187Z_probe-run.jsonl", sessionId: "probe-run" } }), true);
  const response = await pending;
  assert.equal(response.data.sessionFile, "/session/2026-11-21T15-31-18-187Z_probe-run.jsonl");
});

test("RpcCorrelator rejects success:false responses", async () => {
  const rpc = new RpcCorrelator(() => {}, 5_000);
  const pending = rpc.command("prompt", { message: "hi" });
  rpc.onRecord({ type: "response", command: "prompt", success: false, id: "req-1", error: "Model not found" });
  await assert.rejects(pending, /Model not found/);
});

test("RpcCorrelator times out an unanswered command", async () => {
  const rpc = new RpcCorrelator(() => {}, 30);
  await assert.rejects(rpc.command("get_state"), /timed out after 30ms/);
});

test("promptDisposition: only 'started' begins a run", () => {
  assert.equal(promptDisposition({ data: { disposition: "started" } }), "started");
  assert.equal(promptDisposition({ data: { disposition: "handled" } }), "handled");
  assert.equal(promptDisposition({}), undefined);
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

test("validateManifest enforces the mandated publication kinds (task 12, decision 42)", () => {
  const workspace = tempDir("ws-");
  // a manifest that lists no publication at all still fails when the kind is mandated — an agent
  // that never attempted its mandatory publication cannot "succeed" unverified
  const noPublication = { schemaVersion: 1, summary: "done", artifacts: [] };
  assert.match(
    validateManifest(noPublication, workspace, new Set(["call-1"]), ["issue_comment"]) ?? "",
    /github_publication_failed: required publication kind 'issue_comment' has no confirmed successful tool execution/);
  // a listed but unconfirmed publication also fails the mandate (error ends never correlate)
  const unconfirmed = { schemaVersion: 1, summary: "done", artifacts: [],
    requiredPublications: [{ toolCallId: "call-err", kind: "issue_comment", externalId: "p1" }] };
  assert.match(validateManifest(unconfirmed, workspace, new Set(), ["issue_comment"]) ?? "", /github_publication_failed/);
  // confirmed mandate passes
  const confirmed = { schemaVersion: 1, summary: "done", artifacts: [],
    requiredPublications: [{ toolCallId: "call-1", kind: "issue_comment", externalId: "42" }] };
  assert.equal(validateManifest(confirmed, workspace, new Set(["call-1"]), ["issue_comment"]), null);
  rmSync(workspace, { recursive: true, force: true });
});

test("terminalFromManifest carries the Bridge-discovered session file path (decision 57)", () => {
  process.env.PI_WORKSPACE = tempDir("ws-nogit-");
  const terminal = terminalFromManifest({ schemaVersion: 1, summary: "s", artifacts: [] }, "run-9",
    "/session/2026-11-21T15-31-18-187Z_run-9.jsonl");
  assert.equal(terminal.body.sessionFilePath, "/session/2026-11-21T15-31-18-187Z_run-9.jsonl");
  delete process.env.PI_WORKSPACE;
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

test("renderPrompt renders the ticket-11 context package sections", () => {
  const prompt = renderPrompt({
    schemaVersion: 2,
    profile: { name: "planner", version: 4 },
    task: { type: "plan", targetHeadSha: null, targetBaseSha: null },
    repository: { fullName: "o/r", defaultBranch: "main" },
    workItem: { repository: "o/r", number: 15, type: "issue", title: "T", body: "Issue body.", state: "open", author: "alice", labels: ["bug"] },
    instructions: "You are the planner agent.",
    command: "plan",
    commandComment: { author: "bob", githubCommentId: 42, createdAt: "2026-11-21T10:00:00Z", text: "@bot /plan with trailing user context" },
    conversation: [
      { githubCommentId: 43, kind: "issue_comment", author: "bob", createdAt: "2026-11-21T11:00:00Z", body: "one line answer" },
      { githubCommentId: 44, kind: "issue_comment", author: "carol", createdAt: "2026-11-21T12:00:00Z", body: "multi\nline\nbody" },
    ],
    artifacts: [{ id: "a1", taskSequence: 1, type: "plan", purpose: "approved_plan", format: "markdown", createdAt: "", content: "THE PLAN" },
      { id: "a2", taskSequence: 2, type: "log_reference", format: "text", createdAt: "", content: null, reference: "/var/lib/pi-agent/workflows/x/runs/y/logs" }],
  });

  assert.match(prompt, /^# Task/);
  assert.match(prompt, /## Instructions\nYou are the planner agent\./);
  assert.match(prompt, /## User command/);
  assert.match(prompt, /```text\n@bot \/plan with trailing user context\n```/);
  assert.match(prompt, /## Input artifacts/);
  assert.match(prompt, /Artifact plan — purpose: approved_plan/);
  assert.match(prompt, /THE PLAN/);
  assert.match(prompt, /Artifact log_reference.*\[reference: \/var\/lib\/pi-agent\/workflows\/x\/runs\/y\/logs\]/);
  assert.match(prompt, /## Conversation \(chronological\)/);
  assert.match(prompt, /bob at 2026-11-21T11:00:00Z \(comment 43\): one line answer/);
  assert.match(prompt, /carol at .*: multi line body/); // multi-line bodies collapse to one line
  assert.doesNotMatch(prompt, /artifactReferences/); // superseded by artifacts
});

test("renderPrompt renders reviewer context sections and skips absent ones", () => {
  const prompt = renderPrompt({
    task: { type: "review", targetHeadSha: "head-sha", targetBaseSha: "base-sha" },
    repository: { fullName: "o/r", defaultBranch: "main" },
    workItem: { repository: "o/r", number: 7, type: "pull_request", title: "PR", body: "", state: "open", author: "alice", labels: [] },
    instructions: "review", command: "review",
    reviews: [{ githubReviewId: 9, author: "dave", state: "changes_requested", body: "Please fix X", submittedAt: "2026-11-21T09:00:00Z", commitSha: null }],
    threads: [{ githubCommentId: 50, resolved: null, state: "changes_requested", path: "src/a.ts", line: 12,
      comments: [{ githubCommentId: 50, author: "dave", createdAt: "", body: "This looks wrong" }] }],
  });
  assert.match(prompt, /Target head SHA: head-sha/);
  assert.match(prompt, /Target base SHA: base-sha/);
  assert.match(prompt, /Task type: review/);
  assert.match(prompt, /## Submitted reviews \(newest first\)/);
  assert.match(prompt, /dave at 2026-11-21T09:00:00Z — state: changes_requested/);
  assert.match(prompt, /Thread 50 \(at src\/a\.ts:12\).*review state: changes_requested/);
  assert.match(prompt, /resolution unknown/);
  assert.match(prompt, /dave at .*: This looks wrong/);
  // sections that were not assembled never render
  assert.doesNotMatch(prompt, /Conversation/);
  assert.doesNotMatch(prompt, /git facts/);
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
    "--session-id", "run-1"], { // workspace from PI_WORKSPACE env (the Bridge never passes --workspace)
    env: { ...process.env, STUB_BEHAVIOR: "succeed", PI_WORKSPACE: workspace },
    input: '{"type":"prompt","message":"do a thing"}\n',
    encoding: "utf8",
  });
  assert.equal(child.status, 0, child.stderr);
  const types = child.stdout.split("\n").filter(Boolean).map((line) => JSON.parse(line).type);
  assert.deepEqual(types, [
    "response", // the prompt acceptance disposition (pi-shape response record)
    "agent_start", "turn_start", "message_update", "tool_execution_start",
    "tool_execution_update", "tool_execution_end", "turn_end", "agent_settled",
  ]);
  const accepted = JSON.parse(child.stdout.split("\n")[0]);
  assert.equal(accepted.type, "response");
  assert.equal(accepted.command, "prompt");
  assert.equal(accepted.success, true);
  assert.deepEqual(accepted.data, { disposition: "started" });
  assert.ok(existsSync(join(workspace, "result.json")));
  const manifest = JSON.parse(readFileSync(join(workspace, "result.json"), "utf8"));
  assert.equal(manifest.schemaVersion, 1);
  assert.equal(manifest.outcome, undefined);
  assert.ok(existsSync(join(sessionDir, "run-1.jsonl"))); // session history on the pinned path
  rmSync(workspace, { recursive: true, force: true });
  rmSync(sessionDir, { recursive: true, force: true });
});

test("stub handshake: get_state echoes the discovered session file, then the prompt resolves", () => {
  const sessionDir = tempDir("session-handshake-");
  const child = spawnSync(process.execPath, [AGENT, "--session-dir", sessionDir, "--session-id", "handshake-1"], {
    env: { ...process.env, STUB_BEHAVIOR: "succeed", PI_WORKSPACE: tempDir("ws-hs-") },
    input: '{"id":"req-1","type":"get_state"}\n{"id":"req-2","type":"prompt","message":"go"}\n',
    encoding: "utf8",
  });
  assert.equal(child.status, 0, child.stderr);
  const records = child.stdout.split("\n").filter(Boolean).map((line) => JSON.parse(line));
  const state = records.find((r) => r.type === "response" && r.command === "get_state");
  assert.equal(state.success, true);
  assert.equal(state.id, "req-1");
  assert.equal(state.data.sessionId, "handshake-1");
  assert.equal(state.data.sessionFile, join(sessionDir, "handshake-1.jsonl")); // decision 57 discovery
  const accepted = records.find((r) => r.type === "response" && r.command === "prompt");
  assert.equal(accepted.id, "req-2");
  assert.equal(accepted.data.disposition, "started");
});

test("stub agent crash behavior: fail reports an error line and exits non-zero", () => {
  const workspace = tempDir("ws-fail-");
  const child = spawnSync(process.execPath, [AGENT, "--session-dir", tempDir("s-"),
    "--session-id", "r"], {
    env: { ...process.env, STUB_BEHAVIOR: "fail", PI_WORKSPACE: workspace },
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
    "--session-id", "r"], {
    env: { ...process.env, STUB_BEHAVIOR: "needs_human", PI_WORKSPACE: workspace },
    input: '{"type":"prompt","message":"q"}\n',
    encoding: "utf8",
  });
  assert.equal(child.status, 0);
  const manifest = JSON.parse(readFileSync(join(workspace, "result.json"), "utf8"));
  assert.equal(manifest.outcome, "needs_human_input");
  rmSync(workspace, { recursive: true, force: true });
});
