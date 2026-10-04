#!/usr/bin/env sh
# Mirror the object store (photos, documents) to the backup volume. Runs in the minio/mc image.
# Env: S3_ENDPOINT S3_ACCESS_KEY S3_SECRET_KEY S3_BUCKET BACKUP_DIR.
set -eu
: "${S3_BUCKET:?S3_BUCKET is required}"
BACKUP_DIR="${BACKUP_DIR:-/backups}"
mc alias set reticula "$S3_ENDPOINT" "$S3_ACCESS_KEY" "$S3_SECRET_KEY" > /dev/null
mkdir -p "$BACKUP_DIR/files/$S3_BUCKET"
# No --remove: files deleted from the store stay in the backup.
mc mirror --overwrite --preserve "reticula/$S3_BUCKET" "$BACKUP_DIR/files/$S3_BUCKET"
echo "mirror: done"
