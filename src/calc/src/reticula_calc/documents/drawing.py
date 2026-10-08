"""Reticulation layout drawing as DXF (plan 6.2), after Eskom 240-87658920 (DST 34-195) drawing practice.

Model space is in metres in the South African Lo system nearest the design. Following the document's colour and line
conventions (§4.3.4, Annex D) as Reticula transcribes them in src/web/src/app/shared/symbols.ts:

- MV overhead red, dash-dot; LV overhead green, dashed; MV cable magenta and LV cable blue, continuous;
  services thin grey, dashed when underground.
- Symbols from Annex B as blocks: transformer (two overlapping circles), mini-sub (the circles boxed), connection
  point, LV pole (filled circle), MV pole (open circle with a dot); metering kiosks as a small box (Reticula's symbol:
  Annex B has none for a kiosk).
- Pole tags with height and class, stays drawn away from the line's resultant pull, conductor codes along each span,
  transformer labels with rating, and each transformer's zone (§4.3.8): the hull of the buildings it feeds.

Paper space has an A1 sheet with the drawing in a viewport, a legend and the title block with every stamp field.
ElecCell.cel and ElecLines.rsc are named by the standard but not held, so blocks and linetypes are Reticula's.
"""

from __future__ import annotations

import io
import itertools
import math

import ezdxf
from ezdxf.enums import TextEntityAlignment
from shapely.geometry import MultiPoint

from ..design.run import Design
from ..lv.network import LvNetwork
from . import DocumentRequest
from .common import NOT_FIT, Projector, stamp

# name: (ACI colour, linetype, lineweight in 1/100 mm, description)
LAYERS = {
    "RET-MV-OH": (1, "RET_MV", 50, "MV overhead line"),
    "RET-MV-UG": (6, "Continuous", 50, "MV cable"),
    "RET-LV-OH": (3, "RET_LV", 35, "LV overhead line (ABC)"),
    "RET-LV-UG": (5, "Continuous", 35, "LV cable"),
    "RET-SERVICE": (8, "Continuous", 13, "Service connection"),
    "RET-SERVICE-UG": (8, "RET_SERVICE", 13, "Service cable"),
    "RET-POLE": (7, "Continuous", 25, "Poles"),
    "RET-STAY": (7, "Continuous", 18, "Stays"),
    "RET-TX": (1, "Continuous", 35, "Transformers and mini-subs"),
    "RET-KIOSK": (5, "Continuous", 25, "Metering kiosks"),
    "RET-CP": (1, "Continuous", 35, "Connection point"),
    "RET-ZONE": (6, "RET_ZONE", 18, "Transformer zones"),
    "RET-LABEL": (7, "Continuous", 13, "Labels"),
    "RET-SHEET": (7, "Continuous", 35, "Sheet, title block and legend"),
    "RET-VIEWPORT": (8, "Continuous", 13, "Viewport frame"),
}
# Patterns in drawing units (m in model space, mm on the sheet): total, then dashes (+), gaps (−) and dots (0).
LINETYPES = {
    "RET_MV": ("MV overhead __ . __ .", [7.2, 4.0, -1.5, 0.0, -1.5]),
    "RET_LV": ("LV overhead __ __", [4.0, 2.5, -1.5]),
    "RET_SERVICE": ("Underground service _ _", [2.0, 1.0, -1.0]),
    "RET_ZONE": ("Transformer zone _ . . _", [6.0, 3.0, -1.0, 0.0, -1.0, 0.0, -1.0]),
}
TEXT_M = 1.8
SHEET = (841.0, 594.0)


