"""Parse planner and map files into WGS84 features, with early validation.

Formats: KML/KMZ, GeoJSON, DXF (closed polylines with text labels), Overpass JSON (OSM `out geom`).
Kinds:   stands (polygons with erf numbers) and buildings (footprint polygons with OSM tags).
"""

from __future__ import annotations

import io
import json
import re
import zipfile
from collections import Counter
from dataclasses import dataclass, field
from typing import Any, Literal
from xml.etree import ElementTree as ET

import ezdxf
from ezdxf import recover
from ezdxf.path import make_path
from pydantic import BaseModel
from pyproj import Geod
from shapely import STRtree, make_valid
from shapely.geometry import MultiPolygon, Point, Polygon, mapping, shape
from shapely.geometry.base import BaseGeometry
from shapely.ops import transform

from . import crs as crs_mod

Kind = Literal["stands", "buildings"]
Format = Literal["kml", "kmz", "geojson", "dxf", "overpass"]
GEOD = Geod(ellps="WGS84")

ERF_KEYS = ("erf", "erf_no", "erfno", "erf_number", "stand", "stand_no", "standno", "stand_number", "name", "label")
ZONING_KEYS = ("zoning", "zone", "land_use", "landuse", "use_zone")
MAX_ISSUE_SAMPLES = 10


class Issue(BaseModel):
    severity: Literal["error", "warning"]
    code: str
    message: str
    count: int = 1
    samples: list[str] = []


class LayerInfo(BaseModel):
    name: str
    closed_polylines: int = 0
    open_polylines: int = 0
    texts: int = 0


class ImportedFeature(BaseModel):
    ref: str
    geometry: dict[str, Any]
    area_m2: float
    erf: str | None = None
    zoning: str | None = None
    osm_id: str | None = None
    tags: dict[str, str] = {}
    attributes: dict[str, Any] = {}


class ImportResult(BaseModel):
    kind: Kind
    format: Format
    source_crs: str | None
    crs_reason: str
    features: list[ImportedFeature]
    issues: list[Issue]
    layers: list[LayerInfo] = []


class UnreadableFileError(ValueError):
    """The file cannot be imported at all (unreadable, unsupported, no CRS)."""


@dataclass
class _Raw:
    ref: str
    geom: BaseGeometry
    props: dict[str, Any] = field(default_factory=dict)


def detect_format(filename: str, data: bytes) -> Format:
    name = filename.lower()
    if name.endswith(".kmz"):
        return "kmz"
    if name.endswith(".kml"):
        return "kml"
    if name.endswith(".dxf"):
        return "dxf"
    if name.endswith((".geojson", ".json")):
        try:
            doc = json.loads(data)
        except ValueError as e:
            raise UnreadableFileError(f"{filename} is not valid JSON: {e}") from e
        return "overpass" if isinstance(doc, dict) and "elements" in doc else "geojson"
    raise UnreadableFileError(f"Unsupported file type for {filename}; use KML, KMZ, GeoJSON, Overpass JSON or DXF")


# ---------- parsers (source coordinates) ----------

def _local(tag: str) -> str:
    return tag.rsplit("}", 1)[-1]


def _kml_coords(text: str | None) -> list[tuple[float, float]]:
    pts = []
    for token in (text or "").split():
        parts = token.split(",")
        if len(parts) >= 2:
            pts.append((float(parts[0]), float(parts[1])))
    return pts


def _parse_kml(data: bytes) -> list[_Raw]:
    try:
        root = ET.fromstring(data)
    except ET.ParseError as e:
        raise UnreadableFileError(f"KML is not valid XML: {e}") from e
    out: list[_Raw] = []
    for i, pm in enumerate(el for el in root.iter() if _local(el.tag) == "Placemark"):
        props: dict[str, Any] = {}
        for child in pm:
            if _local(child.tag) == "name" and child.text:
                props["name"] = child.text.strip()
        for d in (el for el in pm.iter() if _local(el.tag) in ("Data", "SimpleData")):
            key = d.get("name")
            if not key:
                continue
            val = next((c.text for c in d if _local(c.tag) == "value"), None) if _local(d.tag) == "Data" else d.text
            if val is not None:
                props[key] = val.strip()
        for j, poly in enumerate(el for el in pm.iter() if _local(el.tag) == "Polygon"):
            rings: dict[str, list] = {"outer": [], "inner": []}
            for b in poly:
                which = "outer" if _local(b.tag) == "outerBoundaryIs" else "inner" if _local(b.tag) == "innerBoundaryIs" else None
                if which:
                    for c in b.iter():
                        if _local(c.tag) == "coordinates":
                            rings[which].append(_kml_coords(c.text))
            if rings["outer"] and len(rings["outer"][0]) >= 3:
                out.append(_Raw(f"placemark-{i + 1}" + (f".{j + 1}" if j else ""), Polygon(rings["outer"][0], rings["inner"]), props))
    return out


