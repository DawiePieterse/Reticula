"""Overhead line design (plan 2.5): poles, spans, sag and tension, ground clearance, pole class and stays.

**Poles.** Every node of a route is a pole: ends, junctions, joints, marked pole sites and the LV board where a source
links in. So is every route vertex that turns the line by more than `min_deviation_deg`, unless it is closer than
`min_span_m` to the previous pole. Between those, intermediate poles go at equal spacing so that no span is longer
than `max_span_m`. `place_poles` returns the new poles as pole sites, so the network can be built again with them:
then every route branch is one span, and the loads have poles to take their services (plan 2.2).

**Sag and tension.** Spans between strain poles form a section, strung at one horizontal tension. Strain poles are
terminals, junctions, sources and angles over `stay_angle_deg`. The section's ruling span is √(ΣL³/ΣL). The everyday
tension (no wind, everyday temperature) is `everyday_tension_pct` of the breaking load; the parabolic state-change
equation gives the tension at the hottest state (largest sag) and at the coldest state with wind (largest tension):

    T₂²·(T₂ − T₁ + w₁²·S²·EA / (24·T₁²) + α·EA·(θ₂ − θ₁)) = w₂²·S²·EA / 24

with S the ruling span, w the load per metre (weight, and wind at the cold state), EA the axial stiffness and α the
expansion coefficient. Sag in each span is w·L²/(8·T) at the hot tension.

**Clearance.** The conductor is attached `attachment_below_top_m` below the top of a pole set `fraction·H + fixed`
deep. Mid-span clearance is the mean attachment level less the sag, against ground from contours where given (flat
otherwise). Each pole takes the shortest height of the list that clears its spans; a span that cannot clear fails.

**Poles and stays.** A pole carries wind on half of each adjacent span, plus the resultant of the conductor tensions
at the cold state. A stayed pole passes the tension resultant to its stay, of tension H / sin(slope). The pole class is
the smallest whose working tip load holds what is left.

Every value comes from the rules file's `overhead` section; all of it is a placeholder until ESKOM-OHL is held.
"""

from __future__ import annotations

import itertools
import math
from collections import defaultdict
from dataclasses import dataclass
from typing import Literal

from pydantic import BaseModel
from shapely.geometry import LineString

from ..issues import Issue, issue
from ..lv.network import CandidateIn, LvNetwork
from ..rules import RuleSet
from ..trace import Traced, traced
from .geometry import Frame, Ground, deviation_deg

Level = Literal["lv", "mv"]
G = 9.81

SAG_ID = "oh.sag.parabolic.v1"
SAG_FORMULA = "sag = w·L² / (8·T_hot); T_hot from the state-change equation over the ruling span"
TENSION_ID = "oh.tension.state-change.v1"
TENSION_FORMULA = "T₂²·(T₂ − T₁ + w₁²S²EA/(24T₁²) + α·EA·(θ₂ − θ₁)) = w₂²S²EA/24; S = √(ΣL³/ΣL); T₁ = everyday % × breaking load"
CLEARANCE_ID = "oh.clearance.midspan.v1"
CLEARANCE_FORMULA = "c = (h_a + z_a + h_b + z_b)/2 − sag − z_mid; h = H − (f·H + d) − attachment"
POLE_ID = "oh.pole.tip-load.v1"
POLE_FORMULA = "F_tip = p·d·(L₁ + L₂)/2 + |Σ T_cold·û| (unstayed); stay T = |Σ T_cold·û| / sin(slope)"


class Pole(BaseModel):
    id: str
    """The network node."""
    label: str | None = None
    network: Level
    coordinates: tuple[float, float]
    kind: Literal["terminal", "suspension", "angle", "junction", "source"]
    deviation_deg: float
    height_m: float
    pole_class: str | None
    tip_load_kn: float
    stays: int
    stay_tension_kn: float
    ground_m: float | None = None
    generated: bool
    """Placed by the design, not marked in the field."""
    passes: bool


