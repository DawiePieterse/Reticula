"""LV overhead line checks (plan 2.5): spans, sag and tension, ground clearance, pole loads and stays.

Every marked pole and every source is a **support**. A span runs straight between two supports, along a route that
may have joints but no other support. Route ends and junctions that are not at a pole are flagged, and checked as if
a pole stood there: the conductor cannot end or branch in mid-air. Where a route strays further than the rules
file allows from the straight line between its supports, a pole is missing at a bend.

Each support has a **role**:
- terminal (one span) or junction (three or more)
- intermediate or angle (two spans), by how far the line deviates there.

Terminals, junctions, sources, sharp angles and changes of conductor are strain supports. They split the line into
**strain sections**, whose spans share one horizontal tension.

**Tension.** A section is strung at the everyday tension, unless that would pull harder than the maximum tension in
the cold or wind case; then it is strung slacker, so that the worse of those just reaches the maximum. Short spans
are usually governed this way. A section's tension in each loading case follows from its everyday tension by the
parabolic state change equation on the section's ruling span, RS = √(Σl³/Σl):

    H₂²·(H₂ − H₁ + E·A·w₁²·RS²/(24·H₁²) + E·A·α·(θ₂ − θ₁)) = E·A·w₂²·RS²/24

Here w is the weight of the bundle per metre combined with the wind on its diameter, and A is the area of all its
cores, since self-supporting ABC carries the tension on every core.

**Sag and clearance.** A span's sag is w·l²/(8·H) at the hot case. The ground clearance at mid-span is the attachment
height less the sag, on flat ground with both ends at the same height.

**Pole load.** The horizontal load at the attachment is |Σ H·u| over the spans at the support (u points along each
span), plus the wind on half of each span. The governing case is the larger of cold and wind. The lightest pole class
that carries the load unstayed is chosen. A support whose load is beyond every class is stayed, and so is every
terminal when the rules say so. The stay then carries the load at its angle.

All settings come from the rules file's `lv_overhead` section; every one is a placeholder until the Eskom overhead line
standard is held, and the results say so.
"""

from __future__ import annotations

import math
from typing import Literal

from pydantic import BaseModel
from pyproj import Geod
from scipy.optimize import brentq
from shapely.geometry import LineString, Point

from ..geo import crs as crs_mod
from ..rules import RulesError, RuleSet
from ..trace import Traced, traced
from .network import MAX_SAMPLES, Issue, LvNetwork

GEOD = Geod(ellps="WGS84")
G = 9.81
CLEARANCE_ID = "lv.oh.clearance.v1"
CLEARANCE_FORMULA = "clearance = h_attach − w·l²/(8·H_hot); H_hot from the everyday tension by the parabolic state change on the ruling span"
POLE_LOAD_ID = "lv.oh.pole-load.v1"
POLE_LOAD_FORMULA = "F = |Σ H_i·u_i| + Σ p·d·l_i/2, the larger of the cold and wind cases"
CASES = ("everyday", "hot", "cold", "wind")

Role = Literal["terminal", "intermediate", "angle", "strain", "junction"]


class OverheadRequest(BaseModel):
    rules: str
    network: LvNetwork
    conductors: dict[str, str] = {}
    """Conductor per branch id; branches not listed use the rules file's lv_design default."""


class SpanResult(BaseModel):
    id: str
    from_node: str
    to_node: str
    from_label: str
    to_label: str
    branches: list[str]
    feeder: str | None
    conductor: str
    length_m: float
    bend_m: float
    """How far the route strays from the straight line between the supports."""
    section: str | None
    sag_m: float | None
    clearance_m: float | None
    tension_kn: float | None
    """The section's greatest tension, cold or wind."""
    passes: bool
    coordinates: list[tuple[float, float]]


class SupportResult(BaseModel):
    id: str
    label: str
    kind: str
    """The network node's kind: pole, source, junction or end."""
    marked: bool
    """A marked pole or source; false where a pole is needed but not marked."""
    role: Role
    spans: int
    deviation_deg: float | None
    load_kn: float | None
    governing_case: str | None
    pole_class: str | None
    """None at a source (the transformer structure is designed with it) or where no load could be worked out."""
    stay: bool
    stay_tension_kn: float | None
    passes: bool
    coordinates: tuple[float, float]


