# Coder agent profile image (ticket 08): agent-runtime-base + the coder's heavy toolchain.
# The agent delta is data (agent.json (clone_mode = branch-continue) + the prompt) plus the
# .NET SDK and the git push credential helper; mcp.json is GENERATED at build from agent.json
# (the ticket-04 generator baked in the base). The GitHub MCP allow-list is READ-ONLY: the
# pull request and the summary are the Orchestrator's publications (submit_pr_description) —
# the branch itself is the single carve-out, delivered with git push (ADR-0001).
# The .NET SDK the coder builds/tests with is a FLEET policy, not a per-repository fit: the
# coder runs against every repository in deploy/config/repositories.yaml (tomorrow —
# hundreds), so the image carries the latest SDK of the major the fleet is expected to use. A
# target repo's own global.json (it arrives via the workspace clone) remains the repo's lever:
# latestFeature roll-forwards match; an exact pin that disagrees fails the build honestly and
# diagnosably. Supporting a NEW SDK major = bump this ARG = a profile image bump.
# Copied from the official image, never a curl|bash install script at build time. (.NET 10
# publishes no Debian-slim variants: the official SDK image is Ubuntu noble; the SDK ships its
# own bits, only its libc deps are installed below.)
ARG DOTNET_SDK_IMAGE=mcr.microsoft.com/dotnet/sdk:10.0

FROM ${DOTNET_SDK_IMAGE} AS dotnet

FROM agent-runtime-base:latest
# The .NET runtime dependencies (libicu/ICU data, libstdc++, zlib) the copied SDK needs on
# bookworm-slim (libssl3/libgcc-s1/libc6 already ship in the base).
RUN apt-get update \
    && apt-get install -y --no-install-recommends libicu72 libstdc++6 zlib1g \
    && rm -rf /var/lib/apt/lists/*

COPY --from=dotnet /usr/share/dotnet /usr/share/dotnet
ENV DOTNET_ROOT=/usr/share/dotnet \
    DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    DOTNET_NOLOGO=1 \
    PATH="/usr/share/dotnet:/usr/share/dotnet/tools:${PATH}"

COPY agent.json profile.yaml APPEND_SYSTEM.md settings.json models.json /opt/pi-agent/profile/
COPY git-credential-pat-file /usr/local/bin/git-credential-pat-file
RUN node /opt/agent-runtime/communication-channel/dist/generate-mcpjson.mjs \
      /opt/pi-agent/profile/agent.json \
      --bundle /opt/agent-runtime/communication-channel/dist/server.mjs \
      --manifest-arg /opt/pi-agent/profile/agent.json \
      -o /opt/pi-agent/profile/mcp.json

# The token-file path pointer is non-secret configuration only — the token VALUE stays in the
# host-managed read-only secret file. The coder holds the repo-scoped WRITE PAT, and it is a
# GIT-push credential only: the GitHub MCP server reads the same file for context reads, the
# branch push goes through the credential helper (never a remote URL, never persisted config).
ENV GIT_CLONE_TOKEN_FILE=/run/secrets/github-write-pat.txt
# The delivery guard: the communication-channel server answers submit_pr_description and
# report_completion with an isError result until the workspace git state is delivery-ready
# (on an agent branch, HEAD pushed) — the PR head invalid-422 never leaves the run (the
# Orchestrator publishes from the run_completed git facts; the guard keeps them publishable).
ENV CC_COMPLETION_GUARD=/workspace

# uid 1000 (`node`) is the user the Run Worker chowns the per-Run mounts to. /opt/pi-agent/profile
# is chowned to `node` — the FILES remain image-baked (nothing mounts over them), the ownership
# only lets pi write its own state files (mcp.log et al.) inside the ephemeral container layer.
RUN chmod +x /usr/local/bin/git-credential-pat-file \
    && chown -R node:node /opt/pi-agent/profile
USER node
