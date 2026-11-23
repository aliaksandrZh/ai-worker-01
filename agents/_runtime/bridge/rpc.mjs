/**
 * pi RPC command/response correlation (pi docs `rpc.md`, `rpc-commands.md`): every command may carry a
 * string `id`; the matching stdout `response` record repeats it. The Bridge needs exactly two rounds:
 * the launch handshake (`get_state` → `data.sessionFile`, the real per-Run session file path recorded
 * per decision 57) and the single task-input delivery (`prompt` → `data.disposition === "started"` —
 * "started" means a run began and will end with `agent_settled`; "queued"/"handled" would not).
 */
export class RpcCorrelator {
  /**
   * @param send one framed command line to the agent's stdin (`(line: string) => void`)
   * @param timeoutMs per-command wait budget; a timeout rejects (the Bridge turns it into the failure path)
   */
  constructor(send, timeoutMs = 60_000) {
    this.sequence = 0;
    this.pending = new Map(); // id → {resolve, reject, timer}
    this.send = send;
    this.timeoutMs = timeoutMs;
  }

  /**
   * Feeds one parsed stdout record. Returns true when the record satisfied a pending response (the
   * caller then skips event mapping); event lines and unrelated records return false.
   */
  onRecord(record) {
    if (record?.type !== "response" || typeof record.id !== "string") return false;
    const pending = this.pending.get(record.id);
    if (!pending) return false;
    this.pending.delete(record.id);
    clearTimeout(pending.timer);
    if (record.success === false)
      pending.reject(new RpcCommandError(record.error ?? "unspecified error"));
    else
      pending.resolve(record);
    return true;
  }

  /**
   * Sends one command and resolves with its matching `response` record. One outstanding command at a
   * time is the whole the Bridge handshake needs — a second concurrent command is a caller bug.
   */
  command(type, extra = {}) {
    if (this.pending.size > 0)
      return Promise.reject(new Error(`rpc command '${type}' refused: a command handshake is already in flight`));
    this.sequence += 1;
    const id = `req-${this.sequence}`;
    return new Promise((resolve, reject) => {
      const timer = setTimeout(() => {
        this.pending.delete(id);
        reject(new Error(`rpc command '${type}' timed out after ${this.timeoutMs}ms`));
      }, this.timeoutMs);
      this.pending.set(id, { resolve, reject, timer });
      this.send(JSON.stringify({ id, type, ...extra }));
    });
  }
}

/** A `success: false` response (04 §4: the prompt was rejected before acceptance). */
export class RpcCommandError extends Error {
  constructor(message) {
    super(message);
    this.name = "RpcCommandError";
  }
}

/** The `prompt` disposition ("started" | "queued" | "handled") — only "started" begins a run. */
export function promptDisposition(response) {
  return response?.data?.disposition;
}
