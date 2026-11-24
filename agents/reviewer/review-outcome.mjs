#!/usr/bin/env node
/**
 * Review-outcome MCP server (the reviewer image only): ONE tool — `request_changes`.
 *
 * The reviewer agent calls it when its review demands changes (after the inline comments are added
 * and the review is submitted). The Runtime Bridge OBSERVES the successful tool execution in the
 * event stream and reports the verdict as `outcome: "changes_requested"` in the terminal `completed`
 * callback — the Orchestrator's review→fix loop reads exactly that. The absence of the call means
 * the review passed. Nothing here talks to GitHub; the tool is a verdict marker, not a publication.
 *
 * Minimal MCP stdio server: JSON-RPC 2.0 lines over stdin/stdout (initialize, notifications/initialized,
 * tools/list, tools/call). Deliberately dependency-free and total: every request is answered, unknown
 * methods return a JSON-RPC error, malformed lines are skipped.
 */
import { createInterface } from "node:readline";

process.stdin.setEncoding("utf8");

const tool = {
  name: "request_changes",
  description:
    "Report that your review REQUESTS CHANGES (fixes are needed). Call it AFTER you added the inline " +
    "comments and submitted the review; include a one-sentence reason. If the review passed, do NOT call it.",
  inputSchema: {
    type: "object",
    properties: {
      reason: {
        type: "string",
        description: "One sentence summarizing why the changes are needed.",
      },
    },
    required: ["reason"],
    additionalProperties: false,
  },
};

function reply(id, result) {
  process.stdout.write(JSON.stringify({ jsonrpc: "2.0", id, result }) + "\n");
}

function error(id, code, message) {
  process.stdout.write(JSON.stringify({ jsonrpc: "2.0", id, error: { code, message } }) + "\n");
}

createInterface({ input: process.stdin }).on("line", (line) => {
  let message;
  try {
    message = JSON.parse(line);
  } catch {
    return; // not protocol output; skip
  }
  if (message?.id === undefined) return; // a notification (notifications/initialized et al.)
  switch (message.method) {
    case "initialize":
      reply(message.id, {
        protocolVersion: message.params?.protocolVersion ?? "2024-11-05",
        capabilities: { tools: {} },
        serverInfo: { name: "review-outcome", version: "1.0.0" },
      });
      break;
    case "tools/list":
      reply(message.id, { tools: [tool] });
      break;
    case "tools/call": {
      const args = message.params?.arguments ?? {};
      if (typeof args.reason !== "string" || args.reason.trim().length === 0)
        error(message.id, -32602, "request_changes requires a non-empty 'reason'");
      else
        reply(message.id, {
          content: [{ type: "text", text: `Recorded: the review requests changes — ${args.reason.trim()}` }],
        });
      break;
    }
    default:
      error(message.id, -32601, `unknown method: ${message.method ?? "(none)"}`);
  }
});