class Span(BaseModel):
    id: str
    """The network branch."""
    network: Level
    from_pole: str
    to_pole: str
    conductor: str
    length_m: float
    section: str
    sag_m: float | None
    clearance_m: float | None
    passes: bool


class Section(BaseModel):
    id: str
    network: Level
    conductor: str
    spans: int
    ruling_span_m: float
    everyday_tension_kn: float
    hot_tension_kn: float
    cold_tension_kn: float
    max_tension_kn: float
    """The most the conductor may carry: max_tension_pct of its breaking load."""
    passes: bool


class OverheadResult(BaseModel):
    clause: str
    index: str | None
    poles: list[Pole]
    spans: list[Span]
    sections: list[Section]
    issues: list[Issue]
    worst_sag: Traced | None = None
    worst_clearance: Traced | None = None
    worst_tension: Traced | None = None
    worst_pole: Traced | None = None
    placeholders: list[str] = []


@dataclass(frozen=True)
class _Level:
    max_span: float
    min_span: float
    heights: tuple[float, ...]
    attach_below_top: float
    depth_fraction: float
    depth_fixed: float
    min_clearance: float
    everyday_pct: float
    max_pct: float


def _level(rules: RuleSet, level: Level) -> _Level:
    s = rules.section("overhead", "eskom/0.8.0")[level]
    return _Level(float(s["max_span_m"]), float(s["min_span_m"]), tuple(sorted(float(h) for h in s["pole_heights_m"])),
                  float(s["attachment_below_top_m"]), float(s["setting_depth_fraction"]), float(s["setting_depth_fixed_m"]),
                  float(s["min_ground_clearance_m"]), float(s["everyday_tension_pct"]), float(s["max_tension_pct"]))


# ---------------------------------------------------------------- pole placement


def place_poles(net: LvNetwork, rules: RuleSet, level: Level, prefix: str = "auto-pole") -> list[CandidateIn]:
    """Pole sites for every route node without one, every turn in a route, and between them at most max_span apart."""
    sec = rules.section("overhead", "eskom/0.8.0")
    lv = _level(rules, level)
    min_dev = float(sec["min_deviation_deg"])
    nodes = {n.id: n for n in net.nodes}
    frame = Frame([n.coordinates for n in net.nodes] or [(27.0, -26.0)])
    out: list[CandidateIn] = []
    seen: set[str] = set()

    def add(lonlat: tuple[float, float]) -> None:
        out.append(CandidateIn(id=f"{prefix}-{len(out) + 1}", kind="pole", geometry={"type": "Point", "coordinates": list(lonlat)}))

    for b in net.branches:
        if b.kind != "route":
            continue
        for end in (b.from_node, b.to_node):
            n = nodes[end]
            if end not in seen and n.kind not in ("pole", "source", "tap"):
                seen.add(end)
                add(n.coordinates)
        xy = [frame.xy(c) for c in b.coordinates]
        kept = [xy[0]]
        for i in range(1, len(xy) - 1):
            if deviation_deg(kept[-1], xy[i], xy[i + 1]) >= min_dev and math.dist(kept[-1], xy[i]) >= lv.min_span \
                    and math.dist(xy[i], xy[-1]) >= lv.min_span:
                kept.append(xy[i])
                add(frame.lonlat(xy[i]))
        kept.append(xy[-1])
        for a, c in itertools.pairwise(kept):
            d = math.dist(a, c)
            n = math.ceil(d / lv.max_span - 1e-9)
            for k in range(1, n):
                t = k / n
                add(frame.lonlat((a[0] + (c[0] - a[0]) * t, a[1] + (c[1] - a[1]) * t)))
    return out


# ---------------------------------------------------------------- sag, tension, clearance, poles


