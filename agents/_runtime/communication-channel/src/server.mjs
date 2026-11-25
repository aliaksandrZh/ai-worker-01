#!/usr/bin/env node
/**
 * Communication-channel MCP server (the agent's ONE channel to the Orchestrator/User).
 *
 * Exposes ONLY the communication tools the agent's agent.json manifest lists (intersection
 * with the fixed 11-name vocabulary of docs/flows/tools.md — context tools are served by
 * other MCP servers, unknown manifest names are ignored). The server is "mute": a call is a
 * signal — it talks to nothing and publishes nothing to GitHub (publication is the
 * Orchestrator's act). Every argument payload is validated with zod against the tools.md
 * signature; an invalid payload is answered with a JSON-RPC error naming the offending field,
 * which surfaces to the agent as an isError tool result — early feedback before the
 * Orchestrator ever sees the call. A valid call returns a success result carrying a hint:
 *   - escalation tools: the flow parks until a user event resumes it as a NEW Run; the agent
 *     must still end THIS Run with report_completion
 *   - report_completion: the run can settle
 *   - everything else: a short acknowledgment
 *
 * The Runtime Bridge observes each successful call and turns it into exactly one `tool`
 * callback (docs/run-worker.md); failed calls produce no callback.
 *
 * Minimal MCP stdio server: JSON-RPC 2.0 lines over stdin/stdout (initialize,
 * notifications/initialized, tools/list, tools/call), in the style of agents/coder/run-outcome.mjs,
 * but built from source with zod validation. Usage:
 *   node dist/server.mjs /path/to/agent.json     (or env AGENT_MANIFEST)
 */
import { createInterface } from "node:readline";
import { readFileSync } from "node:fs";
import { pathToFileURL } from "node:url";
import { ESCALATION_TOOLS, TOOLS, VOCABULARY, hint, jsonSchema, zodSchema } from "./spec.mjs";

process.stdin.setEncoding("utf8");

/** agent.json -> registered tools. Only manifest names inside the vocabulary register. */
export function registerTools(manifest) {
  const listed = manifest?.tools;
  if (!Array.isArray(listed) || listed.some((t) => typeof t !== "string"))
    throw new Error("agent.json: 'tools' must be an array of tool names");
  const registered = new Map();
  for (const name of listed) {
    if (!VOCABULARY.has(name)) continue; // context tool (or stale name) — not this server's
    const spec = TOOLS[name];
    registered.set(name, {
      name,
      description: spec.description,
      inputSchema: jsonSchema(spec),
      schema: zodSchema(spec),
    });
  }
  return registered;
}

/** First zod issue -> a JSON-RPC error message naming the offending field. */
export function describeIssue(toolName, zodError) {
  const issue = zodError.issues[0];
  const path = issue.path.length ? issue.path.join(".") : "(arguments)";
  return `${toolName}: invalid '${path}' — ${issue.message}`;
}

function reply(id, result) {
  process.stdout.write(JSON.stringify({ jsonrpc: "2.0", id, result }) + "\n");
}

function error(id, code, message) {
  process.stdout.write(JSON.stringify({ jsonrpc: "2.0", id, error: { code, message } }) + "\n");
}

export function handleMessage(registered, message) {
  if (message?.id === undefined) return null; // a notification (notifications/initialized et al.)
  switch (message.method) {
    case "initialize":
      return {
        protocolVersion: message.params?.protocolVersion ?? "2024-11-05",
        capabilities: { tools: {} },
        serverInfo: { name: "communication-channel", version: "1.0.0" },
      };
    case "tools/list":
      return {
        tools: [...registered.values()].map(({ schema, ...listed }) => listed), // schema stays internal
      };
    case "tools/call": {
      const tool = registered.get(message.params?.name);
      if (!tool) return { __error: [-32602, `tool '${message.params?.name ?? "(none)"}' is not registered for this agent`] };
      const parsed = tool.schema.safeParse(message.params?.arguments ?? {});
      if (!parsed.success) return { __error: [-32602, describeIssue(tool.name, parsed.error)] };
      return { content: [{ type: "text", text: hint(tool.name) }] };
    }
    default:
      return { __error: [-32601, `unknown method: ${message.method ?? "(none)"}`] };
  }
}

function start() {
  const manifestPath = process.argv[2] ?? process.env.AGENT_MANIFEST;
  if (!manifestPath) {
    console.error("usage: node dist/server.mjs <agent.json>   (or env AGENT_MANIFEST)");
    process.exit(1);
  }
  let registered;
  try {
    registered = registerTools(JSON.parse(readFileSync(manifestPath, "utf8")));
  } catch (e) {
    console.error(`communication-channel: cannot load manifest ${manifestPath}: ${e.message}`);
    process.exit(1);
  }

  createInterface({ input: process.stdin }).on("line", (line) => {
    let message;
    try {
      message = JSON.parse(line);
    } catch {
      return; // not protocol output; skip
    }
    const result = handleMessage(registered, message);
    if (result === null) return; // notification
    if (result.__error) error(message.id, result.__error[0], result.__error[1]);
    else reply(message.id, result);
  });
}

// esbuild keeps this guard out of the bundle path check when bundled — run directly from src
// or as dist/server.mjs entry; both execute start() because the bundle entry IS this module.
if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) start();
