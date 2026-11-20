#!/bin/sh
# Self-check: config snapshots are pruned to the last 24 (mirrors the dump retention).
# Run: sh src/backup/config-snapshot-retention-test.sh   (offline — clones a local git repo)
set -eu

HERE="$(cd "$(dirname "$0")" && pwd)"
TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT
BACKUPS_DIR="$TMP/backups"
mkdir -p "$BACKUPS_DIR"

# seed 25 stale snapshots, all older than anything the run creates
i=1
while [ "$i" -le 25 ]; do
  : > "$BACKUPS_DIR/config-2000-01-01T000000Z-seed$i.tar.gz"
  i=$(( i + 1 ))
done
for old in "$BACKUPS_DIR"/config-*.tar.gz; do touch -t 200001010000 "$old"; done

# local source repo to clone from
git init -q "$TMP/repo"
git -C "$TMP/repo" -c user.email=t@test -c user.name=test commit -q --allow-empty -m seed

CONFIG_SNAPSHOT_RUN_ONCE=1 CONFIG_SNAPSHOT_INTERVAL_MINUTES=1440 \
  CONFIG_SNAPSHOT_REPO_URL="file://$TMP/repo" BACKUPS_DIR="$BACKUPS_DIR" \
  sh "$HERE/config-snapshot-loop.sh" >/dev/null

left=$(ls -1 "$BACKUPS_DIR"/config-*.tar.gz 2>/dev/null | wc -l | tr -d ' ')
if [ "$left" -le 24 ]; then
  echo "RETENTION-OK (left=$left)"
else
  echo "RETENTION-FAIL (left=$left, expected <= 24)"
  exit 1
fi
