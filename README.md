# Reticula

Plans and designs electrical infrastructure for South African residential areas, from a tablet field inspection to a submission-ready document set. See `docs/PLAN.md` for the build plan and checklist.

## Layout

| Path | What |
| --- | --- |
| `src/web` | Angular 22 PWA (tablet field capture, desktop design review) |
| `src/api` | .NET 10 API: auth, projects, sync, revisions, documents, assistant gateway |
| `src/calc` | Python calc service: the only place engineering numbers are computed |
| `rules/` | Versioned authority rules files (YAML) and their schema |
| `rates/` | Material library and dated rate lists |
| `test-cases/` | Hand-worked validation cases run by the calc test suite |
| `infra/` | docker-compose for Postgres/PostGIS, MinIO, calc and api |
| `docs/` | Plan, ADRs, standards clause index |

## Run locally

Prerequisites: .NET 10 SDK, Node 24, uv, Docker.

```
docker compose -f infra/docker-compose.yml up db storage   # Postgres + MinIO
uv --directory src/calc run reticula-calc                  # calc on :8001
dotnet run --project src/api/Reticula.Api                  # api on :5000 (see launchSettings)
npm --prefix src/web start                                 # web on :4200, proxies /api
```

## Test

```
uv --directory src/calc run pytest
dotnet test src/api
npm --prefix src/web test
```

## Rules

A design run always names a rules file as `authority/version`. The calc service validates it against `rules/schema.json`, hashes it, and stamps the hash on every traced value and document. No rule value is hard-coded in calculation code.
