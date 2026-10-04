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
