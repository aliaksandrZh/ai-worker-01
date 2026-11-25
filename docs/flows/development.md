```mermaid
flowchart TB
    START(["Development started"])

    subgraph DEVELOPMENT["2. DEVELOPMENT — Coder"]
        direction TB

        D1{"Execution mode"}

        D1 -.- T0["Tools (all modes)<br/><br/>built-in code read / edit / write<br/>github_pr_read<br/>github_comments_read<br/>web_search<br/>project-specific build &amp; tests"]

        subgraph IMPLEMENTATION["Implementation"]
            I1["Implement the task from scratch"]
        end

        subgraph FIXING["Fixing"]
            F1["Resolve code review comments"]
            F2["Fix bugs"]
        end

        D1 --> I1
        D1 --> F1
        D1 --> F2

        I1 --> V1
        F1 --> V1
        F2 --> V1

        V1["Coder: verifies own changes"]
        V1 --> V2{"Verification passed?"}

        V2 -- "No" --> V3["Coder: analyzes and fixes issues"]
        V3 --> V1

        V2 -- "Yes" --> V4{"Blocker encountered?"}

        V4 -- "Yes" --> V5{"Can Coder resolve it independently?"}
        V5 -- "Yes" --> V3
        V5 -- "No" --> V6["Coder → Orchestrator: reports blocker, causes, and possible solutions<br/><br/>report_blocker"]
        V6 --> V7["Orchestrator → User: requests decision / information / access"]
        V7 --> V8["Waiting for Human"]
        V8 --> V9["User provides response"]
        V9 --> V10["Orchestrator → Coder: passes user response"]
        V10 --> V1

        V4 -- "No" --> V11{"Work completed?"}
        V11 -- "No" --> V1
        V11 -- "Yes" --> V12["Coder: creates or updates Pull Request<br/><br/>github_pr_create / github_pr_update"]
    end

    START --> D1
    V12 --> NEXT(["Development completed"])
```