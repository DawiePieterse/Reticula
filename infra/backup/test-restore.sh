#!/usr/bin/env bash
# Proves backup.sh and restore.sh round-trip: seeds data (including PostGIS geometry and jsonb),
# backs it up, restores into a fresh database and compares.
# Env: PGHOST PGPORT PGUSER PGPASSWORD. The role needs CREATEDB.
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
src="reticula_backup_src_$$"
dst="reticula_backup_dst_$$"
dir="$(mktemp -d)"
cleanup() { dropdb --if-exists "$src" >/dev/null 2>&1 || true; dropdb --if-exists "$dst" >/dev/null 2>&1 || true; rm -rf "$dir"; }
trap cleanup EXIT

createdb "$src"
psql -q -v ON_ERROR_STOP=1 -d "$src" <<'SQL'
CREATE EXTENSION postgis;
CREATE TABLE projects (id uuid PRIMARY KEY, name text NOT NULL, area geometry(Polygon,4326) NOT NULL, meta jsonb);
INSERT INTO projects VALUES
  ('00000000-0000-0000-0000-000000000001', 'Ext 19', ST_GeomFromText('POLYGON((28.1 -25.52,28.11 -25.52,28.11 -25.51,28.1 -25.52))',4326), '{"rules":"eskom/0.1.0"}'),
  ('00000000-0000-0000-0000-000000000002', 'Ext 20', ST_GeomFromText('POLYGON((28.2 -25.52,28.21 -25.52,28.21 -25.51,28.2 -25.52))',4326), null);
SQL

PGDATABASE="$src" BACKUP_DIR="$dir" "$here/backup.sh"
dump="$(ls "$dir"/db/*.dump)"

"$here/restore.sh" "$dump" "$dst"
"$here/restore.sh" "$dump" "$dst" 2>/dev/null && { echo "FAIL: restore over an existing database was allowed" >&2; exit 1; }

q="SELECT string_agg(id || '|' || name || '|' || ST_AsText(area) || '|' || coalesce(meta::text,''), ';' ORDER BY id) FROM projects"
a="$(psql -tAc "$q" -d "$src")"
b="$(psql -tAc "$q" -d "$dst")"
if [[ "$a" != "$b" || -z "$a" ]]; then
  echo "FAIL: restored data differs" >&2
  echo "source:   $a" >&2
  echo "restored: $b" >&2
  exit 1
fi
echo "PASS: backup restored identically ($(psql -tAc 'SELECT count(*) FROM projects' -d "$dst") rows, PostGIS and jsonb intact)"
