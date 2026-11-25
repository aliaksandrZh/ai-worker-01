# Glossary

Language of the agent orchestration docs (`docs/flows/`).

## Language

### Flows and runs

**Flow**:
One of the four ordered agent workflows — plan, development, review, testing.
_Avoid_: stage, phase, pipeline

**Run**:
A single execution of an agent inside a flow. Every resume of a flow is a new Run.
_Avoid_: session, execution

**Run lifecycle**:
The state machine every Run passes through — scheduled, processing, completed, failed —
where each transition is an event the Orchestrator reacts to.

**Waiting for Human**:
A flow state paused until exactly one user event arrives.
_Avoid_: blocked, on hold

**Trigger**:
What produces an event that starts a flow: a user command, a board transition, or a
label. The trigger set is defined outside the flow docs.
_Avoid_: slash command, hook

### Signals

**Event**:
A signal the Orchestrator reacts to by starting or restarting a flow: a user action, a
GitHub state change, a board transition, or an agent's tool call.
_Avoid_: trigger, notification

**Completion**:
The explicit parameterless signal with which every agent ends its Run (`report_completion`).
_Avoid_: done marker, finish signal

**Escalation**:
Handing an issue to the User because the agent cannot proceed on its own: clarification,
blocker, decision, or criteria clarification.
_Avoid_: raising, escalation ticket

**Blocker**:
A problem the Coder cannot resolve independently, handed to the User with causes and options.
_Avoid_: impediment

### Artifacts

**Plan**:
Scope and acceptance criteria for a task, published by the Orchestrator as a comment in
the issue.
_Avoid_: spec, ticket description

**Acceptance criteria**:
The verifiable conditions in the Plan against which review and testing judge the result.
_Avoid_: requirements, definition of done

**Review comment**:
A reviewer's finding on the PR, published to the PR by the Orchestrator and resolved by
the Coder in a fixing Run.
_Avoid_: review note, feedback

### Responsibilities

**Publication**:
The Orchestrator's exclusive act of writing agent-produced artifacts (plan, review
comments, bug reports, PR) to GitHub; agents never write there directly. Code delivery
is the single exception: the Coder pushes its working branch via git protocol.
_Avoid_: posting, pushing

**Round cap**:
The Orchestrator's limit on review → fix cycles; deliberately outside the flow diagrams.
_Avoid_: loop limit, retry limit
