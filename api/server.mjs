import { createServer } from "node:http";

// shortcut: komodo_client's auth module touches browser localStorage at import and we never store jwt tokens; revisit on a komodo_client node-only auth release.
globalThis.localStorage ??= { getItem: () => null, setItem: () => {} };
const { KomodoClient } = await import("komodo_client");

const PORT = process.env.PORT || 3000;
const DEPLOYMENT = process.env.PI_WORKER_DEPLOYMENT || "pi-worker";

const komodo = KomodoClient(process.env.KOMODO_URL || "http://localhost:9120", {
  type: "api-key",
  params: {
    key: process.env.KOMODO_API_KEY || "",
    secret: process.env.KOMODO_API_SECRET || "",
  },
});

const send = (res, code, body) =>
  res.writeHead(code, { "content-type": "application/json" }).end(JSON.stringify(body));

createServer(async (req, res) => {
  try {
    if (req.method === "GET" && req.url === "/status") {
      const r = await komodo.read("GetDeploymentContainer", { deployment: DEPLOYMENT });
      return send(res, 200, { deployment: DEPLOYMENT, state: r.state });
    }
    const action =
      req.method === "POST" && req.url === "/start" ? "StartDeployment"
      : req.method === "POST" && req.url === "/stop" ? "StopDeployment"
      : null;
    if (action) {
      const update = await komodo.execute(action, { deployment: DEPLOYMENT });
      return send(res, 200, { deployment: DEPLOYMENT, action, ok: update.success, operation: update.operation });
    }
    send(res, 404, { error: "routes: GET /status, POST /start, POST /stop" });
  } catch (err) {
    // komodo_client rejects with { status, result } — surface komodo's own error text when present
    const msg = err?.result?.error ?? err?.result?.result?.error ?? err?.message ?? err;
    send(res, 502, { error: typeof msg === "string" ? msg : JSON.stringify(msg) });
  }
}).listen(PORT, () =>
  console.log(`pi-worker api on http://localhost:${PORT} — komodo ${process.env.KOMODO_URL || "http://localhost:9120"}, deployment "${DEPLOYMENT}"`),
);
