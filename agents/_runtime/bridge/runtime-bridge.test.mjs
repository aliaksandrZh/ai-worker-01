/**
 * Node test suite for the Runtime Bridge + stub agent. The shared bridge source lives at
 * * agents/_runtime/bridge/ (single copy, merged into every profile's pinned build snapshot — see
 * ConfigSnapshotBuilder); the stub CLI it drives stays baked in the stub profile. Runs with:
 *   node --test agents/_runtime/bridge/runtime-bridge.test.mjs
 * (no npm dependencies)
 */
import test from "node:test";
import assert from "node:assert/strict";
import { spawnSync, execFileSync } from "node:child_process";
import { existsSync, mkdtempSync, readFileSync, rmSync, writeFileSync, readdirSync } from "node:fs";
import { join } from "node:path";
import { tmpdir } from "node:os";
import { fileURLToPath } from "node:url";
import { EventMapper, isSuccessfulToolEnd, normalizeToolName } from "./events.mjs";
import { openSpool } from "./spool.mjs";
import { RpcCorrelator, promptDisposition } from "./rpc.mjs";
import { openLogs } from "./logs.mjs";
import { Trio } from "./logs.mjs";
import { completionNudgePrompt, renderPrompt } from "./prompt.mjs";
import { completedTerminal, gitFacts } from "./terminal.mjs";

// The stub CLI lives in the stub profile (only agent that bakes it); cross-reference kept explicit.
const AGENT = fileURLToPath(new URL("../../stub/stub-agent/pi-stub.mjs", import.meta.url));

function tempDir(prefix) {
  return mkdtempSync(join(tmpdir(), prefix));
}

// ---- EventMapper: pi→RunEvent mapping (the canonical stream — top-level fields,
// typed assistantMessageEvent, isError; no dual-protocol shapes remain) ----

test("EventMapper maps the canonical prompt event sequence one to one", () => {
  const emitted = [];
  const mapper = new EventMapper((entry) => emitted.push(entry.body), 5);
  for (const line of [
    JSON.stringify({ id: "1", type: "agent_start" }),
    JSON.stringify({ id: "2", type: "turn_start" }),
    JSON.stringify({ id: "3", type: "message_update",
      assistantMessageEvent: { type: "text_delta", contentIndex: 0, delta: "Analyzing " } }),
    JSON.stringify({ id: "4", type: "tool_execution_start", toolCallId: "c1", toolName: "t", args: {} }),
    JSON.stringify({ id: "5", type: "tool_execution_update", toolCallId: "c1" }),
    JSON.stringify({ id: "6", type: "tool_execution_end", toolCallId: "c1", toolName: "t", isError: false }),
    JSON.stringify({ id: "7", type: "turn_end" }),
    JSON.stringify({ id: "8", type: "agent_settled", aborted: false }),
    JSON.stringify({ id: "c1", type: "response", command: "prompt", success: true, data: { disposition: "started" } }),
  ]) mapper.onLine(line);

  assert.deepEqual(emitted.map((e) => e.type), [
    "agent_started", "turn_started", "assistant_output", "tool_started", "tool_progress", "tool_completed", "turn_completed", "agent_settled",
  ]);
  assert.equal(mapper.settled, true);
  const toolCompleted = emitted.find((e) => e.type === "tool_completed");
  assert.deepEqual(toolCompleted.payload, { toolCallId: "c1", toolName: "t", isError: false });
  assert.equal(emitted.every((e) => typeof e.eventId === "string" && e.eventId.length > 0), true);
});

test("EventMapper aggregates text deltas (no per-token events) and flushes on settle", () => {
  const emitted = [];
  const mapper = new EventMapper((entry) => emitted.push(entry.body), 5);
  for (const line of [
    JSON.stringify({ type: "message_update", assistantMessageEvent: { type: "text_delta", delta: "one " } }),
    JSON.stringify({ type: "message_update", assistantMessageEvent: { type: "text_delta", delta: "two" } }),
    JSON.stringify({ type: "agent_settled", aborted: false }),
  ]) mapper.onLine(line);
  const assistant = emitted.filter((e) => e.type === "assistant_output");
  assert.equal(assistant.length, 1); // aggregated BEFORE persistence
  assert.deepEqual(assistant[0].payload, { text: "one two" });
});

test("EventMapper: thinking deltas, untyped updates, queue updates and empty text are not events", () => {
  const emitted = [];
  const mapper = new EventMapper((entry) => emitted.push(entry.body), 5);
  for (const line of [
    // typed thinking stream events never become RunEvents
    JSON.stringify({ type: "message_update", assistantMessageEvent: { type: "thinking_delta", contentIndex: 1, delta: "hmm" } }),
    // canonical protocol: only typed text_delta is assistant output — an untyped delta is not
    JSON.stringify({ type: "message_update", delta: "legacy shape" }),
    JSON.stringify({ type: "message_update", assistantMessageEvent: { type: "text_delta", delta: "" } }),
    JSON.stringify({ type: "queue_update", queued: 1 }),
    JSON.stringify({ type: "unknown_stream_type" }),
    JSON.stringify({ type: "response", command: "prompt", success: true, id: "r1" }),
  ]) mapper.onLine(line);
  assert.equal(emitted.length, 0);
});

// ---- real pi record shapes (pi docs json.md) ----

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

test("normalizeToolName strips the server prefix; a bare name passes through", () => {
  assert.equal(normalizeToolName("mcp__github__add_issue_comment"), "add_issue_comment");
  assert.equal(normalizeToolName("mcp__communication__report_completion"), "report_completion");
  assert.equal(normalizeToolName("bash"), "bash");
});

/** Mirrors the Bridge's completion-mandate set building (only error-free completed ends count,
 * names normalized to their bare form). */
function completedToolNamesFrom(records) {
  const names = new Set();
  const mapper = new EventMapper((entry) => {
    if (entry.body.type === "tool_completed" && isSuccessfulToolEnd(entry.body.payload))
      names.add(normalizeToolName(entry.body.payload.toolName));
  }, 5);
  for (const line of records) mapper.onLine(line);
  return names;
}

test("isSuccessfulToolEnd: the canonical isError field determines the correlation set", () => {
  const names = completedToolNamesFrom([
    JSON.stringify({ type: "tool_execution_end", toolCallId: "ok", toolName: "mcp__communication__report_completion", isError: false }),
    JSON.stringify({ type: "tool_execution_end", toolCallId: "bad", toolName: "mcp__communication__report_completion", isError: true }),
  ]);
  // the server-prefixed name is normalized — the completion mandate matches the bare name
  assert.deepEqual([...names], ["report_completion"]);
  assert.equal(isSuccessfulToolEnd({ toolCallId: "ok", isError: false }), true);
  assert.equal(isSuccessfulToolEnd({ toolCallId: "bad", isError: true }), false);
  assert.equal(isSuccessfulToolEnd({ toolCallId: "x" }), true); // canonical ends carry isError; absent is not a failure
  assert.equal(isSuccessfulToolEnd(null), false);
});

