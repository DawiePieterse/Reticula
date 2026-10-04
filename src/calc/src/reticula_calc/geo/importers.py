"""Parse planner and map files into WGS84 features, with early validation.

Formats: KML/KMZ, GeoJSON, DXF, Overpass JSON (OSM `out geom`), zipped shapefile, CSV (network points) and
GeoTIFF elevation models (contoured on import).
Kinds:   stands (polygons with erf numbers), buildings (footprints with OSM tags), roads (lines), contours (lines with
         an elevation) and network (the authority's existing assets: lines and points with required fields).
"""

from __future__ import annotations

import csv
import io
import json
import math
import re
import zipfile
from collections import Counter
from dataclasses import dataclass, field
from typing import Any, Literal
from xml.etree import ElementTree as ET

import ezdxf
import numpy as np
from ezdxf import recover
from ezdxf.path import make_path
from pydantic import BaseModel
from pyproj import CRS, Geod
from shapely import STRtree, get_coordinates, make_valid, wkt
from shapely.geometry import LineString, MultiLineString, MultiPoint, MultiPolygon, Point, Polygon, mapping, shape
from shapely.geometry.base import BaseGeometry
from shapely.ops import transform

from . import crs as crs_mod
from .network import NetworkCheck, check_network

Kind = Literal["stands", "buildings", "roads", "contours", "network"]
Format = Literal["kml", "kmz", "geojson", "dxf", "overpass", "shapefile", "csv", "geotiff"]
GEOD = Geod(ellps="WGS84")

ERF_KEYS = ("erf", "erf_no", "erfno", "erf_number", "stand", "stand_no", "standno", "stand_number", "name", "label")
ZONING_KEYS = ("zoning", "zone", "land_use", "landuse", "use_zone")
NAME_KEYS = ("name", "road_name", "street", "label")
ROAD_CLASS_KEYS = ("highway", "class", "road_class", "type", "road_type", "category")
ELEVATION_KEYS = ("elevation", "elev", "height", "z", "contour", "level", "altitude", "elevation_m")
MAX_ISSUE_SAMPLES = 10
MAX_RASTER_CELLS = 16_000_000
POLYGON_KINDS = ("stands", "buildings")
LINE_KINDS = ("roads", "contours")


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
    points: int = 0


class ImportedFeature(BaseModel):
    ref: str
    geometry: dict[str, Any]
    area_m2: float = 0
    length_m: float = 0
    erf: str | None = None
    zoning: str | None = None
    osm_id: str | None = None
    name: str | None = None
    subtype: str | None = None
    elevation_m: float | None = None
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


@dataclass
class _Parsed:
    raws: list[_Raw]
    issues: list[Issue] = field(default_factory=list)
    layers: list[LayerInfo] = field(default_factory=list)
    #: A coordinate system the file declares (shapefile .prj, GeoTIFF keys); used unless the user chose one.
    declared_crs: str | None = None
    declared_reason: str = ""


def detect_format(filename: str, data: bytes) -> Format:
    name = filename.lower()
    if name.endswith(".kmz"):
        return "kmz"
    if name.endswith(".kml"):
        return "kml"
    if name.endswith(".dxf"):
        return "dxf"
    if name.endswith(".zip"):
        return "shapefile"
    if name.endswith(".csv"):
        return "csv"
    if name.endswith((".tif", ".tiff")):
        return "geotiff"
    if name.endswith((".geojson", ".json")):
        try:
            doc = json.loads(data)
        except ValueError as e:
            raise UnreadableFileError(f"{filename} is not valid JSON: {e}") from e
        return "overpass" if isinstance(doc, dict) and "elements" in doc else "geojson"
    raise UnreadableFileError(
        f"Unsupported file type for {filename}; use KML, KMZ, GeoJSON, Overpass JSON, DXF, a zipped shapefile, CSV or GeoTIFF")


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


