#!/bin/sh
# Scheduled config-plane Git snapshots (decisions 52/76). Skips with a log line when
# CONFIG_SNAPSHOT_REPO_URL is unset; CONFIG_SNAPSHOT_RUN_ONCE=1 runs one attempt and exits.

set -eu

INTERVAL_MINUTES="${CONFIG_SNAPSHOT_INTERVAL_MINUTES:-1440}"
REPO_URL="${CONFIG_SNAPSHOT_REPO_URL:-}"
DIR_ROOT="${BACKUPS_DIR:-/backups}"

log() { printf '{"timestamp":"%s","level":"%s","service":"backups","message":"%s","correlationId":null,"repositoryId":null,"workflowId":null,"taskId":null,"runId":null,"eventId":null}\n' "$(date -u +%Y-%m-%dT%H:%M:%SZ)" "$1" "$2"; }

snap() {
  if [ -z "$REPO_URL" ]; then
    log info "config snapshot skipped: CONFIG_SNAPSHOT_REPO_URL unset"
    return 0
  fi
  WORK="$DIR_ROOT/.config-snapshot-work"
  mkdir -p "$DIR_ROOT"
  rm -rf "$WORK"
  if git clone --depth 1 --single-branch "$REPO_URL" "$WORK" >/dev/null 2>&1; then
    FILE="$DIR_ROOT/config-$(date -u +%Y%m%dT%H%M%SZ).tar.gz"
    tar -czf "$FILE" -C "$WORK" .
    rm -rf "$WORK"
    log info "config snapshot created: $(basename "$FILE")"
    # keep the last 24 snapshots (mirrors the dump retention)
    ls -1t "$DIR_ROOT"/config-*.tar.gz 2>/dev/null | tail -n +25 | while IFS= read -r old; do rm -f "$old"; done
  else
    rm -rf "$WORK"
    log warn "config snapshot failed: clone failed"
    return 1
  fi
}

if [ "${CONFIG_SNAPSHOT_RUN_ONCE:-0}" = "1" ]; then
  snap
  exit 0
fi

log info "config snapshot loop started: every ${INTERVAL_MINUTES}m"
while true; do
  snap || :
  sleep "$(( INTERVAL_MINUTES * 60 ))"
done
