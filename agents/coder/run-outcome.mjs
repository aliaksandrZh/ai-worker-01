#!/usr/bin/env node
/**
 * Run-outcome MCP server (the coder image): ONE tool — `work_completed`.
 *
 * The coder agent calls it when its work is DONE (branch pushed, publications confirmed). The
 * Runtime Bridge OBSERVES the successful tool execution and MANDATES it (PI_REQUIRED_OUTCOME):
 * a settled run without the call is nudged back, and repeated silence fails the run with
 * `outcome_missing` — the control plane then marks the task AI Failed. The call itself is a
 * completion marker, not a publication: it talks to nothing.
 *
 * Minimal MCP stdio server: JSON-RPC 2.0 lines over stdin/stdout (initialize, notifications/initialized,
 * tools/list, tools/call). Dependency-free and total: every request is answered, unknown methods
 * return a JSON-RPC error, malformed lines are skipped.
 */
import { createInterface } from "node:readline";

process.stdin.setEncoding("utf8");

const tool = {
  name: "work_completed",
  description:
    "Report that your work is COMPLETE (the branch is pushed and the publications are confirmed). " +
    "Call it ONCE right before finishing. Without it the run cannot complete.",
  inputSchema: {
    type: "object",
    properties: {
      summary: {
        type: "string",
        description: "One sentence stating what was completed.",
      },
    },
    required: ["summary"],
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
        serverInfo: { name: "run-outcome", version: "1.0.0" },
      });
      break;
    case "tools/list":
      reply(message.id, { tools: [tool] });
      break;
    case "tools/call": {
      const args = message.params?.arguments ?? {};
      if (typeof args.summary !== "string" || args.summary.trim().length === 0)
        error(message.id, -32602, "work_completed requires a non-empty 'summary'");
      else
        reply(message.id, {
          content: [{ type: "text", text: `Recorded: the work is complete — ${args.summary.trim()}` }],
        });
      break;
    }
    default:
      error(message.id, -32601, `unknown method: ${message.method ?? "(none)"}`);
  }
});