def _parse_kml(data: bytes) -> _Parsed:
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
                    which = "outer" if _local(b.tag) == "outerBoundaryIs" else "inner" if _local(b.tag) == "innerBoundaryIs" else None
                    if which:
                        for c in b.iter():
                            if _local(c.tag) == "coordinates":
                                rings[which].append(_kml_coords(c.text))
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
    return _Parsed(out)


def _parse_kmz(data: bytes) -> _Parsed:
    try:
        with zipfile.ZipFile(io.BytesIO(data)) as z:
            name = next((n for n in z.namelist() if n.lower().endswith(".kml")), None)
            if name is None:
                raise UnreadableFileError("KMZ contains no KML document")
            return _parse_kml(z.read(name))
    except zipfile.BadZipFile as e:
        raise UnreadableFileError("KMZ is not a valid zip archive") from e


def _geojson_features(feats: list[dict[str, Any]]) -> list[_Raw]:
    out: list[_Raw] = []
    for i, f in enumerate(feats):
        if not f.get("geometry"):
            continue
        geom = shape(f["geometry"])
        props = dict(f.get("properties") or {})
        ref = str(f.get("id") or props.get("@id") or f"feature-{i + 1}")
        z = _z_of(f["geometry"])
        if z is not None:
            props.setdefault("_z", z)
        parts = geom.geoms if isinstance(geom, (MultiPolygon, MultiLineString, MultiPoint)) else [geom]
        for j, part in enumerate(parts):
            out.append(_Raw(ref if j == 0 else f"{ref}.{j + 1}", part, props))
    return out


def _z_of(geometry: dict[str, Any]) -> float | None:
    """The common third coordinate of a GeoJSON geometry (contour lines often carry their height as z)."""
    coords = np.asarray(_flatten(geometry.get("coordinates")), dtype=float)
    if coords.ndim != 2 or coords.shape[1] < 3:
        return None
    zs = coords[:, 2]
    return float(zs[0]) if np.allclose(zs, zs[0]) else None


def _flatten(c: Any) -> list[list[float]]:
    if isinstance(c, (list, tuple)) and c and isinstance(c[0], (int, float)):
        return [list(c)]
    return [p for part in (c or []) for p in _flatten(part)]


def _parse_geojson(data: bytes) -> _Parsed:
    doc = json.loads(data)
    feats = doc.get("features", []) if doc.get("type") == "FeatureCollection" else [doc] if doc.get("type") == "Feature" else []
    return _Parsed(_geojson_features(feats))


def _parse_overpass(data: bytes, kind: Kind) -> _Parsed:
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
        tags = {k: str(v) for k, v in (el.get("tags") or {}).items()}
        props = {"@id": f"way/{el['id']}", **tags}
        if kind in POLYGON_KINDS:
            if len(pts) < 4 or pts[0] != pts[-1]:
                continue
            out.append(_Raw(f"way/{el['id']}", Polygon(pts), props))
        elif len(pts) >= 2:
            out.append(_Raw(f"way/{el['id']}", LineString(pts), props))
    issues = []
    if skipped and kind in POLYGON_KINDS:
        issues.append(Issue(severity="warning", code="osm_relations_skipped",
                            message="Multipolygon relations are not imported yet; large buildings drawn as relations are missing.",
                            count=skipped))
    return _Parsed(out, issues)


