# Reticula – Build Plan

Source: Electrical Infrastructure Planning and Design App – Requirements Specification (Oct 3, 2026).
Status legend: `[ ]` not started · `[~]` in progress · `[x]` done · `[!]` blocked.

---

## 1. Decisions and assumptions

A. **Stack.** Angular 22 PWA · .NET 10 Web API · PostgreSQL 17 + PostGIS · Python 3.12 calculation service (FastAPI). The spec says "a Python back end does all calculations"; this is honoured by putting every engineering calculation in the Python service. .NET owns auth, projects, sync, revisions, documents orchestration and the assistant gateway. If a single-language back end is preferred, the .NET layer collapses into the Python service; the phase plan does not change.
B. **Calculation boundary.** Rule: no engineering number is computed in .NET, Angular or the LLM. .NET and Angular only display, store and orchestrate. The Python service is the only place that reads the rules file for calculation.
C. **Rules file.** One versioned YAML per authority (`rules/<authority>/<version>.yaml`), schema-validated, hashed; the hash is stored on every design run and printed on every document.
D. **Offline scope.** Offline = field capture + income/ADMD tool only (spec open item 4 assumed "no" until decided). Cached rules tables shipped to the PWA for those two tools.
E. **First authority.** Eskom rules encoded first (open item 3, assumed). Municipal overrides added as a second rules file to prove the override mechanism.
F. **Drawings.** DXF via ezdxf. DWG only where an authority requires it (open item 2); if needed, convert via ODA File Converter in the documents job.
G. **Maps.** MapLibre GL + OSM vector tiles, PMTiles for offline packs per project area. Google satellite as optional raster layer (licence-gated, off by default).
H. **Single engineer.** One registered engineer role plus an inspector role (same person on tablet, or a delegate). Only the engineer role can sign off.
I. **Assistant.** Claude via tool-use against the same .NET/Python endpoints the UI calls. Feature-flagged; app must pass all tests with the flag off.

---

## 2. Architecture

```
Angular 22 PWA ──HTTPS──> .NET 10 API ──HTTP──> Python calc service (FastAPI)
   │ IndexedDB (offline)        │ EF Core / Npgsql        │ pandapower, networkx, shapely,
   │ MapLibre + PMTiles         │ PostGIS                 │ ezdxf, weasyprint, openpyxl,
   │ Service worker sync        │ Hangfire (jobs)         │ geopandas/pyshp, numpy
                                │ Claude tool gateway     │
                                └──> Object storage (photos, tiles, documents)
```

A. **Projects:** `Reticula.Api` (.NET), `Reticula.Domain`, `Reticula.Infrastructure`, `Reticula.Calc` (Python), `reticula-web` (Angular), `rules/`, `test-cases/` (hand-worked calcs).
B. **Jobs:** design runs, optimisation, document generation and sync reconciliation run as background jobs with progress events (SignalR).
C. **Traceability record:** every calculated value stored as `{value, unit, inputs[], formula_id, clause_ref, rules_hash}`. Documents render from these records, never recompute.
D. **Revisions:** a design revision is an immutable snapshot (inputs, rules hash, rate-library date, results, documents). Any input/rule/choice change marks dependent checks and documents stale and re-queues them.

---

## 3. Repository layout

```
/src/api/            .NET 10 solution
/src/calc/           Python calc service
/src/web/            Angular 22 PWA
/rules/              Authority rules files + JSON schema
/rates/              Material library + rate lists (CSV/JSON)
/test-cases/         Hand-worked validation cases (inputs + expected outputs)
/docs/               PLAN.md, ADRs, standards clause index
/infra/              docker-compose, migrations, CI
```

---

## 4. Core data model

