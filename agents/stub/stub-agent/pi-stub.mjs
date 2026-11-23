#!/usr/bin/env node
/**
 * Stub agent CLI (ticket 09): the pi-RPC-shaped fake headless agent (04 §4 protocol shape, decision 33).
 * Reads JSON objects on stdin (one per line), answers one `prompt` with the 04 §4 event set on stdout,
 * writes a session `.jsonl` (history) plus `result.json` + the manifest's artifact files into the
 * workspace, then exits. Behavior (env STUB_BEHAVIOR): succeed (default) | fail | needs_human | hang
 * | never_start (ticket 13: accepts the prompt, then never emits anything — startup_timeout) |
 * permission_denied (ticket 13: the run's mandatory publication tool call is denied by the
 * unattended permission policy — decision 69's fail-closed path).
 *
 * The scenario lever exists only to make the failure/needs-human/hang paths testable; in the real
 * profiles (ticket 12) these paths come from real agent work and MCP failures.
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

const sessionFile = join(sessionDir, `${sessionId}.jsonl`);
mkdirSync(sessionDir, { recursive: true });

function emit(event) {
  appendFileSync(sessionFile, JSON.stringify(event) + "\n");
  process.stdout.write(JSON.stringify(event) + "\n");
}

/** pi RPC `response` record (rpc.md): echoes the command id; the Bridge correlates on it. */
function respond(id, command, data) {
  const record = { id, type: "response", command, success: true, data };
  process.stdout.write(JSON.stringify(record) + "\n");
}

function settleWithManifest(outcome = "completed") {
  // agent-local git so the Bridge can genuinely derive branch/commitSha via Git (decision 59)
  if (!existsSync(join(workspace, ".git"))) {
    execFileSync("git", ["init", "-b", "agent/pipe-stub"], { cwd: workspace, stdio: "ignore" });
  }
  const artifactDir = join(workspace, "artifacts");
  mkdirSync(artifactDir, { recursive: true });
  writeFileSync(join(artifactDir, "summary.md"),
    "# Pipeline stub result\n\nThe stub agent completed the pipeline with the received prompt.\n");
  execFileSync("git", ["add", "."], { cwd: workspace, stdio: "ignore" });
  execFileSync("git", ["-c", "user.name=Stub Agent", "-c", "user.email=stub@invalid",
    "commit", "-m", "stub agent work"], { cwd: workspace, stdio: "ignore" });
  writeFileSync(join(workspace, "result.json"), JSON.stringify({
    schemaVersion: 1,
    summary: "Stub pipeline result: the agent completed the run.",
    ...(outcome === "needs_human_input" ? { outcome } : {}),
    artifacts: [
      { type: "implementation_summary", format: "markdown", path: "artifacts/summary.md" },
    ],
    requiredPublications: [
      { toolCallId: "call-1", kind: "issue_comment", externalId: "stub-pub-1" },
    ],
  }, null, 2) + "\n");
}

function answerPrompt() {
  runInFlight = true;
  if (behavior === "never_start") {
    // ticket 13's startup_timeout lever: the process stays alive (heartbeats continue) but never
    // emits ANYTHING — not even agent_start — so the Run never became ready
    return;
  }
  emit({ id: "e1", type: "agent_start", data: { sessionId } });
  const toolCallId = "call-1";
  if (behavior === "fail") {
    emit({ id: "e2", type: "turn_start" });
    process.stdout.write(JSON.stringify({
      id: "e3", type: "error",
      data: { code: "planner_stub_error", message: "stub forced failure" },
    }) + "\n");
    appendFileSync(sessionFile, JSON.stringify({ id: "e3", type: "error", data: { code: "planner_stub_error" } }) + "\n");
    process.exit(3);
  }
  emit({ id: "e2", type: "turn_start" });
  if (behavior === "permission_denied") {
    // ticket 13's decision-69 lever: the mandatory publication tool call is denied by the
    // permission policy — the agent answers the denial itself (no dialog), the Bridge records the
    // `permission_blocked` RunEvent and verifies the required publication in the manifest
    const deniedToolCallId = "call-1";
    emit({ id: "e3", type: "message_update", data: { delta: "Trying to publish (stub)." } });
    emit({ id: "e4", type: "tool_execution_start", data: { toolCallId: deniedToolCallId, toolName: "stub_publish" } });
    emit({ id: "e5", type: "permission_denied", data: { toolCallId: deniedToolCallId, toolName: "stub_publish", reason: "not on the profile's permission allow-list" } });
    emit({ id: "e6", type: "tool_execution_end", data: { toolCallId: deniedToolCallId, status: "denied" } });
    emit({ id: "e7", type: "turn_end" });
    settleWithManifest("completed");
    emit({ id: "e8", type: "agent_settled", data: { sessionId } });
    setTimeout(() => process.exit(0), 200);
    return;
  }
  if (behavior === "hang") {
    // agent_started, then never settles again while the process stays alive (ticket 13's timeouts;
    // ticket 14's stop lever — an `abort` RPC settles the run with aborted=true and exits)
    return;
  }
  emit({ id: "e3", type: "message_update", data: { delta: "Analyzing the received prompt (stub)." } });
  emit({ id: "e4", type: "tool_execution_start", data: { toolCallId, toolName: "stub_publish" } });
  emit({ id: "e5", type: "tool_execution_update", data: { toolCallId, step: "writing artifacts" } });
  emit({ id: "e6", type: "tool_execution_end", data: { toolCallId, status: "completed" } });
  emit({ id: "e7", type: "turn_end" });
  settleWithManifest(behavior === "needs_human" ? "needs_human_input" : "completed");
  emit({ id: "e8", type: "agent_settled", data: { sessionId } });
  // one-shot sessions (decision 57): after settling the agent is done with this run — exit regardless
  // of stdin state (the Bridge closes stdin after settle either way)
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
        // rpc-commands.md: the response means accepted — the run then streams to `agent_settled`
        respond(rpc.id, "prompt", { disposition: "started" });
        answerPrompt();
        break;
      case "get_state":
        // rpc-commands.md: `sessionFile` is the actual on-disk path (decision 57 discovery)
        respond(rpc.id, "get_state", {
          sessionId,
          sessionFile: sessionFile,
          isStreaming: false,
        });
        break;
      default:
        // ticket 14: pi-shaped RPCs the Bridge's stop sequence sends. `abort` settles an in-flight run
        // with aborted=true and exits (the aborted run never writes a manifest); stdin EOF then ends
        // the process like a normal settle.
        respond(rpc.id, rpc.type);
        if (rpc.type === "abort" && runInFlight) {
          runInFlight = false;
          emit({ id: "abort-1", type: "agent_settled", data: { sessionId, aborted: true } });
          setTimeout(() => process.exit(0), 100);
        }
        break;
    }
  }
});
process.stdin.on("end", () => process.exit(0));
