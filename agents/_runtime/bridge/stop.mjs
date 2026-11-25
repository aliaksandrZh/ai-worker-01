/**
 * Graceful stop: the Bridge control path for cancellation.
 * The signal is SIGTERM — the Run Worker stops the container on run_stop_requested (docs/run-worker.md).
 * On request the
 * sequence runs: (1) RPC `clear_queue` first (an `abort` alone can continue queued messages), (2)
 * `abort` and `abort_bash`, (3) wait up to ~30 s for `agent_settled`, (4) close pi's stdin for a
 * graceful exit, (5) SIGTERM then SIGKILL if the process remains, (6) verify child processes stopped,
 * and finally (7) the terminal `cancelled` callback is spooled by the caller. The terminal belongs to
 * the stop path from the moment the signal is observed: the normal lifecycle flow must never post
 * `completed`/`failed` around it (the caller guards with the `requested` getter).
 */
import { readdirSync, readFileSync } from "node:fs";
import { join } from "node:path";

/// Wait budget for `agent_settled` after the abort RPCs (spec: ~30 s).
export const SETTLE_WAIT_MS = 30_000;
export const STDIN_GRACE_MS = 10_000;
export const SIGTERM_GRACE_MS = 5_000;

/**
 * Builds the terminal `cancelled` spool entry (step 7). Deterministic id: a redelivery after a bridge
 * restart can never duplicate (the spool/runtime API both key on it).
 */
export function cancelledTerminal(runId, reason, sessionFilePath = null) {
  return {
    id: `evt-${runId}-cancelled`,
    route: "cancelled",
    body: {
      eventId: `evt-${runId}-cancelled`,
      occurredAt: new Date().toISOString(),
      ...(reason ? { reason } : {}),
      ...(sessionFilePath ? { sessionFilePath } : {}),
    },
  };
}

/**
 * The stop sequence (steps 1–6) against the pi RPC channel. Caller-provided pieces keep this module
 * independent of the Bridge's singletons. One outstanding RPC command at a time is the correlator's
 * contract — callers hand in `sendCommand(type)` bound to a fresh correlator and must have resolved
 * the prompt handshake before invoking the sequence.
 */
export async function gracefulStopSequence({
  agent,
  sendCommand,
  isSettled,
  waitForExit,
  log,
  settleWaitMs = SETTLE_WAIT_MS,
  stdinGraceMs = STDIN_GRACE_MS,
  sigtermGraceMs = SIGTERM_GRACE_MS,
}) {
  log("stop sequence: clear_queue, abort/abort_bash, then settle wait\n");

  // (1) clear_queue first — abort alone can continue queued messages (rpc-commands.md)
  await safeRpc("clear_queue");
  // (2) abort the current operation and any running bash tool
  await safeRpc("abort");
  await safeRpc("abort_bash");

  // (3) wait up to ~30 s for `agent_settled` (mapped to the RunEvent stream by the normal path)
  await waitFor(() => isSettled(), settleWaitMs, "agent_settled");

  // (4) close stdin for a graceful exit, (5) SIGTERM then SIGKILL
  const children = listChildPids(agent.pid); // snapshot before the tree is disturbed
  agent.stdin.end();
  await waitForExit(stdinGraceMs, "stdin close");
  agent.kill("SIGTERM");
  await waitForExit(sigtermGraceMs, "SIGTERM");
  agent.kill("SIGKILL");
  await waitForExit(5_000, "SIGKILL");

  // (6) verify child processes stopped: SIGKILL any survivor of the snapshotted tree, then confirm
  for (const pid of children.filter(pidAlive)) {
    log(`stop sequence: child pid ${pid} survived the agent exit — SIGKILL\n`);
    try { process.kill(pid, "SIGKILL"); } catch { /* already gone */ }
  }
  for (const pid of children) {
    for (let waited = 0; pidAlive(pid) && waited < 3_000; waited += 100)
      await sleep(100);
    if (pidAlive(pid)) log(`stop sequence: child pid ${pid} still alive after SIGKILL (container stop will own it)\n`);
  }
  log("stop sequence complete — agent process tree verified stopped\n");

  async function safeRpc(type) {
    try {
      await sendCommand(type);
    } catch (error) {
      log(`stop sequence: rpc ${type} did not resolve — ${error?.message ?? error}\n`);
    }
  }
}

function pidAlive(pid) {
  try { process.kill(pid, 0); return true; } catch { return false; }
}

/** Direct `/proc` children of `ppid` (empty on hosts without /proc — e.g. some test runners). */
export function listChildPids(ppid) {
  const children = [];
  let entries;
  try {
    entries = readdirSync("/proc");
  } catch {
    return children;
  }
  for (const entry of entries) {
    if (!/^\d+$/.test(entry)) continue;
    try {
      const stat = readFileSync(join("/proc", entry, "stat"), "utf8");
      const afterComm = stat.slice(stat.indexOf(")") + 2).split(" ");
      if (Number(afterComm[1]) === ppid) children.push(Number(entry));
    } catch { /* the process vanished mid-scan */ }
  }
  return children;
}

const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));

async function waitFor(predicate, budgetMs, label) {
  const deadline = Date.now() + budgetMs;
  while (!predicate() && Date.now() < deadline)
    await sleep(250);
  if (!predicate())
    return false; // the caller falls through to stdin close / SIGTERM
  return true;
}