class SectionResult(BaseModel):
    id: str
    spans: list[str]
    conductor: str
    ruling_span_m: float
    tension_kn: dict[str, float]
    """Horizontal tension in each loading case."""
    governing: Literal["everyday", "max_tension"]
    """What set the everyday tension: the everyday limit, or keeping the cold and wind cases within the maximum tension."""
    max_pull_kn: float
    passes: bool


class OverheadSummary(BaseModel):
    spans: int
    longest_span_m: float
    sections: int
    supports: int
    poles_needed: int
    stays: int
    pole_classes: dict[str, int]


class Overhead(BaseModel):
    rules_ref: str
    rules_hash: str
    clause: str
    summary: OverheadSummary
    spans: list[SpanResult]
    supports: list[SupportResult]
    sections: list[SectionResult]
    issues: list[Issue]
    lowest_clearance: Traced | None = None
    highest_pole_load: Traced | None = None
    placeholders: list[str] = []


def state_change(h1: float, w1: float, t1: float, w2: float, t2: float, span: float, ea: float, alpha: float) -> float:
    """Horizontal tension (N) in a second state, from the first, on a ruling span (parabolic state change)."""
    k = ea * w1 * w1 * span * span / (24 * h1 * h1) + ea * alpha * (t2 - t1)
    c = ea * w2 * w2 * span * span / 24

    def g(h: float) -> float:
        return h * h * (h + k - h1) - c

    hi = max(h1, 1.0)
    while g(hi) < 0:
        hi *= 2
    return float(brentq(g, 1e-9, hi, xtol=1e-9, rtol=1e-12))


