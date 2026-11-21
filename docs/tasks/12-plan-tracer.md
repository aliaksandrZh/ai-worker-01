# 12: /plan tracer bullet — real planner end to end

**What to build:** The first real agent flow, replacing the stub with Pi: authorized `/plan` on an open Issue → Context Assembler builds the snapshot → Runtime API starts a Run with the planner profile image (Build Worker ready) → Bridge launches Pi in `--mode rpc` with `--session-dir` + `--session-id` pointed at the per-Run mount, `PI_CODING_AGENT_DIR` baked → task input delivered as one RPC `prompt` (structured Markdown from the snapshot) → real pi→RunEvent mapping (agent_started … tool events … agent_settled) → the planner analyzes, publishes plan/questions via GitHub MCP, writes `result.json` with artifacts and `requiredPublications` → Bridge validates in-place, derives Git facts, correlates publications with tool events → `completed` → plan Artifact persisted → Task Succeeded, Workflow `waiting_for_human`. Planner publication verified per decision 42/59; failure → `github_publication_failed` + Outbox notification, artifacts kept, `/retry` recovery. Profile prompt instructs MCP posting + manifest writing; planner's MCP allow-list pinned in the profile version.

**Spec:** `docs/pi-agent-architecture/06-command-reference.md` §3 (/plan), §13 (system notifications + required publications); `04-api-contracts.md` §3–§4; `03-state-machines.md` §2–§4; `05-v1-decisions.md` decisions 11, 12, 26, 33, 34, 36 (phase 1), 42, 57, 58, 59, 63; `01-architecture-and-flows.md` §6 (plan flow diagram), §15 (Pi mapping).

**Blocked by:** 09, 10, 11, 11b, 08

**Status:** implemented on branch `task-12-plan-tracer` (staged uncommitted, pending owner review).
Automated verification: `dotnet test src/PiAgent.slnx` **547/547 green** (Architecture 2, Platform 9,
Infrastructure 47, Hosts 489) plus `node --test src/config/agents/stub/runtime-bridge/*.test.mjs` **27/27**.
**Real-Pi mechanics were probed outside docker and inside a throwaway image** (all one-shot, evidence in
"What has been done" below); **the full live-stack walkthrough has NOT been executed in this session — it
needs Komodo + the stack up (owner go-ahead); every live step in "How to manually test" is unverified.**
Owner decisions recorded before implementation (2026-11-21):
- **LLM + MCP mirror the PoC** (owner: "POC Pi image already solved it"): baked `models.json` (Ollama via
  `host.docker.internal:11434`, `glm-5.3-flash:cloud`) + `settings.json`; official `github-mcp-server`
  binary baked from `ghcr.io/github/github-mcp-server:v2.2.0` (multi-stage COPY) with the allow-list
  pinned in the baked `mcp.json` (`GITHUB_TOOLS=issue_read,add_issue_comment`; the server refuses unknown
  tool names at startup — verified — and pi reaches only those two, verified live-in-image). Workspace
  clone mirrors `pi-worker-entry`: baked `agent-entry` script, `GIT_CLONE_REPO` env set by the Runtime API
  at `/start` (repo NAME only), token read from the mounted secret file, token scrubbed from the remote.
- **Publication correlation stays strict** (owner: "no external mechanism"): prompt-echoed `toolCallId`
  correlated with error-free `tool_execution_end` records; no fallback matching. Plus one stricter
  Bridge check: the image bakes `PI_REQUIRED_PUBLICATION_KINDS=issue_comment` — an agent that never
  even attempted the publication fails deterministically instead of "succeeding" unverified.
- **Outbox notification scope**: `github_publication_failed` only (06 §13 decision-42 template with the
  artifacts-kept note); the generic task-failure notification stays with ticket 15.
- **/status phase = mirror the latest RunEvent type verbatim** (owner: "no extra enum"); heartbeat rows
  are skipped by the query — heartbeat never advances the phase (decision 58).
