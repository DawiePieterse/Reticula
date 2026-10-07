"""What every document shares: the stamp, file names, the drawing projection and PDF text that the bundled font can show."""

from __future__ import annotations

import math
import os
import re
import unicodedata
from dataclasses import dataclass
from functools import lru_cache

from pydantic import BaseModel

from ..design.run import Design
from ..geo import crs as crs_mod
from ..trace import Traced
from . import KINDS, DocumentMeta

NOT_FIT = "NOT FIT TO SUBMIT"


@dataclass(frozen=True)
class Stamp:
    project: str
    revision: str
    design_date: str
    rules: str
    rates: str
    inputs: str
    status: str
    """'NOT FIT TO SUBMIT: …', or the fitness and sign-off."""
    fit: bool
    reasons: tuple[str, ...]
    authority: str
    engineer: str
    number: str | None

    def lines(self) -> list[tuple[str, str]]:
        return [("Project", self.project), ("Authority", self.authority), ("Revision", self.revision), ("Design date", self.design_date),
                ("Rules", self.rules), ("Rates", self.rates), ("Inputs", self.inputs), ("Engineer", self.engineer), ("Status", self.status)]

    def footer(self) -> str:
        return (f"{self.project} · {self.revision} · design {self.design_date}" + (f" · {self.number}" if self.number else "")
                + f"\nrules {self.rules} · rates {self.rates} · inputs {self.inputs}")


def unfit_reasons(d: Design) -> list[str]:
    out: list[str] = []
    if d.placeholders:
        out.append(f"{len(d.placeholders)} placeholder value{'s' if len(d.placeholders) != 1 else ''} in the rules")
    failed = [c for c in d.checks if not c.passes and c.category != "not_inspected"]
    if failed:
        out.append(f"{len(failed)} failed check{'s' if len(failed) != 1 else ''}")
    if d.not_inspected:
        out.append(f"{len(d.not_inspected)} proposed element{'s' if len(d.not_inspected) != 1 else ''} not inspected")
    if d.bulk.stopped:
        out.append("bulk supply studies not run")
    errors = {i.code for i in d.issues if i.severity == "error"}
    if errors and not failed:
        out.append(f"errors: {', '.join(sorted(errors))}")
    return out


def stamp(meta: DocumentMeta, d: Design) -> Stamp:
    reasons = [] if d.fit_to_submit else unfit_reasons(d) or ["the design is not fit to submit"]
    rev = f"Rev {meta.revision_number}" + (f" ({meta.revision_label})" if meta.revision_label else "")
    if meta.signed_off and meta.engineer_name:
        signed = f"signed off by {meta.engineer_name}" + (f", ECSA {meta.engineer_registration}" if meta.engineer_registration else "")
        signed += f" on {meta.signed_off_at}" if meta.signed_off_at else ""
    else:
        signed = "not signed off"
    status = f"{NOT_FIT}: " + "; ".join(reasons) if reasons else f"Fit to submit, {signed}"
    engineer = meta.engineer_name or "not assigned"
    if meta.engineer_registration:
        engineer += f" (ECSA {meta.engineer_registration})"
    c = d.cost
    rates = f"{c.library}, {c.rate_date}" + (" (indicative, estimate only)" if c.indicative else "")
    return Stamp(project=" · ".join(x for x in (meta.project_code, meta.project_name) if x), revision=rev, design_date=meta.design_date,
                 rules=f"{d.rules_ref} ({d.rules_hash})", rates=rates, inputs=d.inputs_hash, status=status, fit=not reasons,
                 reasons=tuple(reasons), authority=meta.authority, engineer=engineer, number=meta.document_number)


def slug(text: str) -> str:
    return re.sub(r"[^a-z0-9]+", "-", text.lower()).strip("-") or "project"


def filename(meta: DocumentMeta, kind: str) -> str:
    short = {"drawing_dxf": "layout", "report_pdf": "report", "boq_xlsx": "boq", "boq_pdf": "boq", "load_schedule_xlsx": "load-schedule",
             "geojson": "network", "kml": "network", "shapefile_zip": "network-shp", "pack": "submission-pack"}[kind]
    ext = "zip" if kind == "pack" else KINDS[kind][2]
    return f"{slug(meta.project_code or meta.project_name)}_{short}_R{meta.revision_number}.{ext}"


