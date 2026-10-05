# Operations

## Logs

- **API:** outside Development, logs are JSON on stdout with UTC timestamps and scopes. Each request logs method, path, status and duration. Job log lines carry `JobId`, `JobKind` and `TraceId`.
- **Calc:** JSON on stdout. Each request logs method, path, status, `duration_ms` and `trace_id`. Set `RETICULA_LOG_LEVEL` to change the level.
- **Tracing:** the API forwards W3C `traceparent` to the calc service. Search both logs for one trace id to follow a request or a job end to end.

## Error reporting

- Server errors return problem details with a `traceId`. The web app shows it to the user as a reference.
- Uncaught browser errors are sent to `POST /api/system/client-errors`. They appear in the API log as warnings from the `ClientError` category. Each tab sends at most 20 distinct errors.

## Offline map source

Offline map packs (plan item 1.9) are cut by the calc service from one large PMTiles vector basemap, named by `RETICULA_MAP_SOURCE`. It is either an `https://` URL that answers range requests, or a file path inside the calc container. Without it, **Prepare offline map** fails with a message naming the setting.

- **Recommended:** keep a South Africa extract in your own storage. Take it from a [Protomaps build](https://maps.protomaps.com/builds/) with the `pmtiles` command-line tool, put it in `infra/maps/`, and set `RETICULA_MAP_SOURCE=/maps/south-africa.pmtiles`:

  ```
  pmtiles extract https://build.protomaps.com/<yyyymmdd>.pmtiles infra/maps/south-africa.pmtiles --bbox=16.3,-35.0,33.0,-22.1
  ```

  The file is large, so keep it out of the repository. Refresh it every few months to pick up new roads.
- **For a quick start:** point `RETICULA_MAP_SOURCE` at a dated Protomaps build URL. Daily builds are deleted after about a week, so this is only for trying the feature.
- Packs cover the project area plus 500 m, at zooms 0 to 15. A township of a few kilometres is a few MB. Areas wider than 0.25° are refused.
- Map data is © OpenStreetMap contributors (ODbL). The packs and the map show the attribution.

The pack files live in the file store next to photos, and are backed up with them. A rebuilt pack replaces the old file.

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
