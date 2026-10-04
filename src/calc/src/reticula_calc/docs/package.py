"""The design package the documents are made from (plan Phase 6), and what every document shares.

The API gathers the stored results (LV design per transformer site, MV design, bulk study, option search), the load
schedule, the connection point, the assumptions and the stamp, and sends them here. Documents only present these
results: nothing is recalculated, so a document always matches the design runs it names.
"""

from __future__ import annotations

import os
from functools import lru_cache
from pathlib import Path
from typing import Any, Literal

import yaml
from pydantic import BaseModel, Field
from pyproj import Transformer

from ..geo.crs import _proj_for, nearest_lo
from ..lv.costs import RatesRef

DocKind = Literal["drawings", "report", "boq_pdf", "boq_xlsx", "gis_geojson", "gis_kml", "gis_shp", "loads_xlsx", "registers", "submission_pack"]
ALL_KINDS: tuple[DocKind, ...] = ("drawings", "report", "boq_pdf", "boq_xlsx", "gis_geojson", "gis_kml", "gis_shp", "loads_xlsx", "registers",
                                  "submission_pack")


class Stamp(BaseModel):
    """Printed on every document (plan 6.8)."""

    rules: str
    rules_hash: str
    rate_list: str
    rate_date: str
    design_date: str
    revision: str
    generated_at: str
    engineer: str | None = None


class ProjectInfo(BaseModel):
    id: str
    name: str
    authority: str = "eskom"
    reference: str | None = None


class LvSite(BaseModel):
    site_id: str
    label: str
    run_id: str
    result: dict[str, Any]
    #: The construction to document; the first option that passes when not given.
    construction: str | None = None


class StoredRun(BaseModel):
    run_id: str
    result: dict[str, Any]


class LoadRow(BaseModel):
    building_id: str
    erf: str | None = None
    kind: str
    load_class: str | None = None
    kva: float | None = None
    phases: int = 1
    status: str | None = None
    overridden: bool = False
    override_reason: str | None = None
    lon: float
    lat: float


class AssumptionRow(BaseModel):
    text: str
    source: str | None = None
    status: str = "open"


class Stand(BaseModel):
    erf: str | None = None
    coordinates: list[list[tuple[float, float]]]


class DocumentPackage(BaseModel):
    project: ProjectInfo
    stamp: Stamp
    lv_designs: list[LvSite] = []
    mv_design: StoredRun | None = None
    bulk_study: StoredRun | None = None
    option_search: StoredRun | None = None
    loads: list[LoadRow] = []
    connection_point: dict[str, Any] | None = None
    assumptions: list[AssumptionRow] = []
    stands: list[Stand] = []
    rates: RatesRef = "indicative/2026-10"


class RenderRequest(BaseModel):
    package: DocumentPackage
    kinds: list[DocKind] = Field(default=list(ALL_KINDS), min_length=1)


class RenderedFile(BaseModel):
    kind: DocKind
    name: str
    title: str
    content_type: str
    size: int
    sha256: str
    data_b64: str


class ChecklistItem(BaseModel):
    id: str
    text: str
    status: Literal["met", "not met", "manual"]
    detail: str = ""


class RenderResult(BaseModel):
    files: list[RenderedFile]
    checklist: list[ChecklistItem]
    warnings: list[str]


def templates_dir() -> Path:
    env = os.environ.get("RETICULA_TEMPLATES_DIR")
    return Path(env) if env else Path(__file__).resolve().parents[5] / "templates"


@lru_cache(maxsize=8)
def load_template(authority: str) -> dict:
    path = templates_dir() / f"{authority}.yaml"
    if not path.is_file():
        path = templates_dir() / "eskom.yaml"
    return yaml.safe_load(path.read_text(encoding="utf-8"))


def chosen_option(site: LvSite) -> dict | None:
    """The LV option documented for a site: the requested construction, else the first that passes, else the first."""
    options = site.result.get("options", [])
    if site.construction:
        hit = next((o for o in options if o["construction"] == site.construction), None)
        if hit:
            return hit
    return next((o for o in options if o.get("passed")), options[0] if options else None)


def all_points(pkg: DocumentPackage) -> list[tuple[float, float]]:
    pts: list[tuple[float, float]] = []
    for s in pkg.lv_designs:
        o = chosen_option(s)
        if o:
            pts += [(n["lon"], n["lat"]) for n in o["network"]["nodes"]]
    if pkg.mv_design and pkg.mv_design.result.get("mv_network"):
        pts += [(n["lon"], n["lat"]) for n in pkg.mv_design.result["mv_network"]["nodes"]]
    pts += [(r.lon, r.lat) for r in pkg.loads]
    return pts


class Projection:
    """WGS84 to the drawing's metric system (the template's `crs`; LO-auto picks the Lo zone nearest the project)."""

    def __init__(self, pkg: DocumentPackage, spec: str):
        pts = all_points(pkg)
        if spec.upper() == "LO-AUTO":
            spec = nearest_lo(sum(p[0] for p in pts) / len(pts) if pts else 29.0)
        self.spec = spec.upper()
        self._t = Transformer.from_crs("EPSG:4326", _proj_for(self.spec), always_xy=True)

    def xy(self, lon: float, lat: float) -> tuple[float, float]:
        x, y = self._t.transform(lon, lat)
        return float(x), float(y)


def stamp_lines(pkg: DocumentPackage) -> list[str]:
    s = pkg.stamp
    return [f"Rules {s.rules} ({s.rules_hash})", f"Rates {s.rate_list}, rate date {s.rate_date}", f"Design date {s.design_date}",
            f"Revision {s.revision}", f"Generated {s.generated_at} by Reticula"]


def stamp_text(pkg: DocumentPackage) -> str:
    return " · ".join(stamp_lines(pkg))


def unverified(pkg: DocumentPackage) -> list[str]:
    out: set[str] = set()
    for s in pkg.lv_designs:
        out.update(s.result.get("unverified", []))
    for run in (pkg.mv_design, pkg.bulk_study, pkg.option_search):
        if run:
            out.update(run.result.get("unverified", []))
    return sorted(out)