def _setup(doc) -> None:
    for name, (desc, pattern) in LINETYPES.items():
        doc.linetypes.add(name, pattern=pattern, description=desc)
    for name, (colour, lt, lw, desc) in LAYERS.items():
        layer = doc.layers.add(name, color=colour, linetype=lt)
        layer.dxf.lineweight = lw
        layer.description = desc
    b = doc.blocks.new("RET_TX", dxfattribs={"description": "Transformer (Annex B)"})
    for x in (-0.9, 0.9):
        b.add_circle((x, 0), 1.5, dxfattribs={"layer": "0"})
    b = doc.blocks.new("RET_MINISUB", dxfattribs={"description": "Mini-substation (Annex B)"})
    for x in (-0.8, 0.8):
        b.add_circle((x, 0), 1.2, dxfattribs={"layer": "0"})
    b.add_lwpolyline([(-3, -2), (3, -2), (3, 2), (-3, 2)], close=True, dxfattribs={"layer": "0"})
    b = doc.blocks.new("RET_CP", dxfattribs={"description": "Connection point (Annex B)"})
    b.add_lwpolyline([(-2, -2), (2, -2), (2, 2), (-2, 2)], close=True, dxfattribs={"layer": "0"})
    b.add_lwpolyline([(0.6, 1.4), (-0.8, -0.1), (0.2, -0.1), (-0.6, -1.4), (0.8, 0.1), (-0.2, 0.1)], close=True, dxfattribs={"layer": "0"})
    b = doc.blocks.new("RET_POLE_LV", dxfattribs={"description": "LV pole (Annex B)"})
    b.add_circle((0, 0), 0.8, dxfattribs={"layer": "0"})
    h = b.add_hatch(dxfattribs={"layer": "0"})
    h.paths.add_edge_path().add_arc((0, 0), 0.8, 0, 360)
    b = doc.blocks.new("RET_POLE_MV", dxfattribs={"description": "MV pole (Annex B)"})
    b.add_circle((0, 0), 1.0, dxfattribs={"layer": "0"})
    b.add_circle((0, 0), 0.35, dxfattribs={"layer": "0"})
    b = doc.blocks.new("RET_KIOSK", dxfattribs={"description": "Metering kiosk (Reticula symbol)"})
    b.add_lwpolyline([(-1.2, -0.8), (1.2, -0.8), (1.2, 0.8), (-1.2, 0.8)], close=True, dxfattribs={"layer": "0"})
    b.add_line((-1.2, -0.8), (1.2, 0.8), dxfattribs={"layer": "0"})


def _label(msp, text: str, at, height: float = TEXT_M, angle: float = 0.0, layer: str = "RET-LABEL") -> None:
    msp.add_text(text, height=height, rotation=angle, dxfattribs={"layer": layer}).set_placement(at, align=TextEntityAlignment.MIDDLE_CENTER)


def _midpoint(pts: list[tuple[float, float]]) -> tuple[tuple[float, float], float]:
    """The point halfway along a polyline and the readable angle of the segment there."""
    total = sum(math.dist(a, b) for a, b in itertools.pairwise(pts))
    run = 0.0
    for a, b in itertools.pairwise(pts):
        seg = math.dist(a, b)
        if run + seg >= total / 2 and seg > 0:
            t = (total / 2 - run) / seg
            ang = math.degrees(math.atan2(b[1] - a[1], b[0] - a[0]))
            if ang > 90 or ang < -90:
                ang += 180
            return (a[0] + t * (b[0] - a[0]), a[1] + t * (b[1] - a[1])), ang
        run += seg
    return pts[0], 0.0


def _network(msp, p: Projector, net: LvNetwork, layer: str, conductors: dict[str, str], min_label_m: float) -> None:
    for b in net.branches:
        pts = p.line(b.coordinates)
        if len(pts) < 2:
            continue
        msp.add_lwpolyline(pts, dxfattribs={"layer": layer})
        code = conductors.get(b.id)
        if code and b.length_m >= min_label_m:
            at, ang = _midpoint(pts)
            nx, ny = -math.sin(math.radians(ang)), math.cos(math.radians(ang))
            _label(msp, code, (at[0] + nx * 1.8, at[1] + ny * 1.8), TEXT_M * 0.8, ang)