A. **Project** – name, authority, rules version, rate date, area polygon, CRS (default EPSG:4326 storage, local UTM/LO for calc).
B. **Stand** – polygon, erf no., zoning, source (CAD/KML/OSM).
C. **Building** – footprint, predicted type + confidence + source, confirmed type, status (predicted / confirmed / not present / new).
D. **LoadPoint** – building ref, phase, ADMD kVA, special load kVA, income band, inspection record ref, override flags.
E. **InspectionRecord** – GPS, time, photos, notes, inspector, device id, sync state.
F. **CandidateSite / CandidateRoute** – type (transformer, mini-sub, MV route, pole, LV route), geometry, notes.
G. **NetworkElement** – node/branch model: source, transformer, bus, segment (OH/UG/mixed), conductor, pole, stay, cable; each with calc results and traceability refs.
H. **Assumption** – text, source, status (open/confirmed), linked entity.
I. **DesignRun / DesignOption** – objective (capex / lifetime / spare), parameters, results, cost, rules hash.
J. **Revision** – snapshot, sign-off record (engineer, time, registration no.), document set.
K. **Document** – type, format, file ref, rules hash, rate date, design date.
L. **RateItem / Assembly** – code, description, unit, rate, rate date, source, user override.
M. **Contours / ExistingNetwork / AuthorityData** – imported layers with provenance.

---

## 5. Phases

Each phase ends usable on its own. Every phase includes: unit tests, hand-worked validation cases where calcs exist, UI smoke tests, and a short ADR for any decision.

### Phase 0 – Foundation (prerequisite)

Deliverables: running skeleton, auth, project CRUD, map, CI.

- [x] 0.1 Repo scaffold: .NET solution, Angular workspace, Python service, docker-compose (Postgres+PostGIS, MinIO).
- [x] 0.2 CI: build, test, lint for all three; migration check; Python calc test gate.
- [x] 0.3 Auth: single-tenant login, roles `engineer` and `inspector`. (ADR 0002)
- [x] 0.4 Project entity + area polygon drawing on MapLibre.
- [x] 0.5 PWA shell: service worker, install prompt, offline route guard.
- [x] 0.6 Rules-file JSON schema, loader, hashing, version endpoint.
- [x] 0.7 Traceability record type and a sample calc end-to-end (LV voltage drop) proving the pipeline. (DB storage of traced results lands with the first design run in Phase 2)
- [x] 0.8 Background job framework with progress events. (ADR 0003)
- [x] 0.9 Logging, error reporting, backup job for Postgres and object store. (docs/operations.md)

### Phase 1 – Field capture

Deliverables: predicted building types, tablet inspection, income/ADMD tool, load schedule; all offline-capable.

- [~] 1.1 Importers: CAD (DXF) and KML stand layouts; contours (DXF/SHP/GeoTIFF); OSM building/road extract for area; authority network data (CSV/SHP/GeoJSON with required fields). (Done: stands from KML/KMZ/GeoJSON/DXF, buildings from Overpass/GeoJSON; see docs/imports.md. To do: roads, contours, authority network, live Overpass fetch.)
- [x] 1.2 Import validation: CRS detection, duplicate stands, missing erf numbers flagged early.
- [~] 1.3 Building-type predictor: rules on OSM tags, zoning, footprint area; rooftop-image classifier behind licence flag; output type + confidence + source. (Done: tags, zoning, footprint from the rules file. To do: rooftop images, pending licence.)
- [x] 1.4 Inspection UI (tablet): map-first, one-tap confirm, type picker (house/shop/school/other/not present/new), photo capture, notes, GPS, timestamp. (docs/field.md; offline in 1.8)
- [x] 1.5 Candidate marking: transformer, mini-sub, MV route, pole site, LV route.
- [x] 1.6 Progress panel: confirmed / outstanding / low-confidence-first list.
- [x] 1.7 Income and ADMD tool: observable inputs → income band → category → ADMD (per NRS 034-1 tables in rules file); group after-diversity demand; special loads at own kVA; override with reason. (eskom/0.2.0: NRS 034 and SANS 507-1 load classes and Herman-Beta group demand; see docs/load-data.md. Score thresholds still a heuristic; SANS 507 C8 unverified.)
- [ ] 1.8 Offline: IndexedDB store, cached rules tables, photo queue, sync engine with conflict detection and side-by-side resolution UI (never auto-overwrite).
- [ ] 1.9 Offline map tiles: PMTiles pack per project area, download/refresh.
- [~] 1.10 Load schedule view and export (CSV/Excel). (Done: view and CSV. To do: Excel with the documents in Phase 6.)
- [x] 1.11 Assumptions register v1: every estimate and override auto-registered.
- [~] 1.12 Validation: ADMD hand-worked cases from NRS 034-1 examples in `/test-cases`. (Done: hand-worked ADMD and Herman-Beta cases, and a self-check of every load class against its α, β and c. To do: compare with NRS 034-1 worked examples and ReticMaster results.)

