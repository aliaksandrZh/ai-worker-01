import test from "node:test";
import assert from "node:assert";
import { spawn } from "node:child_process";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

const SERVER = join(dirname(fileURLToPath(import.meta.url)), "mock-server.mjs");

// start(): spawn the server, collect stdout records; next() resolves the next JSON-RPC message
async function start() {
  const child = spawn(process.execPath, [SERVER], { stdio: ["pipe", "pipe", "inherit"] });
  child.buf = "";
  child.records = [];
  child.wake = null;
  child.stdout.on("data", (d) => {
    child.buf += d.toString("utf8");
    let i;
    while ((i = child.buf.indexOf("\n")) >= 0) {
      const line = child.buf.slice(0, i);
      child.buf = child.buf.slice(i + 1);
      const rec = JSON.parse(line);
      const p = child.pending.shift();
      if (p) p.resolve(rec);
      else child.records.push(rec);
    }
  });
  child.pending = []; // next() waiters, resolved/flushed in order
  child.on("error", (e) => { child.fail = `spawn failed: ${e.message}`; });
  child.on("exit", (code) => {
    child.fail = child.fail ?? `server exited (code ${code}) before responding`;
  });
  child.next = () =>
    new Promise((resolve, reject) => {
      if (child.fail) return reject(new Error(child.fail));
      child.pending.push({ resolve, reject });
    });
  child.rpc = (req) => {
    child.stdin.write(JSON.stringify(req) + "\n");
    return child.next();
  };
  // a failed/exited server must fail every waiter instantly, never hang a test
  const flushPending = () => {
    for (const p of child.pending.splice(0)) p.reject(new Error(child.fail));
  };
  child.on("error", flushPending);
  child.on("exit", flushPending);
  return child;
}

test("initialize echoes the client protocol version", async () => {
  const s = await start();
  try {
    const r = await s.rpc({ jsonrpc: "2.0", id: 1, method: "initialize", params: { protocolVersion: "2025-06-18" } });
    assert.equal(r.jsonrpc, "2.0");
    assert.equal(r.id, 1);
    assert.equal(r.result.protocolVersion, "2025-06-18");
    assert.equal(r.result.serverInfo.name, "mock");
    assert.deepEqual(r.result.capabilities, { tools: { listChanged: false } });
  } finally {
    s.kill();
  }
});

test("tools/list exposes exactly one tool: echo", async () => {
  const s = await start();
  try {
    const r = await s.rpc({ jsonrpc: "2.0", id: 2, method: "tools/list" });
    assert.deepEqual(r.result.tools.map((t) => t.name), ["echo"]);
    assert.equal(r.result.tools[0].inputSchema.type, "object");
  } finally {
    s.kill();
  }
});

test("tools/call echoes the input text", async () => {
  const s = await start();
  try {
    const r = await s.rpc({
      jsonrpc: "2.0", id: 3, method: "tools/call",
      params: { name: "echo", arguments: { text: "ping-through-mcp" } },
    });
    assert.equal(r.result.isError, false);
    assert.deepEqual(r.result.content, [{ type: "text", text: "ping-through-mcp" }]);
  } finally {
    s.kill();
  }
});

test("unknown method answers JSON-RPC -32601", async () => {
  const s = await start();
  try {
    const r = await s.rpc({ jsonrpc: "2.0", id: 4, method: "resources/list" });
    assert.equal(r.error.code, -32601);
    assert.equal(r.id, 4);
  } finally {
    s.kill();
  }
});