def _stays(msp, p: Projector, net: LvNetwork, poles) -> None:
    """Each stayed pole: its stays point away from the resultant pull of its spans, 5 m long, fanned when two or more."""
    xy = {n.id: p.xy(n.coordinates) for n in net.nodes}
    adj: dict[str, list[str]] = {}
    for b in net.branches:
        adj.setdefault(b.from_node, []).append(b.to_node)
        adj.setdefault(b.to_node, []).append(b.from_node)
    for pole in poles:
        if not pole.stays or pole.id not in xy:
            continue
        here = xy[pole.id]
        rx = ry = 0.0
        for other in adj.get(pole.id, []):
            dx, dy = xy[other][0] - here[0], xy[other][1] - here[1]
            d = math.hypot(dx, dy) or 1.0
            rx, ry = rx + dx / d, ry + dy / d
        if math.hypot(rx, ry) < 1e-6:
            continue
        base = math.atan2(-ry, -rx)
        for k in range(pole.stays):
            ang = base + math.radians(20 * (k - (pole.stays - 1) / 2))
            end = (here[0] + 5 * math.cos(ang), here[1] + 5 * math.sin(ang))
            msp.add_line(here, end, dxfattribs={"layer": "RET-STAY"})
            msp.add_circle(end, 0.4, dxfattribs={"layer": "RET-STAY"})


def _zones(msp, p: Projector, d: Design) -> None:
    """Transformer zones: the hull of each transformer's buildings and service points, 5 m clear of them."""
    source_of = {f.id: f.source for f in d.lv.network.feeders}
    pts: dict[str, list[tuple[float, float]]] = {}
    for a in d.lv.allocation.allocations:
        s = source_of.get(a.feeder or "")
        if s is None:
            continue
        pts.setdefault(s, []).append(p.xy(a.at))
        if a.location:
            pts[s].append(p.xy(a.location))
    for t in d.transformers.transformers:
        group = pts.get(t.id, []) + [p.xy(t.coordinates)]
        if len(group) < 3:
            continue
        hull = MultiPoint(group).convex_hull.buffer(5.0, quad_segs=4)
        msp.add_lwpolyline(list(hull.exterior.coords)[:-1], close=True, dxfattribs={"layer": "RET-ZONE"})
        c = hull.centroid
        _label(msp, f"{t.label or t.id} zone", (c.x, c.y), TEXT_M * 1.4, layer="RET-ZONE")