def _parse_dxf(data: bytes, layer: str | None, kind: Kind) -> _Parsed:
    try:
        doc, _auditor = recover.read(io.BytesIO(data))
    except (OSError, ezdxf.DXFStructureError) as e:
        raise UnreadableFileError(f"DXF cannot be read: {e}") from e
    msp = doc.modelspace()
    layers: dict[str, LayerInfo] = {}
    polys: list[_Raw] = []
    lines: list[_Raw] = []
    points: list[_Raw] = []
    texts: list[tuple[Point, str]] = []
    open_on_layer = 0

    for e in msp:
        name = e.dxf.layer
        info = layers.setdefault(name, LayerInfo(name=name))
        kind_ = e.dxftype()
        on_layer = layer is None or name == layer
        if kind_ in ("TEXT", "MTEXT"):
            info.texts += 1
            if on_layer:
                content = e.plain_text() if kind_ == "MTEXT" else e.dxf.text
                texts.append((Point(e.dxf.insert.x, e.dxf.insert.y), content.strip()))
        elif kind_ in ("LWPOLYLINE", "POLYLINE"):
            closed = bool(e.closed if kind_ == "LWPOLYLINE" else e.is_closed)
            if closed:
                info.closed_polylines += 1
            else:
                info.open_polylines += 1
            if not on_layer:
                continue
            # Flatten arcs (bulges) to 5 cm.
            path = make_path(e)
            pts3 = [(v.x, v.y, v.z) for v in path.flattening(0.05)]
            pts = [(x, y) for x, y, _ in pts3]
            props: dict[str, Any] = {"layer": name}
            z = _dxf_elevation(e, pts3)
            if z is not None:
                props["_z"] = z
            ref = f"{kind_.lower()}-{e.dxf.handle}"
            if closed and len(pts) >= 3:
                polys.append(_Raw(ref, Polygon(pts), props))
                lines.append(_Raw(ref, LineString([*pts, pts[0]]), props))
            elif not closed:
                open_on_layer += 1
                if len(pts) >= 2:
                    lines.append(_Raw(ref, LineString(pts), props))
        elif kind_ == "LINE":
            info.open_polylines += 1
            if on_layer:
                s, t = e.dxf.start, e.dxf.end
                props = {"layer": name, **({"_z": s.z} if s.z == t.z and s.z != 0 else {})}
                lines.append(_Raw(f"line-{e.dxf.handle}", LineString([(s.x, s.y), (t.x, t.y)]), props))
        elif kind_ in ("POINT", "INSERT"):
            info.points += 1
            if on_layer:
                at = e.dxf.location if kind_ == "POINT" else e.dxf.insert
                props = {"layer": name}
                if kind_ == "INSERT":
                    props["block"] = e.dxf.name
                    for a in e.attribs:
                        props[a.dxf.tag.lower()] = a.dxf.text
                points.append(_Raw(f"{kind_.lower()}-{e.dxf.handle}", Point(at.x, at.y), props))

    issues: list[Issue] = []
    if kind in POLYGON_KINDS:
        raws = polys
        if open_on_layer:
            issues.append(Issue(severity="warning", code="open_polylines",
                                message="Open polylines were skipped; only closed polylines become outlines.", count=open_on_layer))
        _label_polygons(polys, texts, issues)
    elif kind in LINE_KINDS:
        raws = lines
        if kind == "contours":
            _label_lines(lines, texts)
    else:
        raws = [*lines, *points]
    return _Parsed(raws, issues, sorted(layers.values(), key=lambda li: -(li.closed_polylines + li.open_polylines + li.points)))


def _dxf_elevation(e: Any, pts3: list[tuple[float, float, float]]) -> float | None:
    """A contour's height: the LWPOLYLINE elevation, or the common z of a 3D polyline."""
    if e.dxftype() == "LWPOLYLINE":
        z = float(e.dxf.get("elevation", 0) or 0)
        return z if z != 0 else None
    zs = {round(p[2], 3) for p in pts3}
    if len(zs) != 1:
        return None
    z = zs.pop()
    return z if z != 0 else None


def _label_polygons(polys: list[_Raw], texts: list[tuple[Point, str]], issues: list[Issue]) -> None:
    """Label each polygon with the text inside it, preferring numeric labels (erf numbers)."""
    if not polys or not texts:
        return
    tree = STRtree([p.geom for p in polys])
    for pt, content in texts:
        for idx in tree.query(pt, predicate="within"):
            polys[int(idx)].props.setdefault("_labels", []).append(content)
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


def _label_lines(lines: list[_Raw], texts: list[tuple[Point, str]]) -> None:
    """Contours drawn flat in 2D carry their height as a nearby text; take the nearest number within 5 m."""
    numeric = [(pt, float(t)) for pt, t in texts if re.fullmatch(r"-?\d+(\.\d+)?", t)]
    if not lines or not numeric:
        return
    tree = STRtree([ln.geom for ln in lines])
    for pt, value in numeric:
        idx = tree.query_nearest(pt, max_distance=5)
        for i in idx[:1]:
            lines[int(i)].props.setdefault("_z_label", value)


