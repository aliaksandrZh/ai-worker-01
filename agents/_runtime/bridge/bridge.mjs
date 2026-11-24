#!/usr/bin/env node
/**
 * Runtime Bridge — the supervisor INSIDE the agent container. Not a Pi
 * extension: it launches the agent CLI in `--mode rpc` (real Pi baked via PI_AGENT_BIN/PI_AGENT_ARGS;
 * the stub image keeps its pi-RPC-shaped protocol agent), reads the stdout JSONL
 * event stream (the realtime source; the session .jsonl is history), maps pi → RunEvent, spools every
 * callback to the durable per-Run spool BEFORE sending, retries network/5xx/429 with the same
 * eventId, coalesces heartbeats to the latest value, never drops lifecycle/terminal callbacks,
 * runs the publication toolcheck after every `agent_settled` (each mandated publication kind must
 * have an OBSERVED error-free completed tool execution; a missing mandate is nudged back to the
 * agent at most PI_PUBLICATION_NUDGES times) and delivers the terminal `completed` (or `failed`)
 * callback — the delivery cycle ends only on the Runtime API's persistence acknowledgement. Before
 * the task prompt it probes pi's `/mcp` status (the FULL failure reason incl. the server's stderr
 * — `mcp_unavailable` on a failed server, before any work). It also captures Pi stdout/stderr plus
 * its own diagnostics into the per-Run log trio.
 */
import { spawn } from "node:child_process";
import { existsSync, readFileSync, writeFileSync } from "node:fs";
import { join } from "node:path";
import { fileURLToPath } from "node:url";
import { openSpool } from "./spool.mjs";
import { EventMapper, isSuccessfulToolEnd } from "./events.mjs"; // the mapper owns the observed-tool-call stream
import { RpcCorrelator, promptDisposition } from "./rpc.mjs";
import { openLogs } from "./logs.mjs";
import { pollStopRequest, gracefulStopSequence, cancelledTerminal, POLL_INTERVAL_MS } from "./stop.mjs";
import { renderPrompt, gitFacts, checkPublications, nudgePrompt, verdictNudgePrompt, outcomeNudgePrompt, reviewVerdict, collectArtifacts, completedTerminal, normalizeToolName } from "./toolcheck.mjs";

const env = process.env;
const runId = env.PI_RUN_ID || fail("PI_RUN_ID is required");
const baseUrl = (env.PI_CALLBACK_BASE_URL || "http://runtime-api:8080").replace(/\/$/, "");
const tokenFile = env.PI_CALLBACK_TOKEN_FILE || fail("callback token file (PI_CALLBACK_TOKEN_FILE) is required");
const workspace = env.PI_WORKSPACE || "/workspace";
const sessionDir = env.PI_SESSION_DIR || "/session";
// the image is the sole source of the runtime configuration — the baked env picks the
// agent binary and its fixed flags; the Bridge appends only the per-Run session flags.
// The agent process runs with cwd = the workspace (that cwd is pi's project scope). The stub default
// therefore resolves its agent module ABSOLUTELY (a relative arg would miss from /workspace).
const agentBin = env.PI_AGENT_BIN || "node";
const defaultStubAgent = fileURLToPath(new URL("../stub-agent/pi-stub.mjs", import.meta.url));
const agentArgs = (env.PI_AGENT_ARGS || defaultStubAgent).split(" ").filter(Boolean);
// mandated publication kinds that MUST be observed as successful tool executions before `completed`
// (comma-separated; empty = no mandate — the stub). The verification is the Bridge's publication
// toolcheck against the event stream; the agent never self-reports (there is no result manifest).
const requiredPublicationKinds = (env.PI_REQUIRED_PUBLICATION_KINDS || "")
  .split(",").map((kind) => kind.trim()).filter(Boolean);
