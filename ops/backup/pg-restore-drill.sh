#!/usr/bin/env bash
# Restore drill: proves the latest nightly backup can actually be restored.
#
# Starts a throwaway Postgres container (never touches production), restores the master registry and a
# sample of academy databases from the newest complete backup, runs sanity queries, then removes the
# container. A backup nobody has restored is only a hope; run this weekly (see README.md).
#
# Settings (environment variables, all optional):
#   BACKUP_DIR     Where nightly backups live                    (default: /var/backups/meettomanage)
#   PG_IMAGE       Postgres image; match production's major      (default: postgres:17-alpine)
#   SAMPLE_TENANTS How many academy databases to restore         (default: 3; 0 = master only)
#   BACKUP_DATE    A specific backup folder name to test         (default: newest)
set -euo pipefail

BACKUP_DIR="${BACKUP_DIR:-/var/backups/meettomanage}"
PG_IMAGE="${PG_IMAGE:-postgres:17-alpine}"
SAMPLE_TENANTS="${SAMPLE_TENANTS:-3}"
NAME="mtm-restore-drill-$$"

log() { echo "$(date -u +%Y-%m-%dT%H:%M:%SZ) $*"; }
fail() { log "ERROR: $*" >&2; exit 1; }
cleanup() { docker rm -f "$NAME" >/dev/null 2>&1 || true; }
trap cleanup EXIT

if [ -n "${BACKUP_DATE:-}" ]; then
  src="$BACKUP_DIR/$BACKUP_DATE"
else
  src="$(find "$BACKUP_DIR" -mindepth 1 -maxdepth 1 -type d -name '20*' ! -name '*.partial' | sort | tail -n 1)"
fi
[ -n "$src" ] && [ -d "$src" ] || fail "no complete backup found in $BACKUP_DIR"
log "testing backup $src"

[ -f "$src/globals.sql" ] || fail "globals.sql missing from $src"
master="$src/meettomanage_master.dump"
[ -f "$master" ] || fail "meettomanage_master.dump missing from $src"

docker run -d --name "$NAME" -e POSTGRES_PASSWORD=drill "$PG_IMAGE" >/dev/null
for _ in $(seq 1 30); do
  docker exec "$NAME" pg_isready -U postgres >/dev/null 2>&1 && break
  sleep 1
done
docker exec "$NAME" pg_isready -U postgres >/dev/null 2>&1 || fail "scratch Postgres did not start"

q() { docker exec "$NAME" psql -U postgres -d "$1" -AtX -v ON_ERROR_STOP=1 -c "$2"; }

# Roles first; "already exists" for postgres itself is expected.
docker exec -i "$NAME" psql -U postgres -d postgres -X < "$src/globals.sql" >/dev/null 2>&1 || true

restore_db() {
  local db="$1" file="$2"
  docker exec "$NAME" psql -U postgres -d postgres -AtX -c "CREATE DATABASE \"$db\"" >/dev/null
  # --no-owner: the drill server only has the roles from globals.sql, not necessarily every owner.
  docker exec -i "$NAME" pg_restore -U postgres -d "$db" --no-owner --exit-on-error < "$file" \
    || fail "restore of $db failed"
}

restore_db meettomanage_master "$master"
tenants="$(q meettomanage_master 'SELECT count(*) FROM tenants' 2>/dev/null || echo '?')"
[ "$tenants" != "?" ] || fail "restored master has no Tenants table"
log "ok   master restored ($tenants tenant(s) registered)"

checked=0
if [ "$SAMPLE_TENANTS" -gt 0 ]; then
  # Newest dumps first; they are the ones most likely to have just been migrated.
  while IFS= read -r file; do
    db="$(basename "$file" .dump)"
    restore_db "$db" "$file"
    users="$(q "$db" 'SELECT count(*) FROM users' 2>/dev/null || echo '?')"
    [ "$users" != "?" ] || fail "restored $db has no Users table"
    log "ok   $db restored ($users user(s))"
    checked=$((checked + 1))
  done < <(find "$src" -maxdepth 1 -name 'mt_*.dump' -printf '%T@ %p\n' | sort -rn | head -n "$SAMPLE_TENANTS" | cut -d' ' -f2-)
fi

mkdir -p "$BACKUP_DIR"
printf '%s %s master + %s tenant(s)\n' "$(date -u +%Y-%m-%dT%H:%MZ)" "$(basename "$src")" "$checked" > "$BACKUP_DIR/last-restore-drill"
log "done: backup $(basename "$src") restores cleanly"
