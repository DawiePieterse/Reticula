# Documents (plan Phase 6)

"Generate all" on the project's Documents page makes every document from the latest finished design runs in one job
(`documents.generate`). The calc service renders them (`POST /docs/render`); nothing is recalculated, so each document
shows exactly the stored results it names. Each generation is a draft revision D1, D2 … (formal revisions and sign-off
come with plan 7.2–7.3).

## Rate lists and material library (6.1)

- Shipped lists live in `rates/<name>/<date>.yaml`. The Rates page copies one (or another rate list) into an editable list.
- Every change is an override with a date and source; the previous rate is kept. The list's revision goes up.
- Supplier price lists import as CSV: header with `code` and `rate` (or `price`); optional `section` (default
  `materials`), `description`, `unit`, `date`, `source`. Unknown codes are reported; a new material is added when the
  row has a description.
- Assemblies (e.g. an erected pole) are built from materials and labour; their rate follows the materials.
- The project page chooses the rate list; design runs send it inline and keep it in their input.

## What is made

| Kind | File | Content |
|---|---|---|
| drawings | `…-drawings.dxf` | Model space in Lo (nearest zone) metres; layers per the template; A1 sheets: overall layout and one per LV site, scaled viewport, legend, title block (6.2) |
| report | `…-report.pdf` | Design basis, connection point, loads, LV per site (comparison, traced demand, voltage drop and fault with formulas and inputs, branch table), MV, bulk supply, options, assumptions, every check with its clause, drawing list (6.3) |
| boq_pdf, boq_xlsx | `…-boq.pdf/.xlsx` | Sections per LV site and MV, summary, assemblies and their components, rate overrides with dates; every page marked ESTIMATE (6.4) |
| gis_geojson, gis_kml, gis_shp | `…-network.geojson/.kml`, `…-shapefiles.zip` | LV branches and nodes, customers, MV branches and nodes with their results, WGS84 (6.5) |
| loads_xlsx | `…-load-schedule.xlsx` | The load schedule (also downloadable from the Loads page) (1.10) |
| registers | `…-registers.pdf/.csv` | Drawing register, document register, authority checklist (6.6) |
| submission_pack | `…-submission-pack.zip` | All of the above and `STAMP.txt` (6.6) |

With an MV design, the BoQ costs transformers as the MV design's units (pole-mount or mini-sub with structure and MV
fuses) and leaves the bare transformer out of the LV sections. DWG is not produced; where an authority requires DWG,
convert the DXF with the ODA File Converter (open item: which authorities require it).

## Authority template

`templates/<authority>.yaml`: drawing CRS (`LO-auto`), sheet, scales, layers (colour, linetype, weight), title block
fields, and the submission checklist. Checklist items marked `auto` are assessed from the package (connection point,
loads, LV/MV/bulk checks, unverified rules values, open assumptions, same revision); the rest are for the engineer.

## Stale documents (6.7)

Each set records its sources: the LV run per transformer site, MV, bulk and option runs, the connection point, the
rate list revision, the rules, and the loads and assumptions (count and last change). The Documents page compares
them with the current ones and lists what changed ("a newer LV design", "loads changed" …) until the set is generated again.

## Stamp (6.8)

Every document carries the rules version and hash, rate list and rate date, design date (the latest run's finish),
revision, generation time and engineer: in the DXF title block, every PDF page footer, a Stamp sheet and header rows in
the spreadsheets, the GeoJSON `reticula` member, the KML description, the shapefile README and the pack's `STAMP.txt`.
Documents made with unverified rules values are marked NOT FOR SUBMISSION.

## API

- `POST /api/projects/{id}/document-sets` {engineer} → job; `GET …/document-sets` (with `stale` and `changes`); `GET …/document-sets/{setId}`
- `GET /api/projects/{id}/documents/{docId}/link` → signed URL valid 10 minutes; `GET /api/document-files/{docId}?token=…`
- `GET /api/projects/{id}/load-schedule.xlsx`
- `GET/POST /api/rate-lists`, `GET /api/rate-lists/{id}`, `PUT …/{id}/rates`, `POST …/{id}/import`, `GET/PUT /api/projects/{id}/rate-list`
