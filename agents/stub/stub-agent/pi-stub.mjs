#!/usr/bin/env node
/**
 * Stub agent CLI: the pi-shaped fake headless agent speaking the CANONICAL stream protocol
 * (real-pi record shapes — top-level stream fields, typed `assistantMessageEvent` text deltas,
 * `isError` on tool ends; the dual-shape `data`-nesting world is deleted with ticket 07).
 * Reads JSON objects on stdin (one per line), answers one `prompt` with the event set on stdout,
 * writes a session `.jsonl` (history) plus the artifact files into the workspace (the artifacts/
 * directory convention the Bridge collects), then exits. Behavior (env STUB_BEHAVIOR):
 * succeed (default) | no_completion | escalate | bad_payload | fail | hang
 * | never_start (accepts the prompt, then never emits anything — startup_timeout)
 * | permission_denied (a communication tool call is denied by the unattended permission
 * policy — the fail-closed path) | mcp_failed (the Bridge's /mcp status probe sees a failed
 * github server — the fail-fast path).
 *
 * The agent communicates only through the communication tools (docs/flows/tools.md): the calls
 * use the normalized server-prefixed names `mcp__communication__<name>` from the eleven-name
 * vocabulary. `report_completion` is the mandatory final call of every Run.
 *
 * The scenario lever exists only to make the failure/needs-human/hang paths testable; in the real
 * profiles these paths come from real agent work and MCP failures.
 */
import { appendFileSync, existsSync, mkdirSync, writeFileSync } from "node:fs";
import { execFileSync } from "node:child_process";
import { join } from "node:path";

const args = Object.fromEntries(process.argv.slice(2).reduce((acc, arg, i, all) => {
  if (arg.startsWith("--")) acc.push([arg.slice(2), all[i + 1]]);
  return acc;
}, []));
const sessionDir = args["session-dir"] ?? "/session";
const sessionId = args["session-id"] ?? "stub";
const workspace = args["workspace"] ?? process.env.PI_WORKSPACE ?? "/workspace";
const behavior = process.env.STUB_BEHAVIOR || argvBehavior() || "succeed";

function argvBehavior() {
  const flag = process.argv.indexOf("--behavior");
  return flag > -1 ? process.argv[flag + 1] : null;
}

// The communication vocabulary (docs/flows/tools.md) under the normalized server prefix the
// Bridge resolves to the bare name (the last `__` segment) when it observes the calls.
const comm = (name) => `mcp__communication__${name}`;
const SAVE_PLAN = comm("save_plan");
const SUBMIT_PR_DESCRIPTION = comm("submit_pr_description");
const REPORT_BLOCKER = comm("report_blocker");
const REPORT_COMPLETION = comm("report_completion");

const sessionFile = join(sessionDir, `${sessionId}.jsonl`);
mkdirSync(sessionDir, { recursive: true });

let eventSeq = 0;
let callSeq = 0; // toolCallId counter (uniqueness within the run is all the Bridge needs)
function emit(event) {
  appendFileSync(sessionFile, JSON.stringify(event) + "\n");
  process.stdout.write(JSON.stringify(event) + "\n");
}

/** Next stream event id (`e<N>`, pi json.md's per-record id field). */
function nextId() {
  eventSeq += 1;
  return `e${eventSeq}`;
}

/** pi RPC `response` record (rpc.md): echoes the command id; the Bridge correlates on it. */
function respond(id, command, data) {
  const record = { id, type: "response", command, success: true, data };
  process.stdout.write(JSON.stringify(record) + "\n");
}

/** One observed tool call: the start/end pair the Bridge maps (and, from ticket 06, routes
 * to the `tool` callback when the name is in the vocabulary and the end is error-free). */
function toolCall(toolName, args, { isError = false, progress = false } = {}) {
  const callId = `call-${++callSeq}`;
  emit({ id: nextId(), type: "tool_execution_start", toolCallId: callId, toolName,
    ...(args !== undefined ? { args } : {}) });
  if (progress)
    emit({ id: nextId(), type: "tool_execution_update", toolCallId: callId, toolName });
  emit({ id: nextId(), type: "tool_execution_end", toolCallId: callId, toolName,
    result: isError ? { error: `${toolName} rejected the payload` } : {}, isError, durationMs: 12 });
  return callId;
}