def state_change(t1: float, w1: float, w2: float, span: float, ea: float, alpha: float, d_theta: float) -> float:
    """Horizontal tension in a new state, N, from the parabolic state-change equation (see the module docstring)."""
    k = -t1 + w1 * w1 * span * span * ea / (24 * t1 * t1) + alpha * ea * d_theta
    m = w2 * w2 * span * span * ea / 24
    # f(T) = T³ + k·T² − m has one positive root; Newton from a point past it converges monotonically.
    t = max(t1, -k, 1.0) * 2 + 1.0
    for _ in range(200):
        f = t * t * t + k * t * t - m
        df = 3 * t * t + 2 * k * t
        step = f / df if df else 0.0
        t -= step
        if abs(step) < 1e-9 * max(t, 1.0):
            break
    return t


@dataclass
class _Chain:
    spans: list[str]
    conductor: str


def check(net: LvNetwork, rules: RuleSet, level: Level, conductors: dict[str, str], default_conductor: str,
          generated: set[str], ground: Ground | None = None) -> OverheadResult:
    """Sag, tension, clearance, pole class and stays for an overhead network whose route branches are single spans."""
    sec = rules.section("overhead", "eskom/0.8.0")
    lv = _level(rules, level)
    st = sec["states"]
    mech = sec["mechanical"]
    stay_angle = float(sec["stay_angle_deg"])
    slope = math.radians(float(sec["stay_slope_deg"]))
    classes = sorted(((c["class"], float(c["tip_load_kn"])) for c in sec["pole_classes"]), key=lambda c: c[1])
    issues: list[Issue] = []
    frame = Frame([n.coordinates for n in net.nodes] or [(27.0, -26.0)])
    nodes = {n.id: n for n in net.nodes}
    xy = {n.id: frame.xy(n.coordinates) for n in net.nodes}
    spans_in = [b for b in net.branches if b.kind in ("route", "link")]
    code = {b.id: conductors.get(b.id, default_conductor) for b in spans_in}
    length = {b.id: math.dist(xy[b.from_node], xy[b.to_node]) for b in spans_in}
    at: dict[str, list[str]] = defaultdict(list)
    for b in spans_in:
        at[b.from_node].append(b.id)
        at[b.to_node].append(b.id)
    ends = {b.id: (b.from_node, b.to_node) for b in spans_in}

    def other(bid: str, nid: str) -> str:
        a, c = ends[bid]
        return c if a == nid else a

    def dev(nid: str) -> float:
        if len(at[nid]) != 2:
            return 0.0
        a, c = (other(b, nid) for b in at[nid])
        return deviation_deg(xy[a], xy[nid], xy[c])

    def pole_kind(nid: str) -> str:
        n = nodes[nid]
        if n.kind in ("source", "tap"):
            return "source"
        k = len(at[nid])
        return "terminal" if k <= 1 else "junction" if k >= 3 else "angle" if dev(nid) > stay_angle else "suspension"

    kinds = {nid: pole_kind(nid) for nid in at}

    # Sections: chains of spans through suspension poles of one conductor.
    chains: list[_Chain] = []
    done: set[str] = set()
    for b in spans_in:
        if b.id in done:
            continue
        chain, stack = [b.id], [b.id]
        done.add(b.id)
        while stack:
            cur = stack.pop()
            for nid in ends[cur]:
                if kinds[nid] != "suspension":
                    continue
                for nb in at[nid]:
                    if nb not in done and code[nb] == code[b.id]:
                        done.add(nb)
                        chain.append(nb)
                        stack.append(nb)
        chains.append(_Chain(sorted(chain, key=lambda s: (len(s), s)), code[b.id]))

    no_mech: set[str] = set()
    sections: list[Section] = []
    tension_hot: dict[str, float] = {}
    tension_cold: dict[str, float] = {}
    section_of: dict[str, str] = {}
    worst_t = None
    prefix = level.upper()
    for i, ch in enumerate(chains, start=1):
        sid = f"{prefix}-S{i}"
        m = mech.get(ch.conductor)
        lens = [max(length[s], 0.1) for s in ch.spans]
        ruling = math.sqrt(sum(x**3 for x in lens) / sum(lens))
        for s in ch.spans:
            section_of[s] = sid
        if m is None:
            no_mech.add(ch.conductor)
            continue
        w = float(m["mass_kg_per_km"]) / 1000 * G
        wind = float(st["wind_pressure_pa"]) * float(m["diameter_mm"]) / 1000
        w_cold = math.hypot(w, wind)
        ea = float(m["modulus_gpa"]) * 1e9 * float(m["area_mm2"]) * 1e-6
        alpha = float(m["expansion_per_c"])
        breaking = float(m["breaking_load_kn"]) * 1000
        t0 = lv.everyday_pct / 100 * breaking
        th = state_change(t0, w, w, ruling, ea, alpha, float(st["max_temp_c"]) - float(st["everyday_temp_c"]))
        tc = state_change(t0, w, w_cold, ruling, ea, alpha, float(st["min_temp_c"]) - float(st["everyday_temp_c"]))
        allowed = lv.max_pct / 100 * breaking
        ok = max(t0, th, tc) <= allowed + 1e-6
        sections.append(Section(id=sid, network=level, conductor=ch.conductor, spans=len(ch.spans), ruling_span_m=round(ruling, 2),
                                everyday_tension_kn=round(t0 / 1000, 3), hot_tension_kn=round(th / 1000, 3), cold_tension_kn=round(tc / 1000, 3),
                                max_tension_kn=round(allowed / 1000, 3), passes=ok))
        for s in ch.spans:
            tension_hot[s] = th
            tension_cold[s] = tc
        ratio = max(t0, th, tc) / allowed
        if worst_t is None or ratio > worst_t[0]:
            worst_t = (ratio, sid, ch.conductor, t0, th, tc, allowed, ruling, w, w_cold, ea, alpha)

    # Ground and pole heights: the shortest height that clears every adjacent span.
    z = {nid: (ground.at(xy[nid]) if ground else None) for nid in at}

    def z_or_0(v: float | None) -> float:
        return v if v is not None else 0.0

    def attach(h: float) -> float:
        return h - (lv.depth_fraction * h + lv.depth_fixed) - lv.attach_below_top

    sag: dict[str, float] = {}
    mid_z: dict[str, float] = {}
    for b in spans_in:
        m = mech.get(code[b.id])
        if m is None or b.id not in tension_hot:
            continue
        w = float(m["mass_kg_per_km"]) / 1000 * G
        sag[b.id] = w * length[b.id] ** 2 / (8 * tension_hot[b.id])
        a, c = xy[b.from_node], xy[b.to_node]
        zm = ground.at(((a[0] + c[0]) / 2, (a[1] + c[1]) / 2)) if ground else None
        mid_z[b.id] = z_or_0(zm) if zm is not None else (z_or_0(z[b.from_node]) + z_or_0(z[b.to_node])) / 2

    height = {nid: lv.heights[0] for nid in at}

    def clearance(bid: str) -> float | None:
        if bid not in sag:
            return None
        a, c = ends[bid]
        return (attach(height[a]) + z_or_0(z[a]) + attach(height[c]) + z_or_0(z[c])) / 2 - sag[bid] - mid_z[bid]

    for _ in range(len(lv.heights) * 2):
        raised = False
        for b in spans_in:
            cl = clearance(b.id)
            if cl is None or cl >= lv.min_clearance:
                continue
            for nid in ends[b.id]:
                if height[nid] < lv.heights[-1]:
                    height[nid] = next(h for h in lv.heights if h > height[nid])
                    raised = True
        if not raised:
            break

    spans: list[Span] = []
    worst_c = None
    worst_s = None
    for b in spans_in:
        cl = clearance(b.id)
        ok = cl is None or cl >= lv.min_clearance - 1e-6
        ok = ok and next((s.passes for s in sections if s.id == section_of[b.id]), True)
        spans.append(Span(id=b.id, network=level, from_pole=b.from_node, to_pole=b.to_node, conductor=code[b.id],
                          length_m=round(length[b.id], 2), section=section_of[b.id],
                          sag_m=round(sag[b.id], 3) if b.id in sag else None, clearance_m=round(cl, 3) if cl is not None else None, passes=ok))
        if cl is not None and (worst_c is None or cl < worst_c[0]):
            worst_c = (cl, b.id)
        if b.id in sag and (worst_s is None or sag[b.id] > worst_s[0]):
            worst_s = (sag[b.id], b.id)

    # Pole loads and stays, at the cold state with wind.
    poles: list[Pole] = []
    worst_p = None
    for nid in sorted(at, key=lambda x: (len(x), x)):
        hx = hy = 0.0
        wind_load = 0.0
        for bid in at[nid]:
            far = other(bid, nid)
            dx, dy = xy[far][0] - xy[nid][0], xy[far][1] - xy[nid][1]
            d = math.hypot(dx, dy) or 1.0
            t = tension_cold.get(bid, 0.0)
            hx += t * dx / d
            hy += t * dy / d
            m = mech.get(code[bid])
            if m is not None:
                wind_load += float(st["wind_pressure_pa"]) * float(m["diameter_mm"]) / 1000 * length[bid] / 2
        resultant = math.hypot(hx, hy)
        kind = kinds[nid]
        stayed = kind in ("terminal", "angle", "junction") or (kind == "source" and resultant > 1.0)
        stays = 0 if not stayed or resultant < 1.0 else 1
        stay_t = resultant / math.sin(slope) if stays else 0.0
        tip = wind_load + (0.0 if stays else resultant)
        cls = next((c for c, cap in classes if cap * 1000 >= tip), None)
        n = nodes[nid]
        poles.append(Pole(id=nid, label=n.label, network=level, coordinates=n.coordinates, kind=kind, deviation_deg=round(dev(nid), 2),
                          height_m=height[nid], pole_class=cls, tip_load_kn=round(tip / 1000, 3), stays=stays,
                          stay_tension_kn=round(stay_t / 1000, 3), ground_m=round(z[nid], 2) if z[nid] is not None else None,
                          generated=nid in generated, passes=cls is not None))
        if worst_p is None or tip > worst_p[0]:
            worst_p = (tip, nid, wind_load, resultant, stays, cls)

    if no_mech:
        issues.append(issue("warning", "no_mechanical", "These conductors have no mechanical data in the rules file, so their sag, "
                            "tension and clearance were not checked.", sorted(no_mech)))
    bad_t = [s.id for s in sections if not s.passes]
    if bad_t:
        issues.append(issue("error", "tension_over_limit", f"Conductor tension exceeds {lv.max_pct:g} % of the breaking load. "
                            "Shorten the spans or use a stronger conductor.", bad_t))
    low = [s for s in spans if s.clearance_m is not None and s.clearance_m < lv.min_clearance - 1e-6]
    if low:
        issues.append(issue("error", "clearance", f"Spans clear the ground by less than {lv.min_clearance:g} m even on "
                            f"{lv.heights[-1]:g} m poles. Add a pole or use a taller one.", [s.id for s in low],
                            [_mid(net, s.id) for s in low]))
    weak = [p for p in poles if p.pole_class is None]
    if weak:
        issues.append(issue("error", "pole_overloaded", "No pole class carries the load at these poles. Stay them or shorten the spans.",
                            [p.label or p.id for p in weak], [p.coordinates for p in weak]))

    o = rules.data["overhead"]
    clause = o.get("clause", "")
    return OverheadResult(
        clause=clause, index=o.get("index"), poles=poles, spans=spans, sections=sections, issues=issues,
        worst_sag=_sag_trace(rules, worst_s, spans, sections, clause) if worst_s else None,
        worst_clearance=_clearance_trace(rules, worst_c, lv, clause) if worst_c else None,
        worst_tension=_tension_trace(rules, worst_t, lv, st, clause) if worst_t else None,
        worst_pole=_pole_trace(rules, worst_p, slope, clause) if worst_p else None,
        placeholders=[f"overhead line settings ({level.upper()}: spans, clearances, tensions, pole classes; ESKOM-OHL not held)",
                      "conductor mechanical data (typical values)"],
    )


