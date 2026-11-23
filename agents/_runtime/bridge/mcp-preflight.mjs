/**
 * MCP preflight: BEFORE the task prompt is delivered, the Bridge verifies that every stdio server in
 * the baked mcp.json actually starts and completes the MCP handshake (initialize → tools/list).
 * Pi only reports "MCP connection closed" with no reason, and an agent without its MCP publication
 * tools either cannot publish or starts working around them (bash + REST) — unverifiable work.
 * Fail fast instead: the run fails with `mcp_unavailable` carrying the server's real stderr —
 * no task, no nudges, no work. The agent process is never even launched on a failed preflight.
 */
import { execFileSync, spawn } from "node:child_process";
import { existsSync, readFileSync } from "node:fs";
import { join } from "node:path";

/** The stdio servers of the baked mcp.json; null when the profile bakes no mcp.json (e.g. the stub). */
export function loadMcpServers(configDir) {
  const path = join(configDir, "mcp.json");
  if (!existsSync(path)) return null;
  const parsed = JSON.parse(readFileSync(path, "utf8"));
  const servers = parsed?.mcpServers;
  if (!servers || typeof servers !== "object") return [];
  return Object.entries(servers)
    .filter(([, config]) => typeof config?.command === "string") // stdio servers only
    .map(([name, config]) => ({ name, command: config.command, args: config.args ?? [], env: config.env ?? {} }));
}

/** Expands mcp.json env values the way pi does (docs/mcp.md): `${VAR}` interpolation, and a
 * `!command` shell-out that must make up the whole value. */
export function expandEnvValue(value, baseEnv) {
  if (typeof value !== "string") return value;
  let out = value.replace(/\$\{([^}]+)\}/g, (_match, name) => baseEnv[name] ?? "");
  if (out.startsWith("!")) out = execFileSync("sh", ["-c", out.slice(1)], { encoding: "utf8" }).trim();
  return out;
}

/** One MCP handshake against one stdio server.
 * @returns null when the server is healthy, else the diagnosable failure string. */
export async function preflightServer(server, baseEnv = process.env, timeoutMs = 10_000) {
  const { name } = server;
  const env = { ...baseEnv };
  for (const [key, value] of Object.entries(server.env ?? {})) env[key] = expandEnvValue(value, baseEnv);

  const stderrTail = [];
  const child = spawn(server.command, server.args, { env, stdio: ["pipe", "pipe", "pipe"] });
  child.stderr.on("data", (chunk) => {
    stderrTail.push(String(chunk));
    if (stderrTail.length > 20) stderrTail.shift(); // keep the tail — the failure is at the end
  });

  const send = (message) => child.stdin.write(JSON.stringify(message) + "\n");
  try {
    send({
      jsonrpc: "2.0", id: 1, method: "initialize",
      params: { protocolVersion: "2025-06-18", capabilities: {}, clientInfo: { name: "pi-agent-bridge-preflight", version: "1.0.0" } },
    });
    const init = await readJsonResponse(child, timeoutMs);
    if (init.error) throw new Error(`initialize rejected: ${JSON.stringify(init.error).slice(0, 200)}`);
    if (!init.result) throw new Error("initialize returned no result");

    send({ jsonrpc: "2.0", method: "notifications/initialized" });
    send({ jsonrpc: "2.0", id: 2, method: "tools/list" });
    const tools = await readJsonResponse(child, timeoutMs);
    if (tools.error) throw new Error(`tools/list rejected: ${JSON.stringify(tools.error).slice(0, 200)}`);
    if (!Array.isArray(tools.result?.tools)) throw new Error("tools/list returned no tools");
    return null; // healthy
  } catch (error) {
    const diagnostics = stderrTail.join("").trim();
    return `${name}: ${error.message}${diagnostics ? ` — ${diagnostics.slice(-400)}` : ""}`;
  } finally {
    child.kill("SIGTERM");
    setTimeout(() => child.kill("SIGKILL"), 2_000).unref();
  }
}

/** @returns null when every baked stdio server is healthy, else the joined failure report. */
export async function preflightMcp(configDir, baseEnv = process.env, timeoutMs = 10_000) {
  let servers;
  try {
    servers = loadMcpServers(configDir);
  } catch (error) {
    return `mcp.json unreadable — ${error.message}`;
  }
  if (servers === null || servers.length === 0) return null; // nothing baked — nothing to gate on
  const failures = [];
  for (const server of servers) {
    const failure = await preflightServer(server, baseEnv, timeoutMs);
    if (failure !== null) failures.push(failure);
  }
  return failures.length > 0 ? failures.join("; ") : null;
}

/** Waits for the next JSON-RPC response record on the child's stdout (notifications skipped).
 * Rejects on timeout or child exit — the preflight never hangs the run start. */
function readJsonResponse(child, timeoutMs) {
  return new Promise((resolve, reject) => {
    const timer = setTimeout(() => {
      cleanup();
      reject(new Error(`no MCP response within ${timeoutMs}ms`));
    }, Math.max(timeoutMs, 1));
    let buffer = "";
    const onStdout = (chunk) => {
      buffer += chunk;
      let newline;
      while ((newline = buffer.indexOf("\n")) >= 0) {
        const line = buffer.slice(0, newline).trim();
        buffer = buffer.slice(newline + 1);
        if (!line) continue;
        let record;
        try {
          record = JSON.parse(line);
        } catch {
          continue; // not protocol output
        }
        if (record.id === undefined) continue; // notifications (logs) are not responses
        cleanup();
        resolve(record);
        return;
      }
    };
    const onExit = (code) => { cleanup(); reject(new Error(`server exited (code ${code})`)); };
    const onError = (error) => { cleanup(); reject(new Error(`spawn failed: ${error.message}`)); };
    const cleanup = () => {
      clearTimeout(timer);
      child.stdout.off("data", onStdout);
      child.off("exit", onExit);
      child.off("error", onError);
    };
    child.stdout.on("data", onStdout);
    child.on("exit", onExit);
    child.on("error", onError);
  });
}
