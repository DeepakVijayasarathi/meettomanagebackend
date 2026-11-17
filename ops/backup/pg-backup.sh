#!/usr/bin/env bash
# Nightly PostgreSQL backup for Meet to Manage (single server).
#
# Dumps every database in the Postgres container separately — the master registry and each academy's
# own database (mt_<id>) — so one academy can be restored without touching the others. Each dump is
# checked with pg_restore --list before it counts, old backups are pruned, and a "last-success" file is
# written for monitoring. See ops/backup/README.md for installing the cron job and restoring.
#
# Settings (environment variables, all optional):
#   PG_CONTAINER  Docker container running Postgres         (default: postgres)
#   PG_USER       Superuser inside the container             (default: postgres)
#   BACKUP_DIR    Where backups are written on this server   (default: /var/backups/meettomanage)
#   KEEP_DAYS     Days of nightly backups to keep            (default: 14)
set -euo pipefail

PG_CONTAINER="${PG_CONTAINER:-postgres}"
PG_USER="${PG_USER:-postgres}"
BACKUP_DIR="${BACKUP_DIR:-/var/backups/meettomanage}"
KEEP_DAYS="${KEEP_DAYS:-14}"

log() { echo "$(date -u +%Y-%m-%dT%H:%M:%SZ) $*"; }
fail() { log "ERROR: $*" >&2; exit 1; }

mkdir -p "$BACKUP_DIR"
chmod 700 "$BACKUP_DIR"

# One run at a time: a slow night must not overlap the next one.
exec 9>"$BACKUP_DIR/.lock"
flock -n 9 || fail "another backup is still running"

docker inspect -f '{{.State.Running}}' "$PG_CONTAINER" 2>/dev/null | grep -q true \
  || fail "container '$PG_CONTAINER' is not running"

psql_in() { docker exec "$PG_CONTAINER" psql -U "$PG_USER" -d postgres -AtX -v ON_ERROR_STOP=1 -c "$1"; }

stamp="$(date -u +%Y-%m-%d_%H%M)"
target="$BACKUP_DIR/$stamp"
partial="$target.partial"
rm -rf "$partial"
mkdir -p "$partial"

# Roles and their settings, needed before restoring onto a fresh server.
docker exec "$PG_CONTAINER" pg_dumpall -U "$PG_USER" --globals-only > "$partial/globals.sql" \
  || fail "could not dump roles"

mapfile -t databases < <(psql_in "SELECT datname FROM pg_database WHERE NOT datistemplate AND datname <> 'postgres' ORDER BY datname")
[ "${#databases[@]}" -gt 0 ] || fail "no databases found"

failed=0
for db in "${databases[@]}"; do
  file="$partial/$db.dump"
  if docker exec "$PG_CONTAINER" pg_dump -U "$PG_USER" -Fc -d "$db" > "$file" \
     && docker exec -i "$PG_CONTAINER" pg_restore --list < "$file" > /dev/null; then
    log "ok   $db ($(du -h "$file" | cut -f1))"
  else
    log "FAIL $db" >&2
    failed=$((failed + 1))
  fi
done

[ "$failed" -eq 0 ] || fail "$failed of ${#databases[@]} database(s) failed; kept the partial copy at $partial"

mv "$partial" "$target"
chmod -R go-rwx "$target"
printf '%s %s databases\n' "$stamp" "${#databases[@]}" > "$BACKUP_DIR/last-success"

# Keep KEEP_DAYS days of complete backups; never prune when today's run is the only copy.
find "$BACKUP_DIR" -mindepth 1 -maxdepth 1 -type d -name '20*' ! -name '*.partial' -mtime +"$KEEP_DAYS" -print -exec rm -rf {} + \
  | sed 's/^/pruned /' || true
find "$BACKUP_DIR" -mindepth 1 -maxdepth 1 -type d -name '*.partial' -mtime +2 -exec rm -rf {} + || true

log "done: ${#databases[@]} databases in $target ($(du -sh "$target" | cut -f1))"
