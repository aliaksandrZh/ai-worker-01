// Pi RPC session: one Komodo attach-mode terminal websocket = pi's RPC stdin/stdout.
// Protocol per pi docs (rpc.md / rpc-commands.md / rpc-extension-ui.md); modeled on
// pi's exported RpcClient, but owning the websocket (RpcClient only spawns child processes
// and cannot answer extension_ui_request dialogs).
import { exec } from "node:child_process";
import { mkdirSync, createWriteStream } from "node:fs";
import { join } from "node:path";

// Split a decoded chunk against previously buffered partial text. Lines lose their LF
// and a single optional trailing CR (pi tolerates CRLF input; terminals may emit it).
export function _splitLines(chunk, rest) {
  const s = rest + (typeof chunk === "string" ? chunk : chunk.toString("utf8"));
  const parts = s.split("\n");
  return {
    lines: parts.slice(0, -1).map((l) => (l.endsWith("\r") ? l.slice(0, -1) : l)),
    rest: parts[parts.length - 1],
  };
}

const EVENT_BUF_CAP = 2000;
const DEFAULT_RPC_TIMEOUT_MS = 120000;

// Session events pi emits on stdout (docs/json.md). Records with any other type are
// PTY echoes of our own sent commands — parse fine (echo), carry no data, get dropped.
const KNOWN_RECORD_TYPES = new Set([
  "agent_start", "agent_end", "agent_settled", "turn_start", "turn_end",
  "message_start", "message_update", "message_end",
  "tool_execution_start", "tool_execution_update", "tool_execution_end",
  "queue_update", "entry_appended", "session_info_changed", "thinking_level_changed",
  "compaction_start", "compaction_end", "auto_retry_start", "auto_retry_end",
  "summarization_retry_scheduled", "summarization_retry_attempt_start", "summarization_retry_finished",
  "bash_execution_update", "extension_error",
]);

export class PiRpcSession {
  constructor({ komodo, server, container, terminal, logsDir, hostBridge = {}, onSignal = null }) {
    this.komodo = komodo;
    this.server = server;
    this.container = container;
    this.terminal = terminal;
    this.hostBridge = { cwd: hostBridge.cwd ?? process.cwd(), timeoutMs: hostBridge.timeoutMs ?? 60000 };
    this.ws = null;
    this.decoder = null;
    this.buf = "";
    this.logStream = null;
    this.pending = new Map(); // request id -> { resolve, reject, timer }
    this.pendingUi = new Map(); // extension_ui_request id -> { method, payload, receivedAt }
    this.eventBuf = []; // [{seq, ts, record}] — seq is monotonic, survives buffer shifts
    this.eventSeq = 0;
    this.rpcSeq = 0;
    this.runInProgress = false;
    this.hasRun = false;
    this.lastSettled = null;
    this.waiters = new Set(); // waiter objects { resolve, timer } waiting for settled/blocked
    this.onSignal = onSignal;
    this._disconnectedSignaled = false;
    this.stateInfo = null;
    this._connected = false;
    this._handshake = null; // { resolve, reject, timer } while connecting
    mkdirSync(logsDir, { recursive: true });
    this.logPath = join(logsDir, `${terminal}.log`);
    this._logStream = createWriteStream(this.logPath, { flags: "a" });
  }

  get connected() {
    return this._connected;
  }

  _log(dir, line) {
    try {
      this._logStream.write(JSON.stringify({ ts: Date.now(), dir, line }) + "\n");
    } catch {
      /* logging must never break the session */
    }
  }

  _rawSend(payload) {
    this._log("out", JSON.stringify(payload));
    // Terminal wire protocol (komodo bin/cli/src/command/terminal.rs): Forward = payload bytes + 0x01 tag
    this.ws.send(new Uint8Array([...new TextEncoder().encode(JSON.stringify(payload) + "\n"), 0x01]));
  }

  // Periphery starts forwarding stdin only after this single Begin (0x00) byte.
  _sendBegin() {
    this._log("out", "<BEGIN>");
    this.ws.send(new Uint8Array([0x00]));
  }

