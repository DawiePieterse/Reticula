"""Design documents (Phase 6): drawing, report, bill of quantities, load schedule, GIS exports and the submission pack.

Every renderer takes a `DocumentRequest` (the stamp's metadata and a full `Design`) and returns bytes. No renderer
computes an engineering number: every value comes from the Design, which the calc service produced and traced. The
renderers only lay it out, count it and sum BoQ amounts that `cost.price` already priced.

**Stamp (6.8).** Every document carries the project, revision, design date, rules version and hash, the rate
library and its date, and the inputs hash; and, prominently, "NOT FIT TO SUBMIT" with the reasons when the design
is not fit (placeholder values, failed checks, elements not inspected, bulk studies not run), or the engineer's
sign-off when it is signed.

**Contract (the API mirrors it in Reticula.Infrastructure/Calc/DocumentContracts.cs).**

- `POST /calc/documents/{kind}`, body `{meta: DocumentMeta, design: Design, rows?: [ {column: value} ],
  totals?: {name: value}, sections?: [ {title, text} ]}`, returns the file with its media type and a `Content-Disposition` file name such as
  `soshanguve-ext-19_report_R2.pdf`. `kind` is one of `KINDS`. `rows` is the load schedule (load_schedule_xlsx);
  `totals` its diversified totals; `sections` are report text sections the engineer approved (report_pdf).
- `POST /calc/documents/pack`, body `{meta, design, files: [ {name, title, kind, number?, content_base64} ]}`, returns
  the submission pack zip: the files, a drawing register, a document register and the authority checklist.

**Not done here (blocked).** DWG: the authority's DWG needs a licensed converter (ODA File Converter), so DXF is the
format until the engineer confirms which authorities insist (PLAN open item). Authority title-block templates:
none held, so the title block is Reticula's own with every stamp field. ElecCell.cel and ElecLines.rsc: 240-87658920
names them but does not contain them, so the symbols and line styles are transcribed from its Annex B and §4.3.4.
"""

from __future__ import annotations

from typing import Any, Literal

from pydantic import BaseModel

from ..design.run import Design

Kind = Literal["drawing_dxf", "report_pdf", "boq_xlsx", "boq_pdf", "load_schedule_xlsx", "geojson", "kml", "shapefile_zip"]

KINDS: dict[str, tuple[str, str, str]] = {
    # kind: (title, media type, extension)
    "drawing_dxf": ("Reticulation layout drawing", "application/dxf", "dxf"),
    "report_pdf": ("Design report", "application/pdf", "pdf"),
    "boq_xlsx": ("Bill of quantities (spreadsheet)", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "xlsx"),
    "boq_pdf": ("Bill of quantities", "application/pdf", "pdf"),
    "load_schedule_xlsx": ("Load schedule", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "xlsx"),
    "geojson": ("GIS export (GeoJSON)", "application/geo+json", "geojson"),
    "kml": ("GIS export (KML)", "application/vnd.google-earth.kml+xml", "kml"),
    "shapefile_zip": ("GIS export (Shapefile)", "application/zip", "zip"),
}


class DocumentMeta(BaseModel):
    """What the stamp says about the project and the revision; the design itself says the rest."""

    project_name: str
    project_code: str | None = None
    area: str | None = None
    authority: str
    client: str | None = None
    revision_number: int
    revision_label: str = ""
    design_date: str
    """ISO date of the design run."""
    document_number: str | None = None
    """Drawing or document number for the title block and registers."""
    engineer_name: str | None = None
    engineer_registration: str | None = None
    signed_off: bool = False
    signed_off_at: str | None = None


class ReportSection(BaseModel):
    title: str
    text: str


class DocumentRequest(BaseModel):
    meta: DocumentMeta
    design: Design
    rows: list[dict[str, Any]] | None = None
    """The load schedule's rows, as the API lists them (load_schedule_xlsx)."""
    totals: dict[str, Any] | None = None
    """The load schedule's totals as the calc service worked them out for the API (load_schedule_xlsx)."""
    sections: list[ReportSection] = []
    """Report text the engineer approved (report_pdf)."""


class PackFile(BaseModel):
    name: str
    title: str
    kind: str
    number: str | None = None
    content_base64: str


class PackRequest(BaseModel):
    meta: DocumentMeta
    design: Design
    files: list[PackFile]


def render(kind: str, req: DocumentRequest) -> bytes:
    from . import boq, drawing, gis, report, schedule

    renderers = {
        "drawing_dxf": drawing.render, "report_pdf": report.render, "boq_xlsx": boq.render_xlsx, "boq_pdf": boq.render_pdf,
        "load_schedule_xlsx": schedule.render, "geojson": gis.render_geojson, "kml": gis.render_kml, "shapefile_zip": gis.render_shapefile,
    }
    return renderers[kind](req)
