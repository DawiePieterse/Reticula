"""Network drawings as DXF (plan 6.2): model space in the drawing's metric system, one A1 sheet per layout.

Model space holds the stands, the LV networks (feeders by construction, services, poles, stays, kiosks, transformers,
conductor labels), the MV network and the connection point, each on its template layer. Paper space has an overall
sheet and one sheet per transformer site, each with a scaled viewport, legend and the template's title block carrying
the stamp. DWG is not produced: convert with the ODA File Converter where an authority requires DWG.
"""

from __future__ import annotations

import io
import math
from dataclasses import dataclass

import ezdxf
from ezdxf.enums import TextEntityAlignment

from .package import DocumentPackage, Projection, chosen_option, load_template, stamp_lines, unverified

SHEETS = {"A1": (841.0, 594.0), "A0": (1189.0, 841.0), "A2": (594.0, 420.0)}
TITLE_W = 180.0
MARGIN = 10.0


@dataclass
class Sheet:
    number: str
    title: str
    scale: int


def _layers(doc, tpl: dict) -> None:
    for name, (colour, linetype, weight) in tpl["drawing"]["layers"].items():
        lt = linetype if linetype in doc.linetypes else "CONTINUOUS"
        doc.layers.add(name, color=colour, linetype=lt, lineweight=weight)


def _blocks(doc) -> None:
    pole = doc.blocks.new("POLE")
    pole.add_circle((0, 0), 0.6)
    stay = doc.blocks.new("STAY")
    stay.add_line((0, 0), (0, -3))
    stay.add_line((-0.5, -2.4), (0, -3))
    stay.add_line((0.5, -2.4), (0, -3))
    kiosk = doc.blocks.new("KIOSK")
    kiosk.add_lwpolyline([(-0.8, -0.6), (0.8, -0.6), (0.8, 0.6), (-0.8, 0.6)], close=True)
    tx = doc.blocks.new("TRANSFORMER")
    tx.add_lwpolyline([(-2, -1.5), (2, -1.5), (0, 2)], close=True)
    tx.add_circle((0, 0), 2.6)
    conn = doc.blocks.new("CONNECTION")
    conn.add_circle((0, 0), 0.3)
    cp = doc.blocks.new("CONNECTION_POINT")
    cp.add_lwpolyline([(0, -3), (3, 0), (0, 3), (-3, 0)], close=True)


def _label(msp, text: str, at: tuple[float, float], height: float, layer: str = "TEXT", angle: float = 0.0) -> None:
    if angle > 90 or angle < -90:
        angle += 180
    msp.add_text(text, height=height, rotation=angle, dxfattribs={"layer": layer}).set_placement(at, align=TextEntityAlignment.MIDDLE_CENTER)


def _extent(points: list[tuple[float, float]]) -> tuple[float, float, float, float]:
    xs, ys = [p[0] for p in points], [p[1] for p in points]
    return min(xs), min(ys), max(xs), max(ys)


def _scale_for(ext: tuple[float, float, float, float], vp_w: float, vp_h: float, scales: list[int]) -> int:
    w, h = max(ext[2] - ext[0], 1.0), max(ext[3] - ext[1], 1.0)
    for s in sorted(scales):
        if w <= vp_w * s / 1000 * 0.9 and h <= vp_h * s / 1000 * 0.9:
            return s
    return max(scales)


