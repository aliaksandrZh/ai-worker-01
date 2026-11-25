# Communication channel

The agent's ONE channel to the Orchestrator/User: a stdio MCP server (`communication`) exposing
the 11 communication tools of [docs/flows/tools.md](../../../docs/flows/tools.md), driven by the
agent's `agent.json` manifest. The server is "mute": a call is a signal — it talks to nothing and
publishes nothing to GitHub (publication is the Orchestrator's act). The Runtime Bridge observes
each successful call and turns it into exactly one `tool` callback
([docs/run-worker.md](../../../docs/run-worker.md)); failed calls produce no callback.

## Layout

- `src/spec.mjs` — the tool vocabulary (names, kinds, field specs) + zod schemas + JSON Schemas,
  both derived from one spec so they cannot drift. Signatures are hand-synced with the payload
  records of the .NET adapter; only schema (types/required) is validated — business rules live
  on the Orchestrator side.
- `src/server.mjs` — the server (initialize, notifications/initialized, tools/list, tools/call)
  in the style of `agents/coder/run-outcome.mjs`, but built from source with zod validation.
  Invalid payload → JSON-RPC error naming the offending field (surfaces to the agent as an
  isError tool result — early feedback). Valid call → success result with a hint:
  - escalation tools: the flow parks until a user event resumes it as a NEW Run; the agent must
    still end THIS Run with `report_completion`
  - `report_completion`: the run can settle
  - everything else: a short acknowledgment
- `src/mcpjson.mjs` + `src/generate-mcpjson.mjs` — the `mcp.json` generator (see below).
- `test/` — `npm test` (node --test), exercises src and the built bundle over stdio.

## Build

```
npm install
npm run build    # esbuild bundle -> dist/server.mjs (zod inlined; no runtime dependencies)
npm test
```

## Manifest contract (`agent.json`, ticket 01)

```json
{
  "name": "coder",
  "clone_mode": "plain",
  "tools": ["issue_read", "add_issue_comment", "report_completion", "submit_pr_description", "report_blocker"]
}
```

`tools[]` is the full catalog (communication + context). The **communication part** is the
intersection with the fixed 11-name vocabulary — only those tools are registered by this server;
unlisted vocabulary names are not registered, and names outside the vocabulary (context tools,
stale names) are ignored. The **context part** is everything else — it becomes the GitHub MCP
server's `GITHUB_TOOLS` allow-list (github-mcp-server tool names, as in the existing hand-baked
`agents/*/mcp.json`).

## Server invocation

```
node dist/server.mjs /path/to/agent.json    # or env AGENT_MANIFEST
```

## mcp.json generation (image build)

```
node src/generate-mcpjson.mjs agent.json -o mcp.json \
  [--bundle <path>] [--manifest-arg <path>] [--github-command <cmd>]
```

Defaults mirror the existing baked entries: github command `/usr/local/bin/github-mcp-server`,
bundle `/opt/pi-agent/profile/communication-channel/dist/server.mjs`,
manifest `/opt/pi-agent/profile/agent.json`. Image assembly (ticket 08) passes its own paths.
