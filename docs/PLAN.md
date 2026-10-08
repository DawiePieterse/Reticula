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

- [x] 1.1 Importers: CAD (DXF) and KML stand layouts; contours (DXF/SHP/GeoTIFF); OSM building/road extract for area; authority network data (CSV/SHP/GeoJSON with required fields). (Done: stands, buildings, roads, contours and existing network from KML/KMZ/GeoJSON/DXF/zipped shapefile/CSV, with required-field checks; buildings and roads fetched live from OpenStreetMap; GeoTIFF elevation models traced to contours at a specified interval; see docs/imports.md.)
- [x] 1.2 Import validation: CRS detection, duplicate stands, missing erf numbers flagged early.
- [!] 1.3 Building-type predictor: rules on OSM tags, zoning, footprint area; rooftop-image classifier behind licence flag; output type + confidence + source and load class per zone and type. (Done: tags, zoning, footprint from the rules file; load class prediction per zone and building type from rules mapping. Blocked: rooftop images, pending the imagery licence (open item). Found 2026-10-07: CD:NGI 25 cm orthophotos are free but carry no formal licence, so written terms are needed for commercial use; Google Maps imagery may not be used to train or run a model; Google Open Buildings footprints, with confidence scores, are CC BY 4.0 or ODbL.)
- [x] 1.4 Inspection UI (tablet): map-first, one-tap confirm, type picker (house/shop/school/other/not present/new), photo capture, notes, GPS, timestamp. (docs/field.md)
- [x] 1.5 Candidate marking: transformer, mini-sub, MV route, pole site, LV route.
- [x] 1.6 Progress panel: confirmed / outstanding / low-confidence-first list.
- [x] 1.7 Income and ADMD tool: observable inputs → income band → category → ADMD (per NRS 034-1 tables in rules file); group after-diversity demand; special loads at own kVA; override with reason. (eskom/0.2.0: NRS 034 and SANS 507-1 load classes and Herman-Beta group demand; see docs/load-data.md. Score thresholds still a heuristic; SANS 507 C8 unverified.)
- [x] 1.8 Offline: IndexedDB store, cached rules tables, photo queue, sync engine with conflict detection and side-by-side resolution UI (never auto-overwrite). (ADR 0004; docs/field.md. Loads saved offline get their kVA from the calc service on sync.)
- [x] 1.9 Offline map tiles: PMTiles pack per project area, download/refresh. (ADR 0005; docs/field.md, docs/operations.md. Needs a map source configured on the server.)
- [x] 1.10 Load schedule view and export (CSV/Excel). (View and CSV on the loads page; Excel as the load schedule document of Phase 6, with the calc service's diversified totals.)
- [x] 1.11 Assumptions register v1: every estimate and override auto-registered.
- [!] 1.12 Validation: ADMD hand-worked cases from NRS 034-1 examples in `/test-cases`. (Done: hand-worked ADMD and Herman-Beta cases, and a self-check of every load class against its α, β and c. Herman-Beta mixing and empirical diversity checked against ReticMaster 21's worked results; see docs/load-data.md. Blocked: NRS 034-1 is not held, so its worked examples cannot be compared.)

### Phase 2 – LV design

Deliverables: LV layout, phasing, conductor sizing, voltage drop, fault level, OH and UG checks.

- [x] 2.0 Pre-design placement: propose transformer sites, the loads each feeds, LV routes along roads and an MV route from the loads, roads and connection point, before the field visit; the field verifies exceptions. (ADR 0010; `POST /calc/lv/placement`, eskom/0.8.1 `mv_design` and `lv_design`, values placeholders. Calc step with Herman-Beta capacity, road-graph reach and cost trace; API job `lv.placement` storing every proposed site and route as a candidate with `source: proposed`, replacing only the previous proposal; reach is min(rules reach, reach derived from the voltage drop limit). On the tablet proposals are drawn faded with Confirm here, Move to my position and Not here. The design run builds feeders from the routes, and every proposal not yet confirmed is flagged not inspected (2.8).)

- [x] 2.1 Network model in Python (networkx graph + shapely geometry), serialisation to/from Postgres. (ADR 0006: marked LV routes and sites joined within the rules file's `lv_network` tolerances from eskom/0.3.0; radial check, feeders and distances; stored in PostGIS per project, flagged out of date when the marked routes or the rules change. Rules values stay placeholders until the Eskom standards are identified.)
- [x] 2.2 Load allocation to candidate LV routes; phase balancing (3-phase/1-phase per rules). (ADR 0007: services from LV poles through service distribution boxes, 2 boxes a pole, 4 loads a box, second box on another phase; boxes phased per feeder from the far end; eskom/0.4.0. The 15 kVA three-phase limit is a placeholder. Overhead only: underground kiosks per Eskom 240-56030637 §3.10 e) still to model. From eskom/0.9.0 (ADR 0015): the reach is the engineer's 50 m service span, and each service gets its conductor (Airdac SNE 10 or 16 mm² overhead) by its own undiversified current and a 2 % drop limit.)
- [!] 2.3 Conductor/cable library from rules file (SANS 1507 LV cables, ABC/bare OH conductors) with ratings. (ADR 0008, eskom/0.5.0: underground Cu and Al cables with ratings in ground, pipe and air and short-circuit constants from Eskom 240-56030637 Rev 2. Single-phase ABC 16–150 mm² (CBi) and three-phase ABC 25–150 mm² (M-TEC) from SANS 1418 manufacturer data sheets. eskom/0.9.0 (docs/conductor-data.md): underground impedances at 70 °C from CBi, averaged with Voltex for copper 4-core, since 240-56063805 has no impedance table and asks the supplier for one; three-phase ABC ratings averaged over three suppliers; Airdac SNE 10 and 16 mm² service cables. Blocked: the three-phase ABC rating basis needs the Eskom ABC specification 240-84758170, which is not held; the engineer is still to accept the supplier data.)
- [!] 2.4 Voltage drop calc (NRS 048-2 limits), thermal loading, LV fault level at ends. (ADR 0009, eskom/0.6.0: Herman-Beta per-phase drop at every node and service connection, section currents against conductor ratings, phase-to-neutral fault current at every point, from the sized transformer of 3.1; per-feeder results and drop colours on the map. From eskom/0.9.0 (ADR 0015): the drop is checked at the meter, service cable included, against the engineer's 8 % (Red Book; 8–10 % in Eskom tenders), with 2 % in the service. Each feeder gets an NH gG fuse with I_B ≤ I_n ≤ I_z, and its least fault must be at least 3 × I_n. These are engineering assumptions after Energex 3064638, and sizing steps up a feeder that fails them. Blocked: Eskom 240-70465489 is still not held (the Eskom clause stays unverified); the fuse assumptions wait for 240-56030637 Table 10 (held, to transcribe) and 240-57649065; a whole-feeder ReticMaster comparison needs a ReticMaster run of the same feeder.)
- [x] 2.5 Overhead checks: span, sag/tension, clearances, pole class, stays. (design/overhead.py, eskom/0.8.0: poles at the maximum span, sag and tension by the ruling span with ground clearance at mid-span, pole class from the tip load, stays at angles and ends, for LV and MV lines; hand-worked case oh_span/case-1. Every value a placeholder until ESKOM-OHL is held. From eskom/0.9.0, overhead services too (design/services.py, ADR 0015): Airdac strung at 25 % of its breaking load as in Aberdare's sag table, spans of at most 50 m, and a 7 m service pole wherever the sag would drop below the clearance; hand-worked cases oh_service/case-1 and case-2, lv_service/case-1 and case-2.)
- [x] 2.6 Underground checks: derating for soil thermal resistivity, depth, grouping. (design/underground.py: ratings from 240-56030637 Rev 2 de-rated for resistivity, depth, ground temperature and grouping, kiosks per 240-56030637 §3.10 e); hand-worked case ug_derate/case-1. The factor tables are placeholders until SANS 10198-4 and the 240-56030637 annex are held.)
- [x] 2.7 OH vs UG side-by-side where both allowed: cost + voltage. (Construction `compare` designs both and picks by the objective; the comparison rows show capital and lifetime cost, worst drop, transformers and spare capacity, flagged too close to call when the cost ranges overlap.)
- [x] 2.8 "Not inspected" flagging for any element outside marked sites/routes, including every `proposed` candidate the field has not confirmed (ADR 0010). (Each is a failing `not_inspected` check, listed on every document's stamp; the design is not fit to submit until the field confirms it.)
- [x] 2.9 LV results UI: map overlay, per-segment table, check pass/fail with clause refs. (Design page: the network on the map with the Eskom drawing-practice symbols and drop colours; feeders, sections, transformers and every check with its clause and formula id.)
- [x] 2.10 Validation: hand-worked LV feeder cases (voltage drop, derating, sag). (lv_drop/case-1, ug_derate/case-1, oh_span/case-1, voltage_drop/case-1, lv_reach/case-1.)

### Phase 3 – MV network

- [x] 3.1 Transformer sizing from the confirmed sites (placed in 2.0) to SANS 780/1019 standard ratings with growth allowance; mini-sub selection per rules. (design/transformers.py: Herman-Beta demand of the loads each feeds, growth allowance, the smallest standard rating that carries it, spare capacity; the engineer may fix a rating. Ratings, impedances and losses are placeholders until 240-56062752 and SANS 780 are held.)
- [x] 3.2 Mini-sub vs pole-mount selection per rules. (Pole-mounted for overhead; a mini-sub for underground where the rules file says so, with its own rating series.)
- [x] 3.3 MV routing along candidate routes; MV cable/conductor library (SANS 97, OH). (mv/network.py: the marked MV routes joined from the connection point, each transformer tapped within the tap reach; eskom/0.8.0 ACSR, AAAC and 11 kV XLPE cables with typical values, placeholders until SANS 182, SANS 1418 and SANS 97 are held.)
- [x] 3.4 MV loading and voltage checks; transformer tap setting. (MV drop to every tap, section loading against rating, regulation and the off-load tap that keeps LV within the band.)
- [x] 3.5 MV results UI and checks table. (Design page: MV sections with conductor and loading, taps with drop and tap setting, the MV line on the map in its drawing-practice style.)
- [x] 3.6 Validation: hand-worked transformer sizing and MV voltage cases. (design/case-1: demand, rating, spare, MV drop, regulation, tap, LV voltage at full load.)

### Phase 4 – Bulk supply

- [x] 4.1 Authority connection point input form; hard stop if capacity or fault level missing. (Design page form with the source and date of the authority's values; without capacity and fault level the studies stop and the design is not fit to submit.)
- [x] 4.2 pandapower model builder from network model. (bulk.py: external grid from the fault level and X/R, MV and LV lines, transformers with impedance, losses and tap, loads spread over the LV nodes.)
- [x] 4.3 Load flow (balanced; unbalanced LV later via OpenDSS). (Balanced Newton-Raphson; bus voltages against the band at design demand. Unbalanced LV is the later OpenDSS feature.)
- [x] 4.4 IEC 60909 fault studies (3-ph, 1-ph) at all buses. (pandapower's IEC 60909 module: three-phase at the maximum case, single-phase at the minimum; conductor withstand K·A/√t and the MV switchgear rating checked.)
- [x] 4.5 Supply sizing and bulk feeder selection. (Notified maximum demand from the Herman-Beta demand of every consumer with growth, the smallest supply step, within the connection point's capacity; MV conductor sized to carry it.)
- [!] 4.6 Validation: pandapower results vs hand-worked IEC 60909 example; vs authority's accepted method sample. (Done: design/case-1 checks the pandapower fault current at LV against a hand-worked IEC 60909-0 calculation. Blocked: no accepted-method sample from the authority is held.)

### Phase 5 – Optimisation and comparison

- [x] 5.1 Cost model: material library, assemblies, estimated dated rates, losses cost, lifetime NPV (period, discount rate, energy cost, growth). (cost.py and rates/: assemblies of rate items, low and high totals from each item's uncertainty, I²R and transformer losses, lifetime cost. The indicative library is a placeholder; every cost from it says estimate.)
- [x] 5.2 Objective runners: lowest capex, lowest lifetime cost, most spare capacity under ceiling.
- [x] 5.3 Heuristic engine: local search from the 2.0 placement (move transformer, re-route, re-size, re-phase, OH/UG flip); every candidate re-checked against all rules. MILP per cluster (scipy HiGHS) where a proven optimum is wanted. (ADR 0011, design/optimise.py: every neighbour is a full design run; options ranked by soundness, then failed checks, then the objective; siting by capacitated facility location, one site per connected LV network within MV tap reach. Re-routing is limited to the marked and proposed routes; re-phasing is the load allocation's balancing in every evaluation.)
- [x] 5.4 Compare view: three options side by side; "too close to call" flag using rate uncertainty band. (Adopting an option makes it the project's design without recomputing it.)
- [x] 5.5 Run parameter UI and run history. (Run form for construction, MV line, objective and the optimisation; the runs table with each run's mode, result, capital cost and stale reason, and the compare view to adopt an option.)
- [x] 5.6 Validation: small benchmark networks with known optimum. (The siting MILP against brute-force enumeration on random problems, two clusters that need two sites, and one network that may only have one; and a regression that no option is less sound than the marked design.)

### Phase 6 – Documents

- [x] 6.1 Material library and rate list management UI; supplier price-list import; override with date. (Rates page: CSV import with name, date and source; the engineer's own rate per item with its date and source.)
- [!] 6.2 DXF drawings (ezdxf): layers, symbols, title block, sheet layouts per authority template; DWG conversion if required. (ADR 0012. Done: DXF with the Eskom 240-87658920 layers, colours, line styles and Annex B symbols, an A1 sheet with legend, title block and stamp. Blocked: authority title-block templates are not held, so the title block is Reticula's own; ElecCell.cel and ElecLines.rsc are not held; DWG needs the authorities that require it (open item). ODA File Converter is free for non-commercial use only, so commercial use needs ODA membership; LibreDWG (GPL) writes only R2000 DWG, with about 80 % coverage.)
- [x] 6.3 Design report PDF: calcs, assumptions, clause refs, traceability tables, versions footer. (Summary, the engineer's approved report sections, every network part, bulk supply, cost, Appendix A checks and Appendix B traceability with every formula id; the stamp in the footer of every page.)
- [x] 6.4 BoQ: PDF + Excel, indicative costs marked "estimate". (A sheet per network part and a summary; low and high totals.)
- [x] 6.5 GIS exports: KML, GeoJSON, Shapefile.
- [x] 6.6 Submission pack: drawing register, document register, authority checklist, zipped. (The checklist reads the design's checks by category. No authority's own checklist is held, so its items are Reticula's.)
- [x] 6.7 One-step "Generate all" job; stale-document detection on any upstream change. (Job `documents.generate`; a document is stale when a newer run replaced its design or the inputs changed; earlier sets are superseded, never deleted.)
- [x] 6.8 Every document stamped with rules version, rate date, design date, revision. (And the inputs hash, and NOT FIT TO SUBMIT with its reasons, or the sign-off.)

### Phase 7 – Review and sign-off

- [x] 7.1 Assumptions register v2: full listing, confirm/clear workflow, blocks sign-off until empty. (Placeholder rules values become assumptions per run, cleared when no longer used, reopened when they return.)
- [x] 7.2 Revision snapshot and numbering; reproduce-run from a revision. (ADR 0013: a revision passes reproduction only when the stored request gives the same result hash; a difference names where.)
- [x] 7.3 Sign-off: engineer only, registration number captured, document set locked. (Blocked until every check holds: ECSA number on the account (set on the Users page), fit to submit, no open assumption, inputs unchanged, reproduced, documents generated. Signing regenerates the documents with the sign-off and locks them. In practice no revision can be signed while the rules carry placeholders. The signing engineer's ECSA registration was confirmed active on 2026-10-07.)
- [x] 7.4 Audit trail: who changed what, when, before/after. (An EF Core interceptor writes an audit entry in the same save; derived and bulk-imported records are audited by their import or run.)
- [x] 7.5 Open-format export of whole project (GeoJSON + JSON + files).
- [x] 7.6 Release gate: all `/test-cases` pass before tagging a release. (.github/workflows/release.yml on `v*` tags: the whole of CI, then each release gate item as a named step, then the GitHub release. Every step passes locally; it has not yet run on a tag.)

### Phase 8 – Design assistant

- [x] 8.1 Tool gateway: expose existing endpoints as Claude tools with strict schemas; no calculation tool exists outside the Python service. (ADR 0014: read tools over the stored project and design; strict input schemas with no additional properties.)
- [x] 8.2 Run setup: assistant drafts run parameters, user confirms. (`propose_run_parameters`, checked by the same option rules as a design run; nothing runs until the engineer accepts.)
- [x] 8.3 Option explanation from traceability records only. (`get_traces` and `get_option_comparison`; the system prompt forbids any number without a record.)
- [x] 8.4 Report text drafting into editable sections; engineer edits and approves. (`draft_report_section`; an accepted draft becomes a draft report section that the engineer edits and approves.)
- [x] 8.5 Feature flag + graceful absence; full test suite passes with flag off. (Off by default; the release gate runs the API suite with it off.)
- [x] 8.6 Prompt-injection and data-boundary tests (assistant cannot alter authority inputs or sign off).

---

## 6. Cross-cutting workstreams

A. **Standards clause index.** `/docs/standards-index.md` mapping each check to standard, clause and edition; every rules-file entry references an index id. Must be checked against current editions before Phase 2. (Drafted 2026-10-05 with every clause *to confirm*; rules files may carry `index` ids from 0.3.0, checked by the calc tests.)
B. **Validation library.** `/test-cases/<topic>/case-N/{inputs.json, expected.json, source.md}`; CI fails on tolerance breach. Grows every phase.
C. **Offline and sync.** Designed in Phase 1, regression-tested every phase.
D. **Traceability.** Enforced by a Python decorator/record type; a lint rule fails any calc output lacking inputs, formula id and clause ref.
E. **Security.** Single tenant, HTTPS, signed document downloads, encrypted backups, photo EXIF stripped of personal data; records per load point, not per person.
F. **Performance targets.** Field UI usable on mid-range Android tablet; 2,000-stand township design run under 5 min; documents under 2 min.

---

## 7. Open items (from spec)

- [x] Confirm the signing engineer's ECSA registration is active for submissions. (Confirmed by the engineer, 2026-10-07.)
- [ ] Check which authorities require DWG rather than DXF. (Converter licences checked 2026-10-07. ODA File Converter is free for non-commercial use only; commercial use needs ODA membership. LibreDWG's dxf2dwg is GPL and writes R2000 only, at about 80 %. Which authorities require DWG is still to ask.)
- [ ] Choose which authority's rules to encode first (assumed Eskom).
- [ ] Decide whether any design calculation must also run offline (assumed no).
- [ ] Confirm current editions of NRS 034-1, NRS 048-2, NRS 097-2-1, SANS 10142-1, SANS 10098, SANS 780, SANS 1019, SANS 1507, SANS 97, IEC 60909, Red Book. (2026-10-05: none held yet. Eskom projects follow Eskom's own standards. Eskom 240-56030637 Rev 2, LV cable systems, now held and confirmed current, with five more Eskom numbers identified from it. See docs/standards-index.md.)
- [ ] Confirm Google satellite and rooftop-image licence terms. (2026-10-07: Google Maps Platform terms bar creating content from its imagery and using it for machine learning, so Google imagery is out. CD:NGI 25 cm orthophotos are free, on a hard disk from CD:NGI in Cape Town or as tiles at aerial.openstreetmap.org.za, but carry no formal licence; ask CD:NGI to confirm commercial use in writing. Google Open Buildings footprints, with confidence scores, are CC BY 4.0 or ODbL and need no imagery licence.)
- [ ] Confirm .NET + Python split vs Python-only back end (Decision A).

---

## 8. Master checklist

### Setup
- [ ] Decisions A–I confirmed by engineer (needs the engineer)
- [ ] Open items resolved or explicitly deferred (needs the engineer; section 7)
- [x] Standards clause index drafted (clauses still to confirm by the engineer)
- [x] Phase 0 complete

### Build
- [x] Phase 1 Field capture – usable offline on tablet (1.3 rooftop images and the 1.12 NRS 034-1 comparison blocked)
- [x] Phase 2 LV design – checks pass on validation cases (rules values placeholders; 2.3 and 2.4 remainders blocked on documents not held)
- [x] Phase 3 MV network – checks pass on validation cases
- [~] Phase 4 Bulk supply – load flow and IEC 60909 validated (against a hand-worked case; the authority's accepted-method sample is not held)
- [x] Phase 5 Optimisation – three options + compare view
- [x] Phase 6 Documents – one-step full document set (DXF; DWG and authority title blocks blocked)
- [x] Phase 7 Review and sign-off – revision, audit, export
- [x] Phase 8 Design assistant – flagged, optional, tested

### Release gate (every release)
Automated by `.github/workflows/release.yml` (plan 7.6). Ticked items passed locally on 2026-10-07; the workflow has not yet run on a tag.
- [x] All hand-worked test cases pass
- [x] Full suite passes with assistant disabled
- [x] Offline sync regression passes
- [x] Every document carries rules version, rate date, design date
- [x] Backup and restore tested
- [x] Reproduce a prior revision bit-for-bit

### Later features (post v1, in order)
- [ ] Protection and earthing (LV feeder fuse selection and the end-of-feeder fault check done in 2.4, eskom/0.9.0; earthing and discrimination still to do)
- [ ] Servitude and clash checks
- [ ] Revision comparison
- [ ] PV and EV hosting capacity (OpenDSS)
- [ ] As-built redlines; read-only review link
- [ ] Public lighting (SANS 10098); staged development; MV switching and reliability; existing network upgrades
- [ ] Rules change log; "why this size/route" assistant query
