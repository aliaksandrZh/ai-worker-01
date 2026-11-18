// Mock MCP server (stdio). One tool: echo — returns the text it gets. No dependencies;
// speaks newline-delimited JSON-RPC 2.0 per the MCP stdio transport. Pi wires it in with:
//   pi mcp add mock -- node mcp/mock-server.mjs   (tool name as seen by models: mcp__mock__echo)
import { createInterface } from "node:readline";

const TOOLS = [
  {
    name: "echo",
    description: "Returns the text it receives — mock for verifying MCP wiring and tool-call round-trips.",
    inputSchema: {
      type: "object",
      properties: { text: { type: "string", description: "Text to echo back" } },
      required: ["text"],
    },
  },
];

const reply = (id, result) => process.stdout.write(JSON.stringify({ jsonrpc: "2.0", id, result }) + "\n");
const replyError = (id, code, message) =>
  process.stdout.write(JSON.stringify({ jsonrpc: "2.0", id, error: { code, message } }) + "\n");

createInterface({ input: process.stdin }).on("line", (line) => {
  if (!line.trim()) return;
  let req;
  try {
    req = JSON.parse(line);
  } catch (e) {
    console.error(`mock-mcp: unparseable line: ${e.message}`); // diagnostics go to stderr only
    return;
  }
  if (req.id === undefined || req.id === null) return; // notification — nothing to answer
  switch (req.method) {
    case "initialize":
      reply(req.id, {
        protocolVersion: req.params?.protocolVersion ?? "2025-06-18", // echo the client's pick
        capabilities: { tools: { listChanged: false } },
        serverInfo: { name: "mock", version: "0.1.0" },
      });
      return;
    case "tools/list":
      reply(req.id, { tools: TOOLS });
      return;
    case "tools/call": {
      if (req.params?.name !== "echo")
        return reply(req.id, { content: [{ type: "text", text: `unknown tool: ${req.params?.name}` }], isError: true });
      const args = req.params.arguments ?? {};
      const text = typeof args.text === "string" ? args.text : JSON.stringify(args);
      reply(req.id, { content: [{ type: "text", text }], isError: false });
      return;
    }
    case "ping":
      reply(req.id, {});
      return;
    default:
      replyError(req.id, -32601, `method not found: ${req.method}`);
  }
});

process.on("disconnect", () => process.exit(0)); // client closed stdin — shut down cleanly
