"""Parse planner, survey, authority and map files into WGS84 features, with early validation.

Formats: KML/KMZ, GeoJSON, DXF, Overpass JSON (OSM `out geom`), zipped shapefiles, and CSV point lists.
Kinds:   stands (polygons with erf numbers), buildings (footprint polygons with OSM tags), roads (lines with a
         name and class), contours (lines with an elevation) and network (the authority's existing assets: points
         and lines with a type and the fields each type needs).
"""

from __future__ import annotations

import csv
import io
import json
import re
import zipfile
from collections import Counter
from dataclasses import dataclass, field
from typing import Any, Literal
from xml.etree import ElementTree as ET

import ezdxf
import shapefile
from ezdxf import recover
from ezdxf.path import make_path
from pydantic import BaseModel
from pyproj import CRS, Geod, Transformer
from pyproj.exceptions import CRSError
from shapely import STRtree, force_2d, get_coordinates, make_valid
from shapely.geometry import (
    LineString,
    MultiLineString,
    MultiPoint,
    MultiPolygon,
    Point,
    Polygon,
    mapping,
    shape,
)
from shapely.geometry.base import BaseGeometry
from shapely.ops import transform

from . import crs as crs_mod

Kind = Literal["stands", "buildings", "roads", "contours", "network"]
Format = Literal["kml", "kmz", "geojson", "dxf", "overpass", "shapefile", "csv"]
GEOD = Geod(ellps="WGS84")

POLYGON_KINDS = ("stands", "buildings")
LINE_KINDS = ("roads", "contours")

ERF_KEYS = ("erf", "erf_no", "erfno", "erf_number", "stand", "stand_no", "standno", "stand_number", "name", "label")
ZONING_KEYS = ("zoning", "zone", "land_use", "landuse", "use_zone")
ROAD_NAME_KEYS = ("name", "street", "street_name", "streetname", "road_name", "roadname", "str_name", "label")
ROAD_CLASS_KEYS = ("highway", "road_class", "class", "type", "category", "fclass")
ELEVATION_KEYS = ("elevation", "elev", "height", "contour", "z", "level", "altitude", "alt", "elevation_m")
ASSET_TYPE_KEYS = ("asset_type", "assettype", "type", "asset", "kind", "class", "feature", "description", "power")
LABEL_KEYS = ("label", "name", "asset_id", "id", "number", "ref", "tag")
VOLTAGE_KEYS = ("voltage_kv", "voltage", "kv", "volt", "volts", "nominal_voltage")
RATING_KEYS = ("rating_kva", "kva", "rating", "size_kva", "size", "capacity_kva", "transformer_kva")
CAPACITY_KEYS = ("capacity_kva", "available_kva", "capacity", "spare_kva", "nmd_kva")
FAULT_KEYS = ("fault_level_ka", "fault_ka", "fault_level", "fault_current_ka", "ik_ka")
LON_COLUMNS = ("lon", "long", "longitude", "lng", "x", "easting", "y_lo")
LAT_COLUMNS = ("lat", "latitude", "y", "northing", "x_lo")
MAX_ISSUE_SAMPLES = 10

