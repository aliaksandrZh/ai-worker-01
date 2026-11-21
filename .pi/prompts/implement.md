---
description: Start the task
argument-hint: "[task]"
---

# Implementation Instructions

Task ${1:-tasknumber}

## Rules
- Report ambiguities and conflicts instead of making assumptions. Discuss unclear points with the user. Make decisions collaboratively; do not make them independently.
- Ask for permission before running long-lived processes against the user's environment
  (starting services, stacks, deployments). Read-only introspection (`ps`, `logs`, `inspect`)
  needs no permission.
- When finished, stop everything you started, then verify it: nothing you started may still be
    running — confirm with a status check and report its output. A stop command issued is not a
    stop confirmed.
- When a change affects persisted state, deployed images, or an environment where older state
  already exists (database volumes, prior deployments), the proposal must state what breaks and
  the one-time reconciliation steps; get explicit approval for destructive remediation
  (drops, wipes) before running it.
- - Do not modify the following folders; they are for reference only:
  - `api`
  - `github-polling`
  - `pi`
  - `komodo`
  - `docs/pi-agent-architecture` (accepted project specifications)
- [REQUIRED] Follow `karpathy-guidelines` skill
- [REQUIRED] Follow `ponytail` skill

## Git
- Default branch: `main`.
- Working directory: `/src`.
- Use a new branch based on default one. Branch name: `task-<number>-<slug>`.
- Do not commit without user review.


## Verification
- Claims must match their evidence and scope: "tests pass" (suite X, N green) is not "works
  in the running system" — live verification is required before any deployment claim, otherwise
  state explicitly what remains unverified.
- Every verification claim names the evidence: the command run and its output.
- Manual-test instructions in the task file only contain commands that were actually executed
  during development (with expected outputs copied from real runs); anything not executed is
  marked unverified and listed separately.
- Once implementation and verification are complete, wait for the user's manual review.
- Add to the task file a short description of what has been done and instructions on how to manually test it including prerequirements.