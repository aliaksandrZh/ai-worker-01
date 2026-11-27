# Agent Tools

> **Status: contract.** The tool set, signatures and availability below are fixed for the
> flows rework. The eleven communication tool names are the Runtime Bridge's `tool`-route
> vocabulary (see [../run-worker.md](../run-worker.md)): the Bridge observes each completed
> call and turns it into exactly one event — a change to the set or a signature is a bridge
> contract bump (`bridge.json` stamp).
>
> Agents communicate with the Orchestrator / User **only through tools**. Every
> "Agent → Orchestrator/User" arrow in a flow diagram maps to one of the communication
> tools below.
>
> **The reverse direction is not a tool.** The Orchestrator reacts to events
> ([events.md](events.md)): an `@bot /resume` command or a decision resumes the flow as
> a **new Run** with freshly assembled context — there is no "receive" tool.
>
> **Publication rule.** Agents never write to GitHub directly. Every artifact an agent
> produces (plan, review comments, bug reports, the Pull Request itself) is published
> to GitHub by the Orchestrator; the agent only signals it through a communication tool.
>
> **Code delivery carve-out.** The working branch is the single exception: the Coder
> alone holds the workspace, so it delivers the branch itself via `git push` (git
> protocol, not the GitHub API) with a repo-scoped, contents-only write credential.
> The PR entity itself is still created/updated by the Orchestrator after
> `run_completed` — a PR cannot exist before its head branch.
>
> **Completion rule.** Every agent Run ends with a call to `report_completion` — a
> parameterless signal that the agent's work is done. Specific tools carry the payload
> (plan, comments, summaries); `report_completion` is the universal "finished" marker
> the Orchestrator validates.
>
> **Event rule.** Every communication tool call produces exactly one Orchestrator event
> (tool → event mapping in [events.md](events.md)); the Orchestrator reacts only to
> events.
>
> **Out of scope:** file operations (read / search / edit / write) and `git push` are
> built-in agent capabilities, and building the project / running tests is
> project-specific — neither belongs to this tool list. Creating or updating the Pull
> Request is the Orchestrator's job, triggered by the Coder's completion.

## Categories

1. **Context (read-only)** — reading GitHub entities (issue, comments, PR, diff).
2. **Communication** — the only channel to the Orchestrator/User.

---

## 1. Context tools (read-only)

### `github_issue_read` [explicit]
- **Purpose:** Read a GitHub issue (title, body, labels, state).
- **Signature:** `github_issue_read(issueId) → Issue`
- **Used by:** Planner, Coder, Review Agent, Testing Agent
- **Stage:** Plan (P1); Development (D1); Review (R1); Testing (T1)

### `github_comments_read` [explicit]
- **Purpose:** Read comments on an issue or PR (paginated, newest first). The **plan is
  published as a comment in the issue** — agents read it with this tool.
- **Signature:** `github_comments_read(entityRef, page?) → Comment[]`
- **Used by:** Planner, Coder, Review Agent, Testing Agent
- **Stage:** Plan (P1); Development — resolving review comments (F1); Review (R1); Testing (T1)

### `github_pr_read` [explicit]
- **Purpose:** Read a pull request: description, changed files, status, review threads.
- **Signature:** `github_pr_read(prId) → PullRequest`
- **Used by:** Planner, Coder, Review Agent, Testing Agent
- **Stage:** Plan (P1); Development (D1, F1); Review (R1); Testing (T1)

### `github_pr_diff`
- **Purpose:** Read the unified diff of a pull request (per-file hunks).
- **Signature:** `github_pr_diff(prId, filePath?) → Diff`
- **Used by:** Review Agent
- **Stage:** Review — analyzing the PR (R1)

### `web_search`
- **Purpose:** Search the web for external knowledge (documentation, library versions, error messages, best practices).
- **Signature:** `web_search(query, filters?) → Result[]`
- **Used by:** Planner, Coder
- **Stage:** Plan (P1) — when GitHub context alone is not enough; Development (I1, V3) — when implementation requires external docs or error investigation

---

## 2. Communication tools (Agent → Orchestrator/User)

### `report_completion` [new]
- **Purpose:** Explicit, parameterless signal that the agent's work is done. The mandatory
  final call of every Run in every flow; the Orchestrator validates it (a Run that ends
  without it has failed). The payload travels through the specific tools below — this tool
  only marks the end.
- **Signature:** `report_completion() → void`
- **Used by:** all agents
- **Stage:** end of every flow

### `save_plan` [explicit]
- **Purpose:** Save or update the plan: scope and acceptance criteria. The Orchestrator
  publishes the plan **as a comment in the issue** (agents never write to GitHub) and
  runs the plan-approval step.
- **Signature:** `save_plan(plan: { goals, scope, acceptanceCriteria, steps[] }) → Plan`
- **Used by:** Planner
- **Stage:** Plan (P2)

### `request_clarification` [explicit]
- **Purpose:** Request missing information from the User via the Orchestrator. Blocks the
  agent until an `@bot /resume` command resumes the flow as a new Run.
