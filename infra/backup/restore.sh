#!/usr/bin/env bash
# Restore a dump made by backup.sh into a NEW database. Never restores over an existing one.
# Usage: restore.sh <dump-file> <target-database>
# Env: PGHOST PGPORT PGUSER PGPASSWORD (standard libpq).
set -euo pipefail

dump="${1:?usage: restore.sh <dump-file> <target-database>}"
target="${2:?usage: restore.sh <dump-file> <target-database>}"

if [[ -f "$dump.sha256" ]]; then
  [[ "$(sha256sum "$dump" | awk '{print $1}')" == "$(cat "$dump.sha256")" ]] || { echo "restore: checksum mismatch for $dump" >&2; exit 1; }
fi

if psql -d postgres -tAc "SELECT 1 FROM pg_database WHERE datname = '$target'" | grep -q 1; then
  echo "restore: database $target already exists; choose a new name" >&2
  exit 1
fi

echo "restore: creating $target"
createdb "$target"
echo "restore: restoring $dump"
pg_restore --no-owner --exit-on-error --dbname="$target" "$dump"
echo "restore: done. Point ConnectionStrings__Default at $target after checking it."
