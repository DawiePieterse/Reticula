# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

Reticula plans and designs electrical reticulation for South African residential areas. It covers the whole path from a tablet field inspection to a submission-ready document set. `docs/PLAN.md` is the build plan and checklist: each step is marked `[ ]`, `[~]` or `[x]`. Decisions are recorded in `docs/adr/` as ADRs, one per plan step.

## Commands

Prerequisites: .NET 10 SDK, Node 24 (the Angular CLI refuses Node 22.22.0), uv, and Postgres with PostGIS.

```
# Calc service (Python 3.12, FastAPI) — src/calc
uv run pytest -q                                   # all tests, including every case under /test-cases
uv run pytest -q tests/test_lv_overhead.py -k junction
uv run ruff check src tests                        # CI lints with this; ruff format is not enforced
uv run uvicorn reticula_calc.app:app --port 8001   # or: uv run reticula-calc

# API (.NET 10) — src/api
dotnet build -warnaserror                          # CI builds with warnings as errors
dotnet test                                        # needs Postgres+PostGIS; RETICULA_TEST_DB overrides the
                                                   # default "Host=localhost;Username=reticula;Password=reticula"
dotnet test --filter "FullyQualifiedName~LvNetworkTests"
dotnet ef migrations add <Name> -p Reticula.Infrastructure -s Reticula.Api -o Data/Migrations
dotnet ef migrations has-pending-model-changes -p Reticula.Infrastructure -s Reticula.Api   # CI fails if true
dotnet run --project Reticula.Api --urls http://localhost:5000

# Web (Angular 22, zoneless, signals, Vitest) — src/web
npx ng test --watch=false
npx ng test --watch=false --include src/app/features/projects/lv-overhead.spec.ts
npx ng build
npm start                                          # :4200, proxies /api to the API
```

Support services: `docker compose -f infra/docker-compose.yml up db storage` starts Postgres/PostGIS and MinIO. The dev sign-in is `engineer@reticula.local` / `change-me-dev-only`, bootstrapped when the user table is empty.

CI (`.github/workflows/ci.yml`) runs four jobs: calc (ruff + pytest), api (build -warnaserror, pending-migration check, tests), web (build + test), and a backup/restore script test.

## Architecture

```
Angular PWA ──/api──> .NET API (EF Core, Npgsql, PostGIS) ──HTTP──> Python calc service
```

**The calculation boundary is the core rule (ADR 0001).** Every engineering number is computed in the Python calc service (`src/calc`):
- .NET and Angular only store, orchestrate and display.
- The API calls the calc service, stores what it returns (often as jsonb), and never recomputes.
- Don't add engineering arithmetic to C# or TypeScript. Presentation logic such as colour bands from returned values is fine.

**Rules files** (`rules/<authority>/<version>.yaml`) hold every rule value. Calculation code hard-codes none of them.
- A file may name `base: authority/version`. Its keys are deep-merged onto the base: dicts merge, but lists replace whole lists.
  - So to extend a list such as `conductors`, copy it in full, or add a dict keyed by code (as `lv_overhead.conductors` does).
- Files are validated against `rules/schema.json` and hashed.
  - Any new section or key must be added to the schema.
  - Never edit a rules version already used by a design; add a new version file instead.
- A project names its rules as `authority/version`. A rules file missing a section a step needs makes the calc service return 422. The API catches that as `CalcRejectedException` and records a `*_skipped` issue, so older rules files still build.

**Traceability.** Key results are `Traced` records (`src/calc/src/reticula_calc/trace.py`). Each carries the value, unit, formula id, formula text, clause, rules hash, and named inputs with sources. Placeholder inputs must say so in their source, and results built on them carry a `placeholders` list and a "not fit to submit" issue.

**Standards index.** Every rules value from a standard carries `clause` and `index`. The `index` is an id in `docs/standards-index.md`, and `tests/test_standards_index.py` fails if an index id is missing from the doc.

**Eskom documents.** Eskom projects follow Eskom standards over NRS/SANS. The Eskom PDFs are controlled disclosures: never commit them. Transcribe only the values the calculations need, with clause references.

### LV design pipeline (Phase 2)

`POST /api/projects/{id}/lv-network` (`LvNetworkService.BuildAsync`) runs these calc steps in order. Each step lives in `src/calc/src/reticula_calc/lv/`.

1. `network.py`: joins hand-drawn LV routes, sources and poles into a radial node/branch model, then numbers the feeders.
2. `loads.py`: connects buildings through pole service boxes and phases them.
3. `analysis.py`: Herman-Beta per-phase voltage drop, thermal loading and fault level.
4. `overhead.py`: spans, sag and tension, clearance, pole loads and stays.

The API stores the result in one `LvNetwork` row:
- nodes, branches and loads as PostGIS rows
- the other results as jsonb columns
- a stale reason computed against later edits.

The web shows it in `features/projects/lv-network-panel.ts`, with map layers built by `lvLayers()` in `lv-network.api.ts`.

### Adding a calc step end to end

The usual pattern, used by each of plans 2.2 to 2.5:
1. **Rules:** a new rules version and its schema.
2. **Calc:** a calc module with pydantic request and response models, and an endpoint in `app.py`.
3. **API contracts:** records in `Reticula.Infrastructure/Calc/*Contracts.cs`, a method on `ICalcClient`/`CalcClient`, and a `FakeCalc` default in `Reticula.Api.Tests/ReticulaApiFactory.cs`.
4. **API storage and endpoint:** a domain column, `ReticulaDbContext` mapping, a migration, and the DTO in the endpoint.
5. **Web:** types in the feature's `*.api.ts`, a component, and specs. Shared fixtures go in `*.testing.ts`; importing a `.spec.ts` file reruns its tests.
6. **Docs:** an ADR, the `docs/PLAN.md` entry, and the standards index.

## Validation cases

`test-cases/<topic>/case-N/` holds `inputs.json`, `expected.json` and `source.md`, which records who worked it and from what source. `src/calc/tests/test_cases.py` runs each topic in its `RUNNERS` map. Calc tests compare against values worked out independently: by hand, or by a separate method such as `numpy.roots` for the sag-tension cubic. They never compare the code against itself.

## Docs to read before related work

- `docs/field.md`: field inspection, offline use, loads.
- `docs/imports.md`: layout imports.
- `docs/load-data.md`: ADMD and Herman-Beta data, and the ReticMaster comparisons.
- `docs/operations.md`: logging, backups.
- `docs/requirements.md`: the source specification.
