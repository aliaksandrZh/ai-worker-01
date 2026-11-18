import { createServer } from "node:http";
import { readdirSync, statSync } from "node:fs";
import { join, dirname } from "node:path";
import { fileURLToPath } from "node:url";
import { PiRpcSession } from "./rpc-session.mjs";

// shortcut: komodo_client's auth module touches browser localStorage at import and we never store jwt tokens; revisit on a komodo_client node-only auth release.
globalThis.localStorage ??= { getItem: () => null, setItem: () => {} };
const { KomodoClient } = await import("komodo_client");

const API_DIR = dirname(fileURLToPath(import.meta.url));
const REPO_DIR = join(API_DIR, "..");
const LOGS_DIR = join(API_DIR, "logs");
const STATE_DIR = join(REPO_DIR, "pi", "pi-agent-state");
const WORKSPACE_DIR = join(REPO_DIR, "pi", "pi-worker-workspace");
const PREFIX = "pi-session-";

const PORT = process.env.PORT || 3000;
const DEPLOYMENT = process.env.PI_WORKER_DEPLOYMENT || "pi-worker";
const PI_SERVER = process.env.PI_WORKER_SERVER || "Local";
const PI_IMAGE = process.env.PI_WORKER_IMAGE || "pi-worker:latest";
const HOST_BRIDGE = {
  cwd: process.env.PI_HOST_BRIDGE_CWD || process.cwd(),
  timeoutMs: Number(process.env.PI_HOST_BRIDGE_TIMEOUT_MS) || 60000,
};

const komodo = KomodoClient(process.env.KOMODO_URL || "http://localhost:9120", {
  type: "api-key",
  params: {
    key: process.env.KOMODO_API_KEY || "",
    secret: process.env.KOMODO_API_SECRET || "",
  },
});

// id -> PiRpcSession; sessions created in previous api lifetimes are listed from
// ListDeployments and reconnected on demand (POST /sessions/:id/connect).
const sessions = new Map();

const send = (res, code, body) =>
  res.writeHead(code, { "content-type": "application/json" }).end(JSON.stringify(body));

const readBody = (req) =>
  new Promise((resolve, reject) => {
    let raw = "";
    req.on("data", (c) => (raw += c));
    req.on("end", () => {
      if (!raw) return resolve({});
      try {
        resolve(JSON.parse(raw));
      } catch {
        reject(new Error("invalid JSON body"));
      }
    });
    req.on("error", reject);
  });

function* walkJsonl(dir) {
  let entries;
  try {
    entries = readdirSync(dir, { withFileTypes: true });
  } catch {
    return;
  }
  for (const e of entries) {
    const p = join(dir, e.name);
    if (e.isDirectory()) yield* walkJsonl(p);
    else if (e.name.endsWith(".jsonl")) yield p;
  }
}

const deploymentName = (id) => PREFIX + id;

const waitRunning = async (name, { timeoutMs = 90000 } = {}) => {
  for (let waited = 0; waited <= timeoutMs; waited += 1000) {
    const r = await komodo.read("GetDeploymentContainer", { deployment: name });
    if (r.state === "running") return r;
    if (r.state === "exited" || r.state === "dead")
      throw new Error(`session container ${name} exited immediately (state: ${r.state})`);
    await new Promise((s) => setTimeout(s, 1000));
  }
  throw new Error(`session container ${name} did not reach running state after ${timeoutMs}ms`);
};

const createSession = async (id) => {
  const name = deploymentName(id);
  await komodo.write("CreateDeployment", {
    name,
    config: {
      server_id: PI_SERVER,
      image: { type: "Image", params: { image: PI_IMAGE } },
      extra_args: ["--interactive", "--init"], // keep stdin open for attach; init for signal handling
      restart: "no",
      auto_update: false,
      labels: `pi-session=${id}`,
      volumes: [
        `${STATE_DIR}:/home/pi/.pi/agent`,
        `${WORKSPACE_DIR}:/workspace`,
      ].join("\n"),
    },
  });
  await komodo.execute("StartDeployment", { deployment: name });
  await waitRunning(name);
  const session = new PiRpcSession({
    komodo,
    server: PI_SERVER,
    container: name,
    terminal: name,
    logsDir: LOGS_DIR,
    hostBridge: HOST_BRIDGE,
  });
  await session.connect();
  sessions.set(id, session);
  return { id, sessionId: session.stateInfo?.sessionId, sessionFile: session.stateInfo?.sessionFile };
};

const stopSession = async (id) => {
  const name = deploymentName(id);
  sessions.get(id)?.close();
  sessions.delete(id);
  try {
    await komodo.execute("StopDeployment", { deployment: name });
  } catch {
    /* already stopped */
  }
  await komodo.write("DeleteDeployment", { id: name });
};