class Projector:
    """Lon/lat to the South African Lo system nearest the design, in metres, for drawings."""

    def __init__(self, d: Design):
        pts = [n.coordinates for n in d.lv.network.nodes] + ([n.coordinates for n in d.mv_network.nodes] if d.mv_network else [])
        lon = sum(p[0] for p in pts) / len(pts) if pts else 27.0
        self.zone = crs_mod.nearest_lo(lon)
        self._fwd = crs_mod.from_wgs84(self.zone)

    def xy(self, lonlat) -> tuple[float, float]:
        x, y = self._fwd.transform(lonlat[0], lonlat[1])
        return (round(x, 3), round(y, 3))

    def line(self, coords) -> list[tuple[float, float]]:
        return [self.xy(c) for c in coords]


def traces(obj, path: str = "") -> list[tuple[str, Traced]]:
    """Every Traced record in a result, with where it sits, in a stable order."""
    out: list[tuple[str, Traced]] = []
    if isinstance(obj, Traced):
        return [(path, obj)]
    if isinstance(obj, BaseModel):
        for name in type(obj).model_fields:
            out += traces(getattr(obj, name), f"{path}.{name}" if path else name)
    elif isinstance(obj, (list, tuple)):
        for i, x in enumerate(obj):
            out += traces(x, f"{path}[{i}]")
    elif isinstance(obj, dict):
        for k, x in obj.items():
            out += traces(x, f"{path}.{k}")
    return out


def num(v, digits: int = 2) -> str:
    if v is None:
        return "–"
    if isinstance(v, bool):
        return "yes" if v else "no"
    if isinstance(v, (int, float)):
        if isinstance(v, float) and (math.isnan(v) or math.isinf(v)):
            return "–"
        return f"{v:,.{digits}f}".rstrip("0").rstrip(".") if isinstance(v, float) else f"{v:,}"
    return str(v)


def money(v: float | None, currency: str = "ZAR") -> str:
    return "–" if v is None else f"{currency} {v:,.2f}"


# ------------------------------------------------------------------ PDF text

FONT, FONT_BOLD = "RetVera", "RetVera-Bold"


@lru_cache(maxsize=1)
def pdf_fonts() -> frozenset[int]:
    """Registers the Bitstream Vera fonts bundled with reportlab and returns the characters they can show."""
    import reportlab
    from reportlab.pdfbase import pdfmetrics
    from reportlab.pdfbase.ttfonts import TTFont

    root = os.path.join(os.path.dirname(reportlab.__file__), "fonts")
    regular = TTFont(FONT, os.path.join(root, "Vera.ttf"))
    pdfmetrics.registerFont(regular)
    pdfmetrics.registerFont(TTFont(FONT_BOLD, os.path.join(root, "VeraBd.ttf")))
    pdfmetrics.registerFontFamily(FONT, normal=FONT, bold=FONT_BOLD, italic=FONT, boldItalic=FONT_BOLD)
    return frozenset(regular.face.charToGlyph)


_SPELL = {"ℓ": "l", "′": "'", "″": '"', "∑": "Σ"}


def pdf_text(text: str) -> str:
    """Text the PDF font can show: Greek letters it lacks are spelled out (φ → phi), anything else unknown becomes '?'."""
    have = pdf_fonts()
    out = []
    for ch in str(text):
        ch = _SPELL.get(ch, ch)
        if ord(ch) in have or ch in "\n\t":
            out.append(ch)
            continue
        name = unicodedata.name(ch, "")
        m = re.match(r"GREEK (SMALL|CAPITAL) LETTER (\w+)", name)
        if m:
            word = m.group(2).lower()
            out.append(word.capitalize() if m.group(1) == "CAPITAL" else word)
        else:
            out.append("?")
    return "".join(out)


def esc(text) -> str:
    """pdf_text, escaped for a reportlab Paragraph."""
    return pdf_text(str(text)).replace("&", "&amp;").replace("<", "&lt;").replace(">", "&gt;")
