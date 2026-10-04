"""GIS exports of the design (plan 6.5): GeoJSON, KML and Shapefile, all WGS84, with the stamp.

Layers: lv_branches, lv_nodes, customers, mv_branches, mv_nodes (and the connection point). Attributes are the stored
results (conductor, length, design current, loading, voltage drop, phases); nothing is recalculated.
"""

from __future__ import annotations

import io
import json
import zipfile
from typing import Any
from xml.sax.saxutils import escape

import shapefile

from .package import DocumentPackage, chosen_option, stamp_lines, stamp_text

WGS84_PRJ = ('GEOGCS["GCS_WGS_1984",DATUM["D_WGS_1984",SPHEROID["WGS_1984",6378137.0,298.257223563]],'
             'PRIMEM["Greenwich",0.0],UNIT["Degree",0.0174532925199433]]')


def layers(pkg: DocumentPackage) -> dict[str, list[dict[str, Any]]]:
    """GeoJSON features per layer."""
    out: dict[str, list[dict[str, Any]]] = {"lv_branches": [], "lv_nodes": [], "customers": [], "mv_branches": [], "mv_nodes": []}
    for site in pkg.lv_designs:
        o = chosen_option(site)
        if o is None:
            continue
        a = o["analysis"]
        br = {b["id"]: b for b in a["branches"]}
        vd = {n["id"]: n["vdrop_pct"] for n in a["nodes"]}
        nodes = {n["id"]: n for n in o["network"]["nodes"]}
        for b in o["network"]["branches"]:
            r = br.get(b["id"], {})
            out["lv_branches"].append(_f("LineString", b["geometry"], {
                "site": site.label, "id": b["id"], "kind": b["kind"], "constr": b["construction"], "conductor": b["conductor"],
                "length_m": round(b["length_m"], 2), "design_a": r.get("design_current_a"), "rating_a": r.get("rating_a"),
                "loading": r.get("loading_pct"), "vdrop_end": vd.get(b["to_id"])}))
        for n in o["network"]["nodes"]:
            if n["kind"] == "connection":
                continue
            out["lv_nodes"].append(_f("Point", [n["lon"], n["lat"]], {
                "site": site.label, "id": n["id"], "kind": n["kind"], "pole": n.get("pole"), "stays": n.get("stays", 0),
                "kva": a["transformer_kva"] if n["kind"] == "source" else None, "vdrop": vd.get(n["id"])}))
        cres = {c["id"]: c for c in a["customers"]}
        for c in o["network"]["customers"]:
            n = nodes.get(c["node_id"])
            if n is None:
                continue
            r = cres.get(c["id"], {})
            out["customers"].append(_f("Point", [n["lon"], n["lat"]], {
                "site": site.label, "erf": c.get("erf"), "building": c["building_id"], "phases": "".join(c["phases"]), "kind": c["kind"],
                "class": c.get("load_class"), "vdrop": r.get("total_vdrop_pct")}))
    if pkg.mv_design and pkg.mv_design.result.get("mv_network"):
        mv = pkg.mv_design.result
        res = {b["id"]: b for b in (mv.get("mv_analysis") or {}).get("branches", [])}
        for b in mv["mv_network"]["branches"]:
            r = res.get(b["id"], {})
            out["mv_branches"].append(_f("LineString", b["geometry"], {
                "id": b["id"], "kind": b["kind"], "conductor": b["conductor"], "length_m": round(b["length_m"], 1),
                "demand_kva": r.get("demand_kva"), "current_a": r.get("current_a"), "loading": r.get("loading_pct"), "vdrop_end": r.get("vdrop_pct_end")}))
        for n in mv["mv_network"]["nodes"]:
            out["mv_nodes"].append(_f("Point", [n["lon"], n["lat"]], {"id": n["id"], "kind": n["kind"], "site": n.get("site_id")}))
    return out


def _f(kind: str, coords: Any, props: dict) -> dict:
    return {"type": "Feature", "geometry": {"type": kind, "coordinates": coords}, "properties": props}


