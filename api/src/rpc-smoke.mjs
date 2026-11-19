// Drives the real HTTP endpoints end to end against a running api server.
// Usage: BASE=http://localhost:3000 node rpc-smoke.mjs   (server must be running)
import { existsSync, readFileSync } from "node:fs";
import { join, dirname } from "node:path";
import { fileURLToPath } from "node:url";

const BASE = process.env.BASE || "http://localhost:3000";
const API_DIR = dirname(fileURLToPath(import.meta.url));

let step = 0;
const assert = (cond, msg) => {
  if (!cond) throw new Error(`step ${step}: ${msg}`);
  console.log(`ok ${step}: ${msg}`);
};

const call = async (method, path, body) => {
  const res = await fetch(BASE + path, {
    method,
    headers: { "content-type": "application/json" },
    ...(body ? { body: JSON.stringify(body) } : {}),
  });
  const json = await res.json().catch(() => ({}));
  return { status: res.status, json };
};

// Poll fn until it returns truthy; fn receives nothing, uses closures for cursors.
const poll = async (fn, { timeoutMs, intervalMs = 2000, label }) => {
  const t0 = Date.now();
  for (;;) {
    if (await fn()) return;
    if (Date.now() - t0 > timeoutMs) throw new Error(`timeout: ${label} (${timeoutMs}ms)`);
    await new Promise((s) => setTimeout(s, intervalMs));
  }
};

let id;
try {
  // 1. create session: komodo spawns the container, api attaches
  step = 1;
  const created = await call("POST", "/sessions", { name: "smoke" });
  assert(created.status === 201, `POST /sessions → 201 (${created.status})`);
  id = created.json.id;
  assert(id && created.json.sessionFile, `session ${id} created, sessionFile ${created.json.sessionFile}`);

  // 2. RPC round trip without the model: pi's bash command inside the container
  step = 2;
  const bash = await call("POST", `/sessions/${id}/rpc`, { type: "bash", command: "echo pi-rpc-container-ok" });
  assert(bash.status === 200 && bash.json.success, "bash rpc succeeded");
  assert(bash.json.data?.exitCode === 0 && String(bash.json.data?.output).includes("pi-rpc-container-ok"), "bash output round-tripped");

  // 3. model prompt that must produce a tool call (needs a working model config)
  step = 3;
  await call("POST", `/sessions/${id}/rpc`, { type: "prompt", message: "Use the bash tool to run exactly: echo smoke-tool-check" });
  let cursor = 0;
  let toolCalls = 0;
  await poll(
    async () => {
      const ev = await call("GET", `/sessions/${id}/events?since=${cursor}&type=agent_settled,tool_execution_start`);
      for (const r of ev.json.records) {
        cursor = Math.max(cursor, r.seq + 1);
        if (r.record.type === "agent_settled") return true;
        if (r.record.type === "tool_execution_start") toolCalls++;
      }
      return false;
    },
    { timeoutMs: 180000, label: "agent_settled after tool prompt" },
  );
  assert(toolCalls >= 1, `model made ${toolCalls} tool call(s)`);

  // 4. extension-command prompt: /host-check → dialog → api executes on host → answer
  step = 4;
  await call("POST", `/sessions/${id}/rpc`, { type: "prompt", message: "/host-check" });
  await poll(
    async () => {
      const msgs = await call("POST", `/sessions/${id}/rpc`, { type: "get_messages" });
      const blob = JSON.stringify(msgs.json?.data?.messages ?? "");
      return blob.includes("host-bridge-check") && blob.includes('"exitCode":0');
    },
    { timeoutMs: 60000, label: "host-bridge-check message with exitCode 0" },
  );
  assert(true, "host-bridge result landed in session messages");

  // 5. wire proof: the dialog request went over the extension UI protocol
  step = 5;
  const ev = await call("GET", `/sessions/${id}/events`);
  const dialog = ev.json.records.find(
    (r) => r.record.type === "extension_ui_request" && r.record.method === "editor" && r.record.title === "host-bridge",
  );
  assert(dialog, "extension_ui_request(editor, host-bridge) observed on the wire");

  // 6. stop session: container stopped + deleted
  step = 6;
  const del = await call("DELETE", `/sessions/${id}`);
  assert(del.status === 200 && del.json.stopped, "DELETE /sessions/:id → stopped");
  const after = await call("GET", "/sessions");
  assert(!after.json.sessions.some((s) => s.id === id), "session deployment is gone");

  // 7. logging: raw capture — each log line is {ts, dir, line}; parse it back out
  step = 7;
  const logPath = join(API_DIR, "..", "logs", `pi-session-${id}.log`);
  assert(existsSync(logPath), `log file exists: ${logPath}`);
  const protocolRecords = readFileSync(logPath, "utf8")
    .split("\n")
    .filter(Boolean)
    .map((l) => JSON.parse(l))
    .filter((l) => l.dir === "in")
    .map((l) => JSON.parse(l.line))
    .filter((r) => r?.type === "response");
  assert(protocolRecords.length >= 1, `log contains ${protocolRecords.length} protocol response record(s)`);

  console.log(`\nSMOKE PASS (session ${id})`);
} catch (err) {
  console.error(`\nSMOKE FAIL — ${err.message}`);
  if (id) console.error(`(session ${id} may still exist — check GET /sessions)`);
  process.exit(1);
}
