#!/usr/bin/env bash
# Dump the Reticula database to a timestamped custom-format file, verify it, and prune old dumps.
# Env: PGHOST PGPORT PGUSER PGPASSWORD PGDATABASE (standard libpq), BACKUP_DIR, RETENTION_DAYS (default 30).
set -euo pipefail

: "${PGDATABASE:?PGDATABASE is required}"
BACKUP_DIR="${BACKUP_DIR:-/backups}"
RETENTION_DAYS="${RETENTION_DAYS:-30}"
mkdir -p "$BACKUP_DIR/db"

stamp="$(date -u +%Y%m%dT%H%M%SZ)"
target="$BACKUP_DIR/db/${PGDATABASE}-${stamp}.dump"
tmp="$target.partial"

echo "backup: dumping $PGDATABASE to $target"
pg_dump --format=custom --compress=6 --no-owner --file="$tmp"
# A dump that pg_restore cannot list is not a backup.
pg_restore --list "$tmp" > /dev/null
mv "$tmp" "$target"
sha256sum "$target" | awk '{print $1}' > "$target.sha256"

echo "backup: pruning dumps older than $RETENTION_DAYS days"
find "$BACKUP_DIR/db" -name "${PGDATABASE}-*.dump*" -type f -mtime +"$RETENTION_DAYS" -print -delete

echo "backup: done ($(du -h "$target" | cut -f1))"