def _parse_shapefile(data: bytes) -> _Parsed:
    import shapefile  # pyshp

    try:
        z = zipfile.ZipFile(io.BytesIO(data))
    except zipfile.BadZipFile as e:
        raise UnreadableFileError("The .zip is not a valid archive; zip the .shp, .shx, .dbf and .prj together") from e
    names = z.namelist()
    shp = next((n for n in names if n.lower().endswith(".shp")), None)
    if shp is None:
        raise UnreadableFileError("The .zip holds no shapefile (.shp)")
    stem = shp[:-4]

    def part(ext: str) -> io.BytesIO | None:
        n = next((x for x in names if x.lower() == f"{stem.lower()}{ext}"), None)
        return io.BytesIO(z.read(n)) if n else None

    dbf, shx, prj = part(".dbf"), part(".shx"), part(".prj")
    if dbf is None:
        raise UnreadableFileError("The shapefile has no .dbf attribute table")
    try:
        reader = shapefile.Reader(shp=io.BytesIO(z.read(shp)), dbf=dbf, shx=shx)
        fields = [f[0] for f in reader.fields[1:]]
        feats = []
        for i, sr in enumerate(reader.iterShapeRecords()):
            if sr.shape.shapeType == shapefile.NULL:
                continue
            props = {k: (v.strip() if isinstance(v, str) else v) for k, v in zip(fields, sr.record, strict=False)}
            feats.append({"type": "Feature", "id": f"record-{i + 1}", "geometry": sr.shape.__geo_interface__, "properties": props})
    except shapefile.ShapefileException as e:
        raise UnreadableFileError(f"The shapefile cannot be read: {e}") from e

    parsed = _Parsed(_geojson_features(feats))
    if prj is not None:
        parsed.declared_crs, parsed.declared_reason = _crs_from_wkt(prj.read().decode("utf-8", "replace"), "the shapefile's .prj")
    return parsed


def _crs_from_wkt(text: str, where: str) -> tuple[str | None, str]:
    try:
        epsg = CRS.from_wkt(text).to_epsg()
    except Exception:  # noqa: BLE001 - any unparsable .prj is reported the same way
        return None, f"{where} could not be read"
    if epsg is None:
        return None, f"{where} names a system without an EPSG code"
    return _epsg_spec(epsg), f"from {where} (EPSG:{epsg})"


def _epsg_spec(epsg: int) -> str:
    if epsg == 4326:
        return "WGS84"
    if 32734 <= epsg <= 32736:
        return f"UTM{epsg - 32700}S"
    return f"EPSG:{epsg}"


def _parse_csv(data: bytes) -> _Parsed:
    text = data.decode("utf-8-sig", "replace")
    try:
        dialect = csv.Sniffer().sniff(text[:4096], delimiters=",;\t")
    except csv.Error:
        dialect = csv.excel
    rows = list(csv.DictReader(io.StringIO(text), dialect=dialect))
    if not rows:
        return _Parsed([])
    cols = {c.lower().strip(): c for c in rows[0] if c}
    geom_col = next((cols[k] for k in ("wkt", "geometry", "geom", "the_geom") if k in cols), None)
    x_col = next((cols[k] for k in ("lon", "lng", "long", "longitude", "x", "easting") if k in cols), None)
    y_col = next((cols[k] for k in ("lat", "latitude", "y", "northing") if k in cols), None)
    if geom_col is None and (x_col is None or y_col is None):
        raise UnreadableFileError("The CSV needs a WKT column (wkt or geometry) or coordinate columns (lon/lat or x/y)")
    out: list[_Raw] = []
    bad: list[str] = []
    for i, row in enumerate(rows):
        ref = str(row.get(cols.get("asset_id", ""), "") or f"row-{i + 2}")
        props = {k.strip(): v.strip() for k, v in row.items() if k and k not in (geom_col, x_col, y_col) and v not in (None, "")}
        try:
            geom = wkt.loads(row[geom_col]) if geom_col else Point(float(row[x_col]), float(row[y_col]))  # type: ignore[index]
        except (ValueError, TypeError, Exception):  # noqa: BLE001 - shapely raises its own error types
            bad.append(ref)
            continue
        parts = geom.geoms if isinstance(geom, (MultiLineString, MultiPoint, MultiPolygon)) else [geom]
        for j, g in enumerate(parts):
            out.append(_Raw(ref if j == 0 else f"{ref}.{j + 1}", g, props))
    issues = [Issue(severity="warning", code="rows_unreadable", message="Some rows have no readable geometry and were skipped.",
                    count=len(bad), samples=bad[:MAX_ISSUE_SAMPLES])] if bad else []
    return _Parsed(out, issues)