# Existing network asset types, matched against the type field (or DXF layer and block names), first match wins.
# Lines are told apart by voltage words. The fields each type must have so later design steps can use it:
# connection points carry what the spec says the app must never guess (capacity and fault level).
ASSET_PATTERNS: tuple[tuple[str, str], ...] = (
    ("connection_point", r"connection|point of supply|supply point|\bpos\b|\bpoc\b|bulk"),
    ("minisub", r"mini.?sub|\bmss\b|\bmsub\b"),
    ("substation", r"substation|\bsub\b|\bs/s\b"),
    ("transformer", r"transformer|\btrf\b|\btx\b|xfmr|pole.?mount"),
    ("switchgear", r"\brmu\b|ring main|switch|breaker|isolator|recloser"),
    ("pole", r"\bpole"),
    ("mv_cable", r"(mv|11\s*kv|22\s*kv|33\s*kv|medium).*cable|cable.*(mv|11\s*kv|22\s*kv|33\s*kv|medium)"),
    ("lv_cable", r"(lv|400\s*v|low).*cable|cable.*(lv|400\s*v|low)|service"),
    ("mv_line", r"\bmv\b|11\s*kv|22\s*kv|33\s*kv|medium voltage|mv.?line|overhead mv"),
    ("lv_line", r"\blv\b|400\s*v|low voltage|lv.?line|abc|bundle"),
)
LINE_ASSETS = ("mv_line", "lv_line", "mv_cable", "lv_cable")
REQUIRED_FIELDS: dict[str, tuple[str, ...]] = {
    "transformer": ("rating_kva",),
    "minisub": ("rating_kva",),
    "substation": ("rating_kva",),
    "mv_line": ("voltage_kv",),
    "mv_cable": ("voltage_kv",),
    "lv_line": ("voltage_kv",),
    "lv_cable": ("voltage_kv",),
    "connection_point": ("voltage_kv", "capacity_kva", "fault_level_ka"),
}
DEFAULT_KV = {"lv_line": 0.4, "lv_cable": 0.4}


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
    lines: int = 0
    points: int = 0


class ImportedFeature(BaseModel):
    ref: str
    geometry: dict[str, Any]
    area_m2: float = 0
    length_m: float = 0
    erf: str | None = None
    zoning: str | None = None
    osm_id: str | None = None
    tags: dict[str, str] = {}
    attributes: dict[str, Any] = {}
    # Roads: street name and class. Network: asset label and type.
    name: str | None = None
    category: str | None = None
    elevation_m: float | None = None
    voltage_kv: float | None = None
    rating_kva: float | None = None
    capacity_kva: float | None = None
    fault_level_ka: float | None = None
    # Network fields the asset's type needs but the file did not give.
    missing: list[str] = []


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
    if name.endswith(".csv"):
        return "csv"
    if name.endswith(".zip"):
        return "shapefile"
    if name.endswith((".geojson", ".json")):
        try:
            doc = json.loads(data)
        except ValueError as e:
            raise UnreadableFileError(f"{filename} is not valid JSON: {e}") from e
        return "overpass" if isinstance(doc, dict) and "elements" in doc else "geojson"
    if name.endswith(".shp"):
        raise UnreadableFileError("Zip the shapefile with its .dbf, .shx and .prj files and import the zip")
    raise UnreadableFileError(
        f"Unsupported file type for {filename}; use KML, KMZ, GeoJSON, Overpass JSON, DXF, a zipped shapefile or CSV"
    )


# ---------- parsers (source coordinates) ----------


def _local(tag: str) -> str:
    return tag.rsplit("}", 1)[-1]


def _kml_coords(text: str | None) -> list[tuple[float, ...]]:
    pts = []
    for token in (text or "").split():
        parts = token.split(",")
        if len(parts) >= 2:
            pts.append(tuple(float(p) for p in parts[:3]))
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
        geoms: list[BaseGeometry] = []
        for el in pm.iter():
            tag = _local(el.tag)
            if tag == "Polygon":
                rings: dict[str, list] = {"outer": [], "inner": []}
                for b in el:
                    which = {"outerBoundaryIs": "outer", "innerBoundaryIs": "inner"}.get(_local(b.tag))
                    if which:
                        rings[which] += [_kml_coords(c.text) for c in b.iter() if _local(c.tag) == "coordinates"]
                if rings["outer"] and len(rings["outer"][0]) >= 3:
                    geoms.append(Polygon(rings["outer"][0], rings["inner"]))
            elif tag in ("LineString", "Point"):
                coords = next((_kml_coords(c.text) for c in el if _local(c.tag) == "coordinates"), [])
                if tag == "LineString" and len(coords) >= 2:
                    geoms.append(LineString(coords))
                elif tag == "Point" and coords:
                    geoms.append(Point(coords[0]))
        for j, g in enumerate(geoms):
            out.append(_Raw(f"placemark-{i + 1}" + (f".{j + 1}" if j else ""), g, props))
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


def _parts(geom: BaseGeometry) -> list[BaseGeometry]:
    return list(geom.geoms) if isinstance(geom, (MultiPolygon, MultiLineString, MultiPoint)) else [geom]