def _model(msp, p: Projector, d: Design) -> None:
    under = d.construction == "underground"
    _network(msp, p, d.lv.network, "RET-LV-UG" if under else "RET-LV-OH", d.lv.sizing.conductors, 15.0)
    if d.mv_network is not None and d.mv is not None:
        mv_under = d.mv.construction == "underground"
        _network(msp, p, d.mv_network, "RET-MV-UG" if mv_under else "RET-MV-OH", d.mv.conductors, 40.0)
    service_layer = "RET-SERVICE-UG" if under else "RET-SERVICE"
    for a in d.lv.allocation.allocations:
        if a.location:
            msp.add_line(p.xy(a.location), p.xy(a.at), dxfattribs={"layer": service_layer})
    if d.services:
        for sv in d.services.services:
            for i, pole in enumerate(sv.poles, 1):
                at = p.xy(pole)
                msp.add_blockref("RET_POLE_LV", at, dxfattribs={"layer": "RET-SERVICE", "xscale": 0.7, "yscale": 0.7})
                _label(msp, f"SP{i} {d.services.pole_height_m:g}m", (at[0] + 1.8, at[1] + 1.8), TEXT_M * 0.6)
    if d.overhead:
        for pole in d.overhead.poles:
            if pole.kind == "source":
                continue
            at = p.xy(pole.coordinates)
            msp.add_blockref("RET_POLE_LV", at, dxfattribs={"layer": "RET-POLE"})
            tag = f"{pole.label or pole.id} {pole.height_m:g}m" + (f"/{pole.pole_class}" if pole.pole_class else "")
            _label(msp, tag, (at[0] + 2.2, at[1] + 2.2), TEXT_M * 0.8)
        _stays(msp, p, d.lv.network, d.overhead.poles)
    if d.mv_overhead and d.mv_network is not None:
        for pole in d.mv_overhead.poles:
            if pole.kind == "source":
                continue
            at = p.xy(pole.coordinates)
            msp.add_blockref("RET_POLE_MV", at, dxfattribs={"layer": "RET-POLE"})
            _label(msp, f"{pole.label or pole.id} {pole.height_m:g}m", (at[0] + 2.5, at[1] - 2.5), TEXT_M * 0.8)
        _stays(msp, p, d.mv_network, d.mv_overhead.poles)
    if under:
        for n in d.lv.network.nodes:
            if n.kind == "pole":
                at = p.xy(n.coordinates)
                msp.add_blockref("RET_KIOSK", at, dxfattribs={"layer": "RET-KIOSK"})
                _label(msp, n.label or n.id, (at[0], at[1] + 2.2), TEXT_M * 0.8)
    for t in d.transformers.transformers:
        at = p.xy(t.coordinates)
        msp.add_blockref("RET_MINISUB" if t.mounting == "minisub" else "RET_TX", at, dxfattribs={"layer": "RET-TX"})
        _label(msp, f"{t.label or t.id} {t.rating_kva:g} kVA", (at[0], at[1] + 4.0), TEXT_M * 1.2)
    if d.mv_network is not None:
        for n in d.mv_network.nodes:
            if n.kind == "source":
                at = p.xy(n.coordinates)
                msp.add_blockref("RET_CP", at, dxfattribs={"layer": "RET-CP"})
                _label(msp, "Connection point", (at[0], at[1] - 3.5), TEXT_M)
    _zones(msp, p, d)