def render_geojson(pkg: DocumentPackage) -> bytes:
    features = []
    for layer, fs in layers(pkg).items():
        for f in fs:
            features.append({**f, "properties": {"layer": layer, **f["properties"]}})
    doc = {"type": "FeatureCollection", "name": pkg.project.name, "reticula": {"project": pkg.project.name, **pkg.stamp.model_dump()}, "features": features}
    return json.dumps(doc, ensure_ascii=False, indent=1).encode("utf-8")


STYLES = {"lv_branches": ("ffb05309", 3), "mv_branches": ("ff2b8a3e", 4), "lv_nodes": ("ff6a6057", 1), "customers": ("ff37a01a", 1), "mv_nodes": ("ff2b8a3e", 1)}


def render_kml(pkg: DocumentPackage) -> bytes:
    parts = ['<?xml version="1.0" encoding="UTF-8"?>', '<kml xmlns="http://www.opengis.net/kml/2.2"><Document>',
             f"<name>{escape(pkg.project.name)}</name>", f"<description>{escape(stamp_text(pkg))}</description>"]
    for layer, (colour, width) in STYLES.items():
        parts.append(f'<Style id="{layer}"><LineStyle><color>{colour}</color><width>{width}</width></LineStyle>'
                     f"<IconStyle><color>{colour}</color><scale>0.6</scale></IconStyle></Style>")
    for layer, fs in layers(pkg).items():
        parts.append(f"<Folder><name>{layer}</name>")
        for f in fs:
            p = f["properties"]
            name = p.get("erf") or p.get("id") or ""
            desc = "".join(f"<tr><td>{escape(str(k))}</td><td>{escape(str(v))}</td></tr>" for k, v in p.items() if v is not None)
            g = f["geometry"]
            if g["type"] == "Point":
                geom = f"<Point><coordinates>{g['coordinates'][0]:.8f},{g['coordinates'][1]:.8f}</coordinates></Point>"
            else:
                geom = "<LineString><coordinates>" + " ".join(f"{x:.8f},{y:.8f}" for x, y in g["coordinates"]) + "</coordinates></LineString>"
            parts.append(f"<Placemark><name>{escape(str(name))}</name><styleUrl>#{layer}</styleUrl>"
                         f"<description><![CDATA[<table>{desc}</table>]]></description>{geom}</Placemark>")
        parts.append("</Folder>")
    parts.append("</Document></kml>")
    return "\n".join(parts).encode("utf-8")


def render_shapefiles(pkg: DocumentPackage) -> bytes:
    """A zip with one shapefile per layer (.shp, .shx, .dbf, .prj, .cpg) and the stamp."""
    buf = io.BytesIO()
    with zipfile.ZipFile(buf, "w", zipfile.ZIP_DEFLATED) as z:
        for layer, fs in layers(pkg).items():
            if not fs:
                continue
            shp, shx, dbf = io.BytesIO(), io.BytesIO(), io.BytesIO()
            kind = shapefile.POINT if fs[0]["geometry"]["type"] == "Point" else shapefile.POLYLINE
            w = shapefile.Writer(shp=shp, shx=shx, dbf=dbf, shapeType=kind, encoding="utf-8")
            keys = list(fs[0]["properties"].keys())
            text: set[str] = set()
            for k in keys:
                sample = next((f["properties"][k] for f in fs if f["properties"].get(k) is not None), "")
                if isinstance(sample, bool) or not isinstance(sample, int | float):
                    w.field(k[:10], "C", size=80)
                    text.add(k)
                else:
                    w.field(k[:10], "N", size=18, decimal=4 if isinstance(sample, float) else 0)
            for f in fs:
                g = f["geometry"]
                if kind == shapefile.POINT:
                    w.point(*g["coordinates"])
                else:
                    w.line([g["coordinates"]])
                values = [f["properties"].get(k) for k in keys]
                w.record(*[("" if v is None else str(v)) if k in text else v for k, v in zip(keys, values, strict=True)])
            w.close()
            for ext, b in (("shp", shp), ("shx", shx), ("dbf", dbf)):
                z.writestr(f"{layer}.{ext}", b.getvalue())
            z.writestr(f"{layer}.prj", WGS84_PRJ)
            z.writestr(f"{layer}.cpg", "UTF-8")
        z.writestr("README.txt", "\n".join([f"{pkg.project.name} – GIS export (WGS84)", *stamp_lines(pkg)]) + "\n")
    return buf.getvalue()
