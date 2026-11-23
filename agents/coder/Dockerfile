# Coder agent profile image. Own build context, own Git clone path — the coder shares NO code with
# the planner or reviewer (each agent gets its codebase via its own git clone in its own entry
# script). Pi is PINNED at build time (never floating `latest`; a Pi upgrade = a new
# AgentProfileVersion) and the profile configuration is baked at /opt/pi-agent/profile, selected via
# PI_CODING_AGENT_DIR.

# The official GitHub MCP server binary comes from the pinned upstream image (static Go binary at
# /server/github-mcp-server); the tool allow-list travels in the baked mcp.json (GITHUB_TOOLS — the
# server refuses unknown names at startup, fail-closed).
ARG PI_VERSION=1.6.0
ARG GH_MCP_VERSION=v2.2.0
# The .NET SDK the coder builds/tests with is a FLEET policy, not a per-repository fit: the coder
# runs against every repository in deploy/config/repositories.yaml (tomorrow — hundreds), so the
# image carries the latest SDK of the major the fleet is expected to use. A target repo's own
# global.json (it arrives via the workspace clone) remains the repo's lever: latestFeature
# roll-forwards match; an exact pin that disagrees fails the build honestly and diagnosably.
# Supporting a NEW SDK major = bump this ARG = a new AgentProfileVersion.
# Copied from the official image, never a curl|bash install script at build time. (.NET 10
# publishes no Debian-slim variants: the official SDK image is Ubuntu noble; the SDK ships its
# own bits, only its libc deps are installed below.)
ARG DOTNET_SDK_IMAGE=mcr.microsoft.com/dotnet/sdk:10.0

FROM ghcr.io/github/github-mcp-server:${GH_MCP_VERSION} AS gh-mcp

FROM ${DOTNET_SDK_IMAGE} AS dotnet

FROM node:24-bookworm-slim
ARG PI_VERSION
# The .NET runtime dependencies (libicu/ICU data, libstdc++, zlib) the copied SDK needs on
# bookworm-slim (libssl3/libgcc-s1/libc6 already ship in the base), plus the same tool set every
# profile image carries, plus the distro Python (bookworm's python3, pip and venv — pinned by the
# distro; a new Python major = a profile image bump, same policy as the Node/.NET pins).
RUN apt-get update \
    && apt-get install -y --no-install-recommends bash ca-certificates git ripgrep libicu72 libstdc++6 zlib1g python3 python3-pip python3-venv \
    && rm -rf /var/lib/apt/lists/* \
    && npm install -g --ignore-scripts "@earendil-works/pi-coding-agent@${PI_VERSION}" \
    && npm cache clean --force

# .NET SDK from the official image (complete, incl. its bundled tools)
COPY --from=dotnet /usr/share/dotnet /usr/share/dotnet
ENV DOTNET_ROOT=/usr/share/dotnet \
    DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    DOTNET_NOLOGO=1 \
    PATH="/usr/share/dotnet:/usr/share/dotnet/tools:${PATH}"

# Official GitHub MCP server (coder's GitHub write path: the pull request create + the issue
# summary comment; the branch itself is pushed with git, never through MCP)
COPY --from=gh-mcp /server/github-mcp-server /usr/local/bin/github-mcp-server

WORKDIR /workspace
# Profile configuration (control plane) + the pinned Runtime Bridge; uid 1000 (`node`) is the user
# the Runtime API chowns the per-Run mounts to. /opt/pi-agent/profile is chowned to `node` below —
# the FILES remain image-baked (the image is the sole source; nothing mounts over them), the
# ownership only lets pi write its own state files (mcp.log et al.) inside the ephemeral container
# layer.
COPY profile.yaml APPEND_SYSTEM.md settings.json models.json mcp.json /opt/pi-agent/profile/
COPY runtime-bridge/ /opt/pi-agent/profile/runtime-bridge/
COPY entry /usr/local/bin/agent-entry
COPY git-credential-pat-file /usr/local/bin/git-credential-pat-file

ENV PI_CODING_AGENT_DIR=/opt/pi-agent/profile
# Agent launch: real pi in RPC mode; the Bridge appends --session-dir/--session-id per Run.
# PI_REQUIRED_PUBLICATION_KINDS pins the publication mandate in the image: the Bridge's toolcheck
# verifies BOTH against the observed tool executions (nudging a missing one) — the pull request and
# the issue summary comment. PI_PUBLICATION_NUDGES caps those follow-up prompts.
# PI_REQUIRED_PUBLICATION_KINDS / PI_PUBLICATION_NUDGES / the token-file path pointer are
# non-secret configuration only — the token VALUE stays in the host-managed secret file.
ENV PI_AGENT_BIN=pi \
    PI_AGENT_ARGS="--mode rpc" \
    PI_REQUIRED_PUBLICATION_KINDS=issue_comment,pull_request \
    PI_PUBLICATION_NUDGES=2 \
    GIT_CLONE_TOKEN_FILE=/run/secrets/github-write-pat.txt

RUN chmod +x /usr/local/bin/agent-entry /usr/local/bin/github-mcp-server /usr/local/bin/git-credential-pat-file \
    && chown -R node:node /opt/pi-agent/profile
USER node
ENTRYPOINT ["/bin/sh", "/usr/local/bin/agent-entry"]
CMD ["node", "/opt/pi-agent/profile/runtime-bridge/bridge.mjs"]
