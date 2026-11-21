# Planner agent profile image (ticket 10, decisions 54/56).
# Pi is PINNED at build time (never floating `latest`; a Pi upgrade = a new AgentProfileVersion) and
# the profile configuration is baked READ-ONLY at /opt/pi-agent/profile, selected via PI_CODING_AGENT_DIR
# (the pi agent directory: settings/MCP/skills/prompts land here as the profile defines them).

# Ticket-10 status: BUILD-ONLY — this image is not driven by any Run until tickets 11–12 wire the real
# planner. The baked reference Runtime Bridge (v1.0.0 per profile.yaml bridge.version) currently spawns
# the stub protocol agent (PI_AGENT_COMMAND wiring is ticket 11's runtime work), so it is carried here
# as the image's pinned supervisor, not as a runnable run path.

FROM node:24-bookworm-slim
ARG PI_VERSION=1.6.0
RUN apt-get update \
    && apt-get install -y --no-install-recommends bash ca-certificates git ripgrep \
    && rm -rf /var/lib/apt/lists/* \
    && npm install -g --ignore-scripts "@earendil-works/pi-coding-agent@${PI_VERSION}" \
    && npm cache clean --force

WORKDIR /workspace
# Profile configuration (config plane) + the pinned reference Runtime Bridge; uid 1000 (`node`) is the
# user the Runtime API chowns the per-Run mounts to (task 09). Image layers are read-only, matching
# decision 56's "baked read-only" profile.
COPY profile.yaml /opt/pi-agent/profile/profile.yaml
COPY runtime-bridge/ /opt/pi-agent/profile/runtime-bridge/
ENV PI_CODING_AGENT_DIR=/opt/pi-agent/profile
USER node
CMD ["node", "/opt/pi-agent/profile/runtime-bridge/bridge.mjs"]
