/**
 * Node test suite for the ticket-09 Runtime Bridge + stub agent. The shared bridge source lives at
 * * agents/_runtime/bridge/ (single copy, merged into every profile's pinned build snapshot — see
 * ConfigSnapshotBuilder); the stub CLI it drives stays baked in the stub profile. Runs with:
 *   node --test agents/_runtime/bridge/runtime-bridge.test.mjs
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

// The stub CLI lives in the stub profile (only agent that bakes it); cross-reference kept explicit.
const AGENT = fileURLToPath(new URL("../../stub/stub-agent/pi-stub.mjs", import.meta.url));

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

// ---- graceful stop (ticket 14, decision 61) ----

import { cancelledTerminal, listChildPids } from "./stop.mjs";
import { createServer } from "node:http";
import { spawn } from "node:child_process";
import { dirname } from "node:path";

test("cancelledTerminal: deterministic id, reason and session path carried", () => {
  const entry = cancelledTerminal("run-7", "stop_requested", "/session/2026-11-21T15-31-18-187Z_run-7.jsonl");
  assert.equal(entry.id, "evt-run-7-cancelled");
  assert.equal(entry.route, "cancelled");
  assert.equal(entry.body.eventId, "evt-run-7-cancelled");
  assert.equal(entry.body.reason, "stop_requested");
  assert.equal(entry.body.sessionFilePath, "/session/2026-11-21T15-31-18-187Z_run-7.jsonl");
  // the session path is absent when the handshake never discovered one
  const bare = cancelledTerminal("run-8", null);
  assert.equal(bare.body.sessionFilePath, undefined);
  assert.equal(bare.body.reason, undefined);
});

test("listChildPids: detects children via /proc, degrades to empty without it", async () => {
  // ticket 14's (6) verification works on Linux hosts with /proc; macOS/other hosts degrade to []
  const hasProc = existsSync("/proc");
  const child = spawn(process.execPath, ["-e", "setInterval(() => {}, 1000);"], { stdio: "ignore" });
  try {
    await new Promise((resolve) => setTimeout(resolve, 300));
    const children = listChildPids(process.pid);
    if (hasProc) {
      assert.ok(children.includes(child.pid), `expected ${child.pid} among ${children}`);
    } else {
      assert.deepEqual(children, []); // graceful /proc absence — the sequence logs and continues
    }
  } finally {
    child.kill("SIGKILL"); // never orphan the probe child (an assert must not immortalize it)
    await new Promise((resolve) => child.on("exit", resolve));
  }
  assert.equal(listChildPids(-1).length, 0);
});

test("spool treats the cancelled route as terminal (done marker)", async () => {
  const spoolDir = tempDir("spool-cancelled-");
  const markers = [];
  const spool = openSpool(spoolDir, { onTerminalDelivered: () => markers.push(1) });
  spool.append({ id: "c1", route: "cancelled", body: { eventId: "c1" } });
  const entry = await spool.next();
  spool.remove(entry);
  assert.equal(markers.length, 1);
  rmSync(spoolDir, { recursive: true, force: true });
});

test("stub agent: abort settles an in-flight run with aborted=true; clear_queue/abort_bash respond", () => {
  const sessionDir = tempDir("session-abort-");
  const child = spawnSync(process.execPath, [AGENT, "--session-dir", sessionDir, "--session-id", "r"], {
    env: { ...process.env, STUB_BEHAVIOR: "hang", PI_WORKSPACE: tempDir("ws-abort-") },
    input: [
      '{"id":"req-1","type":"clear_queue"}',
      '{"id":"req-2","type":"prompt","message":"long work"}',
      '{"id":"req-3","type":"abort_bash"}',
      '{"id":"req-4","type":"abort"}',
    ].join("\n") + "\n",
    encoding: "utf8",
  });
  assert.equal(child.status, 0, child.stderr);
  const records = child.stdout.split("\n").filter(Boolean).map((line) => JSON.parse(line));
  // every stop RPC got its pi-shaped response
  assert.deepEqual(records.filter((r) => r.type === "response").map((r) => r.id), ["req-1", "req-2", "req-3", "req-4"]);
  // the abort settled the hanging run (aborted flag) — the run never settles on its own in hang mode
  const settled = records.filter((r) => r.type === "agent_settled");
  assert.equal(settled.length, 1);
  assert.equal(settled[0].data.aborted, true);
  const session = readFileSync(join(sessionDir, "r.jsonl"), "utf8");
  assert.match(session, /"aborted":true/);
});

/** Minimal fake of the Runtime API surface the Bridge touches during a stop. */
function fakeRuntimeApi(stopReason) {
  const cancelledBodies = [];
  const completedBodies = [];
  let stopPolls = 0;
  const server = createServer((request, response) => {
    const respond = (status, body = { persisted: true }) => {
      response.writeHead(status, { "content-type": "application/json" });
      response.end(JSON.stringify(body));
    };
    let raw = "";
    request.on("data", (chunk) => { raw += chunk; });
    request.on("end", () => {
      if (request.method === "GET" && request.url.endsWith("/input")) {
        respond(200, {});
        return;
      }
      if (request.method === "GET" && request.url.endsWith("/stop-request")) {
        stopPolls += 1;
        const requested = stopPolls > 2; // requested only after a couple of 2 s polls
        respond(200, { requested, reason: requested ? stopReason : null });
        return;
      }
      if (request.method === "POST" && request.url.endsWith("/cancelled")) {
        cancelledBodies.push(JSON.parse(raw));
        respond(200);
        return;
      }
      if (request.method === "POST" && request.url.endsWith("/completed")) {
        completedBodies.push(JSON.parse(raw));
        respond(200);
        return;
      }
      respond(200); // events, heartbeat
    });
  });
  return { server, cancelledBodies, completedBodies };
}