def check_overhead(req: OverheadRequest, rules: RuleSet) -> Overhead:
    sec = rules.data.get("lv_overhead")
    if not sec:
        raise RulesError(f"rules {rules.ref} has no lv_overhead section; overhead checks need eskom/0.7.0 or later")
    design = rules.data.get("lv_design") or {}
    default = design.get("default_conductor")
    net = req.network
    issues: list[Issue] = []
    clause = sec.get("clause", "")
    pole = sec["pole"]
    h_attach = float(pole["length_m"]) - float(pole["planting_depth_m"]) - float(pole["attach_below_top_m"])
    classes = sorted(sec["pole_classes"], key=lambda c: float(c["capacity_kn"]))
    stay_cfg = sec["stay"]

    nodes = {n.id: n for n in net.nodes}
    if not nodes:
        return _empty(rules, clause, issues)
    lon0 = sum(n.coordinates[0] for n in net.nodes) / len(net.nodes)
    to_xy = crs_mod.from_wgs84(crs_mod.nearest_lo(lon0))

    def xy(ll: tuple[float, float]) -> tuple[float, float]:
        return to_xy.transform(*ll)

    def is_support(nid: str) -> bool:
        return nodes[nid].kind in ("pole", "source", "junction", "end")

    def conductor_of(bid: str) -> str:
        code = req.conductors.get(bid, default)
        if code is None:
            raise RulesError(f"rules {rules.ref} has no lv_design.default_conductor and branch {bid} has none")
        return code

    # ---- spans: support to support through joints ----
    incident: dict[str, list[str]] = {}
    branches = {b.id: b for b in net.branches}
    for b in net.branches:
        incident.setdefault(b.from_node, []).append(b.id)
        incident.setdefault(b.to_node, []).append(b.id)
    used: set[str] = set()
    raw_spans: list[tuple[str, str, list[str], list[tuple[float, float]]]] = []
    for start in (n.id for n in net.nodes if is_support(n.id)):
        for bid in incident.get(start, []):
            if bid in used:
                continue
            path_ids, coords, cur, via = [], [nodes[start].coordinates], start, bid
            while True:
                used.add(via)
                b = branches[via]
                pts = b.coordinates if b.from_node == cur else b.coordinates[::-1]
                coords += pts[1:]
                path_ids.append(via)
                cur = b.to_node if b.from_node == cur else b.from_node
                if is_support(cur):
                    break
                onward = [x for x in incident.get(cur, []) if x not in used]
                if not onward:
                    break
                via = onward[0]
            if cur != start:
                raw_spans.append((start, cur, path_ids, coords))

    spans: list[SpanResult] = []
    span_dir: dict[tuple[str, str], tuple[float, float]] = {}  # (span, support) -> unit vector away from the support
    for k, (a, b, ids, coords) in enumerate(raw_spans):
        pa, pb = xy(nodes[a].coordinates), xy(nodes[b].coordinates)
        _, _, length = GEOD.inv(*nodes[a].coordinates, *nodes[b].coordinates)
        chord = LineString([pa, pb])
        bend = max((chord.distance(Point(xy(c))) for c in coords), default=0.0)
        sid = f"S{k + 1}"
        d = math.dist(pa, pb) or 1.0
        span_dir[(sid, a)] = ((pb[0] - pa[0]) / d, (pb[1] - pa[1]) / d)
        span_dir[(sid, b)] = ((pa[0] - pb[0]) / d, (pa[1] - pb[1]) / d)
        spans.append(SpanResult(
            id=sid, from_node=a, to_node=b, from_label=_label(nodes[a]), to_label=_label(nodes[b]), branches=ids,
            feeder=branches[ids[0]].feeder, conductor=conductor_of(ids[0]), length_m=round(length, 2), bend_m=round(bend, 2),
            section=None, sag_m=None, clearance_m=None, tension_kn=None, passes=True,
            coordinates=[nodes[a].coordinates, nodes[b].coordinates]))
    by_id = {s.id: s for s in spans}
    at_support: dict[str, list[str]] = {}
    for s in spans:
        at_support.setdefault(s.from_node, []).append(s.id)
        at_support.setdefault(s.to_node, []).append(s.id)

    # ---- roles ----
    roles: dict[str, tuple[Role, float | None]] = {}
    strain: set[str] = set()
    for nid, sids in at_support.items():
        n = nodes[nid]
        deviation = None
        if len(sids) == 1:
            role: Role = "terminal"
        elif len(sids) >= 3:
            role = "junction"
        else:
            u1, u2 = span_dir[(sids[0], nid)], span_dir[(sids[1], nid)]
            between = math.degrees(math.acos(max(-1.0, min(1.0, u1[0] * u2[0] + u1[1] * u2[1]))))
            deviation = round(180.0 - between, 2)
            if deviation <= float(sec["intermediate_max_deg"]):
                role = "intermediate"
            elif deviation <= float(sec["strain_angle_deg"]):
                role = "angle"
            else:
                role = "strain"
            if role != "strain" and by_id[sids[0]].conductor != by_id[sids[1]].conductor:
                role = "strain"
        roles[nid] = (role, deviation)
        if role in ("terminal", "junction", "strain") or n.kind == "source":
            strain.add(nid)

    # ---- strain sections ----
    parent = {s.id: s.id for s in spans}

    def find(x: str) -> str:
        while parent[x] != x:
            parent[x] = parent[parent[x]]
            x = parent[x]
        return x

    for nid, sids in at_support.items():
        if nid not in strain and len(sids) == 2:
            parent[find(sids[0])] = find(sids[1])
    groups: dict[str, list[str]] = {}
    for s in spans:
        groups.setdefault(find(s.id), []).append(s.id)

    mech = sec["conductors"]
    materials = sec["materials"]
    everyday = sec["everyday"]
    cases = sec["cases"]
    sections: list[SectionResult] = []
    tension: dict[str, dict[str, float]] = {}  # span -> case -> N
    wind_per_m: dict[str, dict[str, float]] = {}  # span -> case -> N/m
    no_mech: set[str] = set()
    section_data: dict[str, tuple[float, float, dict]] = {}
    for k, members in enumerate(sorted(groups.values(), key=lambda m: int(m[0][1:]))):
        tid = f"T{k + 1}"
        code = by_id[members[0]].conductor
        for m in members:
            by_id[m].section = tid
        md = mech.get(code)
        if md is None:
            no_mech.add(code)
            continue
        cond = rules.conductor(code)
        mat = materials.get(cond.material or "al")
        if mat is None or not cond.size_mm2 or not cond.cores:
            no_mech.add(code)
            continue
        lengths = [by_id[m].length_m for m in members]
        rs = math.sqrt(sum(x**3 for x in lengths) / sum(lengths))
        ea = float(mat["modulus_gpa"]) * 1e9 * cond.cores * cond.size_mm2 * 1e-6
        alpha = float(mat["expansion_per_c"])
        weight = float(md["mass_kg_per_m"]) * G
        dia = float(md["diameter_mm"]) / 1000

        def w(case: dict, weight: float = weight, dia: float = dia) -> float:
            return math.hypot(weight, float(case["wind_pa"]) * dia)

        def state(h1: float, name: str, w=w, rs=rs, ea=ea, alpha=alpha) -> float:
            return state_change(h1, w(everyday), float(everyday["temp_c"]), w(cases[name]), float(cases[name]["temp_c"]), rs, ea, alpha)

        max_pull = float(md["max_pull_kn"])
        limit = float(everyday["max_tension_pct"]) / 100 * max_pull * 1000
        h1, governing = float(everyday["tension_pct"]) / 100 * max_pull * 1000, "everyday"
        if max(state(h1, "cold"), state(h1, "wind")) > limit:
            # Short spans tighten most in the cold: string them slacker, so the worst case just reaches the limit.
            h1 = float(brentq(lambda h, lim=limit: max(state(h, "cold"), state(h, "wind")) - lim, 1.0, h1, xtol=1e-6))
            governing = "max_tension"
        hs = {"everyday": h1, **{name: state(h1, name) for name in ("hot", "cold", "wind")}}
        top = max(hs["cold"], hs["wind"]) / 1000
        sections.append(SectionResult(id=tid, spans=members, conductor=code, ruling_span_m=round(rs, 2),
                                      tension_kn={c: round(hs[c] / 1000, 3) for c in CASES}, governing=governing,
                                      max_pull_kn=max_pull, passes=top <= max_pull + 1e-6))
        section_data[tid] = (rs, weight, md)
        for m in members:
            s = by_id[m]
            tension[m] = hs
            wind_per_m[m] = {c: float((cases.get(c) or everyday)["wind_pa"]) * dia for c in CASES}
            s.sag_m = round(weight * s.length_m**2 / (8 * hs["hot"]), 3)
            s.clearance_m = round(h_attach - s.sag_m, 3)
            s.tension_kn = round(top, 3)

    # ---- supports: load, class, stay ----
    supports: list[SupportResult] = []
    stay_angle = math.radians(float(stay_cfg["angle_from_ground_deg"]))
    for n in net.nodes:
        if n.id not in at_support:
            continue
        sids = at_support[n.id]
        role, deviation = roles[n.id]
        load, governing = None, None
        if all(m in tension for m in sids):
            best = (-1.0, "")
            for case in ("everyday", "cold", "wind"):
                fx = sum(tension[m][case] * span_dir[(m, n.id)][0] for m in sids)
                fy = sum(tension[m][case] * span_dir[(m, n.id)][1] for m in sids)
                f = math.hypot(fx, fy) + sum(wind_per_m[m][case] * by_id[m].length_m / 2 for m in sids)
                if f > best[0]:
                    best = (f, case)
            load, governing = best[0] / 1000, best[1]
        stay = bool(role == "terminal" and stay_cfg["terminals"])
        pole_class = None
        if load is not None:
            fits = [c for c in classes if float(c["capacity_kn"]) >= load]
            if not fits:
                stay = True
            if n.kind != "source":
                pole_class = classes[0]["code"] if stay else fits[0]["code"]
        stay_tension = round(load / math.cos(stay_angle), 3) if stay and load is not None else None
        ok = stay_tension is None or stay_tension <= float(stay_cfg["capacity_kn"])
        marked = n.kind in ("pole", "source")
        supports.append(SupportResult(
            id=n.id, label=_label(n), kind=n.kind, marked=marked, role=role, spans=len(sids), deviation_deg=deviation,
            load_kn=round(load, 3) if load is not None else None, governing_case=governing, pole_class=pole_class, stay=stay,
            stay_tension_kn=stay_tension, passes=ok and marked, coordinates=n.coordinates))

    # ---- span checks ----
    max_span = float(sec["max_span_m"])
    min_clear = float(sec["min_ground_clearance_m"])
    section_ok = {t.id: t.passes for t in sections}
    for s in spans:
        s.passes = (s.length_m <= max_span and (s.clearance_m is None or s.clearance_m >= min_clear)
                    and section_ok.get(s.section or "", True))

    # ---- issues ----
    unmarked = [p for p in supports if not p.marked]
    if unmarked:
        issues.append(_issue("error", "no_support", "Route ends and junctions need a pole. Mark one at each.", unmarked))
    long_spans = sorted((s for s in spans if s.length_m > max_span), key=lambda s: -s.length_m)
    if long_spans:
        issues.append(_span_issue("error", "span_too_long",
                                  f"Spans are longer than {max_span:g} m. Mark a pole part way along.", long_spans))
    bends = sorted((s for s in spans if s.bend_m > float(sec["bend_tolerance_m"])), key=lambda s: -s.bend_m)
    if bends:
        issues.append(_span_issue("warning", "bend_without_pole",
                                  f"Routes bend more than {float(sec['bend_tolerance_m']):g} m away from the straight line "
                                  "between poles. Mark a pole at the bend.", bends))
    low = sorted((s for s in spans if s.clearance_m is not None and s.clearance_m < min_clear), key=lambda s: s.clearance_m or 0)
    if low:
        issues.append(_span_issue("error", "clearance",
                                  f"The conductor sags below {min_clear:g} m above ground. Shorten the span or use a longer pole.", low))
    taut = [t for t in sections if not t.passes]
    if taut:
        issues.append(Issue(severity="error", code="over_tension",
                            message="Strain sections pull harder than the conductor allows in the cold or windy case.",
                            count=len(taut), samples=[t.id for t in taut][:MAX_SAMPLES]))
    stays_over = [p for p in supports if p.stay_tension_kn is not None and p.stay_tension_kn > float(stay_cfg["capacity_kn"])]
    if stays_over:
        issues.append(_issue("error", "stay_overloaded",
                             f"Stays would carry more than {float(stay_cfg['capacity_kn']):g} kN. Use two stays or a strut.", stays_over))
    if no_mech:
        issues.append(Issue(severity="warning", code="no_mech_data",
                            message="Conductors without mechanical data in the rules file; their sag, tension and pole loads are not checked.",
                            count=len(no_mech), samples=sorted(no_mech)[:MAX_SAMPLES]))
    placeholders = ["the Eskom overhead line settings (span, clearance, pole, pole classes, stays and loading cases)",
                    "the modulus and expansion of aluminium"]
    for code in sorted({t.conductor for t in sections}):
        md = mech.get(code, {})
        if md.get("placeholder"):
            placeholders.append(f"{code} {', '.join(md['placeholder'])}")
    issues.append(Issue(severity="warning", code="overhead_placeholders",
                        message="The overhead checks use placeholder values and are not fit to submit: " + "; ".join(placeholders) + "."))

    stays = [p for p in supports if p.stay]
    by_class: dict[str, int] = {}
    for p in supports:
        if p.pole_class:
            by_class[p.pole_class] = by_class.get(p.pole_class, 0) + 1
    return Overhead(
        rules_ref=rules.ref, rules_hash=rules.hash, clause=clause,
        summary=OverheadSummary(spans=len(spans), longest_span_m=max((s.length_m for s in spans), default=0.0), sections=len(sections),
                                supports=len(supports), poles_needed=len(unmarked), stays=len(stays), pole_classes=by_class),
        spans=spans, supports=supports, sections=sections, issues=issues,
        lowest_clearance=_clearance_trace(rules, spans, sections, section_data, h_attach, min_clear, clause),
        highest_pole_load=_load_trace(rules, supports, sec, clause),
        placeholders=placeholders,
    )


