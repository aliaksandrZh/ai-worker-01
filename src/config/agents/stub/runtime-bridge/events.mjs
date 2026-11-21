/**
 * pi RPC → RunEvent mapping (04 §4, decision 58): the realtime source is Pi's stdout JSONL stream.
 * `message_update` text deltas are AGGREGATED before persistence (one RunEvent, not one per token):
 * deltas accumulate and flush as `assistant_output` on the event cadence or at any turn/tool boundary.
 * Heartbeats, thinking deltas, and queue updates are never progress and are not produced here.
 *
 * Two record shapes share this mapper (decision 56: the image owns the agent):
 * - real Pi (pi 04 §4 shapes): `message_update` carries `usage` + the nested `assistantMessageEvent`
 *   (`{type:"text_delta", delta}`); tool/agent events carry their fields at the record top level
 *   (pi docs `json.md`), and `tool_execution_end` carries `isError`.
 * - the reference stub protocol (config/agents/stub/stub-agent/pi-stub.mjs) nests event fields under
 *   `data` and reports `status` instead of `isError`.
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
    // real pi keeps stream fields top-level; the stub nests them under `data`
    const data = rpc.data ?? rpc;
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
      case "message_update": {
        // aggregated assistant output: non-empty text deltas only (decision 58). Real pi wraps the
        // streaming event in `assistantMessageEvent` and TYPES every event — only `text_delta` is
        // assistant output (`thinking_delta` is never progress); the stub protocol omits the type.
        const streamEvent = rpc.assistantMessageEvent ?? data;
        const isTextDelta = streamEvent.type === undefined || streamEvent.type === "text_delta";
        if (isTextDelta && typeof streamEvent.delta === "string" && streamEvent.delta.length > 0)
          this.pending.push(streamEvent.delta);
        break;
      }
      case "tool_execution_start":
        this.flushAggregated();
        this.push("tool_started", { toolCallId: data.toolCallId, toolName: data.toolName });
        break;
      case "tool_execution_update":
        this.push("tool_progress", { toolCallId: data.toolCallId, toolName: data.toolName });
        break;
      case "tool_execution_end":
        this.flushAggregated();
        this.push("tool_completed", endPayload(data));
        break;
      case "agent_settled":
        this.flushAggregated();
        this.push("agent_settled", data); // real pi: {aborted}; stub: {}
        this.settled = true;
        break;
      case "error":
        // pi error events/notices are not RunEvents; the crash path (non-zero exit / no settle) carries them
        return true;
      default:
        return false; // unmapped stream type (`response`, thinking deltas, queue updates — never events)
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

/** Sparse end payload: carries whichever of `status` (stub) / `isError` (pi) the stream provided. */
function endPayload(data) {
  const payload = { toolCallId: data.toolCallId, toolName: data.toolName };
  if (data.status !== undefined) payload.status = data.status;
  if (data.isError !== undefined) payload.isError = data.isError;
  return payload;
}

/**
 * Decision-59 correlation helper: a `tool_execution_end` counts toward requiredPublications only when
 * the tool execution actually finished WITHOUT an error (pi carries `isError`; the stub `status`).
 */
export function isSuccessfulToolEnd(payload) {
  if (!payload || typeof payload !== "object" || typeof payload.toolCallId !== "string") return false;
  if (payload.isError === true) return false;
  return payload.status === undefined || payload.status === "completed";
}
