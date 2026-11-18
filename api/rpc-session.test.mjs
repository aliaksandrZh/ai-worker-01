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

test("echoed commands are dropped from the event buffer, real events kept", () => {
  const s = mkSession();
  s._onRecord({ id: "api-1", type: "get_state" }); // echo of our own sent command (PTY echo)
  s._onRecord({ type: "agent_start" }); // real session event
  s._onRecord({ type: "prompt" }); // echo of a sent prompt command
  const kept = s.eventBuf;
  assert.equal(kept.length, 1);
  assert.equal(kept[0].record.type, "agent_start");
});
