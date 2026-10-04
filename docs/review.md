# Review and sign-off (plan Phase 7)

The project's Review page brings together what stands between the design and its submission: the assumptions
register, sign-off, issued revisions, the audit trail and the open export.

## Assumptions register (7.1)

- Field assumptions (load estimates, overrides, missing indicators) are raised as loads are recorded.
- The latest design runs raise their own: every rules section still marked unverified, the layout warnings of each LV
  site and the MV design (loops opened, routes not connected, buildings not inspected, transformer moved onto a route,
  supply point assumed) and the bulk study's modelling assumptions. The register is brought up to date whenever it is
  read, before documents are generated and before sign-off (one sync per project at a time).
- Each row is **open**, **cleared** (the information was obtained), **accepted** (the engineer accepts it with a
  reason, at least 5 characters) or **withdrawn** (no longer raised by the latest runs). Cleared and accepted rows can
  be reopened; a withdrawn row reopens if a later run raises it again.
- Sign-off needs no open row.

## Sign-off and revisions (7.2, 7.3)

Sign-off is for engineers only and needs:
1. No open assumption.
2. Every latest design run (LV per site, MV, bulk) passing all its checks.
3. A document set generated from the current sources (not stale), which the engineer has reviewed.
4. Full name, ECSA registration number and the declaration: "I have reviewed this design, its assumptions and its
   documents, and I take professional responsibility for it."

Issuing (job `revision.issue`) creates revision A, B, … Z, AA …:
- a snapshot (`projects/<id>/revisions/<label>/snapshot.json`, SHA-256 kept) of the field data the design rests on:
  loads with observations, buildings, candidates, connection point, rate list, assumptions with their resolutions and
  the design runs named by the sources;
- a new, **locked** document set labelled with the revision, every document stamped "Signed off: name, ECSA
  registration, date". Locked sets are never regenerated; later changes go into the next revision.

**Reproduce** (job `revision.reproduce`) re-runs every design run of a revision from its stored input and compares
the new result with the stored one value by value; the first difference is reported per run (release gate:
"reproduce a prior revision bit-for-bit").

## Audit trail (7.4)

Every change to projects, candidates, loads, building inspections, assumptions, the connection point, rate lists,
design runs, imports, document sets and revisions is written with the change (EF Core interceptor): who (the user, or
the user who started the background job), when, the entity and the values before and after. Large stored results are
left out (they are immutable and kept on the run). New imported buildings are audited as their import batch.

## Open export (7.5)

"Export project" (job `project.export`) zips the whole project: `project.json`, field data as GeoJSON (stands,
buildings with their loads, candidates, imported map features), inspections, the assumptions register, every design
run with its exact input and full result, documents per revision, revision snapshots, photos and the audit trail,
with a README describing the layout. Downloads use a signed link valid for 10 minutes.

## Release gate (7.6)

`.github/workflows/release.yml` runs on a `v*` tag: the full CI suite (calc, API, web, backup and restore) and every
hand-worked case under `/test-cases` (a case folder without a runner fails the gate). The summary maps each release
gate item to the job that proves it; publish the release only when the gate passes.

## API

- `GET /api/projects/{id}/review` (readiness, revisions, exports), `GET …/assumption-register`
- `POST …/assumptions/{aid}/accept` {note}, `POST …/assumptions/{aid}/clear` {note}, `POST …/assumptions/{aid}/reopen`
- `POST …/revisions` {fullName, registrationNumber, declaration, notes}, `GET …/revisions/{rid}`, `POST …/revisions/{rid}/reproduce`
- `GET …/audit?entityType=&before=&take=`
- `POST …/exports`, `GET …/exports/{eid}/link`, `GET /api/export-files/{eid}?token=…`
