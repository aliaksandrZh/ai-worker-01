/**
 * pi RPC → RunEvent mapping: the realtime source is Pi's stdout JSONL stream.
 * `message_update` text deltas are AGGREGATED before persistence (one RunEvent, not one per token):
 * deltas accumulate and flush as `assistant_output` on the event cadence or at any turn/tool boundary.
 * Heartbeats, thinking deltas, and queue updates are never progress and are not produced here.
 *
 * Fail-closed: a `permission_denied` stream record (the unattended
 * permission policy's denial of a tool/MCP operation) maps to the `permission_blocked` RunEvent and
 * is collected in `deniedToolCallIds`/`deniedToolNames` — the completion mandate turns a denied
 * `report_completion` into a terminal `required_permission_denied` failure. The denial itself is
 * answered to the agent by the permission layer (never a parked dialog); the Bridge only records
 * and verifies.
 *
 * One canonical record shape (pi docs `json.md`; the stub speaks it since ticket 05): stream
 * fields live at the record top level, `message_update` carries the typed `assistantMessageEvent`,
 * and `tool_execution_end` carries `isError`. No nested-`data` / `status` legacy shapes exist.
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
    this.deniedToolCallIds = new Set(); // tool calls denied by the permission policy
    this.deniedToolNames = new Set(); // tool NAMES denied by the permission policy (bare, normalized)
    this.argsByCallId = new Map(); // toolCallId → the tool_execution_start args
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
    switch (rpc.type) {
      case "agent_start":
        this.push("agent_started", {});
        break;
      case "turn_start":
        this.push("turn_started", {});
        break;
      case "turn_end":
        this.flushAggregated();
        this.push("turn_completed", {});
        break;
      case "message_update": {
        // aggregated assistant output: typed non-empty text deltas only. The typed streaming
        // event is canonical — only `text_delta` is assistant output (`thinking_delta` and
        // untyped records are never progress).
        const streamEvent = rpc.assistantMessageEvent;
        if (streamEvent?.type === "text_delta"
            && typeof streamEvent.delta === "string" && streamEvent.delta.length > 0)
          this.pending.push(streamEvent.delta);
        break;
      }
      case "tool_execution_start":
        this.flushAggregated();
        if (typeof rpc.toolCallId === "string") this.argsByCallId.set(rpc.toolCallId, rpc.args ?? {});
        this.push("tool_started", { toolCallId: rpc.toolCallId, toolName: rpc.toolName });
        break;
      case "tool_execution_update":
        this.push("tool_progress", { toolCallId: rpc.toolCallId, toolName: rpc.toolName });
        break;
      case "tool_execution_end":
        this.flushAggregated();
        this.push("tool_completed", { toolCallId: rpc.toolCallId, toolName: rpc.toolName, isError: rpc.isError === true });
        break;
      case "permission_denied":
        // recorded as `permission_blocked` (never progress, never a timer reset) and
        // tracked so the completion mandate can fail a denied `report_completion` immediately
        this.flushAggregated();
        if (typeof rpc.toolCallId === "string") this.deniedToolCallIds.add(rpc.toolCallId);
        if (typeof rpc.toolName === "string") this.deniedToolNames.add(normalizeToolName(rpc.toolName));
        this.push("permission_blocked", {
          ...(typeof rpc.toolCallId === "string" ? { toolCallId: rpc.toolCallId } : {}),
          ...(typeof rpc.toolName === "string" ? { toolName: rpc.toolName } : {}),
          ...(typeof rpc.reason === "string" ? { reason: rpc.reason } : {}),
        });
        break;
      case "agent_settled":
        this.flushAggregated();
        this.push("agent_settled", { aborted: rpc.aborted === true });
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

/**
 * MCP tool records report the server-prefixed name (`mcp__github__add_issue_comment`) while the
 * bridge's vocabulary and mandates match bare tool names. The normalized name is the last
 * `__` segment; a bare name passes through.
 */
export function normalizeToolName(name) {
  return typeof name === "string" && name.includes("__") ? name.split("__").pop() : name;
}

/**
 * Correlation helper: a `tool_execution_end` counts toward the observed call sets (the `tool`
 * route and the completion mandate) only when the tool execution finished WITHOUT an error
 * (the canonical `isError` field).
 */
export function isSuccessfulToolEnd(payload) {
  if (!payload || typeof payload !== "object" || typeof payload.toolCallId !== "string") return false;
  return payload.isError !== true;
}