def render_drawings(pkg: DocumentPackage) -> tuple[bytes, list[Sheet], str]:
    tpl = load_template(pkg.project.authority)
    dcfg = tpl["drawing"]
    proj = Projection(pkg, dcfg.get("crs", "LO-auto"))
    doc = ezdxf.new("R2018", setup=True)
    doc.header["$INSUNITS"] = 6  # metres
    _layers(doc, tpl)
    _blocks(doc)
    msp = doc.modelspace()
    site_points: dict[str, list[tuple[float, float]]] = {}
    everything: list[tuple[float, float]] = []

    for stand in pkg.stands:
        for ring in stand.coordinates:
            pts = [proj.xy(*p) for p in ring]
            if len(pts) >= 3:
                msp.add_lwpolyline(pts, close=True, dxfattribs={"layer": "STANDS"})
                everything += pts
        if stand.erf and stand.coordinates and stand.coordinates[0]:
            cx = sum(p[0] for p in stand.coordinates[0]) / len(stand.coordinates[0])
            cy = sum(p[1] for p in stand.coordinates[0]) / len(stand.coordinates[0])
            _label(msp, stand.erf, proj.xy(cx, cy), 1.2, "STANDS")

    for site in pkg.lv_designs:
        o = chosen_option(site)
        if o is None:
            continue
        net = o["network"]
        nodes = {n["id"]: n for n in net["nodes"]}
        pts_site: list[tuple[float, float]] = []
        for b in net["branches"]:
            pts = [proj.xy(*p) for p in b["geometry"]]
            pts_site += pts
            layer = "LV-SERVICE" if b["kind"] == "service" else ("LV-FEEDER-OH" if b["construction"] == "overhead" else "LV-FEEDER-UG")
            msp.add_lwpolyline(pts, dxfattribs={"layer": layer})
            if b["kind"] == "feeder" and b["length_m"] >= 15:
                (x1, y1), (x2, y2) = pts[0], pts[-1]
                ang = math.degrees(math.atan2(y2 - y1, x2 - x1))
                off = 1.8
                nx_, ny_ = -math.sin(math.radians(ang)) * off, math.cos(math.radians(ang)) * off
                _label(msp, f"{b['conductor']} {b['length_m']:.0f} m", ((x1 + x2) / 2 + nx_, (y1 + y2) / 2 + ny_), 1.0, angle=ang)
        for n in net["nodes"]:
            xy = proj.xy(n["lon"], n["lat"])
            if n["kind"] == "source":
                msp.add_blockref("TRANSFORMER", xy, dxfattribs={"layer": "TRANSFORMER"})
                kva = o["analysis"]["transformer_kva"]
                _label(msp, f"{site.label} {kva:g} kVA", (xy[0], xy[1] + 4.5), 2.0, "TRANSFORMER")
            elif n.get("pole"):
                msp.add_blockref("POLE", xy, dxfattribs={"layer": "LV-POLE"})
                if n.get("stays"):
                    msp.add_blockref("STAY", xy, dxfattribs={"layer": "LV-STAY"})
            elif n["kind"] == "kiosk":
                msp.add_blockref("KIOSK", xy, dxfattribs={"layer": "LV-KIOSK"})
            elif n["kind"] == "connection":
                msp.add_blockref("CONNECTION", xy, dxfattribs={"layer": "CUSTOMERS"})
        for c in net["customers"]:
            n = nodes.get(c["node_id"])
            if n and c.get("erf"):
                x, y = proj.xy(n["lon"], n["lat"])
                _label(msp, f"{c['erf']} {''.join(c['phases'])}", (x, y - 1.2), 0.7, "CUSTOMERS")
        site_points[site.site_id] = pts_site
        everything += pts_site

    if pkg.mv_design and pkg.mv_design.result.get("mv_network"):
        mvn = pkg.mv_design.result["mv_network"]
        for b in mvn["branches"]:
            pts = [proj.xy(*p) for p in b["geometry"]]
            everything += pts
            if len(pts) < 2 or b["length_m"] < 0.5:
                continue
            ug = b["conductor"].startswith("MV-XLPE")
            msp.add_lwpolyline(pts, dxfattribs={"layer": "MV-CABLE-UG" if ug else "MV-LINE-OH"})
            if b["length_m"] >= 30:
                (x1, y1), (x2, y2) = pts[0], pts[-1]
                _label(msp, f"{b['conductor']} {b['length_m']:.0f} m", ((x1 + x2) / 2, (y1 + y2) / 2 + 3), 2.0, angle=math.degrees(math.atan2(y2 - y1, x2 - x1)))
        sup = next((n for n in mvn["nodes"] if n["kind"] == "supply"), None)
        if sup:
            xy = proj.xy(sup["lon"], sup["lat"])
            msp.add_blockref("CONNECTION_POINT", xy, dxfattribs={"layer": "CONNECTION-POINT"})
            _label(msp, "Connection point", (xy[0], xy[1] + 5), 2.5, "CONNECTION-POINT")
    elif pkg.connection_point:
        xy = proj.xy(pkg.connection_point["lon"], pkg.connection_point["lat"])
        msp.add_blockref("CONNECTION_POINT", xy, dxfattribs={"layer": "CONNECTION-POINT"})
        everything.append(xy)

    if not everything:
        everything = [(0.0, 0.0), (100.0, 100.0)]
    sheets: list[Sheet] = []
    prefix = f"{dcfg.get('number_prefix', 'RET')}-{pkg.project.reference or pkg.project.id[:8].upper()}"
    views = [("Overall network layout", everything)] + [(f"LV network – {s.label}", site_points[s.site_id]) for s in pkg.lv_designs
                                                         if site_points.get(s.site_id)]
    paper_w, paper_h = SHEETS.get(dcfg.get("sheet", "A1"), SHEETS["A1"])
    vp_w, vp_h = paper_w - TITLE_W - 3 * MARGIN, paper_h - 2 * MARGIN
    first = True
    for i, (title, pts) in enumerate(views, 1):
        number = f"{prefix}-{i:03d}"
        ext = _extent(pts)
        scale = _scale_for(ext, vp_w, vp_h, list(dcfg.get("scales", [1000])))
        name = number[-31:]
        layout = doc.layouts.new(name) if not first else doc.layouts.get("Layout1")
        if first:
            doc.layouts.rename("Layout1", name)
            first = False
        layout.page_setup(size=(paper_w, paper_h), margins=(0, 0, 0, 0), units="mm")
        layout.add_lwpolyline([(MARGIN, MARGIN), (paper_w - MARGIN, MARGIN), (paper_w - MARGIN, paper_h - MARGIN), (MARGIN, paper_h - MARGIN)],
                              close=True, dxfattribs={"layer": "TITLE"})
        cx, cy = MARGIN + vp_w / 2, MARGIN + vp_h / 2
        layout.add_viewport(center=(cx, cy), size=(vp_w, vp_h), view_center_point=((ext[0] + ext[2]) / 2, (ext[1] + ext[3]) / 2),
                            view_height=vp_h * scale / 1000, dxfattribs={"layer": "VIEWPORT"})
        _title_block(layout, pkg, tpl, paper_w, paper_h, {"project": pkg.project.name, "sheet_title": title, "drawing_number": number,
                                                          "revision": pkg.stamp.revision, "scale": f"1:{scale} (A1)", "rules": pkg.stamp.rules,
                                                          "rate_date": pkg.stamp.rate_date, "design_date": pkg.stamp.design_date,
                                                          "engineer": pkg.stamp.engineer or "—"})
        sheets.append(Sheet(number=number, title=title, scale=scale))

    out = io.StringIO()
    doc.write(out)
    return out.getvalue().encode("utf-8"), sheets, proj.spec


