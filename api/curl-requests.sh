#!/bin/sh
# Manual test for the pi-worker api. Start the api first:
#   cd api && cp .env.example .env   # fill in key + secret (Create once in Komodo UI: Config -> API Keys)
#   npm install && npm start
# Then in another shell run: bash curl-requests.sh
# Or run the curls one by one. Container state may lag one poll cycle (default 15s).
set -e
BASE=${BASE:-localhost:3000}

# echo "== status (before) =="
# curl -s "$BASE/status"; echo

# echo "== POST /start =="
# curl -s -XPOST "$BASE/start"; echo

# echo "== status (after start, expect running) =="
# curl -s "$BASE/status"; echo

echo "== POST /stop =="
curl -s -XPOST "$BASE/stop"; echo

# echo "== status (after stop, expect exited) =="
# curl -s "$BASE/status"; echo
