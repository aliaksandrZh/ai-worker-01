# Planner agent profile image (decisions 54/56; ticket 12 mirrors the PoC pi-worker image).
# Pi is PINNED at build time (never floating `latest`; a Pi upgrade = a new AgentProfileVersion) and
# the profile configuration is baked at /opt/pi-agent/profile, selected via PI_CODING_AGENT_DIR
# (settings/models/MCP/system-prompt land here as the profile defines them).

# The official GitHub MCP server binary comes from the pinned upstream image (static Go binary at
# /server/github-mcp-server); the tool allow-list travels in the baked mcp.json (GITHUB_TOOLS — the
# server refuses unknown names at startup, fail-closed).
ARG PI_VERSION=1.6.0
ARG GH_MCP_VERSION=v2.2.0

FROM ghcr.io/github/github-mcp-server:${GH_MCP_VERSION} AS gh-mcp

FROM node:24-bookworm-slim
ARG PI_VERSION
RUN apt-get update \
    && apt-get install -y --no-install-recommends bash ca-certificates git ripgrep \
    && rm -rf /var/lib/apt/lists/* \
    && npm install -g --ignore-scripts "@earendil-works/pi-coding-agent@${PI_VERSION}" \
    && npm cache clean --force

# Official GitHub MCP server (planner's GitHub write path: add_issue_comment + issue_read only)
COPY --from=gh-mcp /server/github-mcp-server /usr/local/bin/github-mcp-server

WORKDIR /workspace
# Profile configuration (control plane) + the pinned Runtime Bridge; uid 1000 (`node`) is the user the
# Runtime API chowns the per-Run mounts to (task 09). `/opt/pi-agent/profile` is chowned to `node`
# below — the FILES remain image-baked (decision 56: the image is the sole source; nothing mounts over
# them), the ownership only lets pi write its own state files (mcp.log et al.) inside the ephemeral
# container layer the same way the PoC's owned /home/pi/.pi/agent did.
COPY profile.yaml APPEND_SYSTEM.md settings.json models.json mcp.json /opt/pi-agent/profile/
COPY runtime-bridge/ /opt/pi-agent/profile/runtime-bridge/
COPY entry /usr/local/bin/agent-entry

ENV PI_CODING_AGENT_DIR=/opt/pi-agent/profile
# Agent launch (decision 56): real pi in RPC mode; the Bridge appends --session-dir/--session-id per
# Run (decision 57). PI_REQUIRED_PUBLICATION_KINDS pins the decision-42 publication mandate in the
# image: the run cannot finish `completed` without a confirmed (error-free) issue-comment publication.
# PI_REQUIRED_PUBLICATION_KINDS / the token-file path pointer are non-secret configuration paths only —
# the token VALUE stays in the host-managed secret file (decision 54).
ENV PI_AGENT_BIN=pi \
    PI_AGENT_ARGS="--mode rpc" \
    PI_REQUIRED_PUBLICATION_KINDS=issue_comment \
    GIT_CLONE_TOKEN_FILE=/run/secrets/github-write-pat.txt

RUN chmod +x /usr/local/bin/agent-entry /usr/local/bin/github-mcp-server \
    && chown -R node:node /opt/pi-agent/profile
USER node
ENTRYPOINT ["/bin/sh", "/usr/local/bin/agent-entry"]
CMD ["node", "/opt/pi-agent/profile/runtime-bridge/bridge.mjs"]