def _parse_geojson(data: bytes) -> list[_Raw]:
    doc = json.loads(data)
    feats = doc.get("features", []) if doc.get("type") == "FeatureCollection" else [doc] if doc.get("type") == "Feature" else []
    out: list[_Raw] = []
    for i, f in enumerate(feats):
        if not f.get("geometry"):
            continue
        props = dict(f.get("properties") or {})
        ref = str(f.get("id") or props.get("@id") or f"feature-{i + 1}")
        for j, part in enumerate(_parts(shape(f["geometry"]))):
            out.append(_Raw(ref if j == 0 else f"{ref}.{j + 1}", part, props))
    return out


def _parse_overpass(data: bytes, kind: Kind) -> tuple[list[_Raw], list[Issue]]:
    doc = json.loads(data)
    out: list[_Raw] = []
    skipped = 0
    for el in doc.get("elements", []):
        tags = {k: str(v) for k, v in (el.get("tags") or {}).items()}
        if el.get("type") == "node" and "lon" in el:
            out.append(_Raw(f"node/{el['id']}", Point(el["lon"], el["lat"]), {"@id": f"node/{el['id']}", **tags}))
            continue
        geometry = el.get("geometry")
        if el.get("type") != "way" or not geometry:
            if el.get("type") == "relation":
                skipped += 1
            continue
        pts = [(p["lon"], p["lat"]) for p in geometry]
        closed = len(pts) >= 4 and pts[0] == pts[-1]
        if kind in POLYGON_KINDS:
            if not closed:
                continue
            geom: BaseGeometry = Polygon(pts)
        elif len(pts) >= 2:
            geom = LineString(pts)
        else:
            continue
        out.append(_Raw(f"way/{el['id']}", geom, {"@id": f"way/{el['id']}", **tags}))
    issues = []
    if skipped and kind == "buildings":
        issues.append(Issue(severity="warning", code="osm_relations_skipped",
                            message="Multipolygon relations are not imported yet; large buildings drawn as relations are missing.",
                            count=skipped))
    return out, issues


def _parse_dxf(data: bytes, layer: str | None, kind: Kind) -> tuple[list[_Raw], list[LayerInfo], list[Issue]]:
    try:
        doc, _auditor = recover.read(io.BytesIO(data))
    except (OSError, ezdxf.DXFStructureError) as e:
        raise UnreadableFileError(f"DXF cannot be read: {e}") from e
    msp = doc.modelspace()
    layers: dict[str, LayerInfo] = {}
    raws: list[_Raw] = []
    texts: list[tuple[Point, str]] = []
    open_on_layer = 0
    polygons = kind in POLYGON_KINDS

    for e in msp:
        name = e.dxf.layer
        info = layers.setdefault(name, LayerInfo(name=name))
        etype = e.dxftype()
        wanted = layer is None or name == layer
        ref = f"{etype.lower()}-{e.dxf.handle}"
        if etype in ("TEXT", "MTEXT"):
            info.texts += 1
            if wanted:
                content = e.plain_text() if etype == "MTEXT" else e.dxf.text
                texts.append((Point(e.dxf.insert.x, e.dxf.insert.y), content.strip()))
        elif etype in ("LWPOLYLINE", "POLYLINE"):
            closed = bool(e.closed if etype == "LWPOLYLINE" else e.is_closed)
            if closed:
                info.closed_polylines += 1
            else:
                info.open_polylines += 1
            if not wanted:
                continue
            # Flatten arcs (bulges) to 5 cm.
            pts = [(v.x, v.y) for v in make_path(e).flattening(0.05)]
            if etype == "LWPOLYLINE":
                z = e.dxf.elevation
            else:
                z = e.vertices[0].dxf.location.z if len(e.vertices) else None
            props = {"layer": name, "_z": z}
            if polygons:
                if not closed:
                    open_on_layer += 1
                elif len(pts) >= 3:
                    raws.append(_Raw(ref, Polygon(pts), props))
            elif len(pts) >= 2:
                raws.append(_Raw(ref, LineString(pts), props))
        elif etype == "LINE":
            info.lines += 1
            if wanted and not polygons:
                s, t = e.dxf.start, e.dxf.end
                raws.append(_Raw(ref, LineString([(s.x, s.y), (t.x, t.y)]), {"layer": name, "_z": s.z}))
        elif etype in ("POINT", "INSERT", "CIRCLE"):
            info.points += 1
            if wanted and kind == "network":
                at = e.dxf.location if etype == "POINT" else e.dxf.center if etype == "CIRCLE" else e.dxf.insert
                block = e.dxf.name if etype == "INSERT" else None
                raws.append(_Raw(ref, Point(at.x, at.y), {"layer": name, **({"block": block} if block else {})}))

    issues: list[Issue] = []
    if open_on_layer:
        issues.append(Issue(severity="warning", code="open_polylines",
                            message="Open polylines were skipped; only closed polylines become outlines.", count=open_on_layer))

    # Label each outline (or asset) with the text inside or nearest it, preferring numeric labels (erf numbers).
    if raws and texts and kind in ("stands", "network"):
        tree = STRtree([r.geom for r in raws])
        for pt, content in texts:
            hits = tree.query(pt, predicate="within") if polygons else [tree.nearest(pt)]
            for idx in hits:
                if polygons or raws[int(idx)].geom.distance(pt) < 5:
                    raws[int(idx)].props.setdefault("_labels", []).append(content)
        multi = 0
        for r in raws:
            labels = r.props.pop("_labels", [])
            if labels:
                numeric = [t for t in labels if re.fullmatch(r"\d+[A-Za-z]?", t)]
                r.props["label"] = (numeric or labels)[0]
                if len(numeric) > 1:
                    multi += 1
        if multi and kind == "stands":
            issues.append(Issue(severity="warning", code="multiple_labels",
                                message="Some stands contain more than one number; the first was used.", count=multi))
    order = (lambda li: -li.closed_polylines) if polygons else (lambda li: -(li.open_polylines + li.lines + li.points + li.closed_polylines))
    return raws, sorted(layers.values(), key=order), issues


