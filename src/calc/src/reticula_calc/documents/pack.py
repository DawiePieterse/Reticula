"""Submission pack (plan 6.6): the documents, a drawing register, a document register and the authority checklist, zipped.

The checklist reads the design's checks by category, so each line is a pass, a failure with its count, "not run" or
"not applicable"; plus the connection point data, inspection, placeholder and sign-off lines a submission needs.
"""

from __future__ import annotations

import base64
import csv
import io
import zipfile

from reportlab.lib.pagesizes import A4
from reportlab.lib.units import mm
from reportlab.platypus import Paragraph, SimpleDocTemplate, Spacer

from ..design.run import Design
from . import DocumentMeta, PackRequest
from .common import esc, pdf_text, slug, stamp
from .report import _numbered, _status, kv, styles, table

# (item, check categories, applies when)
CHECKLIST = [
    ("LV voltage drop within the limit at every node and service", ("lv_drop",), "always"),
    ("LV conductor loading within rating", ("lv_loading",), "always"),
    ("LV fault current at feeder ends", ("lv_fault",), "always"),
    ("Overhead ground clearance at mid-span", ("oh_clearance",), "overhead"),
    ("Overhead conductor tension within limit", ("oh_tension",), "overhead"),
    ("Pole class and stays", ("oh_pole",), "overhead"),
    ("Transformer loading with the growth allowance", ("tx_loading",), "always"),
    ("MV voltage drop to every transformer", ("mv_drop",), "always"),
    ("MV conductor loading", ("mv_loading",), "always"),
    ("Supply within the connection point's capacity", ("bulk_supply",), "always"),
    ("Fault level within the switchgear rating", ("bulk_fault",), "always"),
    ("Conductors withstand the fault current", ("bulk_withstand",), "always"),
    ("Bus voltages within the band at design demand", ("bulk_voltage",), "always"),
]


def checklist(d: Design, meta: DocumentMeta) -> list[tuple[str, str, str]]:
    """(item, status, note) for every line of the authority checklist."""
    out: list[tuple[str, str, str]] = []
    has_oh = d.overhead is not None or d.mv_overhead is not None
    for item, cats, when in CHECKLIST:
        cs = [c for c in d.checks if c.category in cats]
        if when == "overhead" and not has_oh:
            out.append((item, "not applicable", "underground construction"))
        elif d.bulk.stopped and cats[0] in ("bulk_fault", "bulk_withstand", "bulk_voltage"):
            out.append((item, "not run", d.bulk.stopped))
        elif not cs:
            note = "the bulk studies did not run" if cats[0].startswith("bulk") and d.bulk.stopped else "no check of this kind in the design"
            if cats == ("lv_fault",):
                note = "fault levels reported only: no minimum end fault current in the rules (LV protection settings not held)"
            out.append((item, "not run", note))
        else:
            failed = [c for c in cs if not c.passes]
            clause = next((c.clause for c in cs if c.clause), "")
            out.append((item, f"fail ({len(failed)} of {len(cs)})" if failed else f"pass ({len(cs)})", clause))
    out.append(("Underground cables de-rated for how they are laid", "pass" if d.underground else "not applicable",
                f"{len(d.underground.ratings)} cable ratings" if d.underground else "overhead construction"))
    out.append(("Connection point data from the authority (capacity, fault level)", "fail" if d.bulk.stopped else "pass", d.bulk.stopped or ""))
    out.append(("Every element inspected in the field", f"fail ({len(d.not_inspected)})" if d.not_inspected else "pass",
                ", ".join(n.label or n.kind for n in d.not_inspected[:10])))
    out.append(("Rules values confirmed (no placeholders)", f"fail ({len(d.placeholders)})" if d.placeholders else "pass",
                "; ".join(d.placeholders[:5])))
    out.append(("Signed off by the registered engineer", "pass" if meta.signed_off else "fail",
                f"{meta.engineer_name or ''} {('ECSA ' + meta.engineer_registration) if meta.engineer_registration else ''}".strip()))
    return out


def _csv(rows: list[list]) -> bytes:
    buf = io.StringIO()
    csv.writer(buf).writerows(rows)
    return buf.getvalue().encode("utf-8")


def _pdf(title: str, req: PackRequest, rows: list[list], fails: list[int] = ()) -> bytes:
    s = stamp(req.meta, req.design)
    st = styles()
    buf = io.BytesIO()
    doc = SimpleDocTemplate(buf, pagesize=A4, leftMargin=15 * mm, rightMargin=15 * mm, topMargin=15 * mm, bottomMargin=17 * mm,
                            title=pdf_text(f"{s.project} {title} {s.revision}"), creator="Reticula calc service")
    story = [Paragraph(esc(title), st["h1"]), kv(s.lines()[:-1], st), Spacer(1, 3 * mm), _status(s, st), Spacer(1, 4 * mm), table(rows, st, fails=fails)]
    doc.build(story, canvasmaker=_numbered(s.footer(), not s.fit))
    return buf.getvalue()


def render(req: PackRequest) -> bytes:
    meta, d = req.meta, req.design
    rev = f"R{meta.revision_number}"
    drawings = [f for f in req.files if f.kind == "drawing_dxf"]
    documents = [f for f in req.files if f.kind != "drawing_dxf"]
    dreg = [["Number", "Title", "Revision", "Date", "Format", "File"]] + [
        [f.number or "–", f.title, rev, meta.design_date, f.name.rsplit(".", 1)[-1].upper(), f.name] for f in drawings]
    oreg = [["Number", "Title", "Revision", "Date", "Format", "File"]] + [
        [f.number or "–", f.title, rev, meta.design_date, f.name.rsplit(".", 1)[-1].upper(), f.name] for f in documents]
    items = checklist(d, meta)
    chk = [["Item", "Status", "Note"]] + [list(x) for x in items]
    fails = [i for i, (_, status, _) in enumerate(items) if status.startswith("fail")]
    base = slug(meta.project_code or meta.project_name)
    out = io.BytesIO()
    with zipfile.ZipFile(out, "w", zipfile.ZIP_DEFLATED) as z:
        for f in req.files:
            z.writestr(f"documents/{f.name}", base64.b64decode(f.content_base64))
        z.writestr(f"registers/{base}_drawing-register_{rev}.csv", _csv(dreg))
        z.writestr(f"registers/{base}_drawing-register_{rev}.pdf", _pdf("Drawing register", req, dreg))
        z.writestr(f"registers/{base}_document-register_{rev}.csv", _csv(oreg))
        z.writestr(f"registers/{base}_document-register_{rev}.pdf", _pdf("Document register", req, oreg))
        z.writestr(f"registers/{base}_authority-checklist_{rev}.csv", _csv(chk))
        z.writestr(f"registers/{base}_authority-checklist_{rev}.pdf", _pdf("Authority checklist", req, chk, fails))
        s = stamp(meta, d)
        z.writestr("README.txt", "Submission pack\n\n" + "\n".join(f"{k}: {v}" for k, v in s.lines()) + "\n")
    return out.getvalue()
