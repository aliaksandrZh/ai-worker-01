#!/usr/bin/env node
/**
 * Runtime Bridge — the supervisor INSIDE the agent container. Not a Pi
 * extension: it launches the agent CLI in `--mode rpc` (real Pi baked via PI_AGENT_BIN/PI_AGENT_ARGS;
 * the stub image keeps its pi-RPC-shaped protocol agent), reads the stdout JSONL
 * event stream (the realtime source; the session .jsonl is history), maps pi → RunEvent, spools every
 * callback to the durable per-Run spool BEFORE sending, retries network/5xx/429 with the same
 * eventId, coalesces heartbeats to the latest value, never drops lifecycle/terminal callbacks,
 * validates the run's `result.json` manifest after `agent_settled` (in place, deriving
 * branch/commitSha via Git, correlating requiredPublications with error-free completed tool
 * executions) and delivers the terminal `completed` (or `failed`) callback — the
 * delivery cycle ends only on the Runtime API's persistence acknowledgement. It also captures Pi
 * stdout/stderr plus its own diagnostics into the per-Run log trio.
 */
import { spawn } from "node:child_process";
import { existsSync, readFileSync, writeFileSync } from "node:fs";
import { join } from "node:path";
import { fileURLToPath } from "node:url";
import { openSpool } from "./spool.mjs";
import { EventMapper, isSuccessfulToolEnd } from "./events.mjs";
import { RpcCorrelator, promptDisposition } from "./rpc.mjs";
import { openLogs } from "./logs.mjs";
import { pollStopRequest, gracefulStopSequence, cancelledTerminal, POLL_INTERVAL_MS } from "./stop.mjs";
import { loadManifest, renderPrompt, terminalFromManifest, validateManifest } from "./manifest.mjs";

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
// mandated publication kinds that MUST be confirmed before `completed`
// (comma-separated; empty = no mandate — every listed publication is still correlated, the stub).
const requiredPublicationKinds = (env.PI_REQUIRED_PUBLICATION_KINDS || "")
  .split(",").map((kind) => kind.trim()).filter(Boolean);
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
      // A validation-class (4xx) rejection of the completed callback (e.g. 422 manifest): the API
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
let sessionFilePath = null; // the actual session path, discovered via `get_state`
let settled = false;

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
    if (entry.body.type === "tool_completed" && isSuccessfulToolEnd(entry.body.payload))
      observedCompletedToolCalls.add(entry.body.payload.toolCallId);
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
  const onAgentExit = new Promise((resolve) => agent.on("exit", (code) => resolve(code)));
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
      if (mapper.onLine(line) && mapper.settled && !settled) {
        settled = true;
        agent.stdin.end(); // one prompt per run: close stdin for a graceful agent exit
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

  const exitCode = await new Promise((resolve) => {
    let grace = null;
    // the 30s exit grace arms only AFTER agent_settled — an agent that has not settled keeps running
    // with its heartbeat (its stop is the stop control path; a silent/never-settling agent is
    // the Runtime API's reconciliation, never the Bridge's own kill)
    const settleWatch = setInterval(() => {
      if (!settled || grace) return;
      clearInterval(settleWatch);
      grace = setTimeout(() => {
        log("agent did not exit 30s after settle — closing stdin, then SIGTERM\n");
        agent.stdin.end();
        setTimeout(() => {
          agent.kill("SIGTERM");
          setTimeout(() => agent.kill("SIGKILL"), 5000);
        }, 10_000);
      }, 30_000);
    }, 250);
    agent.on("exit", (code) => {
      clearInterval(settleWatch);
      if (grace) clearTimeout(grace);
      resolve(code);
    });
  });
  clearInterval(heartbeatTimer);
  clearInterval(flushTimer);
  mapper.flushAggregated();
  log(`agent lifecycle done (exit ${exitCode}, settled ${settled})\n`);

  // the stop path owns the terminal: a run interrupted by /stop is `cancelled` regardless of the
  // exit shape (settled or killed) — no failed report, no manifest
  if (stopSignal) return await finishCancelled();

  if (exitCode !== 0 || !settled) {
    // on a crash the Bridge reports failure with the agent's exit code
    return await spoolFailure("runtime_failure",
      `agent process exited (code ${exitCode}) without settling — reported by the Runtime Bridge`);
  }

  // ---- result manifest: validate in place, derive Git facts, correlate publications ----
  const manifest = loadManifest(workspace, log);
  const invalid = validateManifest(manifest, workspace, observedCompletedToolCalls, requiredPublicationKinds, mapper.deniedToolCallIds);
  if (invalid !== null) {
    log(`result manifest invalid: ${invalid}\n`);
    const code = invalid.startsWith("artifact_too_large") ? "artifact_too_large"
      : invalid.startsWith("github_publication_failed") ? "github_publication_failed"
      : invalid.startsWith("required_permission_denied") ? "required_permission_denied"
      : "manifest_invalid";
    return await spoolFailure(code, `result manifest invalid — ${invalid}`);
  }

  spool.append(terminalFromManifest(manifest, runId, sessionFilePath));
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