def _mid(net: LvNetwork, bid: str) -> tuple[float, float]:
    b = next(x for x in net.branches if x.id == bid)
    p = LineString(b.coordinates).interpolate(0.5, normalized=True)
    return (p.x, p.y)


def _sag_trace(rules: RuleSet, worst, spans: list[Span], sections: list[Section], clause: str) -> Traced:
    value, bid = worst
    span = next(s for s in spans if s.id == bid)
    section = next(s for s in sections if s.id == span.section)
    return traced(value, "m", formula_id=SAG_ID, formula=SAG_FORMULA, clause=clause, rules_hash=rules.hash, inputs={
        "span": (bid, "", "largest sag"), "L": (span.length_m, "m", "pole to pole"), "conductor": (span.conductor, "", "span"),
        "T_hot": (section.hot_tension_kn, "kN", f"section {section.id} at the hottest state"),
        "ruling_span": (section.ruling_span_m, "m", "√(ΣL³/ΣL)")})


def _clearance_trace(rules: RuleSet, worst, lv: _Level, clause: str) -> Traced:
    value, bid = worst
    return traced(value, "m", formula_id=CLEARANCE_ID, formula=CLEARANCE_FORMULA, clause=clause, rules_hash=rules.hash, inputs={
        "span": (bid, "", "least clearance"), "minimum": (lv.min_clearance, "m", f"rules {rules.ref} overhead (placeholder)"),
        "attachment": (lv.attach_below_top, "m", "below the pole top"),
        "setting_depth": (f"{lv.depth_fraction:g}·H + {lv.depth_fixed:g}", "m", f"rules {rules.ref} overhead (placeholder)")})