def _parse_shapefile(data: bytes) -> tuple[list[_Raw], str | None]:
    """A zipped shapefile: .shp and .dbf (and .shx, .prj). Returns the features and the .prj text."""
    try:
        z = zipfile.ZipFile(io.BytesIO(data))
    except zipfile.BadZipFile as e:
        raise UnreadableFileError("The shapefile zip is not a valid zip archive") from e
    with z:
        names = {n.lower(): n for n in z.namelist() if not n.startswith("__MACOSX")}
        shp = next((n for n in names if n.endswith(".shp")), None)
        if shp is None:
            raise UnreadableFileError("The zip contains no .shp file")
        stem = shp[:-4]

        def part(ext: str) -> io.BytesIO | None:
            n = names.get(stem + ext)
            return io.BytesIO(z.read(n)) if n else None

        if part(".dbf") is None:
            raise UnreadableFileError("The shapefile has no .dbf file; zip all its parts together")
        prj = names.get(stem + ".prj")
        prj_text = z.read(prj).decode("utf-8", "replace") if prj else None
        try:
            reader = shapefile.Reader(shp=io.BytesIO(z.read(names[shp])), dbf=part(".dbf"), shx=part(".shx"))
        except shapefile.ShapefileException as e:
            raise UnreadableFileError(f"The shapefile cannot be read: {e}") from e
        out: list[_Raw] = []
        for i, rec in enumerate(reader.iterShapeRecords()):
            gi = rec.shape.__geo_interface__
            if not gi or not gi.get("coordinates"):
                continue
            props = {k: (v.isoformat() if hasattr(v, "isoformat") else v) for k, v in rec.record.as_dict().items()}
            for j, g in enumerate(_parts(shape(gi))):
                out.append(_Raw(f"shape-{i + 1}" + (f".{j + 1}" if j else ""), g, props))
        return out, prj_text