def _nice_interval(span: float, target_levels: int = 40) -> float:
    if span <= 0:
        return 1.0
    raw = span / target_levels
    mag = 10 ** math.floor(math.log10(raw))
    step = next(m * mag for m in (1, 2, 5, 10) if m * mag >= raw)
    return max(step, 0.5)


def _parse_geotiff(data: bytes, interval: float | None) -> _Parsed:
    import contourpy
    import tifffile

    try:
        with tifffile.TiffFile(io.BytesIO(data)) as tif:
            page = tif.pages[0]
            tags = {t.code: t.value for t in page.tags.values()}
            arr = page.asarray().astype(float)
    except (tifffile.TiffFileError, ValueError) as e:
        raise UnreadableFileError(f"GeoTIFF cannot be read: {e}") from e
    if arr.ndim == 3:
        arr = arr[..., 0] if arr.shape[-1] <= 4 else arr[0]
    if arr.ndim != 2:
        raise UnreadableFileError("The GeoTIFF is not a single-band elevation model")
    if arr.size > MAX_RASTER_CELLS:
        raise UnreadableFileError(f"The elevation model has {arr.size:,} cells; crop it to the project area (at most {MAX_RASTER_CELLS:,})")
    scale, tie = tags.get(33550), tags.get(33922)
    if not scale or not tie:
        raise UnreadableFileError("The TIFF has no georeferencing (ModelPixelScale and ModelTiepoint tags)")
    nodata = tags.get(42113)
    if nodata not in (None, ""):
        arr[arr == float(str(nodata).strip("\x00"))] = np.nan
    arr[arr < -1000] = np.nan  # common no-data fill values

    sx, sy = float(scale[0]), float(scale[1])
    i0, j0, x0, y0 = float(tie[0]), float(tie[1]), float(tie[3]), float(tie[4])
    keys = _geokeys(tags.get(34735))
    area_or_point = keys.get(1025, 1)  # 1 = pixel is area: centres sit half a pixel in
    off = 0.5 if area_or_point == 1 else 0.0
    xs = x0 + (np.arange(arr.shape[1]) - i0 + off) * sx
    ys = y0 - (np.arange(arr.shape[0]) - j0 + off) * sy

    valid = arr[~np.isnan(arr)]
    if valid.size == 0:
        raise UnreadableFileError("The elevation model holds no data")
    lo, hi = float(valid.min()), float(valid.max())
    step = interval or _nice_interval(hi - lo)
    levels = np.arange(math.ceil(lo / step) * step, hi + 1e-9, step)
    levels = levels[(levels > lo) & (levels < hi)]  # a level at the very edge of the data only traces the border
    gen = contourpy.contour_generator(x=xs, y=ys, z=np.ma.masked_invalid(arr), line_type=contourpy.LineType.Separate)
    out: list[_Raw] = []
    for level in levels:
        for k, seg in enumerate(gen.lines(float(level))):
            if len(seg) >= 2:
                out.append(_Raw(f"contour-{level:g}-{k + 1}", LineString(seg), {"_z": round(float(level), 3)}))

    epsg = keys.get(3072) or keys.get(2048)
    parsed = _Parsed(out, [Issue(severity="warning", code="contours_generated",
                                 message=f"Contours were drawn from the elevation model every {step:g} m ({lo:.1f}–{hi:.1f} m).",
                                 count=len(out))])
    if epsg and epsg != 32767:
        parsed.declared_crs, parsed.declared_reason = _epsg_spec(int(epsg)), f"from the GeoTIFF keys (EPSG:{epsg})"
    return parsed


