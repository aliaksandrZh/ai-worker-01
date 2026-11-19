# 18: /code + coder profile

**What to build:** The coder phase. `/code` approval semantics: requires the latest successful plan Artifact (the command itself is the approval; no implement-without-plan mode); coder profile (own image, write-PAT secret refs, MCP/Git tooling allow-list). Workspace semantics: the first coder Task clones the original repo from the captured default-branch HEAD (`Workspace.base_sha`), works on `agent/issue-<number>`; later coder Tasks and retries continue the existing agent branch; no automatic rebase of a moved default branch (accepted limitation, manual rebase before PR creation). Push: fine-grained Contents-write PAT mounted read-only at `/run/secrets/` + temporary credential helper (token never stored in remotes or git config); commits authored as the dedicated bot account (name + noreply email), no signing, force-push forbidden, no fork. Success preconditions: push + result summary published (requiredPublications correlation incl. the Git facts). Mid-run blockers: agent publishes its question via MCP, checkpoints to the agent branch, Run succeeds with `outcome = needs_human_input`, Task terminal `Blocked`; after the user answers and re-runs a command, a new Task/Run continues from the agent branch (uncommitted changes not guaranteed). Automatic PR creation: not in V1.

**Spec:** `docs/pi-agent-architecture/06-command-reference.md` §4 (/code + workspace/git rules + mid-run blockers); `02-data-model.md` §16 (workspace); `05-v1-decisions.md` decisions 14, 26, 47, 50, 72, 74 (coder never touches forks), 80; `03-state-machines.md` §2 (Blocked).

**Blocked by:** 12, 11, 11b, 10

**Status:** ready-for-agent

- [ ] `/code` after a successful plan → implement Task on the coder profile; branch `agent/issue-<n>` pushed; summary comment published (correlated before completion; transient GitHub/MCP failures retried within the Run with GitHub verification before any re-post — decision 42)
- [ ] `/code` without a successful plan Artifact → rejected with the recorded reason
- [ ] Mid-run blocker → question comment + branch checkpoint + Run `Succeeded (needs_human_input)` + Task `Blocked`; continuation run starts from the agent branch
- [ ] Commits authored as the bot account; force-push impossible; the PAT never lands in remotes or git config
- [ ] A later `/code` continues the existing agent branch; a moved default branch is not auto-rebased
- [ ] Unpushed work after root cleanup is an accepted risk (documented behavior, no extra tooling)
- [ ] Acceptance scenario "Coding" (`05` matrix) passes