def _parse_csv(data: bytes) -> list[_Raw]:
    text = data.decode("utf-8-sig", "replace")
    try:
        dialect = csv.Sniffer().sniff(text[:4096], delimiters=",;\t")
    except csv.Error:
        dialect = csv.excel
    rows = list(csv.DictReader(io.StringIO(text), dialect=dialect))
    if not rows:
        raise UnreadableFileError("The CSV has no rows")
    header = {h.strip().lower(): h for h in rows[0] if h}
    x_col = next((header[c] for c in LON_COLUMNS if c in header), None)
    y_col = next((header[c] for c in LAT_COLUMNS if c in header), None)
    if x_col is None or y_col is None:
        raise UnreadableFileError("The CSV needs coordinate columns: lon and lat (or x and y)")
    out: list[_Raw] = []
    for i, row in enumerate(rows):
        try:
            x, y = float(row[x_col].replace(",", ".")), float(row[y_col].replace(",", "."))
        except (TypeError, ValueError, AttributeError):
            continue
        props = {k.strip(): v.strip() for k, v in row.items() if k and k not in (x_col, y_col) and isinstance(v, str) and v.strip()}
        out.append(_Raw(f"row-{i + 2}", Point(x, y), props))
    return out


# ---------- normalise ----------


def _first(props: dict[str, Any], keys: tuple[str, ...]) -> str | None:
    lower = {k.lower(): v for k, v in props.items()}
    for k in keys:
        v = lower.get(k)
        if v not in (None, ""):
            return str(v).strip()
    return None


def _number(props: dict[str, Any], keys: tuple[str, ...]) -> float | None:
    text = _first(props, keys)
    if text is None:
        return None
    m = re.search(r"-?\d+(?:[.,]\d+)?", text)
    return float(m.group().replace(",", ".")) if m else None


def _area_m2(geom: BaseGeometry) -> float:
    return abs(GEOD.geometry_area_perimeter(geom)[0])


def _length_m(geom: BaseGeometry) -> float:
    return GEOD.geometry_length(geom)


def _sample(items: list[str]) -> list[str]:
    return items[:MAX_ISSUE_SAMPLES]


def asset_type(props: dict[str, Any], is_line: bool) -> str | None:
    """The asset's type from its type field, or from the DXF layer and block names."""
    words = " ".join(str(v) for v in (_first(props, ASSET_TYPE_KEYS), props.get("layer"), props.get("block")) if v).lower()
    if not words:
        return None
    for kind, pattern in ASSET_PATTERNS:
        if re.search(pattern, words) and (kind in LINE_ASSETS) == is_line:
            return kind
    return None


def _kv(props: dict[str, Any], kind: str | None) -> float | None:
    v = _number(props, VOLTAGE_KEYS)
    if v is None:
        words = " ".join(str(x) for x in (_first(props, ASSET_TYPE_KEYS), props.get("layer")) if x).lower()
        m = re.search(r"(\d+(?:\.\d+)?)\s*kv", words)
        return float(m.group(1)) if m else DEFAULT_KV.get(kind or "")
    return v / 1000 if v >= 1000 else v  # volts written as 11000 or 400


