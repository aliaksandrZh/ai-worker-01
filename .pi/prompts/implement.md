---
description: Start the task
argument-hint: "[task]"
---

# Implementation Instructions

Task ${1:-tasknumber}

## Scope
- Read the requirements and relevant documentation, then provide a list of proposed changes before implementation. Wait for explicit user approval.
- Report ambiguities and conflicts instead of making assumptions.
- May use the Superpowers skills set to help clarify requirements and identify missing details.
- Discuss unclear points with the user. Make decisions collaboratively; do not make them independently.
- If anything is missing, create a proposal file and wait for its resolution. Do not start implementation.
- Do not introduce unrelated changes or expand the scope.
- Do not modify the following folders; they are for reference only:
  - `api`
  - `github-polling`
  - `pi`
  - `komodo`
  - `docs/pi-agent-architecture` (accepted project specifications)
- Verify plan items marked as risks or known pitfalls with an executable check (test or command),
  never by visual inspection of a diff alone.

## Environment
- Ask for permission before running long-lived processes against the user's environment
  (starting services, stacks, deployments). Read-only introspection (`ps`, `logs`, `inspect`)
  needs no permission.
- Changing shared tooling defaults (Taskfile targets, compose defaults, migration tooling) is a
  separate approved change, never folded into an unrelated fix.
- When a change affects persisted state, deployed images, or an environment where older state
  already exists (database volumes, prior deployments), the proposal must state what breaks and
  the one-time reconciliation steps; get explicit approval for destructive remediation
  (drops, wipes) before running it.

## Git
- Default branch: `main`.
- Working directory: `/src`.
- Branch naming: `task-<number>-<slug>`.
- Do not commit without user review.

## Implementation

- [REQUIRED] Follow `superpowers` skills set
- [REQUIRED] Follow `ponytail` skill
- If you need to run a long-lived process (e.g., run the project, Docker), ask for permission first.
- When finished, stop everything you started, then verify it: nothing you started may still be
  running — confirm with a status check and report its output. A stop command issued is not a
  stop confirmed.

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