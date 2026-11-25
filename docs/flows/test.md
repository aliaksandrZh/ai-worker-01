```mermaid
flowchart TB
    ORCH_START["Orchestrator: starts Testing Agent"]

    subgraph TESTING["4. TESTING — Testing Agent"]
        direction TB

        T1["Testing Agent: executes tests<br/><br/>github_issue_read<br/>github_comments_read<br/>github_pr_read<br/><br/>Unit Tests<br/>Integration Tests<br/>E2E Tests<br/>Acceptance Tests<br/><br/>project-specific build &amp; tests"]

        T2{"Have all tests passed?"}
        T3{"What is the cause of the failure?"}

        T4["Testing Agent → Orchestrator:<br/>reports bug, reproduction steps, and test results<br/><br/>report_bug"]

        T5["Testing Agent → Orchestrator:<br/>reports environment or access issue<br/><br/>report_env_issue"]
        T6["Orchestrator → User:<br/>requests environment recovery / access"]
        T7["Waiting for Human"]
        T8["User: restores environment / provides access"]
        T9["user_reply event → Orchestrator:<br/>new Testing Run"]

        T10["Testing Agent → Orchestrator:<br/>requests clarification of acceptance criteria<br/><br/>request_criteria_clarification"]
        T11["Orchestrator → User:<br/>requests clarification of expected behavior"]
        T12["Waiting for Human"]
        T13["User: clarifies expected behavior"]
        T14["user_reply event → Orchestrator:<br/>passes clarification to the Planner"]

        T1 --> T2
        T2 -- "No" --> T3

        T3 -- "Product bug / test code issue" --> T4
        T3 -- "Environment / access issue" --> T5
        T3 -- "No tests exist / cannot be authored" --> T4
        T5 --> T6 --> T7 --> T8 --> T9 --> T1

        T3 -- "Unclear requirements" --> T10
        T10 --> T11 --> T12 --> T13 --> T14
    end

    ORCH_START --> T1

    T4 --> O1["Orchestrator: publishes the bug report<br/>as a comment in the issue, then → Coder:<br/>assigns bug fix and passes test results"]
    O1 --> EXIT1(["Return control to Development Workflow"])

    T14 --> EXIT2(["Return control to Orchestrator"])

    T2 -- "Yes" --> TP["Testing Agent → Orchestrator:<br/>reports all tests passed<br/><br/>report_tests_passed · report_completion"]
    TP --> O2["Orchestrator: checks manual acceptance policy"]
    O2 --> O3{"Manual acceptance required?"}

    O3 -- "No" --> DONE(["Testing completed —<br/>the Orchestrator closes the Workflow"])
    O3 -- "Yes" --> O4["Orchestrator → User:<br/>submits test results for acceptance"]
    O4 --> O5["Waiting for Human"]
    O5 --> O6{"User: accepts the result?"}

    O6 -- "Accepted" --> DONE
    O6 -- "Bug found" --> O1
    O6 -- "Does not meet requirements" --> T10
```

> Every Testing Agent Run ends with `report_completion`. Bug reports and testing outcomes
> are published to the issue by the Orchestrator — the Testing Agent never writes to GitHub.
> Missing tests are authored by the Coder (a fixing round) — the Testing Agent never
> writes product or test code. The manual-acceptance check (O2–O3) is the Orchestrator's
> policy decision.