test("bridge end-to-end stop: signal observed → clear_queue/abort settle → terminal cancelled delivered", async () => {
  const runId = "stop-e2e-run";
  const workspace = tempDir("ws-e2e-");
  const sessionDir = tempDir("session-e2e-");
  const logsDir = tempDir("logs-e2e-");
  const spoolDir = tempDir("spool-e2e-");
  const tokenFile = join(tempDir("tok-"), "callback-token");
  writeFileSync(tokenFile, "per-run-callback-token");

  const fake = fakeRuntimeApi("stop_requested");
  await new Promise((resolve) => fake.server.listen(0, "127.0.0.1", resolve));
  const port = fake.server.address().port;

  const bridge = spawn(process.execPath, [
    fileURLToPath(new URL("./bridge.mjs", import.meta.url)),
  ], {
    env: {
      ...process.env,
      PI_RUN_ID: runId,
      PI_CALLBACK_BASE_URL: `http://127.0.0.1:${port}`,
      PI_CALLBACK_TOKEN_FILE: tokenFile,
      PI_WORKSPACE: workspace,
      PI_SESSION_DIR: sessionDir,
      PI_LOGS_DIR: logsDir,
      PI_SPOOL_DIR: spoolDir,
      PI_AGENT_BIN: process.execPath,
      PI_AGENT_ARGS: AGENT,
      STUB_BEHAVIOR: "hang",
      PI_HEARTBEAT_SECONDS: "300",
      PI_EVENT_CADENCE_SECONDS: "1",
    },
    stdio: ["ignore", "pipe", "pipe"],
  });
  let bridgeStderr = "";
  bridge.stderr.on("data", (chunk) => { bridgeStderr += chunk; });

  const exitCode = await Promise.race([
    new Promise((resolve) => bridge.on("exit", (code) => resolve(code))),
    new Promise((_, reject) => setTimeout(() => reject(new Error(`bridge did not exit; stderr: ${bridgeStderr}`)), 30_000).unref()),
  ]);
  fake.server.close();

  assert.equal(exitCode, 0, bridgeStderr);
  // the terminal is `cancelled` exactly once — never `completed`
  assert.equal(fake.cancelledBodies.length, 1);
  assert.deepEqual(fake.completedBodies, []);
  const cancelled = fake.cancelledBodies[0];
  assert.equal(cancelled.eventId, `evt-${runId}-cancelled`);
  assert.equal(cancelled.reason, "stop_requested");
  assert.match(cancelled.sessionFilePath ?? "", /\.jsonl$/); // the discovered session path rides along
  // the abort settled the agent: the session history carries it
  const session = readFileSync(join(sessionDir, `${runId}.jsonl`), "utf8");
  assert.match(session, /"aborted":true/);
  // the bridge recorded its terminal marker
  assert.ok(existsSync(join(spoolDir, ".terminal-delivered")));
  rmSync(workspace, { recursive: true, force: true });
  rmSync(sessionDir, { recursive: true, force: true });
  rmSync(logsDir, { recursive: true, force: true });
  rmSync(spoolDir, { recursive: true, force: true });
  rmSync(dirname(tokenFile), { recursive: true, force: true });
});