### Phase 2 – LV design

Deliverables: LV layout, phasing, conductor sizing, voltage drop, fault level, OH and UG checks.

- [ ] 2.1 Network model in Python (networkx graph + shapely geometry), serialisation to/from Postgres.
- [ ] 2.2 Load allocation to candidate LV routes; phase balancing (3-phase/1-phase per rules).
- [ ] 2.3 Conductor/cable library from rules file (SANS 1507 LV cables, ABC/bare OH conductors) with ratings.
- [ ] 2.4 Voltage drop calc (NRS 048-2 limits), thermal loading, LV fault level at ends.
- [ ] 2.5 Overhead checks: span, sag/tension, clearances, pole class, stays.
- [ ] 2.6 Underground checks: derating for soil thermal resistivity, depth, grouping.
- [ ] 2.7 OH vs UG side-by-side where both allowed: cost + voltage.
- [ ] 2.8 "Not inspected" flagging for any element outside marked sites/routes.
- [ ] 2.9 LV results UI: map overlay, per-segment table, check pass/fail with clause refs.
- [ ] 2.10 Validation: hand-worked LV feeder cases (voltage drop, derating, sag).

### Phase 3 – MV network

- [ ] 3.1 Transformer placement from candidates; sizing to SANS 780/1019 standard ratings with growth allowance.
- [ ] 3.2 Mini-sub vs pole-mount selection per rules.
- [ ] 3.3 MV routing along candidate routes; MV cable/conductor library (SANS 97, OH).
- [ ] 3.4 MV loading and voltage checks; transformer tap setting.
- [ ] 3.5 MV results UI and checks table.
- [ ] 3.6 Validation: hand-worked transformer sizing and MV voltage cases.

### Phase 4 – Bulk supply

- [ ] 4.1 Authority connection point input form; hard stop if capacity or fault level missing.
- [ ] 4.2 pandapower model builder from network model.
- [ ] 4.3 Load flow (balanced; unbalanced LV later via OpenDSS).
- [ ] 4.4 IEC 60909 fault studies (3-ph, 1-ph) at all buses.
- [ ] 4.5 Supply sizing and bulk feeder selection.
- [ ] 4.6 Validation: pandapower results vs hand-worked IEC 60909 example; vs authority's accepted method sample.

### Phase 5 – Optimisation and comparison

- [ ] 5.1 Cost model: material library, assemblies, estimated dated rates, losses cost, lifetime NPV (period, discount rate, energy cost, growth).
- [ ] 5.2 Objective runners: lowest capex, lowest lifetime cost, most spare capacity under ceiling.
- [ ] 5.3 Heuristic engine: constructive placement + local search (move transformer, re-route, re-size, re-phase, OH/UG flip); every candidate re-checked against all rules.
- [ ] 5.4 Compare view: three options side by side; "too close to call" flag using rate uncertainty band.
- [ ] 5.5 Run parameter UI and run history.
- [ ] 5.6 Validation: small benchmark networks with known optimum.

### Phase 6 – Documents

- [ ] 6.1 Material library and rate list management UI; supplier price-list import; override with date.
- [ ] 6.2 DXF drawings (ezdxf): layers, symbols, title block, sheet layouts per authority template; DWG conversion if required.
- [ ] 6.3 Design report PDF: calcs, assumptions, clause refs, traceability tables, versions footer.
- [ ] 6.4 BoQ: PDF + Excel, indicative costs marked "estimate".
- [ ] 6.5 GIS exports: KML, GeoJSON, Shapefile.
- [ ] 6.6 Submission pack: drawing register, document register, authority checklist, zipped.
- [ ] 6.7 One-step "Generate all" job; stale-document detection on any upstream change.
- [ ] 6.8 Every document stamped with rules version, rate date, design date, revision.