def _title_block(layout, pkg: DocumentPackage, tpl: dict, paper_w: float, paper_h: float, values: dict[str, str]) -> None:
    x0 = paper_w - MARGIN - TITLE_W
    layout.add_line((x0, MARGIN), (x0, paper_h - MARGIN), dxfattribs={"layer": "TITLE"})

    def text(s: str, x: float, y: float, h: float) -> None:
        layout.add_text(s, height=h, dxfattribs={"layer": "TITLE"}).set_placement((x, y), align=TextEntityAlignment.LEFT)

    y = paper_h - MARGIN - 12
    text(tpl.get("title", "Electrification design"), x0 + 5, y, 4.0)
    y -= 10
    for key, label in tpl["drawing"]["title_block"]:
        layout.add_line((x0, y + 6), (paper_w - MARGIN, y + 6), dxfattribs={"layer": "TITLE"})
        text(label, x0 + 5, y, 2.5)
        text(str(values.get(key, ""))[:60], x0 + 45, y, 3.0)
        y -= 12
    y -= 4
    text("Legend", x0 + 5, y, 3.5)
    for layer, label in (("LV-FEEDER-OH", "LV overhead (ABC)"), ("LV-FEEDER-UG", "LV underground cable"), ("LV-SERVICE", "Service connection"),
                         ("MV-LINE-OH", "MV overhead line"), ("MV-CABLE-UG", "MV cable"), ("STANDS", "Stand boundary")):
        y -= 8
        layout.add_line((x0 + 5, y + 1), (x0 + 25, y + 1), dxfattribs={"layer": layer})
        text(label, x0 + 30, y, 2.5)
    y -= 14
    for line in stamp_lines(pkg):
        text(line, x0 + 5, y, 2.2)
        y -= 6
    if unverified(pkg):
        y -= 4
        text("NOT FOR SUBMISSION: rules values not yet verified", x0 + 5, y, 3.0)
        text(", ".join(unverified(pkg))[:80], x0 + 5, y - 6, 2.0)
