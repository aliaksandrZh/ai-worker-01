# Observations — known non-critical debt

> Created 2026-11-20 out of the task-01 self-review (`docs/tasks/01-stack-and-skeleton.md`,
> branch `task-1-stack-and-skeleton`). Owner call 2026-11-20: nothing here is critical —
> record now, revisit later. Each item names its revisit trigger, so none of them rot silently.

## OBS-1: `/healthz` `/readyz` `/metrics` are unauthenticated on the dev-published port

- **Where:** `src/compose.yaml` (`runtime-api` ports: `"${RUNTIME_API_HOST_PORT:-8080}:8080"`), `src/Platform/Http/PlatformEndpointExtensions.cs`.
- **State today:** acceptable — local dev only. The host port exists so walkthrough curls work without tokens; `X-Service-Token` auth is reserved for the internal API (later tasks), and decision-65 contour is otherwise intact (postgres private-only; Komodo's 9120 is UI/bootstrap).
- **Revisit trigger:** any deployment beyond the dev machine, or the first time the runtime-api port is reachable by someone else.
- **Fix sketch:** gate `/metrics` and `/readyz` with `RequireServiceToken()`, or stop publishing the port at all and reach the service through the compose network only.



## OBS-4: Dockerfile has no restore-cache layering

- **Where:** `src/Dockerfile` — a single `dotnet publish` step after the full source `COPY`.
- **Effect:** NuGet restore re-runs on every image build (any source change invalidates the layer). Speed issue only, not correctness — negligible at current size, noticeable as packages/files grow.
- **Revisit trigger:** build times become annoying, or the package set grows.
- **Fix sketch:** standard layered pattern — copy `*.props` + `*.csproj` → `dotnet restore` → copy sources → `dotnet publish`.

## OBS-5 GitHub polls
- An unparseable issue_url/pull_request_url tail (issue_number 0) pins a stream's cursor at that comment
   permanently — data-safe, unrealizable from real GitHub payloads.
- per_page > 100 silently capped by GitHub REST (pagination still correct via Link headers).
## OBS-6: `max_context_tokens` is enforced with a bytes/4 token estimate (decision 63/56)

- **Where:** `src/Hosts/Orchestrator/Context/ContextBudget.cs` — `ContextTokenEstimator` (the named V1 helper): tokens = `ceil(utf8_bytes / 4)` over the SERIALIZED input (the frozen RunContextSnapshot content).
- **State today:** acceptable — V1 has no tokenizer (model selection deferred to V2) and the estimate is deterministic and conservative (JSON overhead and escaped non-ASCII inflate the count). The fit-against-the-model-window check (whether `max_context_tokens` itself fits the model with an answer reserve) is decision 56's V2 validation. The 256 KiB / 512 KiB byte limits are exact and unaffected.
- **Revisit trigger:** model selection lands (V2) — replace the estimate with a real tokenizer; also when profiles start targeting non-ASCII-heavy models the estimate only gets safer (over-counts).
- **Fix sketch:** keep `ContextTokenEstimator` as the single seam; swap the byte-division for a tokenizer call and keep `TokenBudgetBytes` inverted accordingly. Consumers: ContextAssembler only.

## OBS-7: Review-thread resolution state is not part of the V1 sync (decision 64)

- **Where:** `src/Hosts/Poller/PrReviewCommentsStream.cs` (REST `pulls/comments` — no resolved/unresolved field), `src/Hosts/Orchestrator/Context/ContextAssembler.cs` (`ComposeThreads` writes `resolved: null`, ordering treats unknown as unresolved).
- **State today:** acceptable — REST review comments carry threading (`in_reply_to_id`) but GitHub only exposes thread resolution via GraphQL `pullRequest.reviewThreads.isResolved`. So the reviewer context marks every thread `resolved: null` and orders deterministically (newest activity first); the "unresolved threads first" hook is in place and activates the moment resolution data arrives.
- **Revisit trigger:** ticket 19 (reviewer profile) needs real resolution state, or the Poller gains a GraphQL stream — then sync `isResolved` per thread and set the field.
- **Fix sketch:** poller: a per-PR GraphQL fetch of `reviewThreads { id, isResolved, comments { databaseId } }` stored as a thread-state column on `github_comment` (or a small thread table); assembler: read it, sort `unresolved → resolved` and stop writing `null`.

## OBS-8: `run_context_snapshot.content` moved jsonb → text (ticket 11)

- **Where:** `src/Infrastructure/Persistence/AppDbContext.cs` (`Content` column type), migration `20261121142504_SnapshotContentExactBytes`.
- **State today:** fine — `jsonb` re-serialized stored JSON (key order + whitespace), so the frozen input the Bridge pulls via `/input` was not the exact bytes the Orchestrator hashed and budget-checked. As `text`, the assembled bytes are `content_hash`-verifiable end to end. This amends ticket 09's jsonb choice (its note recorded the column as an incidental addition, not an immutability requirement).
- **Revisit trigger:** none expected; reconsider only if a future feature needs server-side JSON operators on the snapshot content (excluded/included sources remain jsonb).

## OBS-9: the planner's LLM provider is a dev-posture baked config (ticket 12)

- **Where:** `src/config/agents/planner/models.json` + `settings.json` (baked into the version image,
  PoC `poc/pi/pi-agent-state` shape): provider `ollama` → `http://host.docker.internal:11434/v1`, model
  `glm-5.3-flash:cloud` (the deployment-time Ollama app proxy). Decision 56 defers LLM model/provider
  selection to V2 — but ticket 12's real Pi runs need SOME model now.
- **State today:** acceptable dev posture — the config is baked per profile version (pinned, no
  floating anything), no secret travels (Ollama's local proxy needs no credential), and a provider
  change is the normal configuration-plane flow (edit → new version → automatic build). The container
  reaches the host via Docker Desktop's `host.docker.internal` (on plain Linux compose, add
  `extra_hosts: host.docker.internal:host-gateway` to the agent deployment or use a LAN IP).
- **Revisit trigger:** V2 model selection (decision 56) — replace with the real per-deployment model
  configuration (or secret-based auth); also if Ollama proxy errors (see `docs/ollama-error.md`) recur,
  flip `defaultModel` to a tool-capable local model (e.g. `qwen3.5:4b`) — one config change + rebuild.