def _label(n) -> str:
    return n.label or n.id


def _issue(severity: Literal["error", "warning"], code: str, message: str, supports: list[SupportResult]) -> Issue:
    return Issue(severity=severity, code=code, message=message, count=len(supports),
                 samples=[p.label for p in supports[:MAX_SAMPLES]], at=[p.coordinates for p in supports[:MAX_SAMPLES]])


def _span_issue(severity: Literal["error", "warning"], code: str, message: str, spans: list[SpanResult]) -> Issue:
    def mid(s: SpanResult) -> tuple[float, float]:
        (a, b), (c, d) = s.coordinates
        return ((a + c) / 2, (b + d) / 2)

    return Issue(severity=severity, code=code, message=message, count=len(spans),
                 samples=[f"{s.from_label}–{s.to_label}" for s in spans[:MAX_SAMPLES]], at=[mid(s) for s in spans[:MAX_SAMPLES]])


def _clearance_trace(rules: RuleSet, spans: list[SpanResult], sections: list[SectionResult], data, h_attach: float,
                     min_clear: float, clause: str) -> Traced | None:
    checked = [s for s in spans if s.clearance_m is not None]
    if not checked:
        return None
    s = min(checked, key=lambda x: x.clearance_m or 0)
    t = next(t for t in sections if t.id == s.section)
    _, weight, md = data[t.id]
    src = f"rules {rules.ref} lv_overhead"
    return traced(s.clearance_m or 0.0, "m", formula_id=CLEARANCE_ID, formula=CLEARANCE_FORMULA, clause=clause, rules_hash=rules.hash, inputs={
        "span": (f"{s.from_label}–{s.to_label}", "", "lowest clearance"), "l": (s.length_m, "m", "between supports"),
        "w": (round(weight, 3), "N/m", f"{t.conductor} mass {md['mass_kg_per_m']} kg/m ({md.get('clause', '')})"),
        "ruling_span": (t.ruling_span_m, "m", f"section {t.id}"),
        "H_everyday": (t.tension_kn["everyday"], "kN", f"{src} everyday, governed by {t.governing.replace('_', ' ')}; max pull {md['max_pull_kn']} kN (placeholder)"),
        "H_hot": (t.tension_kn["hot"], "kN", f"{src} cases.hot (placeholder)"),
        "sag": (s.sag_m or 0.0, "m", "w·l²/(8·H_hot)"),
        "h_attach": (round(h_attach, 3), "m", f"{src} pole (placeholder)"),
        "min_clearance": (min_clear, "m", f"{src} (placeholder)"),
    })


