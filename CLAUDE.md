# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

Reticula plans and designs electrical reticulation for South African residential areas, from tablet field inspection to a submission-ready document set. `docs/PLAN.md` is the build plan and checklist; decisions are in `docs/adr/`.

## Commands

Prerequisites: .NET 10 SDK, Node 24, uv, Docker.

```
docker compose -f infra/docker-compose.yml up db storage   # Postgres+PostGIS, MinIO
uv --directory src/calc run reticula-calc                  # calc on :8001 (/docs for OpenAPI)
dotnet run --project src/api/Reticula.Api                  # api on :5000
npm --prefix src/web start                                 # web on :4200, proxies /api and /hubs to :5000
```

Dev sign-in: `engineer@reticula.local` / `change-me-dev-only` (created on first start when the user table is empty).

Tests and lint (CI runs exactly these; .NET builds with `-warnaserror`):

```
cd src/calc && uv run ruff check src tests && uv run pytest -q
uv --directory src/calc run pytest tests/test_design.py -k bit_for_bit     # single test
cd src/api && dotnet build -warnaserror && dotnet test
dotnet test src/api --filter "FullyQualifiedName~ReviewTests"             # single class
npm --prefix src/web test
cd src/web && npx ng test --include 'src/app/features/field/**/*.spec.ts' # subset
```

API tests need a running Postgres+PostGIS; each test class creates and drops its own database. Override the connection with `RETICULA_TEST_DB` (default `Host=localhost;Username=reticula;Password=reticula`). The role needs CREATEDB and permission to create the postgis extension.

EF migrations (CI fails if the model changed without one):

```
cd src/api && dotnet tool restore
dotnet ef migrations add <Name> -p Reticula.Infrastructure -s Reticula.Api -o Data/Migrations
```

## Architecture

```
Angular 22 PWA ──> .NET 10 API ──HTTP──> Python calc service (FastAPI)
 IndexedDB outbox   EF Core/PostGIS       pandapower, networkx, shapely, ezdxf, reportlab
 MapLibre+PMTiles   Hangfire jobs, SignalR
```

**The calculation boundary is the core rule (ADR 0001).** No engineering number is computed in .NET, Angular or the LLM assistant. They store, orchestrate and display. Every calculation lives in `src/calc/src/reticula_calc`. The API calls it via `Reticula.Infrastructure/Calc/CalcClient.cs` (contracts in the same folder) and stores results; it never recomputes them. Documents render from stored results.

**Rules files.** Every calc names a rules file as `authority/version` (`rules/<authority>/<semver>.yaml`, validated against `rules/schema.json`). The calc service hashes it and stamps the hash on every result and document. No rule value is hard-coded in calc code; every number comes from the rules file with a `clause` reference. A municipal file can `base:` another and override keys. To change a rule value, add a new version file rather than editing a released one. Placeholder values exist; sign-off is impossible while any are used.

**Traceability.** Calculated values are `Traced` records (`reticula_calc/trace.py`): value, unit, formula id and text, clause, rules hash, named inputs. Build them with `traced(...)`.

**Calc service layout.** `app.py` is a thin FastAPI surface (validate, load rules, call). `calcs/` (ADMD, voltage drop), `lv/` (network model, loads, placement, analysis, conductor library), `mv/`, `design/` (full design run, sizing, overhead/underground checks, optimisation), `documents/` (report, BOQ, schedule, DXF drawing, GIS, pack), `geo/` (imports, OSM, CRS, building-type prediction), `maps/` (PMTiles extract), `cost.py` (rates from `rates/`).

**Hand-worked validation cases.** `test-cases/<topic>/case-N/` holds `inputs.json`, `expected.json`, `source.md`; `tests/test_cases.py` runs them all with tolerances. Adding a topic means adding a runner function there. When a rules value changes, rework the affected cases against the new rules version.

**API projects** (`src/api`): `Reticula.Domain` (entities), `Reticula.Infrastructure` (EF Core `Data/`, calc client, job handlers, services), `Reticula.Api` (minimal-API endpoint groups per feature, wired in `Program.cs`). New features follow the same pattern: `Map<Feature>Endpoints()` in the Api project, service in Infrastructure registered in `Program.cs`.

**Background jobs (ADR 0003).** Hangfire on Postgres (`hangfire` schema), but the app reads only its own `job_runs` table. Handlers implement `IJobHandler` (`Infrastructure/Jobs/Abstractions.cs`), are registered with `AddJobHandler<T>()`, and report via `IJobProgress`. Progress is pushed over SignalR at `/hubs/jobs` (`Watch(jobId)` → `jobUpdate`). Jobs never auto-retry. Design runs, placement, documents and map packs are jobs. All API tests share one host in a single xUnit collection because of Hangfire's static state; tests use `FakeCalc`, `JobGate` and `ScriptedModel` from `ReticulaApiFactory`.

