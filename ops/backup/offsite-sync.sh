#!/usr/bin/env bash
# Copies the nightly backups off this server (a backup on the same disk does not survive losing the server).
#
# Uses rclone with any remote you configure (S3-compatible object storage in another region/provider is
# ideal: `rclone config`, then set RCLONE_REMOTE). Dumps are encrypted client-side when RCLONE_CRYPT_REMOTE
# is used as the target. Run after pg-backup.sh, e.g. from the same cron line.
#
#   RCLONE_REMOTE  Destination, e.g. offsite:meettomanage-backups   (required)
#   BACKUP_DIR     Local backup folder                              (default: /var/backups/meettomanage)
set -euo pipefail

BACKUP_DIR="${BACKUP_DIR:-/var/backups/meettomanage}"
: "${RCLONE_REMOTE:?set RCLONE_REMOTE, e.g. offsite:meettomanage-backups}"

command -v rclone >/dev/null || { echo "ERROR: rclone is not installed" >&2; exit 1; }
[ -f "$BACKUP_DIR/last-success" ] || { echo "ERROR: no successful local backup to copy" >&2; exit 1; }

# `copy`, not `sync`: a deleted or emptied local folder must never delete the off-site copies.
rclone copy "$BACKUP_DIR" "$RCLONE_REMOTE" --include '20*/**' --include 'last-success' --exclude '*.partial/**' --transfers 4
printf '%s\n' "$(date -u +%Y-%m-%dT%H:%MZ)" > "$BACKUP_DIR/last-offsite"
echo "$(date -u +%Y-%m-%dT%H:%M:%SZ) offsite copy complete -> $RCLONE_REMOTE"