- **Signature:** `request_clarification(question, context?) → void`
- **Used by:** Planner
- **Stage:** Plan — not enough information (C1)

### `report_blocker` [explicit]
- **Purpose:** Report a blocker that the Coder cannot resolve independently: cause,
  context, and possible solutions. Resumes on an `@bot /resume` command.
- **Signature:** `report_blocker(description, cause, options[]) → void`
- **Used by:** Coder
- **Stage:** Development — blocker encountered (V4–V10)

### `submit_review_comments`
- **Purpose:** Submit review comments to the Orchestrator, which **publishes them to the
  PR** and assigns the fixes to the Coder (a fixing Run — see development.md).
- **Signature:** `submit_review_comments(comments: { file, line, severity, message }[]) → void`
- **Used by:** Review Agent
- **Stage:** Review — issues resolvable without user input (R3 Yes → R4)

### `request_decision`
- **Purpose:** Escalate a review issue that cannot be resolved without user input: issue,
  context, and possible solutions. Resumes on a `user_decision` event.
- **Signature:** `request_decision(issue, context, options[]) → void`
- **Used by:** Review Agent
- **Stage:** Review (R3 No → R6)

### `report_bug`
- **Purpose:** Report a product bug, test-code issue, or missing tests: cause,
  reproduction steps, and test results. The Orchestrator publishes the report **as a
  comment in the issue** and returns control to the Development workflow.
- **Signature:** `report_bug(summary, reproSteps[], testResults) → void`
- **Used by:** Testing Agent
- **Stage:** Testing — failure caused by product/test code or missing tests (T4)

### `report_env_issue`
- **Purpose:** Report an environment or access problem blocking test execution. Testing
  resumes on an `@bot /resume` command after the User restores the environment.
- **Signature:** `report_env_issue(description, whatIsNeeded) → void`
- **Used by:** Testing Agent
- **Stage:** Testing — environment/access failure (T5)

### `request_criteria_clarification`
- **Purpose:** Request clarification of acceptance criteria / expected behavior. The
  Orchestrator routes the answer to the Planner.
- **Signature:** `request_criteria_clarification(question, context?) → void`
- **Used by:** Testing Agent
- **Stage:** Testing — failure caused by unclear requirements (T10)

### `report_tests_passed`
- **Purpose:** Report that all tests passed (with a summary); the Orchestrator then checks
  the manual acceptance policy.
- **Signature:** `report_tests_passed(summary) → void`
- **Used by:** Testing Agent
- **Stage:** Testing — all tests passed (T2 Yes)

### `submit_pr_description` [new]
- **Purpose:** Carry the Coder's result to the Orchestrator: the title and body of the pull
  request the Orchestrator creates after `run_completed` (publication rule — the agent
  never writes to GitHub). This is the summary carrier: `report_completion` stays
  parameterless, the payload travels here.
- **Signature:** `submit_pr_description(title, body) → void`
- **Used by:** Coder
- **Stage:** Development — right before `report_completion` (V12–V13)

## Escalation pattern

`request_clarification`, `report_blocker`, `request_decision` and
`request_criteria_clarification` share one shape — *what happened, context, options → the
flow resumes on a user event* — but they are deliberately four separate tools, not one:

- **Clarifications** (`request_clarification`, `request_criteria_clarification`) ask for
  missing information; **blockers/decisions** (`report_blocker`, `request_decision`) hand
  the User a choice.
- The agent-specific names let the Orchestrator validate tool availability per agent
  profile and route the answer to the right flow stage.

## Tool availability per agent

| Tool | Planner | Coder | Review Agent | Testing Agent |
|---|:---:|:---:|:---:|:---:|
| `github_issue_read` | ✅ | ✅ | ✅ | ✅ |
| `github_comments_read` | ✅ | ✅ | ✅ | ✅ |
| `github_pr_read` | ✅ | ✅ | ✅ | ✅ |
| `github_pr_diff` | — | — | ✅ | — |
| `web_search` | ✅ | ✅ | — | — |
| `report_completion` | ✅ | ✅ | ✅ | ✅ |
| `save_plan` | ✅ | — | — | — |
| `request_clarification` | ✅ | — | — | — |
| `report_blocker` | — | ✅ | — | — |
| `submit_review_comments` | — | — | ✅ | — |
| `request_decision` | — | — | ✅ | — |
| `report_bug` | — | — | — | ✅ |
| `report_env_issue` | — | — | — | ✅ |
| `request_criteria_clarification` | — | — | — | ✅ |
| `report_tests_passed` | — | — | — | ✅ |
| `submit_pr_description` | — | ✅ | — | — |

## Possible consolidation

- The three issue/PR read tools (`github_issue_read`, `github_comments_read`,
  `github_pr_read`) could be merged into a single `github_read(entity, id)`. This does not
  change any flow.
- `github_pr_create` / `github_pr_update` were removed: under the publication rule the
  Orchestrator creates and updates the PR when the Coder's Run completes; the Coder
  delivers the description through `submit_pr_description`.