def _load_trace(rules: RuleSet, supports: list[SupportResult], sec: dict, clause: str) -> Traced | None:
    loaded = [p for p in supports if p.load_kn is not None]
    if not loaded:
        return None
    p = max(loaded, key=lambda x: x.load_kn or 0)
    src = f"rules {rules.ref} lv_overhead"
    return traced(p.load_kn or 0.0, "kN", formula_id=POLE_LOAD_ID, formula=POLE_LOAD_FORMULA, clause=clause, rules_hash=rules.hash, inputs={
        "support": (p.label, "", "highest load"), "role": (p.role, "", f"{p.spans} spans"),
        "deviation": (p.deviation_deg if p.deviation_deg is not None else "", "°", "between its spans"),
        "case": (p.governing_case or "", "", f"{src} cases (placeholder)"),
        "pole_class": (p.pole_class or "", "", f"{src} pole_classes (placeholder)"),
        "stay": (p.stay, "", f"{src} stay (placeholder)"),
        "stay_tension": (p.stay_tension_kn if p.stay_tension_kn is not None else "", "kN", f"at {sec['stay']['angle_from_ground_deg']}° from ground"),
    })


def _empty(rules: RuleSet, clause: str, issues: list[Issue]) -> Overhead:
    return Overhead(rules_ref=rules.ref, rules_hash=rules.hash, clause=clause,
                    summary=OverheadSummary(spans=0, longest_span_m=0, sections=0, supports=0, poles_needed=0, stays=0, pole_classes={}),
                    spans=[], supports=[], sections=[], issues=issues)