def _geokeys(directory: Any) -> dict[int, Any]:
    if directory is None:
        return {}
    d = list(directory)
    keys: dict[int, Any] = {}
    for n in range(d[3]):
        key, loc, _count, value = d[4 + 4 * n: 8 + 4 * n]
        if loc == 0:
            keys[int(key)] = int(value)
    return keys


# ---------- normalise ----------

def _first(props: dict[str, Any], keys: tuple[str, ...]) -> str | None:
    lower = {k.lower(): v for k, v in props.items()}
    for k in keys:
        v = lower.get(k)
        if v not in (None, ""):
            return str(v).strip()
    return None


def _number(value: str | None) -> float | None:
    if value is None:
        return None
    try:
        return float(str(value).replace(",", ".").removesuffix("m").strip())
    except ValueError:
        return None


def _area_m2(geom: BaseGeometry) -> float:
    return abs(GEOD.geometry_area_perimeter(geom)[0])


def _length_m(geom: BaseGeometry) -> float:
    return GEOD.geometry_length(geom) if isinstance(geom, LineString) else 0.0


def _sample(items: list[str]) -> list[str]:
    return items[:MAX_ISSUE_SAMPLES]


def _fits(kind: Kind, geom: BaseGeometry) -> bool:
    if kind in POLYGON_KINDS:
        return isinstance(geom, Polygon)
    if kind in LINE_KINDS:
        return isinstance(geom, LineString)
    return isinstance(geom, (LineString, Point))


_NOUN = {"stands": "polygons", "buildings": "polygons", "roads": "lines", "contours": "lines", "network": "lines or points"}