// ---- ticket 13: permission_blocked / required_permission_denied (decision 69) ----

test("EventMapper maps permission_denied to permission_blocked and collects the denial", () => {
  const emitted = [];
  const mapper = new EventMapper((entry) => emitted.push(entry.body), 5);
  for (const line of [
    JSON.stringify({ type: "agent_start", data: { sessionId: "s" } }),
    JSON.stringify({ type: "permission_denied", data: { toolCallId: "call-1", toolName: "stub_publish", reason: "not allow-listed" } }),
    JSON.stringify({ type: "agent_settled", data: {} }),
  ]) mapper.onLine(line);

  const blocked = emitted.find((e) => e.type === "permission_blocked");
  assert.ok(blocked, "permission_blocked RunEvent emitted");
  assert.deepEqual(blocked.payload, { toolCallId: "call-1", toolName: "stub_publish", reason: "not allow-listed" });
  assert.equal(mapper.settled, true);
  assert.deepEqual([...mapper.deniedToolCallIds], ["call-1"]);
});

test("validateManifest: a required publication denied by the permission policy fails required_permission_denied", () => {
  const workspace = tempDir("ws-denied-");
  const manifest = {
    schemaVersion: 1, summary: "done", artifacts: [],
    requiredPublications: [{ toolCallId: "call-1", kind: "issue_comment", externalId: "p1" }],
  };
  const reason = validateManifest(manifest, workspace, new Set(), [], new Set(["call-1"]));
  assert.match(reason ?? "", /required_permission_denied/);
  // a non-mandated publication that was merely attempted still fails as github_publication_failed
  const transportReason = validateManifest(manifest, workspace, new Set(), [], new Set());
  assert.match(transportReason ?? "", /github_publication_failed/);
  rmSync(workspace, { recursive: true, force: true });
});

test("stub agent permission_denied behavior denies the publication tool call", () => {
  const workspace = tempDir("ws-perm-");
  const child = spawnSync(process.execPath, [AGENT, "--session-dir", tempDir("s-"),
    "--session-id", "r", "--workspace", workspace], {
    env: { ...process.env, STUB_BEHAVIOR: "permission_denied" },
    input: '{"type":"prompt","message":"publish"}\n',
    encoding: "utf8",
  });
  assert.equal(child.status, 0, child.stderr);
  const events = child.stdout.split("\n").filter(Boolean).map((line) => JSON.parse(line));
  assert.ok(events.some((e) => e.type === "permission_denied" && e.data.toolCallId === "call-1"));
  const deniedEnd = events.find((e) => e.type === "tool_execution_end" && e.data.toolCallId === "call-1");
  assert.equal(deniedEnd.data.status, "denied"); // never observed as a completed tool call
  const manifest = JSON.parse(readFileSync(join(workspace, "result.json"), "utf8"));
  assert.ok(manifest.requiredPublications.some((p) => p.toolCallId === "call-1" && p.kind === "issue_comment"));
  rmSync(workspace, { recursive: true, force: true });
});

test("stub agent never_start behavior accepts the prompt and emits nothing (startup_timeout lever)", async () => {
  const child = spawn(process.execPath, [AGENT, "--session-dir", tempDir("s-"),
    "--session-id", "r", "--workspace", tempDir("ws-ns-")], {
    env: { ...process.env, STUB_BEHAVIOR: "never_start" },
    stdio: ["pipe", "pipe", "pipe"],
  });
  child.stdin.write('{"type":"prompt","message":"work"}\n');
  // the response record means accepted — then no agent_start ever arrives
  let accepted = false;
  let sawAgentStart = false;
  const timer = setTimeout(() => child.kill("SIGKILL"), 1500);
  for await (const chunk of child.stdout) {
    for (const line of String(chunk).split("\n").filter(Boolean)) {
      const record = JSON.parse(line);
      if (record.type === "response") accepted = true;
      if (record.type === "agent_start") sawAgentStart = true;
    }
    if (accepted) break;
  }
  clearTimeout(timer);
  child.kill("SIGKILL");
  assert.equal(accepted, true);
  assert.equal(sawAgentStart, false); // the readiness fact never comes — ticket 13's sweeper judges it
});