- **Probe-discovered facts pinned by this implementation:** real pi writes the session file as
  `<timestamp>_<session-id>.jsonl` (NOT `<runId>.jsonl`) — the Bridge discovers the actual path via
  `get_state.sessionFile` and the Runtime API records it on the AgentSession row at the terminal
  callbacks (decision 57). Real pi's `message_update` carries nested per-type `assistantMessageEvent`
  events — only `text_delta` aggregates (the first draft leaked `thinking_delta` into assistant output;
  caught by node tests, fixed). `tool_execution_end` carries `isError` — errored ends never correlate.

- [x] `@bot /plan` on an open Issue → real Pi run in the planner image → plan/gaps/questions comment published via GitHub MCP + plan Artifact row (inline, ≤256 KiB) — *pipeline stages automated-tested; the real-GitHub publication verified only in the (unexecuted) live walkthrough*
- [x] requiredPublications correlated before `completed`; unconfirmed publication → Run/Task fail `github_publication_failed`, notification posted, artifacts kept — *notification + failure path automated-tested (`GithubPublicationFailed_EnqueuesTheDecision42Notification...`); live unverified*
- [x] The planner profile prompt encodes the within-run transient GitHub/MCP retry with GitHub verification before any re-post (no duplicate comments — decision 42) — *baked `APPEND_SYSTEM.md`*
- [x] `/status` during the run shows the current phase from RunEvents with the progress rules (heartbeat never progress) — *automated: phase mirrors the latest non-heartbeat RunEvent type verbatim (owner ruling: no extra enum)*
- [x] Task Succeeded → Workflow `waiting_for_human`; a later `/plan` creates a new Task + new per-Run session at a new per-Run directory — *structure from tickets 07–09 recheck-tested; the repeated-/plan live run unverified*
- [x] Real event mapping persisted (agent_started/tool_*/agent_settled) with text deltas aggregated — *node-tested against the REAL pi record shapes captured in probes (typed deltas, top-level tool events, isError)*
- [ ] Acceptance scenario "Planning" (`05` matrix) passes end to end — *requires the live walkthrough (owner)*

## What has been done (2026-11-21, agent)

- **Planner profile is now a real Pi runtime** (`config/agents/planner/`): baked `models.json`
  (Ollama via `host.docker.internal:11434`, `glm-5.3-flash:cloud`) + `settings.json`
  (`defaultProjectTrust: "never"` — the cloned workspace's `.pi` can never inject project config,
  decision 34 anti-dialog posture) + `mcp.json` ('official `github-mcp-server` binary baked from
  `ghcr.io/github/github-mcp-server:v2.2.0` multi-stage; `GITHUB_TOOLS=issue_read,add_issue_comment`
  — unknown names fail the server at startup, fail-closed; `exposure: "direct"` so every MCP call is a
  top-level tool call with its own `toolCallId`; the token resolves via pi's `!cat` interpolation from
  the mounted `/run/secrets/github-write-pat.txt` — decision 54, no token env var) + `APPEND_SYSTEM.md`
  (the profile prompt: publish ONE plan/gaps/questions comment via `add_issue_comment`; decision-42
  transient-failure rule — verify with `issue_read` method `get_comments` before re-posting, never
  duplicate; the exact `result.json` manifest contract with toolCallId/externalId echo) + `agent-entry`
  (the `pi-worker-entry`-shaped clone: `GIT_CLONE_REPO` env → clone with the mounted token into the
  EMPTY workspace only, token scrubbed from the remote, then `exec` the Bridge).
  `Dockerfile` bakes those, pins Pi 1.6.0 + GH MCP v2.2.0, and sets the baked launch config:
  `PI_AGENT_BIN=pi`, `PI_AGENT_ARGS="--mode rpc"`, `PI_REQUIRED_PUBLICATION_KINDS=issue_comment`.
  profile.yaml: `secrets: [github-write-pat.txt]`, bridge version 1.1.0 (stub bumped identically —
  shared modules). DEV-posture LLM note recorded as OBS-9 (V2 revisit per decision 56).
