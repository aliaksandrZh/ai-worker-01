# Agent Tools

> **Status: proposal.** Derived from the workflow definitions in [plan.md](plan.md),
> [development.md](development.md), [review.md](review.md), [test.md](test.md).
> Only the tools marked **[explicit]** are named in the flow documents; every other name
> and signature is inferred and open to revision.
>
> Agents communicate with the Orchestrator / User **only through tools**. Every
> "Agent → Orchestrator/User" arrow in a flow diagram maps to one of the
> communication tools below.
>
> **Out of scope:** file operations (read / search / edit / write) are built-in agent
> capabilities, and building the project / running tests is project-specific — neither
> belongs to this tool list.

## Categories

1. **Context (read-only)** — reading GitHub entities (issue, comments, PR, diff).
2. **Communication** — the only channel to the Orchestrator/User.

---

## 1. Context tools (read-only)

### `github_issue_read` [explicit]
- **Purpose:** Read a GitHub issue (title, body, labels, state).
- **Signature:** `github_issue_read(issueId) → Issue`
- **Used by:** Planner
- **Stage:** Plan — analyzing requirements and context (P1)

### `github_comments_read` [explicit]
- **Purpose:** Read comments on an issue or PR (paginated, newest first).
- **Signature:** `github_comments_read(entityRef, page?) → Comment[]`
- **Used by:** Planner, Coder, Review Agent
- **Stage:** Plan (P1); Development — resolving review comments (F1); Review — analyzing the PR (R1)

### `github_pr_read` [explicit]
- **Purpose:** Read a pull request: description, changed files, status, review threads.
- **Signature:** `github_pr_read(prId) → PullRequest`
- **Used by:** Planner, Coder, Review Agent
- **Stage:** Plan (P1); Development — Fixing mode (F1); Review (R1)

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

### `save_plan` [explicit]
- **Purpose:** Save or update the plan: scope and acceptance criteria. Triggers the Orchestrator's plan-approval step.
- **Signature:** `save_plan(plan: { goals, scope, acceptanceCriteria, steps[] }) → Plan`
- **Used by:** Planner
- **Stage:** Plan (P2)

### `request_clarification` [explicit]
- **Purpose:** Request missing information from the User via the Orchestrator. Blocks the agent until a response is passed back.
- **Signature:** `request_clarification(question, context?) → UserResponse`
- **Used by:** Planner
- **Stage:** Plan — not enough information (C1)

### `report_blocker`
- **Purpose:** Report a blocker that the Coder cannot resolve independently: cause, context, and possible solutions. Resumes when the User responds.
- **Signature:** `report_blocker(description, cause, options[]) → UserResponse`
- **Used by:** Coder
- **Stage:** Development — blocker encountered (V4–V10)

### `github_pr_create` / `github_pr_update`
- **Purpose:** Create a new pull request or update an existing one (description, commits, draft flag).
- **Signature:** `github_pr_create(title, body, base, head) → PullRequest`
- **Used by:** Coder
- **Stage:** Development — work completed (V12)

### `submit_review_comments`
- **Purpose:** Submit review comments to the Orchestrator, which assigns the fixes to the Coder.
- **Signature:** `submit_review_comments(comments: { file, line, severity, message }[]) → void`
- **Used by:** Review Agent
- **Stage:** Review — issues resolvable without user input (R3 Yes → R4)

### `request_decision`
- **Purpose:** Escalate a review issue that cannot be resolved without user input: issue, context, and possible solutions.
- **Signature:** `request_decision(issue, context, options[]) → UserDecision`
- **Used by:** Review Agent
- **Stage:** Review (R3 No → R6)

### `report_bug`
- **Purpose:** Report a product bug or test-code issue: cause, reproduction steps, and test results. Returns control to the Development workflow.
- **Signature:** `report_bug(summary, reproSteps[], testResults) → void`
- **Used by:** Testing Agent
- **Stage:** Testing — failure caused by product/test code (T4)

### `report_env_issue`
- **Purpose:** Report an environment or access problem blocking test execution. Testing resumes after the User restores the environment.
- **Signature:** `report_env_issue(description, whatIsNeeded) → void`
- **Used by:** Testing Agent
- **Stage:** Testing — environment/access failure (T5)

### `request_criteria_clarification`
- **Purpose:** Request clarification of acceptance criteria / expected behavior. The Orchestrator routes the answer to the Planner.
- **Signature:** `request_criteria_clarification(question, context?) → UserResponse`
- **Used by:** Testing Agent
- **Stage:** Testing — failure caused by unclear requirements (T10)

### `report_tests_passed`
- **Purpose:** Report that all tests passed; the Orchestrator then checks the manual acceptance policy.
- **Signature:** `report_tests_passed(summary) → void`
- **Used by:** Testing Agent
- **Stage:** Testing — all tests passed (T2 Yes)

---

## Tool availability per agent

| Tool | Planner | Coder | Review Agent | Testing Agent |
|---|:---:|:---:|:---:|:---:|
| `github_issue_read` | ✅ | — | — | — |
| `github_comments_read` | ✅ | ✅ | ✅ | — |
| `github_pr_read` | ✅ | ✅ | ✅ | — |
| `github_pr_diff` | — | — | ✅ | — |
| `web_search` | ✅ | ✅ | — | — |
| `save_plan` | ✅ | — | — | — |
| `request_clarification` | ✅ | — | — | — |
| `report_blocker` | — | ✅ | — | — |
| `github_pr_create` / `github_pr_update` | — | ✅ | — | — |
| `submit_review_comments` | — | — | ✅ | — |
| `request_decision` | — | — | ✅ | — |
| `report_bug` | — | — | — | ✅ |
| `report_env_issue` | — | — | — | ✅ |
| `request_criteria_clarification` | — | — | — | ✅ |
| `report_tests_passed` | — | — | — | ✅ |

## Possible consolidation

- The three read tools (`github_issue_read`, `github_comments_read`, `github_pr_read`)
  could be merged into a single `github_read(entity, id)`. This does not change any flow.
