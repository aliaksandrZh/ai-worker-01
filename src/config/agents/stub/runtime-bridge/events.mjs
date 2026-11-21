/**
 * pi RPC → RunEvent mapping (04 §4, decision 58): the realtime source is Pi's stdout JSONL stream.
 * `message_update` text deltas are AGGREGATED before persistence (one RunEvent, not one per token):
 * deltas accumulate and flush as `assistant_output` on the event cadence or at any turn/tool boundary.
 * Heartbeats, thinking deltas, and queue updates are never progress and are not produced here.
 */
export class EventMapper {
  /**
   * @param emit callback receiving `{id, route:'events', body:{eventId, type, occurredAt, payload}}`
   * @param eventCadenceSeconds aggregation window for assistant text deltas
   */
  constructor(emit, eventCadenceSeconds = 5) {
    this.emit = emit;
    this.eventCadenceSeconds = eventCadenceSeconds;
    this.sequence = 0;
    this.pending = []; // [{id, text}] — deltas since the last flush
    this.settled = false;
  }

  nextEventId(prefix = "evt") {
    this.sequence += 1;
    return `${prefix}-${this.sequence}-${Date.now()}`;
  }

  /** One stdout JSONL line from the agent stream. */
  onLine(line) {
    let rpc;
    try {
      rpc = JSON.parse(line);
    } catch {
      return false; // not protocol output; the caller logs it verbatim to stdout.log
    }
    const now = new Date().toISOString();
    const data = rpc.data ?? {};
    switch (rpc.type) {
      case "agent_start":
        this.push("agent_started", data);
        break;
      case "turn_start":
        this.push("turn_started", data);
        break;
      case "turn_end":
        this.flushAggregated();
        this.push("turn_completed", data);
        break;
      case "message_update":
        // aggregated assistant output: non-empty text deltas only (decision 58)
        if (typeof data.delta === "string" && data.delta.length > 0)
          this.pending.push(data.delta);
        break;
      case "tool_execution_start":
        this.flushAggregated();
        this.push("tool_started", { toolCallId: data.toolCallId, toolName: data.toolName });
        break;
      case "tool_execution_update":
        this.push("tool_progress", { toolCallId: data.toolCallId, step: data.step });
        break;
      case "tool_execution_end":
        this.flushAggregated();
        this.push("tool_completed", { toolCallId: data.toolCallId, status: data.status });
        break;
      case "agent_settled":
        this.flushAggregated();
        this.push("agent_settled", data);
        this.settled = true;
        break;
      case "error":
        // pi error events are not RunEvents; the crash path (non-zero exit / no settle) carries them
        return true;
      default:
        return false; // unmapped stream type (thinking deltas, queue updates — never events)
    }
    return true;
  }

  /** Flushes accumulated assistant text as one aggregated `assistant_output` event. */
  flushAggregated() {
    if (this.pending.length === 0) return;
    const text = this.pending.join("");
    this.pending = [];
    this.push("assistant_output", { text });
  }

  push(type, payload) {
    this.emit({
      id: `evt-${this.sequence}-${Date.now()}`,
      route: "events",
      body: {
        eventId: this.nextEventId(),
        type,
        occurredAt: new Date().toISOString(),
        payload,
      },
    });
  }
}
