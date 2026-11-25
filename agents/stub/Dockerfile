# Stub agent image (ticket 08): the shared runtime base + the stub RPC CLI. The stub pins NO
# Pi deliberately — it speaks its own pi-RPC-shaped protocol — so it overrides the base's
# PI_AGENT_BIN/PI_AGENT_ARGS (real pi + `--mode rpc`) with the stub module (ABSOLUTE path: the
# agent process runs with cwd = the workspace, a relative arg would miss from there).
# Runs as the image's non-root `node` user (uid 1000); the Run Worker chowns the per-Run
# mounts to it.
FROM agent-runtime-base:latest

COPY agent.json profile.yaml /opt/pi-agent/profile/
RUN node /opt/agent-runtime/communication-channel/dist/generate-mcpjson.mjs \
      /opt/pi-agent/profile/agent.json \
      --bundle /opt/agent-runtime/communication-channel/dist/server.mjs \
      --manifest-arg /opt/pi-agent/profile/agent.json \
      --github-token-file /run/secrets/github-read-pat.txt \
      -o /opt/pi-agent/profile/mcp.json

COPY stub-agent/ /opt/agent-runtime/stub-agent/

ENV PI_AGENT_BIN=node \
    PI_AGENT_ARGS=/opt/agent-runtime/stub-agent/pi-stub.mjs

RUN chown -R node:node /opt/pi-agent/profile /opt/agent-runtime/stub-agent
USER node
