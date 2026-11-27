# Communication channel

The agent's ONE channel to the Orchestrator/User: a stdio MCP server (`communication`) exposing
the 11 communication tools of [docs/flows/tools.md](../../../docs/flows/tools.md), driven by the
agent's `agent.json` manifest. The server is "mute": a call is a signal — it talks to nothing and
publishes nothing to GitHub (publication is the Orchestrator's act). The Runtime Bridge observes
each successful call and turns it into exactly one `tool` callback
([docs/run-worker.md](../../../docs/run-worker.md)); failed calls produce no callback.

## Layout

- `src/spec.mjs` — the adapter over the tool catalog (agents/_runtime/tools.json: names,
  carriers, kinds, field specs) + zod schemas + JSON Schemas,
  both derived from one spec so they cannot drift. Signatures are hand-synced with the payload
  records of the .NET adapter; only schema (types/required) is validated — business rules live
  on the Orchestrator side.
- `src/server.mjs` — the server (initialize, notifications/initialized, tools/list, tools/call),
  a dependency-free stdio JSON-RPC server with zod validation.
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

`tools[]` is the agent's availability list — it may only contain names declared in the
[tool catalog](../../tools.json) (the ONE declaration: names, carriers — communication /
github-mcp / none — and field specs). The **communication part** is the intersection with the
catalog's `carrier: "communication"` names — only those tools are registered by this server;
unlisted communication names are not registered, and names outside the catalog (stale names)
are ignored. The **context part** is everything else — it becomes the GitHub MCP server's
`GITHUB_TOOLS` allow-list (concrete github-mcp-server tool names — the server refuses unknown
names at startup, fail-closed).

> **Name mapping.** The availability rows of [docs/flows/tools.md](../../../docs/flows/tools.md)
> use flow-level names; the manifest encodes them concretely: `github_issue_read` and
> `github_comments_read` → `issue_read` (its `get`/`get_comments` methods),
> `github_pr_read` and `github_pr_diff` → `pull_request_read` (its `get`/`get_diff` methods).
> `web_search` has NO implementation in the system today (no server, no tool) — the availability
> row stays in tools.md for the future; add it to the agent manifests only together with the
> server that implements it.

## Server invocation

```
node dist/server.mjs /path/to/agent.json    # or env AGENT_MANIFEST
```

## mcp.json generation (image build)

```
node src/generate-mcpjson.mjs agent.json -o mcp.json \
  [--bundle <path>] [--manifest-arg <path>] [--github-command <cmd>]
  [--github-token-file <path>]
```

The token-file default is the coder's write PAT (`/run/secrets/github-write-pat.txt`);
planner/reviewer image builds pass their read-only PAT path (ticket 08: agents never write to
GitHub — [ADR-0001](../../../docs/adr/0001-agents-never-write-to-github.md)). Image assembly
passes the baked base paths: github command `/usr/local/bin/github-mcp-server`,
bundle `/opt/agent-runtime/communication-channel/dist/server.mjs`,
manifest `/opt/pi-agent/profile/agent.json`.
