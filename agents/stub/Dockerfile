# Stub agent image: node + git + the reference Runtime Bridge + the stub RPC CLI.
# Runs as the image's non-root `node` user (uid 1000); the Runtime API chowns the per-Run mounts to it.
# The profile bake ensures every built profile image carries the same layout:
# profile.yaml baked at /opt/pi-agent/profile + PI_CODING_AGENT_DIR. The stub pins NO Pi deliberately —
# it speaks its own pi-RPC-shaped protocol.
FROM node:22-alpine
RUN apk add --no-cache git
WORKDIR /opt/agent
COPY runtime-bridge/ runtime-bridge/
COPY stub-agent/ stub-agent/
COPY profile.yaml /opt/pi-agent/profile/profile.yaml
ENV PI_CODING_AGENT_DIR=/opt/pi-agent/profile
USER node
CMD ["node", "runtime-bridge/bridge.mjs"]
