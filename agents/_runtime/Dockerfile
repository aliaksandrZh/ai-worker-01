# agent-runtime-base (ticket 08): the ONE shared agent runtime. Pi (pinned here — the only pin
# in the repo), the Runtime Bridge, the communication-channel bundle (built at image build —
# zod inlined, no runtime dependencies), the ONE shared entry, and the tool set every agent
# carries (git, ripgrep, distro Python). Agent profiles are slim layers over this base: their
# delta is data (agent.json + the prompt) plus coder's heavy toolchain. Per-agent mcp.json is
# generated at build from agent.json by the generator baked below (dist/generate-mcpjson.mjs).
# Non-root posture: the base stays root — each profile layer bakes its profile files, chowns
# /opt/pi-agent/profile and drops to USER node.
ARG PI_VERSION=1.6.0
ARG GH_MCP_VERSION=v2.2.0

# The official GitHub MCP server binary comes from the pinned upstream image (static Go binary
# at /server/github-mcp-server); the per-agent tool allow-list travels in the generated
# mcp.json (GITHUB_TOOLS — the server refuses unknown names at startup, fail-closed).
FROM ghcr.io/github/github-mcp-server:${GH_MCP_VERSION} AS gh-mcp

# Builder: the communication-channel bundle (npm ci + npm run build; the generator CLI is
# bundled too so profile builds never need node_modules in the final image).
FROM node:24-bookworm-slim AS channel-build
COPY communication-channel/package.json communication-channel/package-lock.json /build/channel/
COPY tools.json /build/tools.json
WORKDIR /build/channel
RUN npm ci
COPY communication-channel/src /build/channel/src
RUN npm run build

FROM node:24-bookworm-slim
ARG PI_VERSION
# bash/ca-certificates/git/ripgrep + the distro Python (bookworm's python3, pip and venv —
# pinned by the distro; a new Python major = a base image bump, same policy as the Node pin).
RUN apt-get update \
    && apt-get install -y --no-install-recommends bash ca-certificates git ripgrep python3 python3-pip python3-venv \
    && rm -rf /var/lib/apt/lists/* \
    && npm install -g --ignore-scripts "@earendil-works/pi-coding-agent@${PI_VERSION}" \
    && npm cache clean --force

COPY --from=gh-mcp /server/github-mcp-server /usr/local/bin/github-mcp-server
COPY --from=channel-build /build/channel/dist /opt/agent-runtime/communication-channel/dist
COPY tools.json /opt/agent-runtime/tools.json
COPY bridge/ /opt/agent-runtime/bridge/
COPY entry /usr/local/bin/agent-entry

LABEL pi.version="${PI_VERSION}"

WORKDIR /workspace
# Profile configuration lands at /opt/pi-agent/profile (each profile layer bakes it there);
# uid 1000 (`node`) is the user the Run Worker chowns the per-Run mounts to.
RUN mkdir -p /opt/pi-agent/profile \
    && chmod +x /usr/local/bin/agent-entry /usr/local/bin/github-mcp-server

# The baked bridge env contract (the image is the sole source of the runtime configuration;
# the publication/outcome-mandate flags of the old world are deleted, not muted): the agent
# binary and its fixed flags, the ONE completion mandate's nudge budget
# (PI_COMPLETION_NUDGES), the /mcp probe deadline, and the callback cadences. Per-Run env
# (PI_RUN_ID, PI_RUN_TOKEN, ...) is injected by the worker.
ENV PI_CODING_AGENT_DIR=/opt/pi-agent/profile \
    PI_AGENT_BIN=pi \
    PI_AGENT_ARGS="--mode rpc" \
    PI_COMPLETION_NUDGES=2 \
    PI_MCP_PROBE_TIMEOUT_SECONDS=5 \
    PI_HEARTBEAT_SECONDS=15 \
    PI_EVENT_CADENCE_SECONDS=5

ENTRYPOINT ["/usr/local/bin/agent-entry"]
CMD ["node", "/opt/agent-runtime/bridge/bridge.mjs"]