def _parse_kmz(data: bytes) -> list[_Raw]:
    try:
        with zipfile.ZipFile(io.BytesIO(data)) as z:
            name = next((n for n in z.namelist() if n.lower().endswith(".kml")), None)
            if name is None:
                raise UnreadableFileError("KMZ contains no KML document")
            return _parse_kml(z.read(name))
    except zipfile.BadZipFile as e:
        raise UnreadableFileError("KMZ is not a valid zip archive") from e


def _parse_geojson(data: bytes) -> list[_Raw]:
    doc = json.loads(data)
    feats = doc.get("features", []) if doc.get("type") == "FeatureCollection" else [doc] if doc.get("type") == "Feature" else []
    out: list[_Raw] = []
    for i, f in enumerate(feats):
        if not f.get("geometry"):
            continue
        geom = shape(f["geometry"])
        props = dict(f.get("properties") or {})
        ref = str(f.get("id") or props.get("@id") or f"feature-{i + 1}")
        for j, part in enumerate(geom.geoms if isinstance(geom, MultiPolygon) else [geom]):
            out.append(_Raw(ref if j == 0 else f"{ref}.{j + 1}", part, props))
    return out


def _parse_overpass(data: bytes) -> tuple[list[_Raw], list[Issue]]:
    doc = json.loads(data)
    out: list[_Raw] = []
    skipped = 0
    for el in doc.get("elements", []):
        geometry = el.get("geometry")
        if el.get("type") != "way" or not geometry:
            if el.get("type") == "relation":
                skipped += 1
            continue
        pts = [(p["lon"], p["lat"]) for p in geometry]
        if len(pts) < 4 or pts[0] != pts[-1]:
            continue
        tags = {k: str(v) for k, v in (el.get("tags") or {}).items()}
        out.append(_Raw(f"way/{el['id']}", Polygon(pts), {"@id": f"way/{el['id']}", **tags}))
    issues = []
    if skipped:
        issues.append(Issue(severity="warning", code="osm_relations_skipped",
                            message="Multipolygon relations are not imported yet; large buildings drawn as relations are missing.",
                            count=skipped))
    return out, issues


def _parse_dxf(data: bytes, layer: str | None) -> tuple[list[_Raw], list[LayerInfo], list[Issue]]:
    try:
        doc, _auditor = recover.read(io.BytesIO(data))
    except (OSError, ezdxf.DXFStructureError) as e:
        raise UnreadableFileError(f"DXF cannot be read: {e}") from e
    msp = doc.modelspace()
    layers: dict[str, LayerInfo] = {}
    polys: list[_Raw] = []
    texts: list[tuple[Point, str]] = []
    open_on_layer = 0

    for e in msp:
        name = e.dxf.layer
        info = layers.setdefault(name, LayerInfo(name=name))
        kind = e.dxftype()
        if kind in ("TEXT", "MTEXT"):
            info.texts += 1
            if layer is None or name == layer:
                content = e.plain_text() if kind == "MTEXT" else e.dxf.text
                texts.append((Point(e.dxf.insert.x, e.dxf.insert.y), content.strip()))
        elif kind in ("LWPOLYLINE", "POLYLINE"):
            closed = bool(e.closed if kind == "LWPOLYLINE" else e.is_closed)
            if closed:
                info.closed_polylines += 1
            else:
                info.open_polylines += 1
            if layer is not None and name != layer:
                continue
            if not closed:
                open_on_layer += 1
                continue
            # Flatten arcs (bulges) to 5 cm.
            pts = [(v.x, v.y) for v in make_path(e).flattening(0.05)]
            if len(pts) >= 3:
                polys.append(_Raw(f"{kind.lower()}-{e.dxf.handle}", Polygon(pts), {"layer": name}))

    issues: list[Issue] = []
    if open_on_layer:
        issues.append(Issue(severity="warning", code="open_polylines",
                            message="Open polylines were skipped; only closed polylines become stands.", count=open_on_layer))

    # Label each polygon with the text inside it, preferring numeric labels (erf numbers).
    if polys and texts:
        tree = STRtree([p.geom for p in polys])
        for pt, content in texts:
            for idx in tree.query(pt, predicate="within"):
                labels = polys[int(idx)].props.setdefault("_labels", [])
                labels.append(content)
        multi = 0
        for p in polys:
            labels = p.props.pop("_labels", [])
            if labels:
                numeric = [t for t in labels if re.fullmatch(r"\d+[A-Za-z]?", t)]
                p.props["label"] = (numeric or labels)[0]
                if len(numeric) > 1:
                    multi += 1
        if multi:
            issues.append(Issue(severity="warning", code="multiple_labels",
                                message="Some stands contain more than one number; the first was used.", count=multi))
    return polys, sorted(layers.values(), key=lambda li: -li.closed_polylines), issues


# ---------- normalise ----------

def _first(props: dict[str, Any], keys: tuple[str, ...]) -> str | None:
    lower = {k.lower(): v for k, v in props.items()}
    for k in keys:
        v = lower.get(k)
        if v not in (None, ""):
            return str(v).strip()
    return None


def _area_m2(geom: BaseGeometry) -> float:
    return abs(GEOD.geometry_area_perimeter(geom)[0])


