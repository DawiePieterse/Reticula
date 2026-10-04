# Operations

## Logs

- **API:** outside Development, logs are JSON on stdout with UTC timestamps and scopes. Each request logs method, path, status and duration. Job log lines carry `JobId`, `JobKind` and `TraceId`.
- **Calc:** JSON on stdout. Each request logs method, path, status, `duration_ms` and `trace_id`. Set `RETICULA_LOG_LEVEL` to change the level.
- **Tracing:** the API forwards W3C `traceparent` to the calc service. Search both logs for one trace id to follow a request or a job end to end.

## Error reporting

- Server errors return problem details with a `traceId`. The web app shows it to the user as a reference.
- Uncaught browser errors are sent to `POST /api/system/client-errors`. They appear in the API log as warnings from the `ClientError` category. Each tab sends at most 20 distinct errors.

## Backups

The compose file runs two backup services that write to the `backups` volume:

| Service | What | When |
| --- | --- | --- |
| backup-db | `pg_dump` custom format, verified with `pg_restore --list`, SHA-256 alongside | daily, 30-day retention |
| backup-files | `mc mirror` of the object store bucket, never deleting | daily |

Keep the `backups` volume off the database host. A backup on the same disk is not a backup.

## Restore

Restore always goes into a new database, never over the live one.

```
PGHOST=... PGUSER=... PGPASSWORD=... infra/backup/restore.sh /backups/db/reticula-<stamp>.dump reticula_restored
```

Check the restored database, then point `ConnectionStrings__Default` at it and restart the API.

CI proves the round trip on every push. `infra/backup/test-restore.sh` seeds PostGIS and jsonb data, backs it up, restores it and compares.

## Offline map tiles

Offline base maps are built on the server from a raster XYZ tile source and downloaded to tablets as one PMTiles file per project. Configure section `Tiles`:

| Setting | Default | Meaning |
| --- | --- | --- |
| `Tiles:SourceUrl` | none | XYZ template, e.g. `https://tiles.example/{z}/{x}/{y}.png?key=…`. Without it, building is refused. |
| `Tiles:Attribution` | © OpenStreetMap contributors | Shown on the map. Set it to what the provider requires. |
| `Tiles:MinZoom` / `Tiles:MaxZoom` | 10 / 18 | Zoom range of a pack. |
| `Tiles:MaxTiles` | 30000 | Budget per pack. The deepest zoom is dropped until the area fits. |
| `Tiles:MarginKm` | 0.5 | Margin around the project area. |
| `Tiles:Concurrency` | 4 | Parallel tile downloads. |

The tile provider's terms must allow downloading tiles for offline use. The public OpenStreetMap tile servers do not allow bulk downloads, so use a self-hosted tile server or a provider with an offline licence. The source shown to users drops the query string, so API keys stay on the server. Packs are kept in the file store under `projects/<id>/tiles/` and are covered by the file-store backup.

## OpenStreetMap fetch

Fetching buildings and roads uses the Overpass API at `Overpass:Url` (default `https://overpass-api.de/api/interpreter`). The public instance is shared and rate-limited; for heavy use, point this at a self-hosted Overpass server. When the server cannot be reached or refuses (for example 429), the import screen shows the reason and an export file can be imported instead.

