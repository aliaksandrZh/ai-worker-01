// Manual webhook receiver for testing POST /sessions/:id/notify — prints every hit.
import { createServer } from "node:http";

let n = 0;
createServer((req, res) => {
  let raw = "";
  req.on("data", (c) => (raw += c));
  req.on("end", () => {
    console.log(`#${++n} ${req.method} ${req.url}`, raw ? JSON.parse(raw) : "");
    res.writeHead(200).end();
  });
}).listen(process.env.PORT ?? 3900, () => console.log(`webhook listener on http://localhost:${process.env.PORT ?? 3900}`));