  // Attach to the terminal, wait for login + first get_state answer.
  connect({ initTimeoutMs = 20000 } = {}) {
    if (this.ws) this.close();
    this.decoder = new TextDecoder();
    this.buf = "";
    return new Promise((resolve, reject) => {
      const fail = (err) => {
        clearTimeout(this._handshake?.timer);
        this._handshake = null;
        reject(err);
      };
      this._handshake = {
        resolve,
        reject,
        timer: setTimeout(() => fail(new Error(`terminal connect timeout after ${initTimeoutMs}ms`)), initTimeoutMs),
      };
      this.ws = this.komodo.connect_terminal({
        query: {
          target: { type: "Container", params: { server: this.server, container: this.container } },
          terminal: this.terminal,
          init: { mode: "attach", recreate: "Never" },
        },
        on_login: () => {
          this._connected = true;
          this._sendBegin();
          this.rpc({ type: "get_state" }, { timeoutMs: initTimeoutMs })
            .then((r) => {
              this.stateInfo = r.success ? r.data : null;
              if (this._handshake) {
                clearTimeout(this._handshake.timer);
                this._handshake = null;
                resolve({ state: this.stateInfo });
              }
            })
            .catch(fail);
        },
        on_message: (e) => this._onData(e.data),
        on_close: () => this._onClose(),
      });
      this.ws.onerror = (err) => fail(new Error(`terminal websocket error: ${err?.message ?? err}`));
    });
  }

  _onData(data) {
    try {
      if (typeof data === "string") {
        // Core rejects the terminal (auth/config/already-exists) with a text frame before
        // any binary forwarding starts — fail the connect fast instead of timing out.
        if (data.startsWith("ERROR:") && this._handshake) {
          this.ws?.close();
          this._handshake.reject(new Error(data));
          this._handshake = null;
        }
        if (data.includes("pty exited")) this._onClose();
        return;
      }
      // TextDecoder(stream) handles UTF-8 multibyte chars split across frames.
      const chunk = this.decoder.decode(new Uint8Array(data), { stream: true });
      const { lines, rest } = _splitLines(chunk, this.buf);
      this.buf = rest;
      for (const line of lines) {
        this._log("in", line);
        let rec;
        try {
          rec = JSON.parse(line);
        } catch {
          continue; // echo/garbage/PTY noise — dropped after logging
        }
        this._onRecord(rec);
      }
    } catch (err) {
      this._log("error", `frame handling: ${err?.message ?? err}`);
    }
  }

  _onRecord(rec) {
    if (rec.type === "response" && rec.id && this.pending.has(rec.id)) {
      const p = this.pending.get(rec.id);
      this.pending.delete(rec.id);
      clearTimeout(p.timer);
      p.resolve(rec);
      return;
    }
    if (rec.type === "extension_ui_request") {
      this._pushEvent(rec);
      if (rec.method === "editor" && rec.title === "host-bridge") {
        this._runHostBridge(rec); // auto-answered — never parked for humans
      } else {
        this.pendingUi.set(rec.id, { method: rec.method, payload: rec, receivedAt: Date.now() });
        this._resolveWaiters({ result: "blocked", pending: this.listPendingUi() });
        this._emitSignal({ event: "blocked", pending: this.listPendingUi() });
      }
      return;
    }
    if (!KNOWN_RECORD_TYPES.has(rec.type)) return; // PTY echo of our own command
    if (rec.type === "agent_start") {
      this.runInProgress = true;
      this.hasRun = true;
    } else if (rec.type === "agent_settled") {
      this.runInProgress = false;
      this.lastSettled = { ts: Date.now(), aborted: rec.aborted === true };
      this._resolveWaiters({ result: "settled", aborted: rec.aborted === true });
      this._emitSignal({ event: "settled", aborted: rec.aborted === true });
    }
    this._pushEvent(rec);
  }

  status() {
    return {
      connected: this._connected,
      phase: this.pendingUi.size > 0 ? "blocked" : this.runInProgress ? "running" : "idle",
      hasRun: this.hasRun,
      lastSettled: this.lastSettled,
      pendingUi: this.listPendingUi(),
    };
  }