def _tension_trace(rules: RuleSet, worst, lv: _Level, st: dict, clause: str) -> Traced:
    _, sid, conductor, t0, th, tc, allowed, ruling, w, w_cold, ea, alpha = worst
    return traced(max(t0, th, tc) / 1000, "kN", formula_id=TENSION_ID, formula=TENSION_FORMULA, clause=clause, rules_hash=rules.hash, inputs={
        "section": (sid, "", "highest tension against its limit"), "conductor": (conductor, "", "section"),
        "S": (round(ruling, 2), "m", "ruling span"), "T_everyday": (round(t0 / 1000, 3), "kN", f"{lv.everyday_pct:g} % of breaking load (placeholder)"),
        "T_hot": (round(th / 1000, 3), "kN", f"{st['max_temp_c']} °C"), "T_cold": (round(tc / 1000, 3), "kN", f"{st['min_temp_c']} °C with {st['wind_pressure_pa']} Pa wind"),
        "w": (round(w, 3), "N/m", "conductor weight"), "w_cold": (round(w_cold, 3), "N/m", "weight and wind"),
        "EA": (round(ea / 1000, 1), "kN", "modulus × area"), "alpha": (alpha, "1/°C", "expansion"),
        "limit": (round(allowed / 1000, 3), "kN", f"{lv.max_pct:g} % of breaking load (placeholder)")})


def _pole_trace(rules: RuleSet, worst, slope: float, clause: str) -> Traced:
    tip, nid, wind_load, resultant, stays, cls = worst
    return traced(tip / 1000, "kN", formula_id=POLE_ID, formula=POLE_FORMULA, clause=clause, rules_hash=rules.hash, inputs={
        "pole": (nid, "", "largest tip load"), "wind": (round(wind_load / 1000, 3), "kN", "on half of each adjacent span"),
        "tension_resultant": (round(resultant / 1000, 3), "kN", "at the cold state"), "stays": (stays, "", "taking the resultant"),
        "stay_slope": (round(math.degrees(slope), 1), "°", f"rules {rules.ref} overhead (placeholder)"),
        "class": (cls or "none", "", f"rules {rules.ref} overhead.pole_classes (placeholder)")})