- **Bridge (shared, byte-identical copies, tests run against the stub dir)**: env-parameterized agent
  launch (`PI_AGENT_BIN`/`PI_AGENT_ARGS`, defaults keep the stub working; agent cwd = the workspace —
  the stub default arg resolves absolutely now); a launch handshake over the pi RPC `response` channel
  (new `rpc.mjs` correlator): `get_state` → the REAL session file path (pi writes
  `<timestamp>_<session-id>.jsonl`, not `<runId>.jsonl` — decision 57's discovered path) and `prompt`
  → disposition check (`"started"` or the run fails `runtime_failure`); real-pi event mapping in
  `events.mjs` (real + stub shapes: nested typed `assistantMessageEvent` — only `text_delta`
  aggregates, `thinking_delta` never leaks; top-level `tool_execution_start/update/end` with
  `isError`; `agent_settled {aborted}`); `manifest.mjs`: `requiredPublicationKinds` mandate +
  `terminalFromManifest` carries the discovered `sessionFilePath`. The terminal `failed` body reports
  it too (launch failures before the handshake have none — the field is optional).
- **Runtime API**: no schema change (no migration). Terminal callbacks accept the optional
  `sessionFilePath` and write it onto the AgentSession row (decision 57); `/start` now sets
  `GIT_CLONE_REPO=<repository full name>` in the container env (provisioning input, not a secret;
  profiles whose entry script clones use it, the stub ignores it).
- **Orchestrator**: `RunTerminalConsumer` enqueues the decision-42 Outbox notification exactly when a
  failed Run reports `github_publication_failed` (06 §13 shape: command, agent, attempt, stage,
  error + diagnostic id `run_<id>`, artifacts-kept note, /retry pointer; idempotency key = the Run id;
  generic task-failure notifications remain ticket 15's). `/status` mirrors the latest non-heartbeat
  RunEvent TYPE as the `Phase:` piece of the Execution line (owner ruling; heartbeat rows are skipped
  by the query — heartbeat never advances the phase, decision 58).
- **Config**: `routing.yaml` flips `/plan → planner` (stub stays for tickets 13–15 levers).
  README write-PAT note updated.
- **Tests**: node 27 (real-pi shapes, typed-delta exclusion, `isSuccessfulToolEnd`, RpcCorrelator,
  mandated kinds, session path, stub handshake/response incl. `get_state.sessionFile`); Hosts 489
  (+6): decision-42 notification (single outbox row, text shape, artifacts-kept, no publication of
  the agent's result; other codes notify nothing), `/status` phase mirror incl. the heartbeat
  exclusion, terminal `sessionFilePath` write-back (completed) + optional-field failure path,
  `GIT_CLONE_REPO` env (set/absent), and one shipped-config integrity test (the real `src/config`
  parses strictly — a dropped profile key aborts CI, not the next stack start).

**Mechanics probed outside docker + inside throwaway images (one-shot runs, evidence):**
1. Host probe `pi --mode rpc` with the baked config shape: `get_state` answered with the real
   `sessionFile` (`<timestamp>_<id>.jsonl`), prompt disposition `"started"`, real record stream
   captured (message_update nesting, auto_retry records, `agent_settled {aborted:false}`).
2. Host probe with a LOCAL tool-capable model (qwen3.5:4b): a REAL `tool_execution_start/update/end`
   with top-level `toolCallId`, `isError: false`, `durationMs` — the exact mapper input; 47
   `thinking_delta` + 6 `text_delta` stream events confirmed the typed-delta exclusion is required
   (the first mapper draft leaked thinking text into `assistant_output` — caught by node tests before
   any live run).
3. Planner image built (one-shot, throwaway tag, removed afterwards): env baked
   (`PI_CODING_AGENT_DIR`, `PI_AGENT_BIN=pi`, `PI_AGENT_ARGS`, `PI_REQUIRED_PUBLICATION_KINDS`,
   `GIT_CLONE_TOKEN_FILE`), files owned uid 1000, `github-mcp-server` binary present (`--help`
   documents `--tools`/`GITHUB_TOOLS`).
4. One-shot container probe (dummy token file): pi connected the model via
   `host.docker.internal:11434` (`glm-5.3-flash:cloud` — no 403), started the MCP server through
   `mcp.json` `!cat` interpolation, and the model reported EXACTLY two GitHub tools:
   `mcp__github__add_issue_comment` + `mcp__github__issue_read` — the allow-list pin verified
   end to end, tool names matching the `tool_execution_*` stream.
5. `GITHUB_TOOLS` fail-closed verified live: an unknown tool name aborts the server at startup
   (`unknown tools specified in WithTools`), and the `issues` toolset was listed to pin exact names.
6. `docker compose config --quiet` OK; no compose/env changes were needed.

**Known behavior to expect (from the probes):** with the model unreachable the run settles without a
manifest → Run failed `manifest_invalid` (honest failure; ticket 13 covers hungs). MCP-server startup
problems surface as a non-blocking pi `notify` extension record (not a parked dialog).

## How to manually test

> **Verification state:** automated verification complete (547 dotnet + 27 node; probes above).
> **NOTHING below has been executed live in this session — needs Komodo + the stack (owner go-ahead).**
> Prerequisites: the standard setup (src/.env, both PAT files, Komodo up) + Docker network reachability
> for ghcr.io and github.com at build/run time + the Ollama app running on the host (OBS-9; on plain
> Linux add `host.docker.internal:host-gateway` for agent containers or bake a LAN baseUrl).

1. ϩ `task stack-rebuild && task stack` (wait green). The Config Loader creates a NEW planner version
   (profile.yaml changed + 5 new baked files) and the Build Worker builds `agent-planner:v<N>` through
   the ghcr multi-stage (first build pulls the ghcr image). Verify:
   ```sh
   docker compose --env-file src/.env -f src/compose.yaml logs orchestrator --since 10m | grep -i "planner"
   #   expected: "planner vN: ready, container_image=agent-planner:vN, sha256:... job completed"
   docker image inspect agent-planner:vN --format '{{json .Config.Env}}'
   #   expected: PI_CODING_AGENT_DIR=/opt/pi-agent/profile, PI_AGENT_BIN=pi, PI_AGENT_ARGS=--mode rpc,
   #             PI_REQUIRED_PUBLICATION_KINDS=issue_comment, GIT_CLONE_TOKEN_FILE=/run/secrets/github-write-pat.txt
   ```
2. ϩ Inject `/plan` on an open registered Issue (real comment `@<bot> /plan`, or the internal hook per
   ticket 09's step 5). Watch the run end to end:
   ```sh
   docker compose --env-file src/.env -f src/compose.yaml logs runtime-api --since 10m | grep "started"
   #   expected: run <runId> started: deployment pi-session-<runId> image agent-planner:vN ...
   #   (order by started_at, NEVER by id — run ids are UUIDs; see ticket 09's diagnostics map)
   ```
   THEN (SQL, per ticket 09's diagnostics map patterns):
   - `run` row: `status='succeeded'`, `result_summary` short (the agent's manifest summary);
   - `run_event` counts: `agent_started`, `turn_started/turn_completed` (several turns), ONE OR MORE
     `assistant_output` (aggregated — NOT one per token), `tool_started/tool_completed` whose payload
     names `mcp__github__issue_read` and `mcp__github__add_issue_comment`, `agent_settled`, `heartbeat`
     (15 s cadence), ONE `completed`, `artifact_produced` ×2 (plan + log_reference). NO thinking-delta
     rows exist (they are never events).
   - `artifact` rows: `plan`/markdown inline content ≤256 KiB with `metadata.branch` = the cloned
     repository's default branch and `metadata.commitSha` = the cloned HEAD (Git facts, decision 59) +
     `log_reference`;
   - `agent_session.session_file_path` = `<per-Run pi-session dir>/<timestamp>_<runId>.jsonl` (the
     Bridge-discovered path, decision 57 — NOT `<runId>.jsonl`);
   - `task.status='succeeded'`, `workflow.status='waiting_for_human'`, container `stopped/stopped`,
     credential `revoked=true`.
   - GitHub side: exactly ONE new bot comment on the Issue with plan/gaps/questions. NO duplicate
     comments.
   - Host tree: `src/storage/workflows/<wf>/tasks/<task>/workspace` contains the CLONE of the
     repository (`.git` present) + `result.json` + `artifacts/plan.md`;
     `runs/<run>/pi-session/YYYY-MM-DDTHH-MM-SS-*.jsonl` + the log trio.
3. ϩ `/status` during a run (`@<bot> /status` while the agent works — planner runs are short, so post
   it right after `/plan` or expect the terminal phase): the reply's Execution line starts with
   `Phase: <event type>` mirroring the latest non-heartbeat RunEvent verbatim (e.g. `Phase:
   tool_started`), and stays unchanged by the 15 s heartbeats.
4. ϩ Planning iteration (acceptance scenario): answer the bot's questions as an ordinary comment, then
   `@<bot> /plan` again → a NEW task + NEW run + NEW per-Run session directory (`attempt_number` 1 on
   the new task; previous task/run rows untouched), and the new planner publishes a NEW comment (the
   old one never edited/re-posted). `runs/<new run>/pi-session/` contains only the new session file.
5. ϩ Forced `github_publication_failed` + notification + /retry (decision 42 end to end):
   ```sh
   cp src/secrets/github-write-pat.txt /tmp/write-pat.bak
   printf 'revoked-token-for-probe' > src/secrets/github-write-pat.txt
   ```
   Inject `/plan` again (new task). Expected: the MCP tool calls fail (401 per call) → the agent
   cannot confirm the publication → Run failed with `error_code='github_publication_failed'`
   (`run_event` `failed` payload / `run.error_code`), Task `failed` (reason recorded), Workflow
   `waiting_for_human`, the notification published via the Outbox (exactly one comment: "Task failed:
   /plan … github_publication_failed … The produced artifacts are kept … (or use /retry)"), artifact
   rows RETAINED. Then restore the PAT (`mv /tmp/write-pat.bak src/secrets/github-write-pat.txt`) and
   `@<bot> /retry` on the failed task → new Run + new session on the same Task, this time succeeding.
   (Alternative deterministic lever: a wrong tool name in `mcp.json` fails the server at startup and
   the required-kinds check fails the run the same way — but that bakes a new version.)
6. ϩ Stub regression (tickets 13–15 depend on it): flip routing `/plan → stub` (config edit +
   orchestrator restart = automatic new stub version — the stub build also re-baked the shared bridge
   modules), inject `/plan` → the stub flow still completes exactly as in ticket 09's walkthrough
   (response-record handshake now included). Flip the rule back afterwards.
7. ϩ Teardown: `task stack-down`; Komodo down (if started for this walkthrough); verify nothing
   remains: `docker ps --filter name=pi-session- --format '{{.Names}}'` and
   `--filter name=pi-agent` → both empty (volumes kept — dev DB state by design).

**Tracing a failed run:** ticket 09's diagnostics map (run facts/`run_event` payloads, runtime-api vs
orchestrator logs, the `bridge.log` trio, host tree paths) applies unchanged. Bridge-specific lines to
grep for: `pi session file:`, `the task prompt did not start a run`, `result manifest invalid:`,
`terminal failure (github_publication_failed)`.

**Unverified in this session** (every step of "How to manually test" as marked ϩ): the actual
`agent-planner:vN` build through the Build Worker (the identical Dockerfile built one-shot locally),
the real GitHub comment publication + plan Artifact row, the repeated-`/plan` acceptance flow, the
forced `github_publication_failed` end to end with the real Outbox comment, `/status` phase during a
live run, the stub regression on the rebuilt bridge, and the real clone through `agent-entry`
(`pi-worker-entry`-shaped mechanics are the PoC-proven shape; the skip-if-non-empty reuse branch is
untested live).
