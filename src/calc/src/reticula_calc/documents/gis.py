"""GIS exports (plan 6.5): the designed network as GeoJSON, KML and a zipped Shapefile set, all WGS84.

One feature list feeds all three: LV and MV branches with their conductor and results, poles, kiosks, transformers,
the connection point and service connections. Properties are the design's own values; nothing is recalculated.
"""

from __future__ import annotations

import io
import json
import zipfile
from xml.sax.saxutils import escape

import shapefile

from ..design.run import Design
from . import DocumentRequest
from .common import stamp

# kind: KML colour (aabbggrr) and width; after the drawing colours of 240-87658920.
STYLES = {
    "mv_line": ("ff1b00e3", 3), "mv_cable": ("ffc700c7", 3), "lv_line": ("ff2e9600", 2), "lv_cable": ("ffd94500", 2),
    "service": ("ff3d3d3d", 1), "pole": ("ff111111", 1), "kiosk": ("ffd94500", 1), "transformer": ("ff1b00e3", 1),
    "minisub": ("ff1b00e3", 1), "connection_point": ("ff1b00e3", 1),
}
WGS84_PRJ = ('GEOGCS["GCS_WGS_1984",DATUM["D_WGS_1984",SPHEROID["WGS_1984",6378137.0,298.257223563]],'
             'PRIMEM["Greenwich",0.0],UNIT["Degree",0.0174532925199433]]')


def features(d: Design) -> list[dict]:
    out: list[dict] = []
    lv_kind = "lv_cable" if d.construction == "underground" else "lv_line"
    branch = {b.id: b for b in d.lv.analysis.branches}
    for b in d.lv.network.branches:
        r = branch.get(b.id)
        out.append(_f("LineString", b.coordinates, kind=lv_kind, id=b.id, network="lv", branch_kind=b.kind, feeder=b.feeder,
                      conductor=d.lv.sizing.conductors.get(b.id), length_m=b.length_m,
                      loading_pct=r.utilisation_pct if r else None, passes=r.passes if r else None))
    worst = {p.id: p for p in d.lv.analysis.points if p.kind == "node"}
    if d.overhead:
        for p in d.overhead.poles:
            if p.kind == "source":
                continue
            out.append(_f("Point", p.coordinates, kind="pole", id=p.id, network="lv", label=p.label, pole_kind=p.kind, height_m=p.height_m,
                          pole_class=p.pole_class, stays=p.stays, generated=p.generated, passes=p.passes,
                          drop_pct=worst[p.id].worst_pct if p.id in worst else None))
    if d.construction == "underground":
        for n in d.lv.network.nodes:
            if n.kind == "pole":
                out.append(_f("Point", n.coordinates, kind="kiosk", id=n.id, network="lv", label=n.label,
                              drop_pct=worst[n.id].worst_pct if n.id in worst else None))
    for t in d.transformers.transformers:
        out.append(_f("Point", t.coordinates, kind="minisub" if t.mounting == "minisub" else "transformer", id=t.id, label=t.label,
                      rating_kva=t.rating_kva, demand_kva=t.demand_kva, loading_pct=t.loading_pct, passes=t.passes))
    for a in d.lv.allocation.allocations:
        if a.location:
            out.append(_f("LineString", [a.location, a.at], kind="service", id=a.load_id, label=a.label, kva=a.kva, phase=a.phase,
                          box=a.box, feeder=a.feeder, service_m=a.service_m))
    if d.mv_network is not None:
        mv_kind = "mv_cable" if d.mv and d.mv.construction == "underground" else "mv_line"
        mvb = {b.id: b for b in d.mv.branches} if d.mv else {}
        for b in d.mv_network.branches:
            r = mvb.get(b.id)
            out.append(_f("LineString", b.coordinates, kind=mv_kind, id=f"MV:{b.id}", network="mv", branch_kind=b.kind, feeder=b.feeder,
                          conductor=r.conductor if r else None, length_m=b.length_m, loading_pct=r.utilisation_pct if r else None,
                          drop_pct=r.drop_pct if r else None, passes=r.passes if r else None))
        for n in d.mv_network.nodes:
            if n.kind == "source":
                out.append(_f("Point", n.coordinates, kind="connection_point", id=f"MV:{n.id}", label=n.label or "CP"))
        if d.mv_overhead:
            for p in d.mv_overhead.poles:
                if p.kind not in ("source",):
                    out.append(_f("Point", p.coordinates, kind="pole", id=f"MV:{p.id}", network="mv", label=p.label, pole_kind=p.kind,
                                  height_m=p.height_m, pole_class=p.pole_class, stays=p.stays, generated=p.generated, passes=p.passes))
    return out


def _f(geom: str, coords, **props) -> dict:
    c = [list(x) for x in coords] if geom == "LineString" else list(coords)
    return {"type": "Feature", "geometry": {"type": geom, "coordinates": c}, "properties": props}


