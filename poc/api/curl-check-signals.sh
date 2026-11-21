#!/bin/sh
# Manual check for the session signals: /status, /wait, /notify (webhook). Start first, in
# two other shells:
#   1) cd api && npm start                   # the pi-worker api (komodo up, pi-worker:latest built)
#   2) cd api && node webhook-listener.mjs   # console webhook receiver on :3900
# Then run:  bash curl-check-signals.sh     # creates one session, prompts, waits, deletes it
# Watch shell 2 while the run works: it should print a {"event":"settled"} line (and
# {"event":"blocked"} if pi raises a dialog needing a human).
set -e
BASE=${BASE:-localhost:3000}
HOOK=${HOOK:-http://localhost:3900/hook}

ID=$(curl -s -XPOST "$BASE/sessions" -d '{"name":"signals-check"}' | node -e "let s='';process.stdin.on('data',d=>s+=d).on('end',()=>console.log(JSON.parse(s).id))")
echo "session: $ID"

echo "== register webhook =="
curl -s -XPOST "$BASE/sessions/$ID/notify" -d "{\"url\":\"$HOOK\"}"; echo

echo "== status right after create =="
curl -s "$BASE/sessions/$ID/status"; echo

echo "== prompt =="
P=$(curl -s -XPOST "$BASE/sessions/$ID/rpc" -d '{"type":"prompt","message":"Create a simple todo app (vanilla HTML/CSS/JS): index.html, styles.css, app.js with localStorage persistence."}')
echo "$P"
case "$P" in *'"success":false'*) echo "prompt failed — aborting"; exit 1;; esac

echo "== wait loop (re-poll /wait until settled/blocked/disconnected) =="
while :; do
  R=$(curl -s "$BASE/sessions/$ID/wait?timeoutMs=55000")
  echo "$R"
  case "$R" in
    *'"timeout"') ;;
    *'"blocked"'*) echo "blocker parked: curl -s $BASE/sessions/$ID/ui  → answer: curl -s -XPOST $BASE/sessions/$ID/ui/<requestId> -d '{\"value\":\"Allow\"}'"; break ;;
    *) break ;;
  esac
done

echo "== final status (expect phase idle, hasRun true, lastSettled set) =="
curl -s "$BASE/sessions/$ID/status"; echo

echo "== cleanup =="
curl -s -XDELETE "$BASE/sessions/$ID"; echo