// the review-verdict tool (the reviewer image only): an OBSERVED error-free execution of this tool
// turns the terminal `completed` into `outcome: "changes_requested"` — the review demanded fixes;
// its absence means the review passed. A verdict, not a publication: never nudged, never mandated.
const changesTool = env.PI_REVIEW_OUTCOME_TOOL || "";
// the review-verdict MANDATE (the reviewer image only): a definite verdict must be OBSERVED —
// the `request_changes` call or a submit with event APPROVE / REQUEST_CHANGES; a COMMENT-only
// submission (or silence) is nudged back at most PI_PUBLICATION_NUDGES times, then fails the run
// with `verdict_missing`. The args of `tool_execution_start` make the submit's event observable.
const verdictRequired = env.PI_REQUIRED_REVIEW_VERDICT === "1";
// the run-outcome MANDATE (the coder image only): the agent MUST call the completion tool
// (PI_OUTCOME_TOOL) before the run can settle — the observed call is the "work is done" signal the
// control plane turns into the task's terminal state. A settle without the call is nudged back at
// most PI_PUBLICATION_NUDGES times, then fails the run with `outcome_missing` (the task ends Failed).
const outcomeTool = env.PI_OUTCOME_TOOL || "";
const outcomeRequired = env.PI_REQUIRED_OUTCOME === "1";
// how many times a settle with unconfirmed mandated publications is nudged back to the agent
// (a follow-up prompt in the same session) before the run fails with `publication_missing`
const publicationNudges = (() => {
  const parsed = Number(env.PI_PUBLICATION_NUDGES);
  return Number.isFinite(parsed) && parsed >= 0 ? Math.floor(parsed) : 2;
})();
// the /mcp status probe waits for pi's status notify EVENT-DRIVEN; the deadline only bounds the
// pathological no-notify case (agents that never notify — e.g. the non-mcp_failed stub behaviors)
const mcpProbeTimeoutMs = clampSeconds(env.PI_MCP_PROBE_TIMEOUT_SECONDS, 5) * 1000;
const heartbeatSeconds = clampSeconds(env.PI_HEARTBEAT_SECONDS, 15);
const eventCadenceSeconds = clampSeconds(env.PI_EVENT_CADENCE_SECONDS, 5);
const maxLogBytes = Number(env.PI_MAX_LOG_MB || 50) * 1024 * 1024;
const logsDir = env.PI_LOGS_DIR || "/logs";
const spoolDir = env.PI_SPOOL_DIR || "/spool";
const terminalMarker = join(spoolDir, ".terminal-delivered");

/** One readable console line per mapped RunEvent (mirrors what the Runtime API persists). */
function echoRunEvent(body) {
  const payload = body.payload ?? {};
  let detail = "";
  if (typeof payload.toolName === "string") detail = ` ${payload.toolName}`;
  if (body.type === "assistant_output" && typeof payload.text === "string") {
    const flat = payload.text.replace(/\s+/g, " ");
    detail = ` ${flat.slice(0, 160)}${flat.length > 160 ? "…" : ""}`;
  }
  process.stdout.write(`agent: ${body.type}${detail}\n`);
}

function fail(message) {
  process.stderr.write(`bridge: ${message}\n`);
  process.exit(4);
}
function clampSeconds(value, fallback) {
  const parsed = Number(value);
  return Number.isFinite(parsed) && parsed > 0 ? parsed : fallback;
}
const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));

const bootLine = `bridge ${runId} boot (agent: ${agentBin} ${agentArgs.join(" ")})\n`;
// console mirror: `docker logs <agent container>` shows the run's activity, not just the entry's
// clone line; the durable per-Run trio (/logs/*) remains the source of truth.
const log = (line) => {
  process.stdout.write(line);
  logs.write("bridge.log", line);
};
const logs = openLogs(logsDir, maxLogBytes, bootLine); // the trio keeps the boot line
process.stdout.write(bootLine);                        // and the container stdout mirrors it

/** Idempotent container restart: terminal delivered in an earlier lifetime → nothing left to do. */
if (existsSync(terminalMarker)) {
  log("terminal already delivered in an earlier bridge lifetime — nothing to do\n");
  process.exit(0);
}