def _crs_from_prj(prj: str) -> tuple[Transformer | None, str | None, str]:
    try:
        src = CRS.from_wkt(prj)
    except CRSError:
        return None, None, "the .prj file could not be read"
    code = src.to_epsg()
    label = "WGS84" if code == 4326 else f"EPSG:{code}" if code else "PRJ"
    return Transformer.from_crs(src, "EPSG:4326", always_xy=True), label, f"from the shapefile's .prj ({src.name})"


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
    prj: str | None = None

    if fmt == "kml":
        raws = _parse_kml(data)
    elif fmt == "kmz":
        raws = _parse_kmz(data)
    elif fmt == "geojson":
        raws = _parse_geojson(data)
    elif fmt == "overpass":
        raws, more = _parse_overpass(data, kind)
        issues += more
    elif fmt == "shapefile":
        raws, prj = _parse_shapefile(data)
    elif fmt == "csv":
        raws = _parse_csv(data)
    else:
        raws, layers, more = _parse_dxf(data, layer, kind)
        issues += more

    raws, skipped = _keep_for_kind(raws, kind)
    if skipped:
        want = {"stands": "outlines", "buildings": "outlines", "roads": "lines", "contours": "lines", "network": "points and lines"}[kind]
        issues.append(Issue(severity="warning", code="wrong_geometry",
                            message=f"Features of the wrong shape were skipped; {kind} need {want}.", count=skipped))

    area_geom = shape(area) if area else None
    tf: Transformer | None = None
    if fmt in ("kml", "kmz", "overpass"):
        spec, reason = "WGS84", f"{fmt.upper()} is always longitude/latitude"
    elif source_crs:
        spec, reason = crs_mod.normalise(source_crs), "chosen by the user"
    elif prj:
        tf, spec, reason = _crs_from_prj(prj)
    else:
        coords = get_coordinates([r.geom for r in raws]) if raws else []
        det = crs_mod.detect([c[0] for c in coords], [c[1] for c in coords], area_geom.centroid.x if area_geom else None)
        spec, reason = det.spec, det.reason
    if tf is None and spec and spec != "WGS84":
        tf = crs_mod.to_wgs84(spec)

    if raws and spec is None:
        return ImportResult(kind=kind, format=fmt, source_crs=None, crs_reason=reason, features=[], layers=layers,
                            issues=[*issues, Issue(severity="error", code="crs_unknown",
                                                   message=f"Cannot tell the coordinate system: {reason}.")])
    if not raws:
        hint = " Choose the layer that holds them." if fmt == "dxf" else ""
        what = "polygons" if kind in POLYGON_KINDS else "lines" if kind in LINE_KINDS else "assets"
        issues.append(Issue(severity="error", code="no_features", message=f"No {what} were found in the file.{hint}"))

    features: list[ImportedFeature] = []
    invalid_fixed: list[str] = []
    invalid_dropped: list[str] = []
    outside: list[str] = []

    for r in raws:
        z = _z_of(r)
        geom = force_2d(r.geom)
        if tf is not None:
            geom = transform(tf.transform, geom)
        if isinstance(geom, Polygon) and not geom.is_valid:
            fixed = make_valid(geom)
            polys = [g for g in getattr(fixed, "geoms", [fixed]) if isinstance(g, Polygon)]
            if not polys:
                invalid_dropped.append(r.ref)
                continue
            geom = max(polys, key=lambda g: g.area)
            invalid_fixed.append(r.ref)
        if area_geom is not None and not area_geom.intersects(geom):
            outside.append(r.ref)
        tags = {k: str(v) for k, v in r.props.items() if fmt in ("overpass", "geojson") and not k.startswith(("@", "_"))}
        f = ImportedFeature(
            ref=r.ref,
            geometry=mapping(geom),
            area_m2=round(_area_m2(geom), 2) if isinstance(geom, Polygon) else 0,
            length_m=round(_length_m(geom), 2) if isinstance(geom, LineString) else 0,
            erf=_first(r.props, ERF_KEYS) if kind == "stands" else None,
            zoning=_first(r.props, ZONING_KEYS) if kind in POLYGON_KINDS else None,
            osm_id=str(r.props["@id"]) if "@id" in r.props else None,
            tags=tags if kind in ("buildings", "roads") else {},
            attributes={k: v for k, v in r.props.items() if isinstance(v, (str, int, float, bool)) and not k.startswith("_")},
        )
        _describe(f, r, kind, z)
        features.append(f)

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
    if kind in POLYGON_KINDS:
        tiny = [f.ref for f in features if f.area_m2 < (20 if kind == "stands" else 2)]
        if tiny:
            issues.append(Issue(severity="warning", code="tiny_features",
                                message="Some outlines are implausibly small; check the drawing units.", count=len(tiny), samples=_sample(tiny)))
    if kind == "contours":
        features, more = _check_contours(features)
        issues += more
    if kind == "network":
        issues += _check_network(features)
    if kind == "roads" and features:
        unnamed = [f.ref for f in features if not f.name]
        if len(unnamed) == len(features):
            issues.append(Issue(severity="warning", code="road_names_missing",
                                message="No road has a name; drawings and reports will show none.", count=len(unnamed)))

    return ImportResult(kind=kind, format=fmt, source_crs=spec, crs_reason=reason, features=features, issues=issues, layers=layers)


