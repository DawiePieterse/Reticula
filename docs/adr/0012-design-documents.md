# ADR 0012 – Design documents

Date: 2026-10-07 · Status: accepted

## Decision

A. **The calc service renders, the API stores.** Every document is rendered by `POST /calc/documents/{kind}` from one design run's result. Renderers lay out, count and sum BoQ amounts that the cost model already priced; they compute no engineering value. The kinds are: DXF drawing, design report PDF, BoQ as Excel and PDF, load schedule Excel, GeoJSON, KML, zipped shapefile and the submission pack.
B. **One job generates the set.** `documents.generate` renders the eight kinds and then the pack from them. Each file goes to object storage at `documents/{project}/{id}.{ext}` with its SHA-256, and gets a number from the project's initials (`DWG-001` for the drawing, `DOC-nnn` for the rest).
C. **Every document carries the stamp.** The stamp holds project, revision, design date, rules reference and hash, rate library and date, and the inputs hash. When the design is not fit to submit, it says **NOT FIT TO SUBMIT** with the reasons: placeholder values, failed checks, uninspected elements, bulk studies not run. Once the revision is signed, the stamp names the engineer and their ECSA number instead.
D. **Documents are never deleted.** A new set supersedes the project's earlier unlocked documents. A document is stale when a newer run replaced its design or the project's inputs changed after it. A signed revision's documents are locked and never stale.
E. **Drawings follow Eskom 240-87658920.** Layers, colours and line styles follow it, and the symbols are transcribed from its Annex B. The title block is Reticula's own, and every sheet carries the stamp.

## Blocked

- **DWG.** Needs a licensed converter (ODA File Converter), and the authorities that insist on DWG are not yet known. DXF is the format until then.
- **Authority title blocks.** No authority template is held.
- **ElecCell.cel and ElecLines.rsc.** The standard names them but does not include them.

## Consequences

- Rendering adds reportlab, openpyxl, ezdxf and pyshp to the calc service; the API needs no document libraries.
- A document is reproducible from its design run's stored result and the same calc version.