def _sample(items: list[str]) -> list[str]:
    return items[:MAX_ISSUE_SAMPLES]


def import_file(
    filename: str,
    data: bytes,
    kind: Kind,
    source_crs: str | None = None,
    layer: str | None = None,
    area: dict[str, Any] | None = None,
) -> ImportResult:
    fmt = detect_format(filename, data)
    issues: list[Issue] = []
    layers: list[LayerInfo] = []

    if fmt == "kml":
        raws = _parse_kml(data)
    elif fmt == "kmz":
        raws = _parse_kmz(data)
    elif fmt == "geojson":
        raws = _parse_geojson(data)
    elif fmt == "overpass":
        raws, more = _parse_overpass(data)
        issues += more
    else:
        raws, layers, more = _parse_dxf(data, layer)
        issues += more

    area_geom = shape(area) if area else None
    if fmt in ("kml", "kmz", "overpass"):
        spec, reason = "WGS84", f"{fmt.upper()} is always longitude/latitude"
    elif source_crs:
        spec, reason = crs_mod.normalise(source_crs), "chosen by the user"
    else:
        xs = [x for r in raws for x, _ in r.geom.exterior.coords]
        ys = [y for r in raws for _, y in r.geom.exterior.coords]
        det = crs_mod.detect(xs, ys, area_geom.centroid.x if area_geom else None)
        spec, reason = det.spec, det.reason

    if raws and spec is None:
        return ImportResult(kind=kind, format=fmt, source_crs=None, crs_reason=reason, features=[], layers=layers,
                            issues=[*issues, Issue(severity="error", code="crs_unknown",
                                                   message=f"Cannot tell the coordinate system: {reason}.")])
    if not raws:
        hint = " Choose the layer that holds the stand outlines." if fmt == "dxf" else ""
        issues.append(Issue(severity="error", code="no_features", message=f"No polygons were found in the file.{hint}"))

    tf = crs_mod.to_wgs84(spec) if spec else None
    features: list[ImportedFeature] = []
    invalid_fixed: list[str] = []
    invalid_dropped: list[str] = []
    outside: list[str] = []

    for r in raws:
        geom = transform(tf.transform, r.geom) if tf and spec != "WGS84" else r.geom
        if not geom.is_valid:
            fixed = make_valid(geom)
            polys = [g for g in getattr(fixed, "geoms", [fixed]) if isinstance(g, Polygon)]
            if not polys:
                invalid_dropped.append(r.ref)
                continue
            geom = max(polys, key=lambda g: g.area)
            invalid_fixed.append(r.ref)
        if area_geom is not None and not area_geom.intersects(geom):
            outside.append(r.ref)
        tags = {k: str(v) for k, v in r.props.items() if fmt in ("overpass", "geojson") and not k.startswith("@")}
        features.append(ImportedFeature(
            ref=r.ref,
            geometry=mapping(geom),
            area_m2=round(_area_m2(geom), 2),
            erf=_first(r.props, ERF_KEYS) if kind == "stands" else None,
            zoning=_first(r.props, ZONING_KEYS),
            osm_id=str(r.props["@id"]) if "@id" in r.props else None,
            tags=tags if kind == "buildings" else {},
            attributes={k: v for k, v in r.props.items() if isinstance(v, (str, int, float, bool))},
        ))

    if invalid_fixed:
        issues.append(Issue(severity="warning", code="geometry_repaired",
                            message="Some outlines crossed themselves and were repaired.", count=len(invalid_fixed), samples=_sample(invalid_fixed)))
    if invalid_dropped:
        issues.append(Issue(severity="warning", code="geometry_dropped",
                            message="Some outlines could not be repaired and were skipped.", count=len(invalid_dropped), samples=_sample(invalid_dropped)))
    if outside:
        sev: Literal["error", "warning"] = "error" if len(outside) == len(features) else "warning"
        msg = ("No features fall inside the project area. Check the coordinate system."
               if sev == "error" else "Some features lie outside the project area.")
        issues.append(Issue(severity=sev, code="outside_area", message=msg, count=len(outside), samples=_sample(outside)))
    if kind == "stands" and features:
        missing = [f.ref for f in features if not f.erf]
        if missing:
            issues.append(Issue(severity="warning", code="erf_missing",
                                message="Some stands have no erf number.", count=len(missing), samples=_sample(missing)))
        dupes = [erf for erf, n in Counter(f.erf for f in features if f.erf).items() if n > 1]
        if dupes:
            issues.append(Issue(severity="warning", code="erf_duplicate",
                                message="Some erf numbers appear more than once.", count=len(dupes), samples=_sample(sorted(dupes))))
    tiny = [f.ref for f in features if f.area_m2 < (20 if kind == "stands" else 2)]
    if tiny:
        issues.append(Issue(severity="warning", code="tiny_features",
                            message="Some outlines are implausibly small; check the drawing units.", count=len(tiny), samples=_sample(tiny)))

    return ImportResult(kind=kind, format=fmt, source_crs=spec, crs_reason=reason, features=features, issues=issues, layers=layers)