def _meta(req: DocumentRequest) -> dict:
    s = stamp(req.meta, req.design)
    return {k.lower().replace(" ", "_"): v for k, v in s.lines()} | {"fit_to_submit": req.design.fit_to_submit}


def render_geojson(req: DocumentRequest) -> bytes:
    fc = {"type": "FeatureCollection", "name": f"{req.meta.project_name} network", "metadata": _meta(req), "features": features(req.design)}
    return json.dumps(fc, ensure_ascii=False, indent=1).encode("utf-8")


def render_kml(req: DocumentRequest) -> bytes:
    s = stamp(req.meta, req.design)
    parts = ['<?xml version="1.0" encoding="UTF-8"?>', '<kml xmlns="http://www.opengis.net/kml/2.2"><Document>',
             f"<name>{escape(s.project)} network {escape(s.revision)}</name>",
             f"<description>{escape(chr(10).join(f'{k}: {v}' for k, v in s.lines()))}</description>"]
    for kind, (colour, width) in STYLES.items():
        parts.append(f'<Style id="{kind}"><LineStyle><color>{colour}</color><width>{width}</width></LineStyle>'
                     f"<IconStyle><color>{colour}</color><scale>0.6</scale></IconStyle></Style>")
    groups: dict[str, list[dict]] = {}
    for f in features(req.design):
        groups.setdefault(f["properties"]["kind"], []).append(f)
    for kind, fs in groups.items():
        parts.append(f"<Folder><name>{escape(kind.replace('_', ' '))}</name>")
        for f in fs:
            p = f["properties"]
            data = "".join(f'<Data name="{escape(k)}"><value>{escape("" if v is None else str(v))}</value></Data>' for k, v in p.items())
            coords = f["geometry"]["coordinates"]
            if f["geometry"]["type"] == "Point":
                geom = f"<Point><coordinates>{coords[0]},{coords[1]}</coordinates></Point>"
            else:
                geom = "<LineString><tessellate>1</tessellate><coordinates>" + " ".join(f"{x},{y}" for x, y, *_ in coords) + "</coordinates></LineString>"
            parts.append(f"<Placemark><name>{escape(str(p.get('label') or p.get('id')))}</name><styleUrl>#{kind}</styleUrl>"
                         f"<ExtendedData>{data}</ExtendedData>{geom}</Placemark>")
        parts.append("</Folder>")
    parts.append("</Document></kml>")
    return "\n".join(parts).encode("utf-8")


FIELDS = [("kind", "C", 20), ("id", "C", 60), ("label", "C", 40), ("network", "C", 4), ("feeder", "C", 30), ("conductor", "C", 20),
          ("length_m", "N", 12, 2), ("loading", "N", 10, 2), ("drop_pct", "N", 10, 4), ("rating", "N", 10, 1), ("height_m", "N", 6, 1),
          ("pclass", "C", 6), ("stays", "N", 3, 0), ("kva", "N", 10, 2), ("phase", "C", 3), ("passes", "C", 5)]
SOURCE = {"loading": "loading_pct", "rating": "rating_kva", "pclass": "pole_class"}


def render_shapefile(req: DocumentRequest) -> bytes:
    """Two layers, points and lines, each a .shp/.shx/.dbf/.prj/.cpg set, zipped."""
    fs = features(req.design)
    out = io.BytesIO()
    base = f"{req.meta.project_code or req.meta.project_name}".lower().replace(" ", "-")
    with zipfile.ZipFile(out, "w", zipfile.ZIP_DEFLATED) as z:
        for layer, geom, shape_type in (("points", "Point", shapefile.POINT), ("lines", "LineString", shapefile.POLYLINE)):
            shp, shx, dbf = io.BytesIO(), io.BytesIO(), io.BytesIO()
            with shapefile.Writer(shp=shp, shx=shx, dbf=dbf, shapeType=shape_type, encoding="utf-8") as w:
                for f in FIELDS:
                    w.field(*f)
                for f in fs:
                    if f["geometry"]["type"] != geom:
                        continue
                    c = f["geometry"]["coordinates"]
                    if geom == "Point":
                        w.point(c[0], c[1])
                    else:
                        w.line([[(x, y) for x, y, *_ in c]])
                    p = f["properties"]
                    rec = []
                    for name, ftype, *_ in FIELDS:
                        v = p.get(SOURCE.get(name, name))
                        if ftype == "C":
                            rec.append("" if v is None else str(v))
                        else:
                            rec.append(None if v is None else float(v))
                    w.record(*rec)
            name = f"{base}_{layer}"
            z.writestr(f"{name}.shp", shp.getvalue())
            z.writestr(f"{name}.shx", shx.getvalue())
            z.writestr(f"{name}.dbf", dbf.getvalue())
            z.writestr(f"{name}.prj", WGS84_PRJ)
            z.writestr(f"{name}.cpg", "UTF-8")
        z.writestr("README.txt", "\n".join(f"{k}: {v}" for k, v in stamp(req.meta, req.design).lines()) + "\n")
    return out.getvalue()