const headers = () => ({
  "content-type": "application/json",
  authorization: `Bearer ${readFileSync(tokenFile, "utf8").trim()}`,
});

/** One HTTP call to the Runtime API; resolves {ok, status, retryable, detail} — never throws. */
async function post(route, body) {
  try {
    const response = await fetch(`${baseUrl}/internal/runtime/runs/${runId}/${route}`, {
      method: "post",
      headers: headers(),
      body: JSON.stringify(body),
      signal: AbortSignal.timeout(30_000),
    });
    if (response.ok) return { ok: true, status: response.status };
    const detail = (await response.text()).slice(0, 400);
    log(`POST /${route} -> ${response.status} ${detail}\n`);
    return {
      ok: false,
      status: response.status,
      // network/429/5xx/408 are retried with the same eventId
      retryable: response.status === 429 || response.status === 408 || response.status >= 500,
      detail,
    };
  } catch (error) {
    log(`POST /${route} transport failure: ${error?.message ?? error}\n`);
    return { ok: false, retryable: true, status: 0, detail: String(error?.message ?? error) };
  }
}

const spool = openSpool(spoolDir, {
  onTerminalDelivered: () => writeFileSync(terminalMarker, `${new Date().toISOString()}\n`),
});

/** Non-retryable statuses back off too, then park — but a parked entry is never dropped: a bridge
 * or container restart redelivers it (the runtime-api restart cases), which is exactly the spool
 * redelivery contract. */
const backoffs = [1, 2, 4, 8, 16, 30];

async function deliverNext() {
  const entry = await spool.next();
  if (!entry) return null;
  // terminal routes (incl. the `cancelled`): never dropped
  const unbounded = entry.route === "completed" || entry.route === "failed" || entry.route === "cancelled";
  for (let attempt = 0; ; attempt++) {
    const result = await post(entry.route, entry.body);
    if (result.ok) {
      spool.remove(entry);
      return true;
    }
    // `cancelled` delivered into an already-terminal run (e.g. the Runtime API's stop-confirm sweeper
    // reconciled the cancellation first): the terminal state exists — treat 409 (already terminal) and
    // 403 token_revoked (post-terminal callback refusal) as "done" and move on
    if (entry.route === "cancelled" && (result.status === 409 || result.status === 403)) {
      log(`terminal cancelled refused (already terminal: ${result.detail}) — accepted\n`);
      spool.remove(entry);
      return true;
    }
    if (!result.retryable) {
      // A validation-class (4xx) rejection of the completed callback (e.g. 422 artifact_too_large): the API
      // persisted nothing from it — report the failure path instead, carrying the API's code.
      if (entry.route === "completed") {
        spool.remove(entry);
        spool.append(terminalRejected(result));
        return true; // the fallback is delivered by the next sender pass
      }
      spool.reject(entry, `permanent rejection ${result.status}: ${result.detail}`);
      log(`entry ${entry.id} parked as rejected — operator follow-up required\n`);
      process.exit(5);
    }
    const wait = unbounded ? 30 : backoffs[Math.min(attempt, backoffs.length - 1)];
    log(`retrying entry ${entry.id} in ${wait}s (${result.detail})\n`);
    await sleep(wait * 1000);
  }
}

function terminalRejected(result) {
  try {
    const parsed = JSON.parse(result.detail);
    return terminalBody(parsed.code ?? "runtime_failure", `runtime API rejected the terminal callback: ${result.detail}`);
  } catch {
    return terminalBody("runtime_failure", `runtime API rejected the terminal callback (${result.status})`);
  }
}

function terminalBody(code, message) {
  return {
    id: `evt-${runId}-failed`,
    route: "failed",
    body: {
      eventId: `evt-${runId}-failed`,
      occurredAt: new Date().toISOString(),
      ...(sessionFilePath ? { sessionFilePath } : {}),
      error: { code, message },
    },
  };
}