### Phase 7 – Review and sign-off

- [ ] 7.1 Assumptions register v2: full listing, confirm/clear workflow, blocks sign-off until empty.
- [ ] 7.2 Revision snapshot and numbering; reproduce-run from a revision.
- [ ] 7.3 Sign-off: engineer only, registration number captured, document set locked.
- [ ] 7.4 Audit trail: who changed what, when, before/after.
- [ ] 7.5 Open-format export of whole project (GeoJSON + JSON + files).
- [ ] 7.6 Release gate: all `/test-cases` pass before tagging a release.

### Phase 8 – Design assistant

- [ ] 8.1 Tool gateway: expose existing endpoints as Claude tools with strict schemas; no calculation tool exists outside the Python service.
- [ ] 8.2 Run setup: assistant drafts run parameters, user confirms.
- [ ] 8.3 Option explanation from traceability records only.
- [ ] 8.4 Report text drafting into editable sections; engineer edits and approves.
- [ ] 8.5 Feature flag + graceful absence; full test suite passes with flag off.
- [ ] 8.6 Prompt-injection and data-boundary tests (assistant cannot alter authority inputs or sign off).

---

## 6. Cross-cutting workstreams

A. **Standards clause index.** `/docs/standards-index.md` mapping each check to standard, clause and edition; every rules-file entry references an index id. Must be checked against current editions before Phase 2.
B. **Validation library.** `/test-cases/<topic>/case-N/{inputs.json, expected.json, source.md}`; CI fails on tolerance breach. Grows every phase.
C. **Offline and sync.** Designed in Phase 1, regression-tested every phase.
D. **Traceability.** Enforced by a Python decorator/record type; a lint rule fails any calc output lacking inputs, formula id and clause ref.
E. **Security.** Single tenant, HTTPS, signed document downloads, encrypted backups, photo EXIF stripped of personal data; records per load point, not per person.
F. **Performance targets.** Field UI usable on mid-range Android tablet; 2,000-stand township design run under 5 min; documents under 2 min.

---

## 7. Open items (from spec)

- [ ] Confirm the signing engineer's ECSA registration is active for submissions.
- [ ] Check which authorities require DWG rather than DXF.
- [ ] Choose which authority's rules to encode first (assumed Eskom).
- [ ] Decide whether any design calculation must also run offline (assumed no).
- [ ] Confirm current editions of NRS 034-1, NRS 048-2, NRS 097-2-1, SANS 10142-1, SANS 10098, SANS 780, SANS 1019, SANS 1507, SANS 97, IEC 60909, Red Book.
- [ ] Confirm Google satellite and rooftop-image licence terms.
- [ ] Confirm .NET + Python split vs Python-only back end (Decision A).

---

## 8. Master checklist

### Setup
- [ ] Decisions A–I confirmed by engineer
- [ ] Open items resolved or explicitly deferred
- [ ] Standards clause index drafted
- [x] Phase 0 complete

### Build
- [ ] Phase 1 Field capture – usable offline on tablet
- [ ] Phase 2 LV design – checks pass on validation cases
- [ ] Phase 3 MV network – checks pass on validation cases
- [ ] Phase 4 Bulk supply – load flow and IEC 60909 validated
- [ ] Phase 5 Optimisation – three options + compare view
- [ ] Phase 6 Documents – one-step full document set
- [ ] Phase 7 Review and sign-off – revision, audit, export
- [ ] Phase 8 Design assistant – flagged, optional, tested

### Release gate (every release)
- [ ] All hand-worked test cases pass
- [ ] Full suite passes with assistant disabled
- [ ] Offline sync regression passes
- [ ] Every document carries rules version, rate date, design date
- [ ] Backup and restore tested
- [ ] Reproduce a prior revision bit-for-bit

### Later features (post v1, in order)
- [ ] Protection and earthing
- [ ] Servitude and clash checks
- [ ] Revision comparison
- [ ] PV and EV hosting capacity (OpenDSS)
- [ ] As-built redlines; read-only review link
- [ ] Public lighting (SANS 10098); staged development; MV switching and reliability; existing network upgrades
- [ ] Rules change log; "why this size/route" assistant query
