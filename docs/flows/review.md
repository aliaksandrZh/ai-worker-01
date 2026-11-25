```mermaid
flowchart TB
START(["Pull Request created / updated"])

subgraph REVIEW["3. CODE REVIEW — Review Agent"]
    direction TB

    R1["Review Agent: analyzes Pull Request<br/><br/>github_issue_read<br/>github_pr_read<br/>github_pr_diff<br/>github_comments_read<br/>built-in code read"]
    R2{"Are there any review comments?"}
    R3{"Can the issues be resolved without user input?"}

    RPASS["Review Agent: report_completion"]
    R4["Review Agent → Orchestrator:<br/>submits review comments<br/><br/>submit_review_comments · report_completion"]
    R5["Orchestrator: publishes the comments<br/>to the PR and starts a fixing<br/>Coder Run (see development.md)"]

    R6["Review Agent → Orchestrator:<br/>reports issue, context, and possible solutions<br/><br/>request_decision"]
    R7["Orchestrator → User:<br/>requests decision"]
    R8["Waiting for Human"]
    R9{"User decision"}
    EXIT(["Review completed —<br/>fixes assigned or decision recorded"])

    R1 --> R2
    R2 -- "No" --> RPASS --> DONE(["Review passed — no comments"])
    R2 -- "Yes" --> R3

    R3 -- "Yes" --> R4 --> R5 --> EXIT
    R3 -- "No" --> R6 --> R7 --> R8 --> R9

    R9 -- "Request fixes" --> R5
    R9 -- "Change requirements / scope" --> ORCH["Orchestrator:<br/>records decision, re-plans"]
    R9 -- "Accept risk / reject comment" --> DONE
    ORCH --> EXIT
    end

START --> R1
```

> Every Review Agent Run ends with `report_completion`. The review outcome (comments,
> approval, request changes) is published to the PR by the Orchestrator — the Review Agent
> never writes to GitHub. Round caps and no-progress handling are Orchestrator policy —
> not part of this flow.
