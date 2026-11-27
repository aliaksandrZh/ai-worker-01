/**
 * mcp.json generator: the single entry point to an agent's tools, emitted from the agent's
 * agent.json manifest at image build (ticket 08). Two servers:
 *   - "github": the GitHub MCP server, GITHUB_TOOLS allow-list = the manifest's CONTEXT tools
 *     (every listed name outside the communication vocabulary of agents/_runtime/tools.json —
 *     concrete github-mcp-server names, see README "Manifest contract");
 *   - "communication": this bundle, which registers the manifest's COMMUNICATION tools
 *     (intersection with the vocabulary) and takes the manifest path as its argument.
 *
 * The token-file path pointer is per-agent (ticket 08): the GitHub MCP server reads the mounted
 * secret AT STARTUP via the `!cat` directive — planner/reviewer carry the read-only PAT,
 * the coder the write PAT (git push only, ADR-0001). Path pointer only — never a token value.
 */
import { isCommunicationTool } from "./spec.mjs";

export function generateMcpJson(manifest, opts = {}) {
  const tokenFile = opts.tokenFile ?? "/run/secrets/github-write-pat.txt";
  const tools = manifest?.tools;
  if (!Array.isArray(tools) || tools.some((t) => typeof t !== "string"))
    throw new Error("agent.json: 'tools' must be an array of tool names");
  const githubTools = tools.filter((name) => !isCommunicationTool(name));
  return {
    mcpServers: {
      github: {
        command: opts.githubCommand ?? "/usr/local/bin/github-mcp-server",
        args: ["stdio"],
        exposure: "direct",
        env: {
          GITHUB_PERSONAL_ACCESS_TOKEN: `!cat ${tokenFile}`,
          GITHUB_TOOLS: githubTools.join(","),
        },
      },
      communication: {
        command: "node",
        args: [
          opts.bundle ?? "/opt/pi-agent/profile/communication-channel/dist/server.mjs",
          opts.manifestArg ?? "/opt/pi-agent/profile/agent.json",
        ],
        exposure: "direct",
      },
    },
  };
}