/** The canonical assistant text delta: typed `assistantMessageEvent` (only `text_delta`
 * is assistant output — thinking deltas never become RunEvents). */
function say(text) {
  emit({ id: nextId(), type: "message_update", usage: {},
    assistantMessageEvent: { type: "text_delta", contentIndex: 0, delta: text } });
}

function settleWork() {
  // agent-local git so the Bridge can genuinely derive branch/commitSha via Git; the artifacts/
  // directory is the convention the Bridge collects after the run
  if (!existsSync(join(workspace, ".git"))) {
    execFileSync("git", ["init", "-b", "agent/pipe-stub"], { cwd: workspace, stdio: "ignore" });
  }
  const artifactDir = join(workspace, "artifacts");
  mkdirSync(artifactDir, { recursive: true });
  writeFileSync(join(artifactDir, "summary.md"),
    "# Pipeline stub result\n\nThe stub agent completed the pipeline with the received prompt.\n");
  execFileSync("git", ["add", "."], { cwd: workspace, stdio: "ignore" });
  execFileSync("git", ["-c", "user.name=Stub Agent", "-c", "user.email=stub@invalid",
    // --allow-empty: a retry-reused workspace may carry no new changes — the settle still completes
    "commit", "--allow-empty", "-m", "stub agent work"], { cwd: workspace, stdio: "ignore" });
}

function answerPrompt() {
  runInFlight = true;
  if (behavior === "never_start") {
    // the startup_timeout lever: the process stays alive (heartbeats continue) but never
    // emits ANYTHING — not even agent_start — so the Run never became ready
    return;
  }
  emit({ id: nextId(), type: "agent_start" });
  if (behavior === "fail") {
    // the crash lever: a mid-run agent error — the stream carries pi's error record, the
    // process exits non-zero WITHOUT settling; the Bridge judges the crash on the exit code
    emit({ id: nextId(), type: "error", code: "stub_forced_error", message: "stub forced failure" });
    process.exit(3);
  }
  emit({ id: nextId(), type: "turn_start", message: {}, toolResults: [] });
  if (behavior === "hang") {
    // agent started, then never settles again while the process stays alive (the timeouts;
    // the stop lever — an `abort` RPC settles the run with aborted=true and exits)
    return;
  }
  if (behavior === "permission_denied") {
    // the permission_denied lever: a communication tool call is denied by the unattended
    // permission policy — the agent answers the denial itself (no dialog), the Bridge records
    // the `permission_blocked` RunEvent and the denial of a mandated call fails the run
    say("Trying to report completion (stub).");
    const deniedCallId = "call-1";
    emit({ id: nextId(), type: "tool_execution_start", toolCallId: deniedCallId,
      toolName: REPORT_COMPLETION, args: {} });
    emit({ id: nextId(), type: "permission_denied", toolCallId: deniedCallId,
      toolName: REPORT_COMPLETION, reason: "not on the profile's permission allow-list" });
    emit({ id: nextId(), type: "tool_execution_end", toolCallId: deniedCallId,
      toolName: REPORT_COMPLETION, result: { error: "denied" }, isError: true, durationMs: 0 });
    emit({ id: nextId(), type: "turn_end", message: {}, toolResults: [] });
    settleWork();
    emit({ id: nextId(), type: "agent_settled", aborted: false });
    setTimeout(() => process.exit(0), 200);
    return;
  }
  say("Analyzing the received prompt and publishing the result (stub).");
  if (behavior === "bad_payload") {
    // the bad_payload lever: an invalid save_plan payload — the tool errors, the end is
    // isError:true and must produce NO `tool` route event; the corrected retry does
    toolCall(SAVE_PLAN, { goals: "ship it" }, { isError: true }); // invalid: no scope/criteria/steps
    toolCall(SAVE_PLAN, {
      goals: "implement the pipe",
      scope: "the runtime bridge",
      acceptanceCriteria: ["bridge delivers the tool callbacks"],
      steps: ["implement", "test"],
    }, { progress: true });
  } else {
    toolCall(SAVE_PLAN, {
      goals: "implement the pipe",
      scope: "the runtime bridge",
      acceptanceCriteria: ["bridge delivers the tool callbacks"],
      steps: ["implement", "test"],
    }, { progress: true });
  }
  if (behavior === "escalate") {
    // the escalate lever: the work hit a blocker the agent cannot resolve — report_blocker
    // hands the User the cause and the options; the run still ends with report_completion
    toolCall(REPORT_BLOCKER, {
      description: "the spool delivery blocks the run",
      cause: "the app host is unreachable",
      options: ["retry later", "restart the app host"],
    });
  } else if (behavior !== "no_completion") {
    // the coder's summary carrier (publication rule — the agent never writes to GitHub)
    toolCall(SUBMIT_PR_DESCRIPTION, {
      title: "stub: the simulated run result",
      body: "The stub agent completed the simulated run; the Orchestrator creates the PR.",
    });
  }
  // the completion rule (docs/flows/tools.md): the mandatory final call of every Run;
  // no_completion deliberately skips it — the nudge scenario the Bridge must catch
  if (behavior !== "no_completion")
    toolCall(REPORT_COMPLETION, {});
  emit({ id: nextId(), type: "turn_end", message: {}, toolResults: [] });
  settleWork();
  emit({ id: nextId(), type: "agent_settled", aborted: false });
  // one-shot sessions: after settling the agent is done with this run — exit regardless
  // of stdin state (the Bridge closes stdin after settle either way). STUB_STAY_ALIVE keeps the
  // process alive instead (exit on stdin EOF) so nudge-loop tests are deterministic — the agent
  // answers the Bridge's follow-up prompts the same way.
  if (process.env.STUB_STAY_ALIVE)
    process.stdin.on("end", () => process.exit(0));
  else
    setTimeout(() => process.exit(0), 200);
}

