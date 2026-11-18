import test from "node:test";
import assert from "node:assert";
import { _splitLines, PiRpcSession } from "./rpc-session.mjs";
import { mkdtempSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";

test("one JSON per frame", () => {
  const r = _splitLines('{"a":1}\n{"b":2}\n', "");
  assert.deepEqual(r, { lines: ['{"a":1}', '{"b":2}'], rest: "" });
});

test("frame split mid-line reassembles and strips CR", () => {
  const a = _splitLines('{"a":1}\r\n{"b":', "");
  assert.deepEqual(a, { lines: ['{"a":1}'], rest: '{"b":' });
  const b = _splitLines('2}\n', a.rest);
  assert.deepEqual(b, { lines: ['{"b":2}'], rest: "" });
});

test("garbage echo line is captured as a line (dropped later at parse)", () => {
  const r = _splitLines('some echo text\n{"c":3}\n', "");
  assert.deepEqual(r.lines, ["some echo text", '{"c":3}']);
});

test("bare CR at end of line is stripped", () => {
  const r = _splitLines("echo ok\r\n", "");
  assert.deepEqual(r.lines, ["echo ok"]);
});

test("Buffer chunks decode as utf8", () => {
  const r = _splitLines(Buffer.from('{"d":"é"}\n'), "");
  assert.deepEqual(r.lines, ['{"d":"é"}']);
});

test("incomplete trailing line stays in rest", () => {
  const r = _splitLines('{"e"', "");
  assert.deepEqual(r, { lines: [], rest: '{"e"' });
});

const mkSession = () =>
  new PiRpcSession({ komodo: {}, server: "x", container: "c", terminal: "t", logsDir: mkdtempSync(join(tmpdir(), "pirpc-")) });

// settle(): awaits one macrotask tick, returns whether promise p already resolved
const settledBy = async (p) => {
  const flag = { done: false };
  p.then((r) => (flag.done = true)).catch(() => {});
  await new Promise((r) => setTimeout(r, 0));
  return flag.done;
};

test("echoed commands are dropped from the event buffer, real events kept", () => {
  const s = mkSession();
  s._onRecord({ id: "api-1", type: "get_state" }); // echo of our own sent command (PTY echo)
  s._onRecord({ type: "agent_start" }); // real session event
  s._onRecord({ type: "prompt" }); // echo of a sent prompt command
  const kept = s.eventBuf;
  assert.equal(kept.length, 1);
  assert.equal(kept[0].record.type, "agent_start");
});

test("_onClose with a pending handshake rejects it instead of crashing", () => {
  const s = mkSession();
  let rejected;
  s._handshake = { timer: null, resolve() {}, reject: (e) => (rejected = e) };
  s._onClose();
  assert.ok(rejected instanceof Error);
  assert.equal(s._handshake, null);
});

test("status derives idle for a fresh session", () => {
  const s = mkSession();
  assert.deepEqual(s.status(), {
    connected: false,
    phase: "idle",
    hasRun: false,
    lastSettled: null,
    pendingUi: [],
  });
});

test("agent_start then agent_settled records the settle and clears the run", () => {
  const s = mkSession();
  s._onRecord({ type: "agent_start" });
  assert.equal(s.status().phase, "running");
  s._onRecord({ type: "agent_settled", aborted: false });
  const st = s.status();
  assert.equal(st.phase, "idle");
  assert.equal(st.hasRun, true);
  assert.equal(st.lastSettled.aborted, false);
  assert.equal(typeof st.lastSettled.ts, "number");
});

test("settled then a new run goes back to running", () => {
  const s = mkSession();
  s._onRecord({ type: "agent_settled", aborted: false });
  s._onRecord({ type: "agent_start" });
  const st = s.status();
  assert.equal(st.phase, "running");
  assert.notEqual(st.lastSettled, null);
});

test("aborted settle carries aborted", () => {
  const s = mkSession();
  s._onRecord({ type: "agent_settled", aborted: true });
  assert.equal(s.status().lastSettled.aborted, true);
});

test("pending dialogs make the phase blocked (highest priority)", () => {
  const s = mkSession();
  s._onRecord({ type: "agent_start" });
  s._onRecord({ type: "extension_ui_request", id: "u1", method: "confirm", title: "Allow?" });
  const st = s.status();
  assert.equal(st.phase, "blocked");
  assert.equal(st.pendingUi.length, 1);
});

test("host-bridge dialogs are auto-handled and never parked as pending", () => {
  const s = mkSession();
  s._onRecord({ type: "extension_ui_request", id: "u1", method: "editor", title: "host-bridge", prefill: "nonsense-json" });
  assert.equal(s.pendingUi.size, 0);
  s._onRecord({ type: "extension_ui_request", id: "u2", method: "confirm", title: "Allow?" });
  assert.equal(s.pendingUi.size, 1);
  assert.equal(s.pendingUi.get("u2").method, "confirm");
});

test("wait resolves settled when the run settles", async () => {
  const s = mkSession();
  s._connected = true;
  s._onRecord({ type: "agent_start" });
  const p = s.wait({ timeoutMs: 1000 });
  assert.equal(await settledBy(p), false); // in progress, still waiting
  s._onRecord({ type: "agent_settled", aborted: false });
  assert.deepEqual(await p, { result: "settled", aborted: false });
});

test("wait resolves blocked instantly when a dialog is parked", async () => {
  const s = mkSession();
  s._connected = true;
  const p = s.wait({ timeoutMs: 1000 });
  s._onRecord({ type: "extension_ui_request", id: "u1", method: "confirm", title: "Allow?" });
  const r = await p;
  assert.equal(r.result, "blocked");
  assert.deepEqual(r.pending.map((x) => x.method), ["confirm"]);
  assert.equal(s.pendingUi.has("u1"), true); // still parked for the answerer
});

test("wait resolves disconnected on terminal close", async () => {
  const s = mkSession();
  s._connected = true;
  const p = s.wait({ timeoutMs: 1000 });
  s._onClose();
  assert.deepEqual(await p, { result: "disconnected" });
  assert.equal(s.connected, false);
});

test("two waiters both resolve", async () => {
  const s = mkSession();
  s._connected = true;
  s._onRecord({ type: "agent_start" });
  const [a, b] = [s.wait({ timeoutMs: 1000 }), s.wait({ timeoutMs: 1000 })];
  s._onRecord({ type: "agent_settled", aborted: false });
  assert.deepEqual(await a, { result: "settled", aborted: false });
  assert.deepEqual(await b, { result: "settled", aborted: false });
});

test("wait times out when nothing happens", async () => {
  const s = mkSession();
  s._connected = true;
  const t0 = Date.now();
  const r = await s.wait({ timeoutMs: 50 });
  assert.deepEqual(r, { result: "timeout" });
  assert.ok(Date.now() - t0 >= 50);
});

test("wait on a never-run session does not insta-settle", async () => {
  const s = mkSession();
  s._connected = true;
  const p = s.wait({ timeoutMs: 80 });
  assert.equal(await settledBy(p), false);
  assert.deepEqual(await p, { result: "timeout" });
});

test("wait insta-resolves settled only after a run", async () => {
  const s = mkSession();
  s._connected = true;
  s._onRecord({ type: "agent_start" });
  s._onRecord({ type: "agent_settled", aborted: false });
  assert.deepEqual(await s.wait({ timeoutMs: 200 }), { result: "settled", aborted: false });
});

test("wait insta-resolves blocked when a dialog is already parked", async () => {
  const s = mkSession();
  s._connected = true;
  s._onRecord({ type: "extension_ui_request", id: "u1", method: "confirm", title: "Allow?" });
  const p = s.wait({ timeoutMs: 200 });
  assert.equal((await p).result, "blocked");
});
