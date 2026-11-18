# Komodo vs. manual docker for the pi-worker API — analysis (2026-11-18)

> Recorded while the pi RPC API on the Komodo path was fully built and verified
> (smoke pass, live todo-app demo). The question: drop Komodo and have the API
> manage docker itself? Nothing is decided; this is the evidence and the trade-off.

## Where Komodo earned it (observed in the build)

- **Lifecycle was cheap**: create/deploy/list/stop/delete were simple client calls
  (`CreateDeployment` → `Deploy` → `ListDeployments` → `StopDeployment` → `DeleteDeployment`).
  Session containers are visible resources with state; `GET /sessions` reads Komodo directly
  and survives API restarts.
- **Infra the API didn't have to write**: image Build pipeline for `pi-worker` +
  auto-redeploy on new digests, Resource Sync (`pi.sync.toml` as single source of truth),
  UI with terminals/logs/update history, API-key auth, and the remote-host path —
  Periphery can run wherever a docker host is; the API stays localhost.
- **No docker socket on the API host** — the API's docker power is whatever Komodo grants it
  (soft point locally: the api already executes host commands via host-bridge).

## Where Komodo cost it (observed in the build)

- **The data plane**: the attach-terminal is a PTY behind two hops — that is why the client
  needs the `0x00` Begin byte + `payload+0x01` Forward frames, and why input echo, CRLF,
  unknown record types, and `"pty exited"` frames exist. A
  `docker exec -i <c> pi --mode rpc` child process would need none of it — clean stdio
  JSONL; roughly half of `api/rpc-session.mjs` would disappear.
- **Lifecycle traps**: `Deploy` vs `StartDeployment` (docker-start-only, fails "No such
  container" on fresh deployments), `Deployment busy` lock after a stop on a
  not-deployed deployment, `not_deployed` states, delete ordering. Each was small; each was
  discoverable only by reading Komodo's Rust source.
- **Two extra daemons** (core + ferretdb + periphery) and API-key setup as prerequisites
  for basic operation.

## The deciding question

Do you need Komodo's **remote/fleet/UI/build story** in the foreseeable future? That is its
genuine value; a local single-box playground gets little else from it.

- **Eventually pi workers on a VPS, a UI to browse them, sync-declared infra, build
  automation → keep Komodo.** Making this path work and verified was the expensive part.
- **Stays a localhost playground → manual docker is defensible and simpler end-to-end.**
  Notably, the design survives the swap: HTTP endpoints, `host-bridge` extension, smoke
  script, and pi-side session persistence stay identical. Only `rpc-session.mjs`
  (terminal ⇄ child-exec transport) and `createSession`/`stopSession` (Komodo calls ⇄
  `docker run/stop/rm` via the docker CLI, still zero new dependencies) change.

## Recommendation

Don't decide from clean-line aesthetics. The Komodo path now passes all its tests; a
migration is a new project (one day of careful work plus live re-verification —
re-verification historically surfaced several hidden behaviors like the Begin byte), not a
cleanup. If the remote/fleet goal is dead, the swap is worth doing once and never again.
If it is uncertain, parking Komodo is the cheaper position.

Migration sketch, if taken:
- Replace the terminal transport with a `docker exec -i` child per session (raw JSONL).
- Replace Komodo lifecycle calls with docker CLI spawns (`docker run`, `docker stop`, `docker rm`,
  `docker ps`) — drop `komodo_client` from `api/package.json`.
- Re-establish what Komodo did by hand: `pi-worker:latest` image build task, base worker
  redeploy, and (if ever remote) a docker over ssh context.
- Keep: endpoints, `host-bridge` extension, smoke script (re-run live), log layout, pi
  session persistence.
