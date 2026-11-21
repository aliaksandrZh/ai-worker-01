# Stub agent image (ticket 09): node + git + the reference Runtime Bridge + the stub RPC CLI.
# Runs as the image's non-root `node` user (uid 1000); the Runtime API chowns the per-Run mounts to it.
FROM node:22-alpine
RUN apk add --no-cache git
WORKDIR /opt/agent
COPY runtime-bridge/ runtime-bridge/
COPY stub-agent/ stub-agent/
USER node
CMD ["node", "runtime-bridge/bridge.mjs"]