createServer(async (req, res) => {
  const url = new URL(req.url, "http://localhost");
  const path = url.pathname;
  try {
    // -- container lifecycle (pre-existing) --
    if (req.method === "GET" && path === "/status") {
      const r = await komodo.read("GetDeploymentContainer", { deployment: DEPLOYMENT });
      return send(res, 200, { deployment: DEPLOYMENT, state: r.state });
    }
    if (req.method === "POST" && (path === "/start" || path === "/stop")) {
      const action = path === "/start" ? "StartDeployment" : "StopDeployment";
      const update = await komodo.execute(action, { deployment: DEPLOYMENT });
      return send(res, 200, { deployment: DEPLOYMENT, action, ok: update.success, operation: update.operation });
    }

    // -- pi sessions --
    const m = path.match(/^\/sessions\/([^/]+)(\/.*)?$/);
    if (req.method === "POST" && path === "/sessions") {
      const body = await readBody(req);
      const id = `s${Date.now().toString(36)}${Math.floor(Math.random() * 0xffff)
        .toString(16)
        .padStart(4, "0")}`;
      const created = await createSession(id);
      return send(res, 201, { ...created, ...(body?.name ? { name: body.name } : {}) });
    }
    if (req.method === "GET" && path === "/sessions") {
      const list = await komodo.read("ListDeployments", { limit: 0 });
      const remote = (list ?? [])
        .filter((d) => typeof d.name === "string" && d.name.startsWith(PREFIX))
        .map((d) => ({
          id: d.name.slice(PREFIX.length),
          state: d.info?.state,
          status: d.info?.status,
          connected: sessions.get(d.name.slice(PREFIX.length))?.connected ?? false,
        }));
      const stored = [...walkJsonl(join(STATE_DIR, "sessions"))]
        .map((p) => ({ path: p.slice(REPO_DIR.length + 1), mtime: statSync(p).mtimeMs }))
        .sort((a, b) => b.mtime - a.mtime)
        .slice(0, 50);
      return send(res, 200, { sessions: remote, stored });
    }
    if (m) {
      const id = m[1];
      const sub = m[2] ?? "";
      let session = sessions.get(id);
      if (req.method === "POST" && sub === "/connect") {
        if (!session) {
          const r = await komodo.read("GetDeploymentContainer", { deployment: deploymentName(id) });
          if (r.state !== "running")
            return send(res, 409, { error: `session container is ${r.state}, cannot reconnect` });
          session = new PiRpcSession({
            komodo,
            server: PI_SERVER,
            container: deploymentName(id),
            terminal: deploymentName(id),
            logsDir: LOGS_DIR,
            hostBridge: HOST_BRIDGE,
          });
          await session.connect();
          sessions.set(id, session);
        } else if (!session.connected) {
          await session.connect();
        }
        return send(res, 200, { connected: true });
      }
      if (!session) return send(res, 404, { error: "unknown session (POST /sessions to create, POST /sessions/:id/connect to reattach)" });

      if (req.method === "GET" && sub === "")
        return send(res, 200, await session.rpc({ type: "get_state" }).then((r) => r.data));
      if (req.method === "DELETE" && sub === "") {
        await stopSession(id);
        return send(res, 200, { stopped: true });
      }
      if (req.method === "POST" && sub === "/rpc") {
        const cmd = await readBody(req);
        if (typeof cmd?.type !== "string") return send(res, 400, { error: "body must be {type, ...}" });
        return send(res, 200, await session.rpc(cmd));
      }
      if (req.method === "GET" && sub === "/events") {
        const since = Number(url.searchParams.get("since") ?? 0);
        return send(res, 200, session.events(since, url.searchParams.get("type")));
      }
      if (req.method === "GET" && sub === "/ui")
        return send(res, 200, { pending: session.listPendingUi() });
      const uiAnswer = sub.match(/^\/ui\/(.+)$/);
      if (req.method === "POST" && uiAnswer) {
        const body = await readBody(req);
        if (!session.answerUi(uiAnswer[1], body))
          return send(res, 404, { error: "pending dialog not found" });
        return send(res, 200, { answered: true });
      }
      return send(res, 404, { error: "unknown session sub-route" });
    }

    send(res, 404, {
      error: "routes: GET /status, POST /start, POST /stop, POST /sessions, GET /sessions, GET|DELETE /sessions/:id, POST /sessions/:id/connect, POST /sessions/:id/rpc, GET /sessions/:id/events?since=&type=, GET /sessions/:id/ui, POST /sessions/:id/ui/:requestId",
    });
  } catch (err) {
    // komodo_client rejects with { status, result } — surface komodo's own error text when present
    const msg =
      err?.result?.error ?? err?.result?.result?.error ?? err?.message ?? err;
    send(res, 502, { error: typeof msg === "string" ? msg : JSON.stringify(msg) });
  }
}).listen(PORT, () =>
  console.log(
    `pi-worker api on http://localhost:${PORT} — komodo ${process.env.KOMODO_URL || "http://localhost:9120"}, deployment "${DEPLOYMENT}", sessions prefix "${PREFIX}"`,
  ),
);
