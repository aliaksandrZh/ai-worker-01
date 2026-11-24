# Reviewer agent profile image. Own build context, own Git clone path — the
# reviewer shares NO code with the planner or the coder (each agent gets its codebase via its own git
# clone in its own entry script). Pi is PINNED at build time.
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

# Official GitHub MCP server (reviewer's GitHub path: pull requests read, pending review, inline
# comments, thread replies, and the pending-review submit — see mcp.json for the exact allow-list)
COPY --from=gh-mcp /server/github-mcp-server /usr/local/bin/github-mcp-server

WORKDIR /workspace
COPY profile.yaml APPEND_SYSTEM.md settings.json models.json mcp.json review-outcome.mjs /opt/pi-agent/profile/
COPY runtime-bridge/ /opt/pi-agent/profile/runtime-bridge/
COPY entry /usr/local/bin/agent-entry

ENV PI_CODING_AGENT_DIR=/opt/pi-agent/profile
# PI_REQUIRED_PUBLICATION_KINDS pins the publication mandate in the image: the Bridge's toolcheck
# verifies the review submission against the observed tool executions (nudging a missing one;
# PI_PUBLICATION_NUDGES caps those follow-up prompts). Non-secret configuration only.
ENV PI_AGENT_BIN=pi \
    PI_AGENT_ARGS="--mode rpc" \
    PI_REQUIRED_PUBLICATION_KINDS=review \
    PI_REVIEW_OUTCOME_TOOL=request_changes \
    PI_PUBLICATION_NUDGES=2 \
    GIT_CLONE_TOKEN_FILE=/run/secrets/github-write-pat.txt

RUN chmod +x /usr/local/bin/agent-entry /usr/local/bin/github-mcp-server \
    && chown -R node:node /opt/pi-agent/profile
USER node
ENTRYPOINT ["/bin/sh", "/usr/local/bin/agent-entry"]
CMD ["node", "/opt/pi-agent/profile/runtime-bridge/bridge.mjs"]
