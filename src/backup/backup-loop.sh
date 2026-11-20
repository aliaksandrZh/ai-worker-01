#!/bin/sh
# Scheduled PostgreSQL dumps (decisions 52/76). One §14-shape JSON log line per action.
# BACKUP_RUN_ONCE=1 runs a single cycle and exits (testability without docker).

set -eu

INTERVAL_MINUTES="${BACKUP_INTERVAL_MINUTES:-60}"
DIR="${BACKUPS_DIR:-/backups}"
DB_HOST="${BACKUP_DB_HOST:-postgres}"
DB_USER="${POSTGRES_USER:-pi_agent}"
DB_NAME="${POSTGRES_DB:-pi_agent}"

# pg_dump needs the password; never printed.
PGPASSWORD="${POSTGRES_PASSWORD:?POSTGRES_PASSWORD is required}"
export PGPASSWORD

log() { printf '{"timestamp":"%s","level":"%s","service":"backups","message":"%s","correlationId":null,"repositoryId":null,"workflowId":null,"taskId":null,"runId":null,"eventId":null}\n' "$(date -u +%Y-%m-%dT%H:%M:%SZ)" "$1" "$2"; }

cycle() {
  FILE="$DIR/$(date -u +%Y%m%dT%H%M%SZ).sql.gz"
  if pg_dump -h "$DB_HOST" -U "$DB_USER" "$DB_NAME" 2>/dev/null | gzip > "$FILE.tmp"; then
    mv "$FILE.tmp" "$FILE"
    log info "backup created: $(basename "$FILE")"
    # keep the last 24 dumps
    ls -1t "$DIR"/*.sql.gz 2>/dev/null | tail -n +25 | while IFS= read -r old; do rm -f "$old"; done
  else
    rm -f "$FILE.tmp"
    log error "backup failed"
    return 1
  fi
}

mkdir -p "$DIR"

if [ "${BACKUP_RUN_ONCE:-0}" = "1" ]; then
  cycle
  exit 0
fi

log info "backup loop started: every ${INTERVAL_MINUTES}m, host ${DB_HOST}"
while true; do
  cycle || :
  sleep "$(( INTERVAL_MINUTES * 60 ))"
done