let buffer = "";
let runInFlight = false; // a prompt is executing and has not settled yet (the `abort` lever)
process.stdin.setEncoding("utf8");
process.stdin.on("data", (chunk) => {
  buffer += chunk;
  let boundary;
  while ((boundary = buffer.indexOf("\n")) >= 0) {
    const line = buffer.slice(0, boundary).trim();
    buffer = buffer.slice(boundary + 1);
    if (!line) continue;
    let rpc;
    try {
      rpc = JSON.parse(line);
    } catch {
      process.stderr.write(`pi-stub: invalid JSON line: ${line.slice(0, 120)}\n`);
      continue;
    }
    switch (rpc.type) {
      case "prompt":
        // the /mcp probe (STUB_BEHAVIOR=mcp_failed): an extension command consumed the prompt —
        // disposition "handled", a notify with the pi status format, and NO run started
        if (behavior === "mcp_failed" && typeof rpc.message === "string" && rpc.message.startsWith("/mcp")) {
          respond(rpc.id, "prompt", { disposition: "handled" });
          emit({
            type: "extension_ui_request",
            method: "notify",
            notifyType: "info",
            message: "github: failed (direct)\n    Connection closed\n    boom: the stub mcp server died (exit 1)",
          });
          return;
        }
        // rpc-commands.md: the response means accepted — the run then streams to `agent_settled`
        respond(rpc.id, "prompt", { disposition: "started" });
        answerPrompt();
        break;
      case "get_state":
        // `sessionFile` is the actual on-disk path (discovery)
        respond(rpc.id, "get_state", {
          sessionId,
          sessionFile: sessionFile,
          isStreaming: false,
        });
        break;
      default:
        // pi-shaped RPCs the Bridge's stop sequence sends. `abort` settles an in-flight run
        // with aborted=true and exits (the aborted run writes no artifacts); stdin EOF then ends
        // the process like a normal settle.
        respond(rpc.id, rpc.type);
        if (rpc.type === "abort" && runInFlight) {
          runInFlight = false;
          emit({ id: nextId(), type: "agent_settled", aborted: true });
          setTimeout(() => process.exit(0), 100);
        }
        break;
    }
  }
});
process.stdin.on("end", () => process.exit(0));
