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
 * notifications/initialized, tools/list, tools/call), built from source with zod validation. Usage:
 *   node dist/server.mjs /path/to/agent.json     (or env AGENT_MANIFEST)
 */
import { createInterface } from "node:readline";
import { execFileSync } from "node:child_process";
import { readFileSync } from "node:fs";
import { pathToFileURL } from "node:url";
import { ESCALATION_TOOLS, TOOLS, VOCABULARY, hint, jsonSchema, zodSchema } from "./spec.mjs";

process.stdin.setEncoding("utf8");

/**
 * The workspace guard behind CC_COMPLETION_GUARD (set in the coder profile image): before the
 * Orchestrator ever sees a delivery call, the workspace git state must be delivery-ready — the
 * agent branch exists, is not the default branch, and its local HEAD is pushed to origin
 * (docs/coder APPEND_SYSTEM "The agent branch"). A violation is a JSON-RPC error — an isError
 * tool result the agent fixes in-run (create/switch/push), never a poisoned downstream event.
 * `defaultBranch` comes from GIT_DEFAULT_BRANCH; without it only the repository is checked.
 */
export function workspaceGuard(workspace, defaultBranch) {
  return () => {
    try {
      const branch = execFileSync("git", ["-C", workspace, "rev-parse", "--abbrev-ref", "HEAD"],
        { encoding: "utf8" }).trim();
      if (!defaultBranch || branch !== defaultBranch) {
        const head = execFileSync("git", ["-C", workspace, "rev-parse", "HEAD"], { encoding: "utf8" }).trim();
        const remote = execFileSync("git", ["-C", workspace, "ls-remote", "origin", `refs/heads/${branch}`],
          { encoding: "utf8" }).trim();
        if (remote.split("\t")[0] !== head)
          return `branch '${branch}' is missing on origin or HEAD is unpushed — git push, then call again`;
      } else {
        return `workspace is on the default branch '${branch}' — create the agent branch ` +
          "agent/<issue-number>-<short-slug>, commit, push, then call again";
      }
      return null;
    } catch {
      return `no git repository state in ${workspace} — cannot verify the agent branch`;
    }
  };
}

/** agent.json -> registered tools. Only manifest names inside the vocabulary register. */
export function registerTools(manifest, guard) {
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
      schema: zodSchema({ ...spec, name }, guard),
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
    const guard = process.env.CC_COMPLETION_GUARD
      ? workspaceGuard(process.env.CC_COMPLETION_GUARD, process.env.GIT_DEFAULT_BRANCH)
      : undefined;
    registered = registerTools(JSON.parse(readFileSync(manifestPath, "utf8")), guard);
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
