#!/usr/bin/env sh
# Container entrypoint: run the given backup script now and then every BACKUP_INTERVAL_SECONDS (default 1 day).
set -eu
interval="${BACKUP_INTERVAL_SECONDS:-86400}"
while true; do
  "$@" || echo "backup: run failed with exit code $?" >&2
  sleep "$interval"
done
