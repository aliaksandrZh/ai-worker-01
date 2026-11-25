# Glossary

Language of the agent orchestration docs (`docs/flows/`).

## Language

### Flows and runs

**Workflow**:
The work on one root item (issue); owns exactly one active Task at a time.
_Avoid_: job, process

**Flow**:
One of the four ordered agent workflows — plan, development, review, testing.
_Avoid_: stage, phase, pipeline

**Task**:
One unit of work in a Workflow: a single execution of a flow stage (plan, implement,
review, test). Retries and resumes are new Runs of the same Task.
_Avoid_: job, step

**Run**:
A single execution of an agent inside a Task.
_Avoid_: session, execution

**Run lifecycle**:
The state machine every Run passes through — scheduled, processing, completed, failed,
cancelled — where each transition is an event the Orchestrator reacts to.

**Waiting for Human**:
A Task state: the flow stage is paused until exactly one user event arrives.
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

**Event log**:
The append-only Postgres log every module writes to and reads from — the single channel
between modules and the single source of truth for what happened.
_Avoid_: bus, queue, outbox

**Stream**:
The ordering unit of the event log: all events of one root item. Strict order inside,
parallel across.
_Avoid_: partition, topic

**Consumer**:
A config-gated worker loop that processes its event types from the log; several
instances may run the same consumer.
_Avoid_: worker (run worker is a specific consumer), handler

**Dead-letter**:
Where the log parks an event after repeated failed deliveries, for inspection and replay.
_Avoid_: poison queue

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
