#!/bin/sh
# Manual check that session pi agents can call MCP tools (pi/pi-agent-state/mcp.json + /mcp mount).
# Start first, in another shell:
#   cd api && npm start
# Then run:  bash curl-check-mcp.sh   # creates one session, prompts one echo call, deletes it
#   KEEP=1 bash curl-check-mcp.sh    # keep the session running to observe it by hand (delete after)
# PASS = events contain a mcp__mock__echo call whose result text equals the prompt's input text.
set -e
BASE=${BASE:-localhost:3000}
CHECK_TEXT=hello-from-mcp-check

ID=$(curl -s -XPOST "$BASE/sessions" -d '{"name":"mcp-check"}' | node -e "let s='';process.stdin.on('data',d=>s+=d).on('end',()=>console.log(JSON.parse(s).id))")
echo "session: $ID"

echo "== prompt (agent should call mcp__mock__echo) =="
P=$(curl -s -XPOST "$BASE/sessions/$ID/rpc" -d '{"type":"prompt","message":"Call the mcp__mock__echo tool exactly once with argument text \"'$CHECK_TEXT'\" and finish."}')
echo "$P"
case "$P" in *'"success":false'*) echo "FAIL: prompt rejected"; curl -s -XDELETE "$BASE/sessions/$ID" >/dev/null; exit 1;; esac

echo "== wait loop =="
while :; do
  R=$(curl -s "$BASE/sessions/$ID/wait?timeoutMs=55000")
  echo "$R"
  case "$R" in
    *'"timeout"') ;;
    *'"blocked"'*) echo "FAIL: unexpected human dialog (blocked)"; curl -s "$BASE/sessions/$ID/ui"; echo; break ;;
    *) break ;;
  esac
done

echo "== verify round-trip in events =="
curl -s "$BASE/sessions/$ID/events?type=tool_execution_start,tool_execution_end" | node -e '
let s = "";
process.stdin.on("data", (d) => (s += d)).on("end", () => {
  const CHECK = process.argv[1];
  const recs = JSON.parse(s).records;
  const start = recs.find((r) => r.record.type === "tool_execution_start" && r.record.toolName === "mcp__mock__echo");
  const end = recs.find((r) => r.record.type === "tool_execution_end" && r.record.toolName === "mcp__mock__echo");
  if (!start || !end) {
    console.log("FAIL: no mcp__mock__echo call in events — session started before the mcp.json/mcp mount change? create a NEW session");
    process.exit(1);
  }
  const arg = start.record.args?.text;
  const out = end.record.result?.content?.[0]?.text;
  if (arg !== CHECK || out !== arg) {
    console.log(`FAIL: arg=${JSON.stringify(arg)} out=${JSON.stringify(out)}`);
    process.exit(1);
  }
  console.log(`PASS: mcp__mock__echo("${arg}") → "${out}"`);
  process.exit(0);
})' "$CHECK_TEXT"

echo "== cleanup =="
if [ -n "${KEEP:-}" ]; then
  echo "KEEP=1 — session $ID left running. Observe it, then delete:"
  echo "  curl -s 'localhost:3000/sessions/$ID/events?type=tool_execution_start,tool_execution_end'"
  echo "  docker top pi-session-$ID | grep mock-server        # the spawned server child process"
  echo "  curl -s -XDELETE localhost:3000/sessions/$ID        # when done"
  exit 0
fi
curl -s -XDELETE "$BASE/sessions/$ID"; echo