**Revisions and audit (ADR 0013).** A revision is an immutable snapshot of a design run, with rules, inputs and result hashes. Reproducing it re-sends the stored request and must produce the same result hash, so calc output must be deterministic. An EF `SaveChangesInterceptor` writes `AuditEntry` rows for domain entity changes. `ExecuteUpdate`/`ExecuteDelete` bypass it, so use them only on derived/bulk records (networks, stands, roads, contours, network assets, map packs), never on audited entities.

**Offline field sync (ADR 0004).** `src/web/src/app/features/field/sync/`. Offline-first with a single code path: every change goes to a per-user IndexedDB outbox (`reticula-field-<user id>`) and is overlaid on the last server snapshot. Changes per item are sent in order with the version returned for the previous change. A 409 is held for the user to choose Keep mine or Keep theirs; nothing is overwritten automatically. Every request must be idempotent (device ids / `opId`). Offline kVA is not computed on the device; it shows as pending until the server returns it. Photos are queued as `ArrayBuffer`, not `Blob`.

**Design assistant (ADR 0014).** Claude tool-use through the API, calling the same endpoints as the UI. It is behind a feature flag (`Assistant:Enabled`), and the full suite must pass with it off.

**Design runs (ADR 0011).** Job `design.run` gathers every project input (`DesignInputsBuilder`), sends it to `/calc/design/run` or `/calc/design/optimise`, and stores the exact request JSON and the calc's result JSON as returned (snake_case); the API reads facts off it (`DesignRuns.Design`) and never recomputes. Modes: run, optimise, adopted (an option copied out, no calc call), reproduce. Hashes are canonical JSON (`CanonicalJson`); the inputs hash excludes run options, and per-part hashes give the stale reason. Each rules placeholder a run used becomes a project assumption `rules_placeholder:<sha16>`. The optimiser ranks options by `run.unsound` (unsupplied loads or transformers, a stopped bulk study) before failed checks, because a part nothing feeds fails no check.

**Pre-design placement (ADR 0010).** `lv/placement.py` proposes transformer sites, LV routes and an MV route; `PlacementJob` stores them as candidates with `Source = proposed`, while tablet-marked ones are `field`. Filter on source when a step must see only one.

**Documents (ADR 0012).** Job `documents.generate` renders the nine kinds in the calc service (`documents/`), stores them stamped, supersedes earlier unlocked sets and locks a signed revision's set. Sign-off blockers live in `ReviewService.SignOffBlockersAsync`.

**Auth (ADR 0002).** Identity bearer tokens, no cookies. Roles `engineer` (creates projects, signs off, manages users on the Users page) and `inspector`. No self-registration; the first start bootstraps the engineer from `Bootstrap:*`.

**API conventions.** Look projects up only through `db.Projects.ActiveAsync` / `AnyActiveAsync` (archived projects are invisible) and read the caller with `user.UserId()`. Errors are Problem Details; a calc outage surfaces via `CalcUnavailableExceptionHandler`. API tests share `TestFixtures` (square polygon, `factory.NewProjectAsync`, `WaitFinishedAsync`).

**Web layout.** Standalone lazy-loaded components under `src/web/src/app/features/<area>`, cross-cutting services in `core/`. `shared/symbols.ts` holds the Eskom drawing-practice symbol set (SVG art, colours, dash patterns, MapLibre icon registration) that maps, tool buttons and legends all draw from, so add a new asset kind there rather than picking a colour in a map. Prettier config is in `src/web/.prettierrc`.

## Release gate

`.github/workflows/release.yml` runs on `v*` tags: full CI, plus named gates (all test cases, document stamps, bit-for-bit design reproduction, API suite with assistant disabled, revision reproduction, field offline-sync specs).

## Conventions

- Work lands on `main` via PR from a `phase-N-*` or `claude/*` branch. Commit subjects name the plan item or area ("Phase 2.4: …", "Conductor library: …").
- Ruff line length is 120, Python 3.12. Pydantic models define every calc request/response; keep `Reticula.Infrastructure/Calc/*Contracts.cs` in step.
- Rules files `0.1.0` are starter placeholders; the current Eskom file is the highest version under `rules/eskom/`. Fields marked `PLACEHOLDER` make results "Not fit to submit".
- `docs/standards-index.md` is the clause index: every rules value and check points to an entry that names the standard, edition and whether the engineer has verified it. Keep that status honest. `docs/field.md`, `docs/imports.md`, `docs/load-data.md`, `docs/design.md` and `docs/operations.md` describe field work, imports, load data, design to sign-off and ops.

Local notes:
- A Homebrew Postgres without the `reticula` role can run the API tests with `RETICULA_TEST_DB="Host=localhost;Username=<your user>"` if that role is superuser or has CREATEDB and postgis is installed.
- macOS: if `uv run` in `src/calc` cannot import `reticula_calc`, the `.venv` picked up the hidden flag and Python skips its `.pth` file. Run `chflags -R nohidden .venv`, or use `PYTHONPATH=src uv run --no-sync ...`.