def _keep_for_kind(raws: list[_Raw], kind: Kind) -> tuple[list[_Raw], int]:
    """The features of the shape the kind needs. Contour rings drawn as polygons become lines."""
    kept: list[_Raw] = []
    skipped = 0
    for r in raws:
        g = r.geom
        if kind in POLYGON_KINDS:
            ok = isinstance(g, Polygon)
        elif kind == "contours" and isinstance(g, Polygon):
            r = _Raw(r.ref, LineString(g.exterior.coords), r.props)
            ok = True
        elif kind in LINE_KINDS:
            ok = isinstance(g, LineString)
        else:
            ok = isinstance(g, (Point, LineString))
        if ok:
            kept.append(r)
        else:
            skipped += 1
    return kept, skipped


def _z_of(r: _Raw) -> float | None:
    z = r.props.get("_z")
    if z is None and r.geom.has_z:
        z = get_coordinates(r.geom, include_z=True)[0][2]
    return float(z) if z is not None else None


def _describe(f: ImportedFeature, r: _Raw, kind: Kind, z: float | None) -> None:
    p = r.props
    if kind == "roads":
        f.name = _first(p, ROAD_NAME_KEYS)
        f.category = _first(p, ROAD_CLASS_KEYS)
    elif kind == "contours":
        f.elevation_m = _number(p, ELEVATION_KEYS)
        # A z of exactly 0 from CAD usually means "not set"; an attribute wins over geometry.
        if f.elevation_m is None and z not in (None, 0.0):
            f.elevation_m = z
    elif kind == "network":
        is_line = isinstance(r.geom, LineString)
        f.category = asset_type(p, is_line) or ("other_line" if is_line else "other")
        f.name = _first(p, LABEL_KEYS)
        f.voltage_kv = _kv(p, f.category)
        f.rating_kva = _number(p, RATING_KEYS)
        f.capacity_kva = _number(p, CAPACITY_KEYS)
        f.fault_level_ka = _number(p, FAULT_KEYS)
        f.missing = [k for k in REQUIRED_FIELDS.get(f.category, ()) if getattr(f, k) is None]


def _check_contours(features: list[ImportedFeature]) -> tuple[list[ImportedFeature], list[Issue]]:
    issues: list[Issue] = []
    without = [f.ref for f in features if f.elevation_m is None]
    kept = [f for f in features if f.elevation_m is not None]
    if without:
        sev: Literal["error", "warning"] = "error" if not kept else "warning"
        issues.append(Issue(severity=sev, code="elevation_missing",
                            message="Contours need an elevation (an attribute such as elevation, or a z value); those without were skipped.",
                            count=len(without), samples=_sample(without)))
    odd = [f.ref for f in kept if not -100 <= f.elevation_m <= 4000]  # type: ignore[operator]
    if odd:
        issues.append(Issue(severity="warning", code="elevation_implausible",
                            message="Some elevations are outside -100 to 4000 m; check the units.", count=len(odd), samples=_sample(odd)))
    return kept, issues


def _check_network(features: list[ImportedFeature]) -> list[Issue]:
    issues: list[Issue] = []
    unknown = [f.ref for f in features if f.category in ("other", "other_line")]
    if unknown:
        sev: Literal["error", "warning"] = "error" if len(unknown) == len(features) else "warning"
        issues.append(Issue(severity=sev, code="network_type_unknown",
                            message="The asset type could not be read; give a type such as transformer, minisub, pole, MV line or LV line.",
                            count=len(unknown), samples=_sample(unknown)))
    missing = [f"{f.name or f.ref}: {', '.join(f.missing)}" for f in features if f.missing]
    if missing:
        issues.append(Issue(severity="warning", code="network_field_missing",
                            message="Some assets lack fields their type needs (transformer kVA, line voltage, connection point capacity and fault level).",
                            count=len(missing), samples=_sample(missing)))
    points = [f for f in features if f.category == "connection_point"]
    if any(f.missing for f in points):
        issues.append(Issue(severity="warning", code="connection_point_incomplete",
                            message="A connection point lacks capacity or fault level. These must come from the authority; the design will stop until they are given.",
                            count=sum(1 for f in points if f.missing)))
    return issues
