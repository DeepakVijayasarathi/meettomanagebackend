# Database backups (single server)

`pg-backup.sh` dumps every database in the `postgres` container every night: the master registry
(`meettomanage_master`), the marketing workspace and each academy's own database (`mt_<id>`). Each
database is a separate file, so one academy can be restored without touching any other.

Backups are written to `/var/backups/meettomanage/<YYYY-MM-DD_HHMM>/`, kept for 14 days, and each dump
is checked with `pg_restore --list` before the run counts as successful.

> These backups live on the same server as the database. They protect against mistakes, bad
> migrations and corrupted data, **not** against losing the server or its disk. Copy them off the
> server (or enable your host's server snapshots) for that.

## Install (once, on the production server)

Run as a user that can use `docker` (root, or a member of the `docker` group):

```bash
sudo install -m 750 ops/backup/pg-backup.sh /usr/local/bin/meettomanage-pg-backup
sudo mkdir -p /var/backups/meettomanage && sudo chmod 700 /var/backups/meettomanage

# Try it once by hand; it prints one line per database and "done" at the end.
sudo /usr/local/bin/meettomanage-pg-backup

# Every night at 02:30 server time.
echo '30 2 * * * root /usr/local/bin/meettomanage-pg-backup >> /var/log/meettomanage-backup.log 2>&1' \
  | sudo tee /etc/cron.d/meettomanage-backup
```

Settings, if the defaults don't fit (set them in the cron line, e.g. `KEEP_DAYS=30 /usr/local/bin/...`):

| Variable | Default | Meaning |
|---|---|---|
| `PG_CONTAINER` | `postgres` | Container running Postgres |
| `PG_USER` | `postgres` | Superuser inside the container |
| `BACKUP_DIR` | `/var/backups/meettomanage` | Where backups go |
| `KEEP_DAYS` | `14` | Days of backups to keep |

## Check it's working

```bash
cat /var/backups/meettomanage/last-success   # date of the last complete backup
tail -n 30 /var/log/meettomanage-backup.log
```

A failed night exits non-zero, writes `ERROR:` to the log and leaves `last-success` unchanged, so an alert
on "last-success older than 26 hours" catches it.

## Restore

List what a backup contains:

```bash
ls -lh /var/backups/meettomanage/2026-11-18_0230/
```

Restore **one academy** (replaces that academy's data only — stop the API first or expect errors for
that academy while it runs):

```bash
B=/var/backups/meettomanage/2026-11-18_0230
DB=mt_0123abcd   # the academy's database name (Tenants.DatabaseName in the master database)
docker exec -i postgres pg_restore -U postgres --clean --if-exists --no-owner -d "$DB" < "$B/$DB.dump"
```

Restore **everything onto a new server** (empty Postgres container):

```bash
B=/var/backups/meettomanage/2026-11-18_0230
docker exec -i postgres psql -U postgres -d postgres < "$B/globals.sql"
for f in "$B"/*.dump; do
  db="$(basename "$f" .dump)"
  docker exec postgres createdb -U postgres "$db" 2>/dev/null || true
  docker exec -i postgres pg_restore -U postgres --no-owner -d "$db" < "$f"
done
```

Practise a restore into a scratch database now and then; a backup that has never been restored is a guess.
