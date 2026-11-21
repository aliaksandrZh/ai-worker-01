#!/usr/bin/env node
/**
 * Runtime Bridge (04 §4, decisions 33/58–61) — the supervisor INSIDE the agent container. Not a Pi
 * extension: it launches the agent CLI in the pi-RPC-shaped protocol, reads the stdout JSONL event
 * stream (the realtime source; the session .jsonl is history), maps pi → RunEvent, spools every
 * callback to the durable per-Run spool BEFORE sending, retries network/5xx/429 with the same
 * eventId, coalesces heartbeats to the latest value, never drops lifecycle/terminal callbacks,
 * validates the run's `result.json` manifest after `agent_settled` (in place, deriving
 * branch/commitSha via Git, correlating requiredPublications with tool events) and delivers the
 * terminal `completed` (or `failed`) callback — the delivery cycle ends only on the Runtime API's
 * persistence acknowledgement. The container image entrypoint.
 */
import { spawn } from "node:child_process";
import { existsSync, readFileSync, writeFileSync } from "node:fs";
import { join } from "node:path";
import { openSpool } from "./spool.mjs";
import { EventMapper } from "./events.mjs";
import { openLogs } from "./logs.mjs";
import { loadManifest, renderPrompt, terminalFromManifest, validateManifest } from "./manifest.mjs";

const env = process.env;
const runId = env.PI_RUN_ID || fail("PI_RUN_ID is required");
const baseUrl = (env.PI_CALLBACK_BASE_URL || "http://runtime-api:8080").replace(/\/$/, "");
const tokenFile = env.PI_CALLBACK_TOKEN_FILE || fail("callback token file (PI_CALLBACK_TOKEN_FILE) is required");
const workspace = env.PI_WORKSPACE || "/workspace";
const sessionDir = env.PI_SESSION_DIR || "/session";
const heartbeatSeconds = clampSeconds(env.PI_HEARTBEAT_SECONDS, 15);
const eventCadenceSeconds = clampSeconds(env.PI_EVENT_CADENCE_SECONDS, 5);
const maxLogBytes = Number(env.PI_MAX_LOG_MB || 50) * 1024 * 1024;
const logsDir = env.PI_LOGS_DIR || "/logs";
const spoolDir = env.PI_SPOOL_DIR || "/spool";
const terminalMarker = join(spoolDir, ".terminal-delivered");

function fail(message) {
  process.stderr.write(`bridge: ${message}\n`);
  process.exit(4);
}
function clampSeconds(value, fallback) {
  const parsed = Number(value);
  return Number.isFinite(parsed) && parsed > 0 ? parsed : fallback;
}
const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));

const logs = openLogs(logsDir, maxLogBytes, `bridge ${runId} boot\n`);
const log = (line) => logs.write("bridge.log", line);

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
      // network/429/5xx/408 are retried with the same eventId (decision 60)
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
 * redelivery contract (decision 60). */
const backoffs = [1, 2, 4, 8, 16, 30];

async function deliverNext() {
  const entry = await spool.next();
  if (!entry) return null;
  const unbounded = entry.route === "completed" || entry.route === "failed"; // terminal: never dropped
  for (let attempt = 0; ; attempt++) {
    const result = await post(entry.route, entry.body);
    if (result.ok) {
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

/** Bridge pull channel (04 §3/§4): the frozen RunContextSnapshot, retried while the API is down. */
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

// ---- pipeline ----

async function main() {
  senderLoop(); // redelivery of pending spool entries from any earlier lifetime happens here

  const input = await pullInput();
  const prompt = renderPrompt(input);

  const observedCompletedToolCalls = new Set();
  const mapper = new EventMapper((entry) => {
    if (entry.body.type === "tool_completed" && entry.body.payload?.toolCallId)
      observedCompletedToolCalls.add(entry.body.payload.toolCallId);
    spool.append(entry);
  }, eventCadenceSeconds);

  const agent = spawn("node", [
    "stub-agent/pi-stub.mjs",
    "--session-dir", sessionDir,
    "--session-id", runId,
    "--workspace", workspace,
  ], { cwd: process.cwd(), env, stdio: ["pipe", "pipe", "pipe"] });

  // 04 §4: the task input is delivered as ONE RPC prompt over the agent's stdin (never env/input file)
  agent.stdin.write(JSON.stringify({ type: "prompt", message: prompt }) + "\n");

  let settled = false;
  agent.on("error", (error) => log(`agent spawn error: ${error.message}\n`));
  agent.on("exit", (code, signal) => log(`agent exited code ${code} signal ${signal}\n`));
  agent.stdout.setEncoding("utf8");
  let stdoutBuffer = "";
  agent.stdout.on("data", (chunk) => {
    stdoutBuffer += chunk;
    let newline;
    while ((newline = stdoutBuffer.indexOf("\n")) >= 0) {
      const line = stdoutBuffer.slice(0, newline).trim();
      stdoutBuffer = stdoutBuffer.slice(newline + 1);
      if (line.length === 0) continue;
      logs.write("stdout.log", line + "\n"); // decision 43: trio capture
      mapper.onLine(line);
      if (mapper.settled && !settled) {
        settled = true;
        agent.stdin.end(); // one prompt per run: close stdin for a graceful agent exit
      }
    }
  });
  agent.stderr.setEncoding("utf8");
  agent.stderr.on("data", (chunk) => logs.write("stderr.log", chunk));

  // heartbeat cadence → coalesced single-slot spool entry (decision 60); heartbeat proves liveness
  // only and never counts as progress (decision 58)
  const beats = spool.coalescingHeartbeats(`hb-${runId}-${Date.now()}`);
  const heartbeatTimer = setInterval(() => {
    if (!settled) beats({ occurredAt: new Date().toISOString() });
  }, heartbeatSeconds * 1000);
  // event cadence → flush aggregated assistant text as ONE assistant_output RunEvent (decision 58)
  const flushTimer = setInterval(() => mapper.flushAggregated(), eventCadenceSeconds * 1000);

  const exitCode = await new Promise((resolve) => {
    let grace = null;
    // the 30s exit grace arms only AFTER agent_settled — an agent that has not settled keeps running
    // with its heartbeat (its stop is ticket 14's control path; a silent/never-settling agent is
    // ticket 13's reconciliation, never the Bridge's own kill)
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

  if (exitCode !== 0 || !settled) {
    // on a crash the Bridge reports failure with the agent's exit code (decision 33)
    spool.append(terminalBody("runtime_failure",
      `agent process exited (code ${exitCode}) without settling — reported by the Runtime Bridge`));
  } else {
    // ---- result manifest: validate in place, derive Git facts, correlate publications ----
    const manifest = loadManifest(workspace, log);
    const invalid = validateManifest(manifest, workspace, observedCompletedToolCalls);
    if (invalid !== null) {
      log(`result manifest invalid: ${invalid}\n`);
      const code = invalid.startsWith("artifact_too_large") ? "artifact_too_large"
        : invalid.startsWith("github_publication_failed") ? "github_publication_failed"
        : "manifest_invalid";
      spool.append(terminalBody(code, `result manifest invalid — ${invalid}`));
    } else {
      spool.append(terminalFromManifest(manifest, runId));
    }
  }

  // the delivery cycle ends only on the Runtime API's persistence acknowledgement of the terminal
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