  wait({ timeoutMs = 600000 } = {}) {
    return new Promise((resolve) => {
      if (!this._connected) return resolve({ result: "disconnected" });
      if (this.pendingUi.size > 0)
        return resolve({ result: "blocked", pending: this.listPendingUi() });
      if (this.hasRun && !this.runInProgress)
        return resolve({ result: "settled", aborted: this.lastSettled?.aborted ?? false });
      const waiter = {
        resolve,
        // shortcut: a dialog answered before the consumer polls again is never observed — orchestrators needing blocking history should read /events?type=agent_settled instead.
        timer: setTimeout(() => {
          this.waiters.delete(waiter);
          resolve({ result: "timeout" });
        }, timeoutMs),
      };
      this.waiters.add(waiter);
    });
  }

  _resolveWaiters(result) {
    const ws = [...this.waiters];
    this.waiters.clear();
    for (const w of ws) {
      clearTimeout(w.timer);
      w.resolve(result);
    }
  }

  _emitSignal(sig) {
    if (sig.event === "disconnected" && this._disconnectedSignaled) return; // ws close may fire twice
    if (sig.event === "disconnected") this._disconnectedSignaled = true;
    this.onSignal?.(sig);
  }

  _pushEvent(rec) {
    this.eventBuf.push({ seq: this.eventSeq++, ts: Date.now(), record: rec });
    while (this.eventBuf.length > EVENT_BUF_CAP) this.eventBuf.shift();
  }

  _onClose() {
    this._connected = false;
    this._resolveWaiters({ result: "disconnected" });
    this._emitSignal({ event: "disconnected" });
    const err = new Error("terminal connection closed");
    const handshake = this._handshake;
    this._handshake = null;
    if (handshake) {
      clearTimeout(handshake.timer);
      handshake.reject(err);
    }
    for (const p of this.pending.values()) {
      clearTimeout(p.timer);
      p.reject(err);
    }
    this.pending.clear();
  }

  _runHostBridge(req) {
    let command;
    try {
      command = JSON.parse(req.prefill).command;
    } catch (err) {
      return this._answerUi(req.id, { value: JSON.stringify({ error: `bad envelope: ${err.message}` }) });
    }
    exec(command, { cwd: this.hostBridge.cwd, timeout: this.hostBridge.timeoutMs }, (err, stdout, stderr) => {
      if (err && typeof err.code !== "number" && !err.killed) {
        return this._answerUi(req.id, { value: JSON.stringify({ error: String(err.message ?? err) }) });
      }
      const result = {
        exitCode: typeof err?.code === "number" ? err.code : 0,
        stdout,
        stderr,
        killed: Boolean(err?.killed),
      };
      this._answerUi(req.id, { value: JSON.stringify(result) });
    });
  }

  _answerUi(id, body) {
    if (!this._connected) return false;
    this._rawSend({ type: "extension_ui_response", id, ...body });
    return true;
  }

  // Send one RPC command, resolve with its response record (success:true or false).
  rpc(cmd, { timeoutMs = DEFAULT_RPC_TIMEOUT_MS } = {}) {
    if (!this.ws || !this._connected) return Promise.reject(new Error("session not connected"));
    const id = `api-${++this.rpcSeq}`;
    return new Promise((resolve, reject) => {
      const timer = setTimeout(() => {
        this.pending.delete(id);
        reject(new Error(`rpc timeout after ${timeoutMs}ms: ${cmd.type}`));
      }, timeoutMs);
      this.pending.set(id, { resolve, reject, timer });
      this._rawSend({ ...cmd, id });
    });
  }

  // Events with seq strictly greater than `since`, optionally filtered by record type.
  events(since = 0, types = null) {
    const wanted = types ? new Set(types.split(",").map((t) => t.trim()).filter(Boolean)) : null;
    const records = this.eventBuf.filter(
      (e) => e.seq > since && (!wanted || wanted.has(e.record.type)),
    );
    return { records, nextSeq: Math.max(since, this.eventSeq - 1) };
  }

  listPendingUi() {
    return [...this.pendingUi.values()];
  }

  answerUi(id, body) {
    const ok = this._answerUi(id, body);
    if (ok) this.pendingUi.delete(id);
    return ok;
  }

  close() {
    this._connected = false;
    if (this.ws) {
      try {
        this.ws.close();
      } catch {
        /* already closed */
      }
      this.ws = null;
    }
  }
}