/** The single sender: FIFO deliveries with resilient per-entry retries, forever. */
async function senderLoop() {
  while (true) {
    const worked = await deliverNext();
    if (worked === null) await sleep(200); // spool momentarily empty; producers keep appending
  }
}

/** Bridge pull channel: the frozen RunContextSnapshot, retried while the API is down. */
async function pullInput() {
  for (let attempt = 0; attempt < 30; attempt++) {
    try {
      const response = await fetch(`${baseUrl}/internal/runtime/runs/${runId}/input`, {
        headers: headers(),
        signal: AbortSignal.timeout(10_000),
      });
      if (response.ok) return await response.json();
      log(`input pull answered ${response.status}\n`);
      if (response.status === 401 || response.status === 403) fail("input pull unauthorized");
    } catch (error) {
      log(`input pull failed: ${error?.message ?? error}\n`);
    }
    await sleep(5_000);
  }
  fail("input pull retries exhausted");
}

// ---- run state shared across the launch/lifecycle/terminal stages ----
const observedCompletedToolCalls = new Set(); // error-free completed tool ids
const completedToolNames = new Set(); // tool names with an error-free completed end (the toolcheck)
const toolNameByCallId = new Map(); // tool_started fallback: ends whose payload omits the name
let lastAssistantText = null; // the last aggregated assistant_output — the run's result summary
let reviewOutcome = undefined; // the review verdict carried in the terminal completed callback
let sessionFilePath = null; // the actual session path, discovered via `get_state`
let mcpProbe = null; // active while the /mcp status probe is in flight: { lines, waiters }
let settled = false;
let resolveSettle = null; // resolves the settle wait (the toolcheck loop awaits it per turn)
let agentExited = false;
let agentExitCode = null;

/** Terminal failure path: spool the `failed` callback, stop the agent, wait for the ack, exit. */
function spoolFailure(code, message) {
  log(`terminal failure (${code}): ${message}\n`);
  spool.append(terminalBody(code, message));
  return finishWait();
}

// ---- pipeline ----