def import_file(
    filename: str,
    data: bytes,
    kind: Kind,
    source_crs: str | None = None,
    layer: str | None = None,
    area: dict[str, Any] | None = None,
    contour_interval: float | None = None,
) -> ImportResult:
    fmt = detect_format(filename, data)
    if fmt == "geotiff" and kind != "contours":
        raise UnreadableFileError("A GeoTIFF elevation model can only be imported as contours")

    if fmt == "kml":
        parsed = _parse_kml(data)
    elif fmt == "kmz":
        parsed = _parse_kmz(data)
    elif fmt == "geojson":
        parsed = _parse_geojson(data)
    elif fmt == "overpass":
        parsed = _parse_overpass(data, kind)
    elif fmt == "shapefile":
        parsed = _parse_shapefile(data)
    elif fmt == "csv":
        parsed = _parse_csv(data)
    elif fmt == "geotiff":
        parsed = _parse_geotiff(data, contour_interval)
    else:
        parsed = _parse_dxf(data, layer, kind)
    issues, layers = list(parsed.issues), parsed.layers

    wrong = [r.ref for r in parsed.raws if not _fits(kind, r.geom)]
    raws = [r for r in parsed.raws if _fits(kind, r.geom)]
    if wrong and raws:
        issues.append(Issue(severity="warning", code="geometry_type_skipped",
                            message=f"Only {_NOUN[kind]} are imported as {kind}; other shapes were skipped.",
                            count=len(wrong), samples=_sample(wrong)))

    area_geom = shape(area) if area else None
    if fmt in ("kml", "kmz", "overpass"):
        spec, reason = "WGS84", f"{fmt.upper()} is always longitude/latitude"
    elif source_crs:
        spec, reason = crs_mod.normalise(source_crs), "chosen by the user"
    elif parsed.declared_crs:
        spec, reason = parsed.declared_crs, parsed.declared_reason
    else:
        coords = get_coordinates([r.geom for r in raws]) if raws else np.empty((0, 2))
        det = crs_mod.detect(list(coords[:, 0]), list(coords[:, 1]), area_geom.centroid.x if area_geom else None)
        spec, reason = det.spec, det.reason

    if raws and spec is None:
        return ImportResult(kind=kind, format=fmt, source_crs=None, crs_reason=reason, features=[], layers=layers,
                            issues=[*issues, Issue(severity="error", code="crs_unknown",
                                                   message=f"Cannot tell the coordinate system: {reason}.")])
    if not raws:
        hint = " Choose the layer that holds them." if fmt == "dxf" else ""
        issues.append(Issue(severity="error", code="no_features", message=f"No {_NOUN[kind]} were found in the file.{hint}"))

    tf = crs_mod.to_wgs84(spec) if spec else None
    features: list[ImportedFeature] = []
    invalid_fixed: list[str] = []
    invalid_dropped: list[str] = []
    outside: list[str] = []

    for r in raws:
        geom = transform(tf.transform, r.geom) if tf and spec != "WGS84" else r.geom
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
        public = {k: v for k, v in r.props.items() if not k.startswith("_")}
        tags = {k: str(v) for k, v in public.items() if fmt in ("overpass", "geojson") and not k.startswith("@")}
        elevation = None
        if kind == "contours":
            elevation = _number(_first(public, ELEVATION_KEYS))
            if elevation is None:
                elevation = r.props.get("_z", r.props.get("_z_label"))
        features.append(ImportedFeature(
            ref=r.ref,
            geometry=mapping(geom),
            area_m2=round(_area_m2(geom), 2) if isinstance(geom, Polygon) else 0,
            length_m=round(_length_m(geom), 2),
            erf=_first(public, ERF_KEYS) if kind == "stands" else None,
            zoning=_first(public, ZONING_KEYS) if kind in POLYGON_KINDS else None,
            osm_id=str(public["@id"]) if "@id" in public else None,
            name=_first(public, NAME_KEYS) if kind in ("roads", "network") else None,
            subtype=_first(public, ROAD_CLASS_KEYS) if kind == "roads" else None,
            elevation_m=elevation,
            tags=tags if kind == "buildings" else {},
            attributes={k: v for k, v in public.items() if isinstance(v, (str, int, float, bool))},
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
    if kind in POLYGON_KINDS:
        tiny = [f.ref for f in features if f.area_m2 < (20 if kind == "stands" else 2)]
        if tiny:
            issues.append(Issue(severity="warning", code="tiny_features",
                                message="Some outlines are implausibly small; check the drawing units.", count=len(tiny), samples=_sample(tiny)))
    if kind == "contours" and features:
        features, more = _check_contours(features)
        issues += more
    if kind == "network" and features:
        result: NetworkCheck = check_network(features)
        features = result.features
        issues += [Issue(**i) for i in result.issues]

    return ImportResult(kind=kind, format=fmt, source_crs=spec, crs_reason=reason, features=features, issues=issues, layers=layers)


def _check_contours(features: list[ImportedFeature]) -> tuple[list[ImportedFeature], list[Issue]]:
    issues: list[Issue] = []
    missing = [f.ref for f in features if f.elevation_m is None]
    kept = [f for f in features if f.elevation_m is not None]
    if missing:
        sev: Literal["error", "warning"] = "error" if not kept else "warning"
        issues.append(Issue(severity=sev, code="contour_elevation_missing",
                            message="Some lines have no elevation (no elevation field, z value or height label)"
                                    + (" and were skipped." if kept else "."),
                            count=len(missing), samples=_sample(missing)))
    odd = [f.ref for f in kept if not -500 <= (f.elevation_m or 0) <= 6000]
    if odd:
        issues.append(Issue(severity="warning", code="elevation_implausible",
                            message="Some elevations are outside -500 to 6000 m; check the units.", count=len(odd), samples=_sample(odd)))
    return (kept if kept else features), issues