// ---- rpc response correlation (pi docs rpc.md) ----

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

// ---- the completion mandate: prompt + terminal shape ----

test("completionNudgePrompt names the completion tool and the no-false-completion rule", () => {
  const message = completionNudgePrompt("report_completion");
  assert.match(message, /report_completion/);
  assert.match(message, /never\s+call it for work you did not finish/i);
});

test("renderPrompt renders only the pointers of the run input (pointers v2)", () => {
  const prompt = renderPrompt({
    owner: "acme", repo: "widgets", itemNumber: 42, prNumber: 7, planCommentId: 12345,
    command: "fix the flaky test", profile: "coder",
    agentBranch: "agent/42", defaultBranch: "main", reviewHeadSha: "abc123",
  });
  assert.match(prompt, /# Task/);
  assert.match(prompt, /- Issue: acme\/widgets#42/);
  assert.match(prompt, /- PR: #7/);
  assert.match(prompt, /- Plan comment: #12345/);
  assert.match(prompt, /fix the flaky test/);
  assert.match(prompt, /- Profile: coder/);
  assert.match(prompt, /- Agent branch: agent\/42/);
  assert.match(prompt, /- Default branch: main/);
  assert.match(prompt, /- Review head SHA: abc123/);
  // the rich sections are dead against the pointers input (ADR-0006) — none survive
  for (const dead of ["## ", "Work item body", "Conversation", "Submitted reviews", "Review threads",
    "Input artifacts", "Labels", "Title:", "Instructions"])
    assert.equal(prompt.includes(dead), false, `rich section leaked: ${dead}`);
});

test("renderPrompt skips pointers absent from the input and invents nothing", () => {
  const prompt = renderPrompt({
    owner: "acme", repo: "widgets", itemNumber: 1, command: "go", profile: "planner",
    defaultBranch: "main",
  });
  assert.match(prompt, /- Issue: acme\/widgets#1/);
  assert.match(prompt, /- Command:\n  go/);
  assert.match(prompt, /- Profile: planner/);
  assert.match(prompt, /- Default branch: main/);
  assert.equal(prompt.includes("PR:"), false);
  assert.equal(prompt.includes("Plan comment"), false);
  assert.equal(prompt.includes("Agent branch"), false);
  assert.equal(prompt.includes("Review head"), false);
});

test("completedTerminal carries summary, git facts and session path — never artifacts or outcome", () => {
  const workspace = tempDir("ws-");
  execFileSync("git", ["init", "-q", "-b", "agent/pipe-stub", workspace]);
  execFileSync("git", ["-C", workspace, "-c", "user.name=t", "-c", "user.email=t@t", "commit", "--allow-empty", "-q", "-m", "k"]);
  const terminal = completedTerminal("sess-run-1", "/session/run-1.jsonl", "s", gitFacts(workspace));
  assert.equal(terminal.route, "completed");
  assert.equal(terminal.id, "evt-sess-run-1-terminal");
  assert.equal(terminal.body.eventId, "evt-sess-run-1-terminal");
  assert.equal(terminal.body.sessionId, "sess-run-1"); // the guard key (docs/run-session-id.md)
  assert.equal(terminal.body.sessionFilePath, "/session/run-1.jsonl");
  assert.equal(terminal.body.result.summary, "s");
  assert.equal(terminal.body.result.branch, "agent/pipe-stub");
  assert.match(terminal.body.result.commitSha, /^[0-9a-f]{40}$/);
  assert.equal("artifacts" in terminal.body.result, false); // the artifacts/ convention is dead
  assert.equal("outcome" in terminal.body, false);          // the review verdict is gone with the mandates

  // a fact-less workspace: null pointers, still a valid terminal without a session path
  const bareWorkspace = tempDir("ws-bare-");
  const bare = completedTerminal("sess-run-2", null, "run completed", gitFacts(bareWorkspace));
  assert.equal(bare.body.result.summary, "run completed");
  assert.equal(bare.body.result.branch, null);
  assert.equal(bare.body.result.commitSha, null);
  assert.equal(bare.body.sessionFilePath, undefined);
  rmSync(workspace, { recursive: true, force: true });
  rmSync(bareWorkspace, { recursive: true, force: true });
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

// ---- log trio with the shared 50 MiB budget ----

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
  const records = child.stdout.split("\n").filter(Boolean).map((line) => JSON.parse(line));
  assert.deepEqual(records.map((record) => record.type), [
    "response", // the prompt acceptance disposition (pi-shape response record)
    "agent_start", "turn_start", "message_update",
    "tool_execution_start", "tool_execution_end", // bash — the ordinary work
    "tool_execution_start", "tool_execution_end", // mcp__github__get_file_contents — the context read
    "tool_execution_start", "tool_execution_update", "tool_execution_end", // save_plan (with the progress record)
    "tool_execution_start", "tool_execution_end", // submit_pr_description
    "tool_execution_start", "tool_execution_end", // report_completion
    "turn_end", "agent_settled",
  ]);
  const accepted = records[0];
  assert.equal(accepted.type, "response");
  assert.equal(accepted.command, "prompt");
  assert.equal(accepted.success, true);
  assert.deepEqual(accepted.data, { disposition: "started" });
  // canonical protocol: stream records carry their fields top-level — no `data` nesting
  const streamRecords = records.filter((record) => record.type !== "response");
  assert.equal(streamRecords.every((record) => !("data" in record)), true);
  const delta = records.find((record) => record.type === "message_update");
  assert.deepEqual(delta.assistantMessageEvent, { type: "text_delta", contentIndex: 0,
    delta: "Analyzing the received prompt and publishing the result (stub)." });
  // the communication tools of the simulated run (docs/flows/tools.md), normalized names,
  // error-free ends carrying pi's `isError` (never the stub's old `status`)
  const toolCalls = records.filter((record) => record.type === "tool_execution_start");
  assert.deepEqual(toolCalls.map((record) => record.toolName), [
    "bash",
    "mcp__github__get_file_contents",
    "mcp__communication__save_plan",
    "mcp__communication__submit_pr_description",
    "mcp__communication__report_completion",
  ]);
  assert.deepEqual(toolCalls[4].args, {}); // report_completion is parameterless
  const ends = records.filter((record) => record.type === "tool_execution_end");
  assert.equal(ends.every((record) => record.isError === false && !("status" in record)), true);
  assert.equal(existsSync(join(workspace, "result.json")), false); // no manifest — ever
  assert.ok(existsSync(join(workspace, "artifacts", "summary.md"))); // the artifacts/ convention
  assert.ok(existsSync(join(sessionDir, "run-1.jsonl"))); // session history on the pinned path
  rmSync(workspace, { recursive: true, force: true });
  rmSync(sessionDir, { recursive: true, force: true });
});

test("EventMapper over the stub succeed stream: the canonical records map without dual-shape help", () => {
  const workspace = tempDir("ws-stub-mapper-");
  const child = spawnSync(process.execPath, [AGENT, "--session-dir", tempDir("s-mapper-"),
    "--session-id", "run-1"], {
    env: { ...process.env, STUB_BEHAVIOR: "succeed", PI_WORKSPACE: workspace },
    input: '{"type":"prompt","message":"do a thing"}\n',
    encoding: "utf8",
  });
  assert.equal(child.status, 0, child.stderr);
  const emitted = [];
  const mapper = new EventMapper((entry) => emitted.push(entry.body), 5);
  for (const line of child.stdout.split("\n").filter(Boolean)) mapper.onLine(line);
  assert.equal(mapper.settled, true);
  assert.deepEqual(emitted.map((e) => e.type), [
    "agent_started", "turn_started", "assistant_output",
    "tool_started", "tool_completed", // bash
    "tool_started", "tool_completed", // mcp__github__get_file_contents
    "tool_started", "tool_progress", "tool_completed", // save_plan
    "tool_started", "tool_completed", // submit_pr_description
    "tool_started", "tool_completed", // report_completion
    "turn_completed", "agent_settled",
  ]);
  assert.deepEqual(
    emitted.find((e) => e.type === "assistant_output").payload,
    { text: "Analyzing the received prompt and publishing the result (stub)." });
  // the observed completed calls under their bare names — the completion mandate's set:
  // every successful tool call of the run, server prefix normalized
  assert.deepEqual([...observedToolNames(emitted)].sort(), [
    "bash",
    "get_file_contents",
    "report_completion",
    "save_plan",
    "submit_pr_description",
  ]);
  rmSync(workspace, { recursive: true, force: true });
});

/** The Bridge's completion-mandate set built from the mapper's emissions. */
function observedToolNames(emitted) {
  const names = new Set();
  for (const body of emitted)
    if (body.type === "tool_completed" && isSuccessfulToolEnd(body.payload))
      names.add(normalizeToolName(body.payload.toolName));
  return names;
}

test("stub no_completion behavior: the work settles WITHOUT report_completion (the nudge lever)", () => {
  const workspace = tempDir("ws-nocomp-");
  const child = spawnSync(process.execPath, [AGENT, "--session-dir", tempDir("s-nocomp-"),
    "--session-id", "run-1"], {
    env: { ...process.env, STUB_BEHAVIOR: "no_completion", PI_WORKSPACE: workspace },
    input: '{"type":"prompt","message":"do a thing"}\n',
    encoding: "utf8",
  });
  assert.equal(child.status, 0, child.stderr);
  const records = child.stdout.split("\n").filter(Boolean).map((line) => JSON.parse(line));
  // the work happened (save_plan observed), the run settled — but the completion call never came
  assert.deepEqual(records.filter((record) => record.type === "tool_execution_start")
    .map((record) => record.toolName), [
    "bash",
    "mcp__github__get_file_contents",
    "mcp__communication__save_plan",
  ]);
  assert.equal(records.some((record) => record.toolName === "mcp__communication__report_completion"), false);
  assert.ok(records.some((record) => record.type === "agent_settled"));
  assert.ok(existsSync(join(workspace, "artifacts", "summary.md"))); // the work itself is done
  rmSync(workspace, { recursive: true, force: true });
});

test("stub escalate behavior: report_blocker, then the mandatory report_completion", () => {
  const child = spawnSync(process.execPath, [AGENT, "--session-dir", tempDir("s-esc-"),
    "--session-id", "run-1"], {
    env: { ...process.env, STUB_BEHAVIOR: "escalate", PI_WORKSPACE: tempDir("ws-esc-") },
    input: '{"type":"prompt","message":"do a thing"}\n',
    encoding: "utf8",
  });
  assert.equal(child.status, 0, child.stderr);
  const records = child.stdout.split("\n").filter(Boolean).map((line) => JSON.parse(line));
  assert.deepEqual(records.filter((record) => record.type === "tool_execution_start")
    .map((record) => record.toolName), [
    "bash",
    "mcp__github__get_file_contents",
    "mcp__communication__save_plan",
    "mcp__communication__report_blocker",
    "mcp__communication__report_completion",
  ]);
  // the escalation carries cause and options; every end stays error-free
  const blocker = records.find((record) => record.type === "tool_execution_start"
    && record.toolName === "mcp__communication__report_blocker");
  assert.deepEqual(Object.keys(blocker.args).sort(), ["cause", "description", "options"]);
  assert.equal(records.filter((record) => record.type === "tool_execution_end")
    .every((record) => record.isError === false), true);
});

test("stub bad_payload behavior: the isError end is observed but never a completed call", () => {
  const child = spawnSync(process.execPath, [AGENT, "--session-dir", tempDir("s-bad-"),
    "--session-id", "run-1"], {
    env: { ...process.env, STUB_BEHAVIOR: "bad_payload", PI_WORKSPACE: tempDir("ws-bad-") },
    input: '{"type":"prompt","message":"do a thing"}\n',
    encoding: "utf8",
  });
  assert.equal(child.status, 0, child.stderr);
  const emitted = [];
  const mapper = new EventMapper((entry) => emitted.push(entry.body), 5);
  for (const line of child.stdout.split("\n").filter(Boolean)) mapper.onLine(line);
  // the invalid save_plan ended with isError — the Bridge maps it as a failed tool execution...
  const badEnd = emitted.find((e) => e.type === "tool_completed"
    && e.payload.toolName === "mcp__communication__save_plan");
  assert.equal(badEnd.payload.isError, true);
  // ...but it joins no correlation set: only the corrected retry (and the completion) count
  assert.deepEqual([...observedToolNames(emitted)].sort(), [
    "bash",
    "get_file_contents",
    "report_completion",
    "save_plan", // the retry with the valid payload
    "submit_pr_description",
  ]);
  assert.equal(isSuccessfulToolEnd(badEnd.payload), false); // no `tool` route event from it (ticket 06)
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
  assert.equal(state.data.sessionFile, join(sessionDir, "handshake-1.jsonl")); // discovery
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
  assert.equal(errorLine.code, "stub_forced_error"); // canonical top-level error fields
  assert.equal(existsSync(join(workspace, "result.json")), false); // no manifest on the failure path
  assert.equal(existsSync(join(workspace, "artifacts")), false); // no artifacts either
  rmSync(workspace, { recursive: true, force: true });
});

// ---- graceful stop ----

import { cancelledTerminal, listChildPids } from "./stop.mjs";
import { createServer } from "node:http";
import { spawn } from "node:child_process";
import { dirname } from "node:path";

test("cancelledTerminal: deterministic id, reason and session path carried", () => {
  const entry = cancelledTerminal("sess-run-7", "stop_requested", "/session/2026-11-21T15-31-18-187Z_run-7.jsonl");
  assert.equal(entry.id, "evt-sess-run-7-cancelled");
  assert.equal(entry.route, "cancelled");
  assert.equal(entry.body.eventId, "evt-sess-run-7-cancelled");
  assert.equal(entry.body.sessionId, "sess-run-7"); // the guard key (docs/run-session-id.md)
  assert.equal(entry.body.reason, "stop_requested");
  assert.equal(entry.body.sessionFilePath, "/session/2026-11-21T15-31-18-187Z_run-7.jsonl");
  // the session path is absent when the handshake never discovered one
  const bare = cancelledTerminal("sess-run-8", null);
  assert.equal(bare.body.sessionFilePath, undefined);
  assert.equal(bare.body.reason, undefined);
});

test("listChildPids: detects children via /proc, degrades to empty without it", async () => {
  // the /proc verification works on Linux hosts with /proc; macOS/other hosts degrade to []
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
  assert.equal(settled[0].aborted, true); // canonical top-level field
  const session = readFileSync(join(sessionDir, "r.jsonl"), "utf8");
  assert.match(session, /"aborted":true/);
});

/** Minimal fake of the events endpoint surface the Bridge touches (docs/run-worker.md). */
function fakeRuntimeApi() {
  const cancelledBodies = [];
  const completedBodies = [];
  const failedBodies = [];
  const toolBodies = []; // route `tool` — the communication-tool callbacks (ticket 06)
  const eventBodies = []; // route `events` — the opaque progress stream
  const otherRoutes = [];
  const server = createServer((request, response) => {
    const respond = (status, body = {}) => {
      response.writeHead(status, { "content-type": "application/json" });
      response.end(JSON.stringify(body));
    };
    let raw = "";
    request.on("data", (chunk) => { raw += chunk; });
    request.on("end", () => {
      if (request.method === "POST" && request.url === "/events") {
        const parsed = JSON.parse(raw);
        if (parsed.route === "cancelled") cancelledBodies.push(parsed.body);
        else if (parsed.route === "completed") completedBodies.push(parsed.body);
        else if (parsed.route === "failed") failedBodies.push(parsed.body);
        else if (parsed.route === "tool") toolBodies.push(parsed.body);
        else if (parsed.route === "events") eventBodies.push(parsed.body);
        else otherRoutes.push(parsed.route);
        respond(202);
        return;
      }
      respond(200);
    });
  });
  return { server, cancelledBodies, completedBodies, failedBodies, toolBodies, eventBodies, otherRoutes };
}

test("bridge end-to-end stop: signal observed → clear_queue/abort settle → terminal cancelled delivered", async () => {
  const runId = "stop-e2e-run";
  const workspace = tempDir("ws-e2e-");
  const sessionDir = tempDir("session-e2e-");
  const logsDir = tempDir("logs-e2e-");
  const spoolDir = tempDir("spool-e2e-");
  const tokenFile = join(tempDir("tok-"), "callback-token");
  writeFileSync(tokenFile, "per-run-callback-token");
  writeFileSync(join(dirname(tokenFile), "input.json"), JSON.stringify({}));
  rmSync(tokenFile, { force: true }); // the token is env now; keep the dir for the input file

  const fake = fakeRuntimeApi();
  await new Promise((resolve) => fake.server.listen(0, "127.0.0.1", resolve));
  const port = fake.server.address().port;

  const bridge = spawn(process.execPath, [
    fileURLToPath(new URL("./bridge.mjs", import.meta.url)),
  ], {
    env: {
      ...process.env,
      PI_RUN_ID: runId,
      PI_SESSION_ID: `sess-${runId}`,
      PI_CALLBACK_BASE_URL: `http://127.0.0.1:${port}`,
      PI_RUN_TOKEN: "per-run-callback-token",
      PI_INPUT_FILE: join(dirname(tokenFile), "input.json"),
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

  // the Run Worker stops the container on run_stop_requested: docker stop → SIGTERM to the bridge
  const stopTimer = setTimeout(() => bridge.kill("SIGTERM"), 5_000);

  const exitCode = await Promise.race([
    new Promise((resolve) => bridge.on("exit", (code) => resolve(code))),
    new Promise((_, reject) => setTimeout(() => reject(new Error(`bridge did not exit; stderr: ${bridgeStderr}`)), 30_000).unref()),
  ]);
  clearTimeout(stopTimer);
  fake.server.close();

  assert.equal(exitCode, 0, bridgeStderr);
  // the terminal is `cancelled` exactly once — never `completed`
  assert.equal(fake.cancelledBodies.length, 1);
  assert.deepEqual(fake.completedBodies, []);
  const cancelled = fake.cancelledBodies[0];
  assert.equal(cancelled.eventId, `evt-sess-${runId}-cancelled`);
  assert.equal(cancelled.sessionId, `sess-${runId}`); // the terminal is instance-scoped
  assert.equal(cancelled.reason, "container stopped");
  assert.match(cancelled.sessionFilePath ?? "", /\.jsonl$/); // the discovered session path rides along
  // the abort settled the agent: the session history carries it — under the session id
  // (docs/run-session-id.md): the pi session file is named by `--session-id`
  const session = readFileSync(join(sessionDir, `sess-${runId}.jsonl`), "utf8");
  assert.match(session, /"aborted":true/);
  // the bridge recorded its terminal marker
  assert.ok(existsSync(join(spoolDir, ".terminal-delivered")));
  rmSync(workspace, { recursive: true, force: true });
  rmSync(sessionDir, { recursive: true, force: true });
  rmSync(logsDir, { recursive: true, force: true });
  rmSync(spoolDir, { recursive: true, force: true });
  rmSync(dirname(tokenFile), { recursive: true, force: true });
});

// ---- the communication-tool wire (ticket 06): route `tool` callbacks ----

/** Spawns the real bridge against the real stub with the standard e2e env. The profile manifest
 * baked into dirs.profileDir carries the route-`events` forward allow-list (empty by default —
 * the bridge forwards nothing on route `events` unless the manifest lists the types) and the
 * `tools` array that gates the route-`tool` callbacks. */
function spawnE2eBridge({ runId, dirs, port, behavior, forwardEvents = [],
                          tools = ["save_plan", "submit_pr_description", "report_completion"],
                          manifest, extraEnv = {} }) {
  writeFileSync(join(dirs.profileDir, "agent.json"),
    JSON.stringify(manifest ?? { name: "stub", forward_progress_events: forwardEvents, tools }));
  return spawn(process.execPath, [fileURLToPath(new URL("./bridge.mjs", import.meta.url))], {
    env: {
      ...process.env,
      PI_RUN_ID: runId,
      PI_SESSION_ID: `sess-${runId}`,
      PI_CALLBACK_BASE_URL: `http://127.0.0.1:${port}`,
      PI_RUN_TOKEN: "per-run-callback-token",
      PI_INPUT_FILE: join(dirname(dirs.tokenFile), "input.json"),
      PI_CODING_AGENT_DIR: dirs.profileDir,
      PI_WORKSPACE: dirs.workspace,
      PI_SESSION_DIR: dirs.sessionDir,
      PI_LOGS_DIR: dirs.logsDir,
      PI_SPOOL_DIR: dirs.spoolDir,
      PI_AGENT_BIN: process.execPath,
      PI_AGENT_ARGS: AGENT,
      STUB_BEHAVIOR: behavior,
      PI_HEARTBEAT_SECONDS: "300",
      PI_EVENT_CADENCE_SECONDS: "1",
      ...extraEnv,
    },
    stdio: ["ignore", "pipe", "pipe"],
  });
}

/** Waits for the bridge process to exit, surfacing its stderr on the timeout. */
async function bridgeExit(bridge, ms = 30_000) {
  let stderr = "";
  bridge.stderr.on("data", (chunk) => { stderr += chunk; });
  return Promise.race([
    new Promise((resolve) => bridge.on("exit", (code) => resolve(code))),
    new Promise((_, reject) => setTimeout(() => reject(new Error(`bridge did not exit; stderr: ${stderr}`)), ms).unref()),
  ]);
}

async function listen(server) {
  await new Promise((resolve) => server.listen(0, "127.0.0.1", resolve));
  return server.address().port;
}

/** The `succeed` run's deterministic call plan: the ordinary work first, then the manifest tools —
 * the completion mandate call (call-5) is the settle signal, never a `tool` callback. */
function expectedToolRoute(runId) {
  return [
    { eventId: `${runId}-tool-call-3`, toolName: "save_plan", args: {
      goals: "implement the pipe",
      scope: "the runtime bridge",
      // tools.md save_plan signature: acceptanceCriteria is the criteria text (scalar), steps[] the only list
      acceptanceCriteria: "bridge delivers the tool callbacks",
      steps: ["implement", "test"],
    } },
    { eventId: `${runId}-tool-call-4`, toolName: "submit_pr_description", args: {
      title: "stub: the simulated run result",
      body: "The stub agent completed the simulated run; the Orchestrator creates the PR.",
    } },
  ];
}

test("bridge end-to-end tool route: each completed communication call becomes one `tool` callback with the deterministic eventId", async () => {
  const runId = "tool-route-e2e-run";
  const dirs = e2eDirs();
  const fake = fakeRuntimeApi();
  const port = await listen(fake.server);

  const exitCode = await bridgeExit(spawnE2eBridge({ runId, dirs, port, behavior: "succeed" }));
  fake.server.close();

  assert.equal(exitCode, 0);
  assert.deepEqual(fake.failedBodies, []);
  assert.equal(fake.completedBodies.length, 1); // the run still settles completed
  // exactly one `tool` callback per manifest-listed tool EXCEPT the completion mandate — the observed
  // report_completion is the settle signal, rendered as the terminal `completed` callback only
  // (eventId <runId>-tool-<callId>, args intact)
  const received = fake.toolBodies.map(({ eventId, occurredAt, toolName, args }) =>
    ({ eventId, occurredAt, toolName, args }));
  assert.equal(received.length, 2);
  assert.deepEqual(received.map((r) => r.toolName), ["save_plan", "submit_pr_description"]);
  for (const [i, expected] of expectedToolRoute(runId).entries()) {
    assert.equal(received[i].eventId, expected.eventId);
    assert.equal(received[i].toolName, expected.toolName);
    assert.deepEqual(received[i].args, expected.args);
    assert.equal(typeof received[i].occurredAt, "string");
  }
  e2eCleanup(dirs);
});

test("bridge end-to-end tool route: an isError-ended call produces NO `tool` callback — its corrected retry does", async () => {
  const runId = "bad-payload-tool-route-run";
  const dirs = e2eDirs();
  const fake = fakeRuntimeApi();
  const port = await listen(fake.server);

  const exitCode = await bridgeExit(spawnE2eBridge({ runId, dirs, port, behavior: "bad_payload" }));
  fake.server.close();

  assert.equal(exitCode, 0);
  assert.equal(fake.completedBodies.length, 1); // the retry confirms the mandates — the run settles
  // call-3 (the invalid save_plan, isError end) is absent; call-4/5 are the corrected save_plan
  // and submit_pr_description — call-6 (report_completion) is the settle mandate, never a `tool` callback
  assert.deepEqual(fake.toolBodies.map((b) => b.eventId), [
    `${runId}-tool-call-4`,
    `${runId}-tool-call-5`,
  ]);
  assert.equal(fake.toolBodies[0].toolName, "save_plan"); // the corrected retry, with the valid args
  assert.deepEqual(Object.keys(fake.toolBodies[0].args).sort(),
    ["acceptanceCriteria", "goals", "scope", "steps"]);
  e2eCleanup(dirs);
});

test("bridge end-to-end tool route: ordinary tool calls stay on route `events` and never get a `tool` callback", async () => {
  const runId = "ordinary-tools-tool-route-run";
  const dirs = e2eDirs();
  const fake = fakeRuntimeApi();
  const port = await listen(fake.server);

  const exitCode = await bridgeExit(spawnE2eBridge({
    runId, dirs, port, behavior: "succeed",
    forwardEvents: ["tool_started", "tool_completed"], // the opaque progress echo needs these two
  }));
  fake.server.close();

  assert.equal(exitCode, 0);
  // bash and the context MCP tool flow as opaque `events` tool_started/tool_completed as today
  const started = fake.eventBodies.filter((b) => b.type === "tool_started").map((b) => b.payload.toolName);
  const completed = fake.eventBodies.filter((b) => b.type === "tool_completed").map((b) => b.payload.toolName);
  assert.ok(started.includes("bash"));
  assert.ok(started.includes("mcp__github__get_file_contents"));
  assert.ok(completed.includes("bash"));
  assert.ok(completed.includes("mcp__github__get_file_contents"));
  // the tool route carries ONLY the manifest-listed tools — never the ordinary calls,
  // never the completion mandate (the settle signal renders as the terminal `completed` callback)
  assert.deepEqual(fake.toolBodies.map((b) => b.toolName).sort(),
    ["save_plan", "submit_pr_description"]);
  assert.equal(fake.toolBodies.some((b) => b.toolName === "bash"
    || b.toolName === "get_file_contents"), false);
  e2eCleanup(dirs);
});

test("bridge end-to-end tool route: the manifest `tools` array gates the callbacks — a listed tool's call produces one, an unlisted tool's call does not", async () => {
  const runId = "manifest-tools-gate-run";
  const dirs = e2eDirs();
  const fake = fakeRuntimeApi();
  const port = await listen(fake.server);

  const exitCode = await bridgeExit(spawnE2eBridge({
    runId, dirs, port, behavior: "succeed",
    tools: ["save_plan", "report_completion"], // submit_pr_description deliberately unlisted
  }));
  fake.server.close();

  assert.equal(exitCode, 0);
  assert.equal(fake.completedBodies.length, 1); // the completion mandate is observed regardless of the gate
  // only the listed tool produced the `tool` callback; the successful submit_pr_description call
  // of the same run produced none — the manifest's `tools` array is the single source
  assert.deepEqual(fake.toolBodies.map((b) => b.toolName), ["save_plan"]);
  assert.equal(fake.toolBodies[0].eventId, `${runId}-tool-call-3`);
  e2eCleanup(dirs);
});

test("bridge end-to-end tool route: a manifest-LISTED context tool's call stays off route `tool` — the catalog classifies, the manifest only gates availability", async () => {
  const runId = "context-tool-listed-tool-route-run";
  const dirs = e2eDirs();
  const fake = fakeRuntimeApi();
  const port = await listen(fake.server);

  const exitCode = await bridgeExit(spawnE2eBridge({
    runId, dirs, port, behavior: "succeed",
    // get_file_contents is the stub's ordinary context call (mcp__github__...) — listed here,
    // yet it is NOT a communication tool of agents/_runtime/tools.json: the pre-registry bug
    // routed it and failed the Run downstream as 'unknown communication tool'
    tools: ["get_file_contents", "save_plan", "submit_pr_description", "report_completion"],
  }));
  fake.server.close();

  assert.equal(exitCode, 0);
  assert.deepEqual(fake.failedBodies, []);
  assert.equal(fake.completedBodies.length, 1); // the run still settles completed
  // only the communication calls routed; the listed context call stayed on route `events`
  assert.deepEqual(fake.toolBodies.map((b) => b.toolName), ["save_plan", "submit_pr_description"]);
  e2eCleanup(dirs);
});

test("bridge restart mid-run: the spool redelivers `tool` callbacks verbatim and the re-run repeats the same deterministic eventIds", async () => {
  const runId = "restart-tool-route-run";
  const dirs = e2eDirs();

  // lifetime 1: hold the first `tool` POST mid-flight — the sender stalls on it while the run
  // keeps spooling; after a grace wait (the one-shot stub run is over) the container dies
  // (SIGKILL — the docker-restart analog) with the spooled-but-undelivered lifetime behind
  let releaseHold = () => {};
  const firstToolBody = new Promise((resolve) => { releaseHold = resolve; });
  const gate = createServer((request, response) => {
    let raw = "";
    request.on("data", (chunk) => { raw += chunk; });
    request.on("end", () => {
      const parsed = JSON.parse(raw);
      if (parsed.route === "tool") {
        releaseHold(parsed.body); // observed; the response never comes — the entry stays spooled
        releaseHold = () => {};
        return;
      }
      response.writeHead(202, { "content-type": "application/json" });
      response.end("{}");
    });
  });
  const port1 = await listen(gate);

  const bridge1 = spawnE2eBridge({ runId, dirs, port: port1, behavior: "succeed" });
  const heldBody = await Promise.race([
    firstToolBody,
    new Promise((_, reject) => setTimeout(() => reject(new Error("no tool callback arrived")), 20_000).unref()),
  ]);
  assert.equal(heldBody.eventId, `${runId}-tool-call-3`); // the first manifest-tool call of the run
  assert.equal(heldBody.toolName, "save_plan");
  await new Promise((resolve) => setTimeout(resolve, 1_500)); // grace: the stub run finishes spooling
  bridge1.kill("SIGKILL");
  await new Promise((resolve) => bridge1.on("exit", resolve));
  gate.close();
  releaseHold({}); // free the (dead) socket

  // lifetime 2: same spool/workspace/session — the backlog redelivers first, then the run re-executes
  const fake = fakeRuntimeApi();
  const port2 = await listen(fake.server);
  const exitCode = await bridgeExit(spawnE2eBridge({ runId, dirs, port: port2, behavior: "succeed" }));
  fake.server.close();

  assert.equal(exitCode, 0);
  assert.deepEqual(fake.failedBodies, []);
  assert.ok(fake.completedBodies.length >= 1); // a terminal (backlog's or the re-run's) is acked
  // spool replay contract: the very body that was in flight at the kill is redelivered VERBATIM
  // (same eventId, same occurredAt, same args)
  assert.deepEqual(fake.toolBodies[0], heldBody);
  // the deterministic eventId: across the redelivered backlog AND the re-run's re-observation,
  // each manifest-tool call's eventId exists exactly twice — delivered or still spool-pending;
  // dedup at ingestion (ticket 03) collapses them to one, never the Bridge
  const pendingToolBodies = readdirSync(dirs.spoolDir)
    .filter((name) => /^\d+-.*\.json$/.test(name))
    .map((name) => JSON.parse(readFileSync(join(dirs.spoolDir, name), "utf8")))
    .filter((entry) => entry.route === "tool")
    .map((entry) => entry.body);
  const ids = [...fake.toolBodies, ...pendingToolBodies].map((b) => b.eventId);
  assert.deepEqual([...new Set(ids)].sort(), expectedToolRoute(runId).map((e) => e.eventId).sort());
  for (const expected of expectedToolRoute(runId)) {
    assert.equal(ids.filter((id) => id === expected.eventId).length, 2,
      `${expected.eventId} must exist exactly twice (redelivery + re-run)`);
  }
  e2eCleanup(dirs);
});

// ---- the completion mandate (end-to-end): ONE loop, `report_completion` hardcoded ----

test("bridge end-to-end completion mandate: the observed report_completion settles the run completed with summary, git facts and session path — no artifacts, no outcome", async () => {
  const runId = "completion-e2e-run";
  const dirs = e2eDirs();
  const fake = fakeRuntimeApi();
  const port = await listen(fake.server);

  const exitCode = await bridgeExit(spawnE2eBridge({ runId, dirs, port, behavior: "succeed" }));
  fake.server.close();

  assert.equal(exitCode, 0);
  assert.deepEqual(fake.failedBodies, []);
  assert.equal(fake.completedBodies.length, 1);
  const completed = fake.completedBodies[0];
  assert.equal(completed.eventId, `evt-sess-${runId}-terminal`);
  assert.equal(completed.sessionId, `sess-${runId}`); // the terminal is instance-scoped
  assert.match(completed.sessionFilePath ?? "", /\.jsonl$/); // the discovered session path rides along
  assert.equal(typeof completed.result.summary, "string");
  assert.ok(completed.result.summary.length > 0); // the last aggregated assistant_output
  assert.equal(completed.result.branch, "agent/pipe-stub"); // the git facts: the pointer producers
  assert.match(completed.result.commitSha, /^[0-9a-f]{40}$/);
  assert.equal("artifacts" in completed.result, false); // the artifacts/ convention is dead
  assert.equal("outcome" in completed, false);          // the review verdict is gone with the mandates
  e2eCleanup(dirs);
});

test("bridge end-to-end completion mandate: a settle without report_completion is nudged once, exhausted nudges fail the run with outcome_missing", async () => {
  const runId = "completion-missing-e2e-run";
  const dirs = e2eDirs();
  const fake = fakeRuntimeApi();
  const port = await listen(fake.server);

  const exitCode = await bridgeExit(spawnE2eBridge({
    runId, dirs, port, behavior: "no_completion",
    forwardEvents: ["agent_settled"], // the nudge-loop assertion observes the settles on route `events`
    extraEnv: {
      STUB_STAY_ALIVE: "1", // answer the Bridge's follow-up prompt deterministically
      PI_COMPLETION_NUDGES: "1",
    },
  }));
  fake.server.close();

  assert.equal(exitCode, 0);
  assert.deepEqual(fake.completedBodies, []);
  assert.equal(fake.failedBodies.length, 1);
  assert.equal(fake.failedBodies[0].error.code, "outcome_missing"); // the completion call never confirmed
  // the ONE nudge loop: the first settle triggered exactly one follow-up prompt; the stub settled
  // again still without the call — the run then failed
  const settles = fake.eventBodies.filter((b) => b.type === "agent_settled");
  assert.equal(settles.length, 2);
  e2eCleanup(dirs);
});

test("bridge end-to-end completion mandate: a permission-denied report_completion fails immediately with required_permission_denied", async () => {
  const runId = "completion-denied-e2e-run";
  const dirs = e2eDirs();
  const fake = fakeRuntimeApi();
  const port = await listen(fake.server);

  const exitCode = await bridgeExit(spawnE2eBridge({ runId, dirs, port, behavior: "permission_denied",
    forwardEvents: ["agent_settled"] }));
  fake.server.close();

  assert.equal(exitCode, 0);
  assert.deepEqual(fake.completedBodies, []);
  assert.equal(fake.failedBodies.length, 1);
  assert.equal(fake.failedBodies[0].error.code, "required_permission_denied");
  assert.match(fake.failedBodies[0].error.message, /report_completion/);
  // a denial cannot be nudged away — no follow-up prompt ever restarted the turn
  assert.equal(fake.eventBodies.filter((b) => b.type === "agent_settled").length, 1);
  e2eCleanup(dirs);
});

// ---- the route-`events` forward allow-list (the profile manifest's forward_progress_events) ----

test("bridge end-to-end forward_progress_events: only the listed types reach route `events`, the rest never leaves the bridge", async () => {
  const runId = "forward-events-run";
  const dirs = e2eDirs();
  const fake = fakeRuntimeApi();
  const port = await listen(fake.server);

  const exitCode = await bridgeExit(spawnE2eBridge({ runId, dirs, port, behavior: "succeed",
    forwardEvents: ["tool_started", "agent_settled"] }));
  fake.server.close();

  assert.equal(exitCode, 0);
  assert.deepEqual(fake.failedBodies, []);
  assert.equal(fake.completedBodies.length, 1); // the run still settles completed
  // route `events` carries ONLY the listed types — agent_started, turn_started, turn_completed,
  // tool_progress, tool_completed, assistant_output are dropped before the spool
  assert.deepEqual([...new Set(fake.eventBodies.map((b) => b.type))].sort(),
    ["agent_settled", "tool_started"]);
  // the mapper kept running: the aggregate `tool` route and the terminal are unaffected
  assert.deepEqual(fake.toolBodies.map((b) => b.toolName).sort(), ["save_plan", "submit_pr_description"]);
  e2eCleanup(dirs);
});

test("bridge end-to-end forward_progress_events: an empty list forwards NOTHING on route `events` — routes `tool`/`completed` always pass", async () => {
  const runId = "forward-events-empty-run";
  const dirs = e2eDirs();
  const fake = fakeRuntimeApi();
  const port = await listen(fake.server);

  const exitCode = await bridgeExit(spawnE2eBridge({ runId, dirs, port, behavior: "succeed",
    forwardEvents: [] }));
  fake.server.close();

  assert.equal(exitCode, 0);
  assert.deepEqual(fake.failedBodies, []);
  assert.deepEqual(fake.eventBodies, []); // nothing on route `events`
  assert.deepEqual(fake.toolBodies.map((b) => b.toolName).sort(), ["save_plan", "submit_pr_description"]);
  assert.equal(fake.completedBodies.length, 1); // the terminal is out of scope for the filter
  e2eCleanup(dirs);
});

test("bridge end-to-end forward_progress_events: a missing or non-array field forwards nothing on route `events`", async () => {
  const runId = "forward-events-invalid-run";
  const dirs = e2eDirs();
  const fake = fakeRuntimeApi();
  const port = await listen(fake.server);

  // a non-array field is tolerated as empty (an absent field behaves the same)
  const exitCode = await bridgeExit(spawnE2eBridge({ runId, dirs, port, behavior: "succeed",
    manifest: { name: "stub", forward_progress_events: "tool_started" } }));
  fake.server.close();

  assert.equal(exitCode, 0);
  assert.deepEqual(fake.eventBodies, []);
  assert.equal(fake.completedBodies.length, 1);
  e2eCleanup(dirs);
});

test("bridge end-to-end forward_progress_events: an unlisted entry redelivered from an earlier lifetime is dropped at the send boundary", async () => {
  const runId = "forward-events-redelivery-run";
  const dirs = e2eDirs();
  // a stale spool from a previous lifetime: one listed, one unlisted route-`events` entry
  writeFileSync(join(dirs.spoolDir, "000001-stale-unlisted.json"), JSON.stringify({
    id: "stale-unlisted", route: "events",
    body: { eventId: "stale-unlisted", type: "agent_started", occurredAt: "t0", payload: {} },
  }));
  writeFileSync(join(dirs.spoolDir, "000002-stale-listed.json"), JSON.stringify({
    id: "stale-listed", route: "events",
    body: { eventId: "stale-listed", type: "tool_started", occurredAt: "t0", payload: { toolName: "bash" } },
  }));
  const fake = fakeRuntimeApi();
  const port = await listen(fake.server);

  // the stale entries are delivered by the sender loop; the run itself fails fast (mcp_failed —
  // it never starts an agent run, so it spools no route-`events` entries of its own)
  const exitCode = await bridgeExit(spawnE2eBridge({ runId, dirs, port, behavior: "mcp_failed",
    forwardEvents: ["tool_started"] }));
  fake.server.close();

  assert.equal(exitCode, 0);
  // the unlisted stale entry is NEVER sent — the listed one is
  assert.deepEqual(fake.eventBodies.map((b) => b.eventId), ["stale-listed"]);
  assert.equal(fake.eventBodies[0].type, "tool_started");
  assert.equal(fake.failedBodies.length, 1); // the terminal (out of scope) still delivered
  assert.equal(fake.failedBodies[0].error.code, "mcp_unavailable");
  e2eCleanup(dirs);
});

function e2eDirs() {
  const workspace = tempDir("ws-outcome-");
  const sessionDir = tempDir("session-outcome-");
  const logsDir = tempDir("logs-outcome-");
  const spoolDir = tempDir("spool-outcome-");
  const profileDir = tempDir("profile-outcome-");
  const tokenFile = join(tempDir("tok-outcome-"), "callback-token");
  writeFileSync(tokenFile, "per-run-callback-token");
  writeFileSync(join(dirname(tokenFile), "input.json"), JSON.stringify({}));
  return { workspace, sessionDir, logsDir, spoolDir, profileDir, tokenFile };
}

function e2eCleanup(dirs) {
  for (const dir of [dirs.workspace, dirs.sessionDir, dirs.logsDir, dirs.spoolDir, dirs.profileDir, dirname(dirs.tokenFile)])
    rmSync(dir, { recursive: true, force: true });
}

// ---- /mcp status probe (mcp_unavailable) ----

test("bridge MCP probe: a failed server fails the run before the task prompt, with pi's full reason", async () => {
  const runId = "mcp-probe-run";
  const workspace = tempDir("ws-mcp-");
  const sessionDir = tempDir("session-mcp-");
  const logsDir = tempDir("logs-mcp-");
  const spoolDir = tempDir("spool-mcp-");
  const tokenFile = join(tempDir("tok-"), "callback-token");
  writeFileSync(tokenFile, "per-run-callback-token");
  writeFileSync(join(dirname(tokenFile), "input.json"), JSON.stringify({}));

  const fake = fakeRuntimeApi();
  await new Promise((resolve) => fake.server.listen(0, "127.0.0.1", resolve));
  const port = fake.server.address().port;

  const bridge = spawn(process.execPath, [
    fileURLToPath(new URL("./bridge.mjs", import.meta.url)),
  ], {
    env: {
      ...process.env,
      PI_RUN_ID: runId,
      PI_SESSION_ID: `sess-${runId}`,
      PI_CALLBACK_BASE_URL: `http://127.0.0.1:${port}`,
      PI_RUN_TOKEN: "per-run-callback-token",
      PI_INPUT_FILE: join(dirname(tokenFile), "input.json"),
      PI_WORKSPACE: workspace,
      PI_SESSION_DIR: sessionDir,
      PI_LOGS_DIR: logsDir,
      PI_SPOOL_DIR: spoolDir,
      PI_AGENT_BIN: process.execPath,
      PI_AGENT_ARGS: AGENT,
      STUB_BEHAVIOR: "mcp_failed",
      PI_HEARTBEAT_SECONDS: "300",
      PI_EVENT_CADENCE_SECONDS: "1",
    },
    stdio: ["ignore", "pipe", "pipe"],
  });
  let bridgeStderr = "";
  bridge.stderr.on("data", (chunk) => { bridgeStderr += chunk; });
  const exitCode = await Promise.race([
    new Promise((resolve) => bridge.on("exit", (code) => resolve(code))),
    new Promise((_, reject) => setTimeout(() => reject(new Error(`bridge did not exit; stderr: ${bridgeStderr}`)), 20_000).unref()),
  ]);
  fake.server.close();

  assert.equal(exitCode, 0, bridgeStderr);
  // exactly one terminal — `failed`, never `completed`
  assert.deepEqual(fake.completedBodies, []);
  assert.deepEqual(fake.cancelledBodies, []);
  assert.equal(fake.failedBodies.length, 1);
  const failed = fake.failedBodies[0];
  assert.equal(failed.error.code, "mcp_unavailable");
  // the message carries pi's full /mcp status: the state AND the stderr tail
  assert.match(failed.error.message, /pi \/mcp reports/);
  assert.match(failed.error.message, /github: failed \(direct\)/);
  assert.match(failed.error.message, /Connection closed/);
  assert.match(failed.error.message, /boom: the stub mcp server died \(exit 1\)/);
  // the task prompt was never delivered — the stub wrote no artifacts
  assert.equal(existsSync(join(workspace, "artifacts")), false);
  assert.equal(existsSync(join(spoolDir, ".terminal-delivered")), true);
  rmSync(workspace, { recursive: true, force: true });
  rmSync(sessionDir, { recursive: true, force: true });
  rmSync(logsDir, { recursive: true, force: true });
  rmSync(spoolDir, { recursive: true, force: true });
  rmSync(dirname(tokenFile), { recursive: true, force: true });
});

// ---- permission_blocked / required_permission_denied ----

test("EventMapper maps permission_denied to permission_blocked and collects the denial", () => {
  const emitted = [];
  const mapper = new EventMapper((entry) => emitted.push(entry.body), 5);
  for (const line of [
    JSON.stringify({ type: "agent_start" }),
    JSON.stringify({ type: "permission_denied", toolCallId: "call-1", toolName: "stub_publish", reason: "not allow-listed" }),
    JSON.stringify({ type: "agent_settled", aborted: false }),
  ]) mapper.onLine(line);

  const blocked = emitted.find((e) => e.type === "permission_blocked");
  assert.ok(blocked, "permission_blocked RunEvent emitted");
  assert.deepEqual(blocked.payload, { toolCallId: "call-1", toolName: "stub_publish", reason: "not allow-listed" });
  assert.equal(mapper.settled, true);
  assert.deepEqual([...mapper.deniedToolCallIds], ["call-1"]);
});

test("stub agent permission_denied behavior denies a communication tool call (canonical records)", () => {
  const workspace = tempDir("ws-perm-");
  const child = spawnSync(process.execPath, [AGENT, "--session-dir", tempDir("s-"),
    "--session-id", "r", "--workspace", workspace], {
    env: { ...process.env, STUB_BEHAVIOR: "permission_denied" },
    input: '{"type":"prompt","message":"publish"}\n',
    encoding: "utf8",
  });
  assert.equal(child.status, 0, child.stderr);
  const events = child.stdout.split("\n").filter(Boolean).map((line) => JSON.parse(line));
  // the denial hits a communication tool (report_completion) — the Bridge records the
  // `permission_blocked` RunEvent and collects the denied tool name for the completion mandate
  const denied = events.find((e) => e.type === "permission_denied");
  assert.equal(denied.toolCallId, "call-1");
  assert.equal(denied.toolName, "mcp__communication__report_completion");
  const deniedEnd = events.find((e) => e.type === "tool_execution_end" && e.toolCallId === "call-1");
  assert.equal(deniedEnd.isError, true); // never observed as a completed tool call
  assert.equal(deniedEnd.status, undefined); // the stub `status` field is gone — canonical isError only
  assert.equal(existsSync(join(workspace, "result.json")), false); // no manifest — ever
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
  assert.equal(sawAgentStart, false); // the readiness fact never comes — the sweeper judges it
});
