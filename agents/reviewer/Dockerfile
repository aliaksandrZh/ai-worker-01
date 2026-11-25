# Reviewer agent profile image (ticket 08): a slim layer over agent-runtime-base (pi + the
# Runtime Bridge + the communication-channel bundle + the ONE shared entry). The agent delta
# is data: agent.json (clone_mode = pinned-review + tools) + the prompt; mcp.json is GENERATED
# at build from agent.json (the ticket-04 generator baked in the base). The GitHub MCP
# allow-list is READ-ONLY: the review is delivered through submit_review_comments /
# request_decision — agents never write to GitHub (ADR-0001), not even the review submission.
FROM agent-runtime-base:latest

COPY agent.json profile.yaml APPEND_SYSTEM.md settings.json models.json /opt/pi-agent/profile/
RUN node /opt/agent-runtime/communication-channel/dist/generate-mcpjson.mjs \
      /opt/pi-agent/profile/agent.json \
      --bundle /opt/agent-runtime/communication-channel/dist/server.mjs \
      --manifest-arg /opt/pi-agent/profile/agent.json \
      --github-token-file /run/secrets/github-read-pat.txt \
      -o /opt/pi-agent/profile/mcp.json

# The token-file path pointer is non-secret configuration only — the token VALUE stays in the
# host-managed read-only secret file. The reviewer holds the read-only PAT (context reads;
# the entry clones with it, nothing in the checkout is ever executed).
ENV GIT_CLONE_TOKEN_FILE=/run/secrets/github-read-pat.txt

# uid 1000 (`node`) is the user the Run Worker chowns the per-Run mounts to. /opt/pi-agent/profile
# is chowned to `node` — the FILES remain image-baked (nothing mounts over them), the ownership
# only lets pi write its own state files (mcp.log et al.) inside the ephemeral container layer.
RUN chown -R node:node /opt/pi-agent/profile
USER node
