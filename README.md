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

## Sign in

There is no self-registration. On first start with an empty user table the API creates the engineer account from `Bootstrap:EngineerEmail` and `Bootstrap:EngineerPassword`. Development settings use `engineer@reticula.local` with the password `change-me-dev-only`. The engineer then adds inspectors under `/api/users`.

Passwords need at least 12 characters. Access tokens last 1 hour and refresh tokens 14 days, so a tablet stays signed in through offline field work.

## Test

```
uv --directory src/calc run pytest
dotnet test src/api            # needs Postgres+PostGIS; set RETICULA_TEST_DB to override the default local connection
npm --prefix src/web test
```

API tests create and drop a throwaway database per test class. The connecting role needs CREATEDB and permission to create the postgis extension.

## Migrations

```
cd src/api
dotnet tool restore
dotnet ef migrations add <Name> -p Reticula.Infrastructure -s Reticula.Api -o Data/Migrations
```

CI fails if the model has changes without a migration.

## Operations

Logging, tracing, error reporting, backups and restore are described in `docs/operations.md`.

## Rules

A design run always names a rules file as `authority/version`. The calc service validates it against `rules/schema.json`, hashes it, and stamps the hash on every traced value and document. No rule value is hard-coded in calculation code.