async function main() {
  senderLoop(); // redelivery of pending spool entries from any earlier lifetime happens here

  const input = await pullInput();
  const prompt = renderPrompt(input);

  const mapper = new EventMapper((entry) => {
    const { type, payload } = entry.body;
    if (type === "tool_started" && typeof payload.toolCallId === "string")
      toolNameByCallId.set(payload.toolCallId, payload.toolName);
    if (type === "tool_completed" && isSuccessfulToolEnd(payload)) {
      observedCompletedToolCalls.add(payload.toolCallId);
      const toolName = typeof payload.toolName === "string"
        ? payload.toolName
        : toolNameByCallId.get(payload.toolCallId);
      // store the NORMALIZED name (the last __ segment): every consumer — checkPublications,
      // the outcome mandate — matches bare tool names against this set
      if (typeof toolName === "string") completedToolNames.add(normalizeToolName(toolName));
    }
    if (type === "assistant_output" && typeof payload?.text === "string") lastAssistantText = payload.text;
    echoRunEvent(entry.body);
    spool.append(entry);
  }, eventCadenceSeconds);

  const agent = spawn(agentBin, [
    ...agentArgs,
    "--session-dir", sessionDir,
    "--session-id", runId,
  ], { cwd: workspace, env, stdio: ["pipe", "pipe", "pipe"] });

  // pi docs rpc.md: commands carry an id; the matching `response` repeats it. The correlator consumes
  // response records; every other stdout line is the realtime event stream.
  const correlator = new RpcCorrelator((line) => agent.stdin.write(line + "\n"));
  agent.on("error", (error) => log(`agent spawn error: ${error.message}\n`));
  agent.on("exit", (code, signal) => log(`agent exited code ${code} signal ${signal}\n`));
  const onAgentExit = new Promise((resolve) => {
    agent.on("exit", (code) => {
      agentExited = true;
      agentExitCode = code;
      resolve(code);
    });
  });
  agent.stdout.setEncoding("utf8");
  let stdoutBuffer = "";
  agent.stdout.on("data", (chunk) => {
    stdoutBuffer += chunk;
    let newline;
    while ((newline = stdoutBuffer.indexOf("\n")) >= 0) {
      const line = stdoutBuffer.slice(0, newline).trim();
      stdoutBuffer = stdoutBuffer.slice(newline + 1);
      if (line.length === 0) continue;
      logs.write("stdout.log", line + "\n"); // trio capture
      let record;
      try {
        record = JSON.parse(line);
      } catch {
        record = null; // not protocol output (e.g. pi shutdown notices) — logged verbatim only
      }
      if (correlator.onRecord(record)) continue; // a handshake response, not an event
      // the /mcp status probe captures pi's FULL MCP server status (stderr tail included) —
      // pi's own startup notice truncates the failure reason to the first line
      if (mcpProbe && record?.type === "extension_ui_request" && record.method === "notify"
          && typeof record.message === "string") {
        mcpProbe.lines.push(record.message);
        for (const wake of mcpProbe.waiters.splice(0)) wake(); // the probe stops waiting on the first notify
      }
      if (mapper.onLine(line) && mapper.settled && !settled) {
        settled = true; // stdin stays open: a failed publication toolcheck nudges a follow-up prompt
        if (resolveSettle) { resolveSettle(); resolveSettle = null; }
      }
    }
  });
  agent.stderr.setEncoding("utf8");
  agent.stderr.on("data", (chunk) => {
    logs.write("stderr.log", chunk);
    process.stderr.write(chunk); // mirror: pi's stderr must be visible in `docker logs` too
  });

  // heartbeat cadence → coalesced single-slot spool entry; heartbeat proves liveness
  // only and never counts as progress. Declared before the launch handshake so the
  // stop path (which may fire before/while the handshake runs) can clear them from anywhere.
  const beats = spool.coalescingHeartbeats(`hb-${runId}-${Date.now()}`);
  const heartbeatTimer = setInterval(() => {
    if (!settled) beats({ occurredAt: new Date().toISOString() });
  }, heartbeatSeconds * 1000);
  // event cadence → flush aggregated assistant text as ONE assistant_output RunEvent
  const flushTimer = setInterval(() => mapper.flushAggregated(), eventCadenceSeconds * 1000);

  // ---- graceful stop control ----
  // The signal lives in PostgreSQL (`run.stop_requested_at`); this Bridge pulls it every 2 s once the
  // agent is spawned. Once observed, the stop path owns the terminal: every other lifecycle branch
  // below defers to it and must never post another terminal around it.
  let stopSignal = null; // { reason } once requested
  let promptStageRelease = () => {}; // resolved when the RPC channel is free of the prompt handshake
  const promptStageDone = new Promise((resolve) => { promptStageRelease = resolve; });
  let stopSequenceStarted = false;
  let cancelledPosted = false;

  async function pollStopSignal() {
    if (stopSignal) return;
    const { requested, reason } = await pollStopRequest({ runId, baseUrl, tokenFile, log });
    if (!requested || stopSignal) return;
    stopSignal = { reason: reason ?? "stop_requested" };
    clearInterval(stopWatch);
    log(`stop signal observed (${stopSignal.reason})\n`);
    startStopSequence();
  }

  const stopWatch = setInterval(() => void pollStopSignal(), POLL_INTERVAL_MS);

  function startStopSequence() {
    if (stopSequenceStarted) return;
    stopSequenceStarted = true;
    void (async () => {
      await promptStageDone; // the RPC correlator allows one outstanding command
      await gracefulStopSequence({
        agent,
        sendCommand: (type) => correlator.command(type),
        isSettled: () => settled,
        waitForExit: (ms) => Promise.race([onAgentExit, sleep(ms)]),
        log,
      });
      await finishCancelled();
    })().catch((error) => {
      log(`stop path crashed: ${error?.stack ?? error}\n`);
      process.exit(4);
    });
  }

  /** Terminal `cancelled` (idempotent append, then the standard terminal ack wait). */
  async function finishCancelled() {
    if (!cancelledPosted) {
      cancelledPosted = true;
      clearInterval(heartbeatTimer);
      clearInterval(flushTimer);
      clearInterval(stopWatch);
      mapper.flushAggregated();
      spool.append(cancelledTerminal(runId, stopSignal?.reason ?? "stop_requested", sessionFilePath));
    }
    await finishWait();
  }

  // ---- launch handshake: discover the real session path, then deliver the ONE task prompt ----
  try {
    const state = await Promise.race([
      correlator.command("get_state"),
      onAgentExit.then((code) => {
        throw new Error(`agent exited before the launch handshake (code ${code})`);
      }),
    ]);
    sessionFilePath = typeof state.data?.sessionFile === "string" ? state.data.sessionFile : null;
    log(`pi session file: ${sessionFilePath ?? "not reported by the agent"}\n`);

    if (stopSignal) {
      // stop already requested: the task prompt is never delivered — the sequence owns the exit
      promptStageRelease();
      startStopSequence();
      return;
    }

    // ---- MCP status probe (pi-native): pi keeps a failed server's stderr in `connection.error`
    // (up to 2000 chars) but its startup notice truncates to the first line ("MCP connection closed").
    // The /mcp command prints the FULL status (formatStatus → the stderr tail). A run without its
    // publication tools is useless work — a failed server fails the run BEFORE the task prompt with
    // the exact cause; the agent process is stopped and no work is done.
    mcpProbe = { lines: [], waiters: [] };
    let probeDisposition = null;
    try {
      const probeResponse = await Promise.race([
        correlator.command("prompt", { message: "/mcp" }),
        onAgentExit.then((code) => {
          throw new Error(`agent exited during the MCP status probe (code ${code})`);
        }),
      ]);
      probeDisposition = promptDisposition(probeResponse);
      // the status notify may land right before or right after the command response — wait for the
      // first notify, not a fixed guess; the deadline only bounds agents that never notify
      if (mcpProbe.lines.length === 0) {
        await Promise.race([
          new Promise((resolve) => mcpProbe.waiters.push(resolve)),
          sleep(mcpProbeTimeoutMs),
        ]);
      }
    } catch (error) {
      mcpProbe = null;
      promptStageRelease();
      if (stopSignal) {
        startStopSequence();
        return;
      }
      agent.kill("SIGTERM");
      return await spoolFailure("runtime_failure", `MCP status probe failed — ${error.message}`);
    }
    const mcpStatus = mcpProbe.lines.join("\n").trim();
    mcpProbe = null;
    if (mcpStatus) log(`MCP status (pi /mcp):\n${mcpStatus}\n`); // healthy runs log it too — free diagnostics
    if (probeDisposition === "handled" && /: failed\b/m.test(mcpStatus)) {
      promptStageRelease();
      agent.kill("SIGTERM");
      return await spoolFailure("mcp_unavailable",
        `the agent's MCP servers failed to start — pi /mcp reports:\n${mcpStatus}`);
    }

    const promptResponse = await Promise.race([
      correlator.command("prompt", { message: prompt }),
      onAgentExit.then((code) => {
        throw new Error(`agent exited during prompt acceptance (code ${code})`);
      }),
    ]);
    promptStageRelease(); // the RPC channel is free from here on — the stop sequence may command it
    const disposition = promptDisposition(promptResponse);
    if (disposition !== "started") {
      // rpc-commands.md: "queued"/"handled" prompts start no run — nothing will settle afterwards
      agent.kill("SIGTERM");
      if (stopSignal) return await finishCancelled();
      return await spoolFailure("runtime_failure",
        `the task prompt did not start a run (disposition ${JSON.stringify(disposition)})`);
    }
  } catch (error) {
    promptStageRelease();
    if (stopSignal) {
      // the agent died while a stop was pending — the stop path owns the terminal (never `failed`)
      startStopSequence();
      return;
    }
    agent.kill("SIGTERM");
    return await spoolFailure("runtime_failure", `agent launch failed — ${error.message}`);
  }

  // ---- settle → publication toolcheck loop ----
  // After every agent_settled the Bridge verifies the mandated publication kinds against the
  // OBSERVED event stream (tool names with an error-free completed end) — the agent never
  // self-reports. A missing mandate is nudged back into the same RPC session (at most
  // PI_PUBLICATION_NUDGES times); a permission-denied mandate fails immediately (a nudge cannot
  // help); exhausted nudges fail the run with `publication_missing`.
  const failNow = (code, message) => {
    clearInterval(heartbeatTimer);
    clearInterval(flushTimer);
    mapper.flushAggregated();
    return spoolFailure(code, message);
  };
  const settleWait = () => new Promise((resolve) => {
    resolveSettle = resolve;
    // the agent may settle BEFORE the loop starts (the prompt acceptance is slow, the run is fast):
    // the wake-up at the stream handler ran with resolveSettle = null — the recorded `settled` flag
    // must resolve the first wait immediately, or the loop hangs forever on a still-alive agent
    if (settled && !confirmed) resolve();
  });
  let nudgesLeft = publicationNudges;
  let confirmed = false;
  for (;;) {
    await Promise.race([settleWait(), onAgentExit]);
    if (stopSignal) break; // the stop path owns the terminal
    if (!settled) break; // the agent exited without settling — judged below
    const check = checkPublications(requiredPublicationKinds, completedToolNames, mapper.deniedToolNames);
    const verdict = verdictRequired ? reviewVerdict(mapper.completedToolCalls, changesTool) : { decided: true, changesRequested: false };
    const outcomeDone = !outcomeRequired
      || completedToolNames.has(normalizeToolName(outcomeTool));
    if (check.ok && verdict.decided && outcomeDone) {
      confirmed = true;
      reviewOutcome = verdict.changesRequested ? "changes_requested" : undefined;
      break;
    }
    if (check.ok && outcomeDone && !verdict.decided) {
      // the verdict mandate: the changes tool denied by policy cannot be nudged away either
      if (changesTool && mapper.deniedToolNames.has(changesTool))
        return await failNow("required_permission_denied",
          `the review verdict tool was denied by the run's permission policy: ${changesTool}`);
      if (agentExited || nudgesLeft <= 0)
        return await failNow("verdict_missing",
          "the review verdict was never confirmed: no request_changes call and no APPROVE/REQUEST_CHANGES submit");
      nudgesLeft -= 1;
      log(`review verdict undetermined — nudging the agent (${nudgesLeft} nudge(s) left)\n`);
      settled = false; // the nudge restarts the turn: heartbeats and the settle wait resume
      mapper.settled = false;
      mapper.flushAggregated();
      try {
        await Promise.race([
          correlator.command("prompt", { message: verdictNudgePrompt(changesTool || "request_changes") }),
          onAgentExit.then((code) => {
            throw new Error(`agent exited during nudge acceptance (code ${code})`);
          }),
        ]);
      } catch (error) {
        return await failNow("runtime_failure", `verdict nudge failed — ${error.message}`);
      }
      continue;
    }
    if (check.ok && verdict.decided && !outcomeDone) {
      // the run-outcome mandate: the completion tool cannot be nudged into existence when denied
      if (outcomeTool && mapper.deniedToolNames.has(outcomeTool))
        return await failNow("required_permission_denied",
          `the run-outcome tool was denied by the run's permission policy: ${outcomeTool}`);
      if (agentExited || nudgesLeft <= 0)
        return await failNow("outcome_missing",
          `the completion tool was never confirmed: ${outcomeTool} — the work is not reported done`);
      nudgesLeft -= 1;
      log(`run-outcome tool missing (${outcomeTool}) — nudging the agent (${nudgesLeft} nudge(s) left)\n`);
      settled = false;
      mapper.settled = false;
      mapper.flushAggregated();
      try {
        await Promise.race([
          correlator.command("prompt", { message: outcomeNudgePrompt(outcomeTool) }),
          onAgentExit.then((code) => {
            throw new Error(`agent exited during nudge acceptance (code ${code})`);
          }),
        ]);
      } catch (error) {
        return await failNow("runtime_failure", `outcome nudge failed — ${error.message}`);
      }
      continue;
    }
    if (check.denied.length > 0) {
      return await failNow("required_permission_denied",
        `mandatory publication kind(s) denied by the run's permission policy: ${check.denied.join(", ")}`);
    }
    if (agentExited || nudgesLeft <= 0) {
      return await failNow("publication_missing",
        `mandatory publication kind(s) never confirmed: ${check.missing.join(", ")}`);
    }
    nudgesLeft -= 1;
    log(`mandatory publication kind(s) missing (${check.missing.join(", ")}) — nudging the agent (${nudgesLeft} nudge(s) left)\n`);
    settled = false; // the nudge restarts the turn: heartbeats and the settle wait resume
    mapper.settled = false;
    mapper.flushAggregated();
    try {
      await Promise.race([
        correlator.command("prompt", { message: nudgePrompt(check.missing) }),
        onAgentExit.then((code) => {
          throw new Error(`agent exited during nudge acceptance (code ${code})`);
        }),
      ]);
    } catch (error) {
      return await failNow("runtime_failure", `publication nudge failed — ${error.message}`);
    }
  }

  clearInterval(heartbeatTimer);
  clearInterval(flushTimer);
  mapper.flushAggregated();

  // the stop path owns the terminal: a run interrupted by /stop is `cancelled` regardless of the
  // exit shape (settled or killed) — no completed report
  if (stopSignal) return await finishCancelled();

  if (!confirmed || !settled) {
    // on a crash the Bridge reports failure with the agent's exit code
    return await spoolFailure("runtime_failure",
      `agent process exited (code ${agentExitCode}) without settling — reported by the Runtime Bridge`);
  }

  // ---- all mandated publications confirmed: collect the run result ----
  const collected = collectArtifacts(workspace);
  if (collected.error) {
    return await spoolFailure(collected.error.code, `artifact collection failed — ${collected.error.message}`);
  }
  const summary = (lastAssistantText ?? "").trim() || "run completed";

  // graceful agent exit: stdin EOF ends real pi; a one-shot stub exits by itself after settling
  if (!agentExited) {
    agent.stdin.end();
    const exited = await Promise.race([onAgentExit.then(() => true), sleep(30_000).then(() => false)]);
    if (!exited) {
      log("agent did not exit 30s after the result collection — SIGTERM, then SIGKILL\n");
      agent.kill("SIGTERM");
      const killed = await Promise.race([onAgentExit.then(() => true), sleep(5_000).then(() => false)]);
      if (!killed) agent.kill("SIGKILL");
    }
    await onAgentExit;
  }
  log(`agent lifecycle done (exit ${agentExitCode}, publications confirmed)\n`);
  if (agentExitCode !== 0) {
    return await spoolFailure("runtime_failure",
      `agent process exited (code ${agentExitCode}) after settling — reported by the Runtime Bridge`);
  }

  spool.append(completedTerminal(runId, sessionFilePath, summary, collected.artifacts, gitFacts(workspace), reviewOutcome));
  await finishWait();
}

/** The delivery cycle ends only on the Runtime API's persistence acknowledgement of the terminal. */
async function finishWait() {
  for (let waited = 0; !existsSync(terminalMarker); waited += 1000) {
    if (waited % 60_000 === 0 && waited > 0) log(`still waiting for terminal acknowledgement (${waited / 1000}s)\n`);
    await sleep(1000);
  }
  log(`terminal persisted by the Runtime API — bridge done\n`);
  process.exit(0);
}

main().catch((error) => {
  log(`bridge crashed: ${error?.stack ?? error}\n`);
  process.exit(4);
});
