# 11b: Secrets resolution — profile secret mounting + PAT lifecycle

**What to build:** The secrets plane between configuration and runtime. Resolution of secret references from repository/profile configuration and read-only mounting of a profile's required secrets into its agent containers at `/run/secrets/<name>` — least privilege (each profile receives only the secrets it needs: read-only sync PAT for the Poller; write PAT for the coder's MCP/Git; MCP tokens for planner/reviewer/test-designer), never via environment variables or image layers; values live only in host-managed files. The per-Run callback credential remains a separate mechanism (ticket 09). PAT lifecycle per the spec: rotation procedure (create replacement with equivalent rights → verify via GitHub API → switch the secret version → revoke the old token only after in-flight Runs finish), expiry tracking with a 14-day warning surfaced in logs/health status, compromise → immediate revocation even if it breaks active operations, and no automatic fallback to another PAT — revoked/expired tokens surface as clear errors (Poller sync errors; agent GitHub operations fail the Run with a clear error code).

**Spec:** `docs/pi-agent-architecture/02-data-model.md` §12 (secrets paragraphs); `01-architecture-and-flows.md` §2 (Poller authentication paragraph); `05-v1-decisions.md` decisions 50 (PAT split per role), 54 (secret mounting rules), 65 (rotation/compromise/expiry); `04-api-contracts.md` §4 (callback credential — separate mechanism).

**Blocked by:** 02, 09

**Status:** ready-for-agent

- [ ] Each agent container receives only its profile's required secrets, mounted read-only at `/run/secrets/<name>` (planner/reviewer/test-designer MCP tokens; coder write PAT)
- [ ] No secret value ever reaches environment variables, image layers, or PostgreSQL
- [ ] Rotation procedure executes as specified: new token verified via GitHub API → secret version switched → old token revoked only after in-flight Runs finish
- [ ] Expiry tracking emits a warning 14 days before expiration (logs + health status)
- [ ] A compromised token is revoked immediately, breaking active operations as specified
- [ ] Revoked/expired PAT: Poller surfaces sync errors clearly; agent GitHub/MCP operations fail the Run with a clear error code — no silent fallback
- [ ] The per-Run callback credential (ticket 09) is functionally independent of profile secrets