def _sheet(doc, req: DocumentRequest, extents) -> None:
    s = stamp(req.meta, req.design)
    sheet = doc.layouts.new("A1 sheet")
    sheet.page_setup(size=SHEET, margins=(0, 0, 0, 0), units="mm")
    w, h = SHEET
    att = {"layer": "RET-SHEET"}
    sheet.add_lwpolyline([(10, 10), (w - 10, 10), (w - 10, h - 10), (10, h - 10)], close=True, dxfattribs=att)
    tb_w, tb_h = 260.0, 120.0
    x0, y0 = w - 10 - tb_w, 10.0
    sheet.add_lwpolyline([(x0, y0), (x0 + tb_w, y0), (x0 + tb_w, y0 + tb_h), (x0, y0 + tb_h)], close=True, dxfattribs=att)

    # Drawing viewport: everything left of the title block and legend column.
    (minx, miny), (maxx, maxy) = extents
    vw, vh = w - 20 - tb_w - 10, h - 20
    cx, cy = (minx + maxx) / 2, (miny + maxy) / 2
    span_h = max(maxy - miny, (maxx - minx) * vh / vw, 1.0) * 1.08
    vp = sheet.add_viewport(center=(10 + vw / 2 + 5, 10 + vh / 2), size=(vw, vh), view_center_point=(cx, cy), view_height=span_h)
    vp.dxf.layer = "RET-VIEWPORT"
    scale = span_h / vh * 1000
    nice = next((x for x in (250, 500, 1000, 1250, 2000, 2500, 5000, 10000, 20000) if x >= scale), round(scale))

    lines = [(f"{s.project}", 6.0), ("Electrical reticulation layout", 4.5)]
    lines += [(f"{k}: {v}", 2.6) for k, v in s.lines() if k not in ("Project", "Status")]
    lines += [(f"Drawing {s.number}" if s.number else "Drawing number not assigned", 2.6),
              (f"Model scale 1:{scale:,.0f} on A1 (about 1:{nice:,}); coordinates {Projector(req.design).zone}, metres", 2.6)]
    y = y0 + tb_h - 10
    for text, size in lines:
        sheet.add_text(text, height=size, dxfattribs=att).set_placement((x0 + 6, y))
        y -= size * 1.75
    status_h = 4.0
    status_lines = [NOT_FIT, *(f"- {r}" for r in s.reasons)] if not s.fit else [s.status]
    sy = y0 + tb_h + 12 + len(status_lines) * status_h * 1.6
    for i, text in enumerate(status_lines):
        sheet.add_text(text, height=status_h if i == 0 else 3.0,
                       dxfattribs={"layer": "RET-SHEET", "color": 1 if not s.fit else 3}).set_placement((x0, sy - i * status_h * 1.6))

    # Legend above the status, in the right-hand column.
    ly = h - 30.0
    sheet.add_text("Legend", height=5, dxfattribs=att).set_placement((x0, ly))
    ly -= 12
    entries: list[tuple[str, str, str]] = [("RET-MV-OH", "line", "MV overhead line"), ("RET-MV-UG", "line", "MV cable"),
                                           ("RET-LV-OH", "line", "LV overhead line (ABC)"), ("RET-LV-UG", "line", "LV cable"),
                                           ("RET-SERVICE", "line", "Service connection"), ("RET-ZONE", "line", "Transformer zone"),
                                           ("RET-TX", "RET_TX", "Pole-mounted transformer"), ("RET-TX", "RET_MINISUB", "Mini-substation"),
                                           ("RET-POLE", "RET_POLE_LV", "LV pole: tag, height/class"), ("RET-POLE", "RET_POLE_MV", "MV pole"),
                                           ("RET-SERVICE", "RET_POLE_LV", "Service pole (SP), on the service"),
                                           ("RET-KIOSK", "RET_KIOSK", "Metering kiosk"), ("RET-CP", "RET_CP", "Connection point")]
    for layer, what, text in entries:
        if what == "line":
            sheet.add_line((x0, ly), (x0 + 24, ly), dxfattribs={"layer": layer})
        else:
            sheet.add_blockref(what, (x0 + 12, ly), dxfattribs={"layer": layer, "xscale": 2.2, "yscale": 2.2})
        sheet.add_text(text, height=3.0, dxfattribs=att).set_placement((x0 + 32, ly - 1.2))
        ly -= 11
    sheet.add_text("Symbols after Eskom 240-87658920 Annex B; kiosk symbol Reticula's", height=2.4, dxfattribs=att).set_placement((x0, ly))


def render(req: DocumentRequest) -> bytes:
    d = req.design
    p = Projector(d)
    doc = ezdxf.new("R2010", setup=False, units=6)
    doc.header["$INSUNITS"] = 6
    doc.header["$LTSCALE"] = 1.0
    doc.header["$PSLTSCALE"] = 0
    _setup(doc)
    msp = doc.modelspace()
    _model(msp, p, d)
    pts = [p.xy(n.coordinates) for n in d.lv.network.nodes] + ([p.xy(n.coordinates) for n in d.mv_network.nodes] if d.mv_network else [])
    pts += [p.xy(a.location) for a in d.lv.allocation.allocations if a.location]
    if not pts:
        pts = [(0.0, 0.0)]
    extents = ((min(x for x, _ in pts) - 20, min(y for _, y in pts) - 20), (max(x for x, _ in pts) + 20, max(y for _, y in pts) + 20))
    _sheet(doc, req, extents)
    if "Layout1" in doc.layouts:
        doc.layouts.delete("Layout1")
    doc.set_modelspace_vport(height=extents[1][1] - extents[0][1], center=((extents[0][0] + extents[1][0]) / 2, (extents[0][1] + extents[1][1]) / 2))
    s = stamp(req.meta, d)
    doc.header["$PROJECTNAME"] = s.project[:100]
    buf = io.StringIO()
    doc.write(buf)
    return buf.getvalue().encode("utf-8")
