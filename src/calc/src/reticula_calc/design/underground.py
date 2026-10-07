"""Underground LV (plan 2.6): kiosks along the routes, and each cable's rating de-rated for how it is laid.

**Kiosks.** Underground LV feeders supply metering kiosks, never customers directly (240-56030637 §2.3.1). Kiosks are
placed along the routes where the customers are, each taking up to `boxes` groups of `box_loads` customers, one group
per phase (§3.5.3 d)–f), §3.10 e)), within `max_service_m` and spread over no more than `spacing_m` of route. Kiosks are pole sites to the network builder and the load allocation, so the same
code connects services to kiosks as to pole boxes.

**De-rating.** A cable's rating is given for standard conditions (§3.9.1 o)). In ground, it is multiplied by factors
for soil thermal resistivity, depth, ground temperature and grouping, each read from the rules file's tables at the
design condition and divided by the factor at the standard condition. Where more than `pipe_threshold_pct` of the
length is in pipe, the pipe rating applies (§3.9.6 i)). Feeders leaving one source share its trench for
`trench_shared_m`, so their first sections are grouped.
"""

from __future__ import annotations

import itertools
import math
from typing import Literal

from pydantic import BaseModel
from shapely.geometry import LineString, Point

from ..issues import Issue, issue
from ..lv.loads import LoadIn
from ..lv.loads import Params as LoadParams
from ..lv.network import CandidateIn, LvNetwork
from ..rules import RuleSet
from ..trace import Traced, traced
from .geometry import Frame

DERATE_ID = "ug.derate.factors.v1"
DERATE_FORMULA = "I = I_installation × (f_soil × f_depth × f_temp at design ÷ at standard) × f_group"


class Derating(BaseModel):
    branch: str
    conductor: str
    installation: Literal["ground", "pipe"]
    base_a: float
    soil: float
    depth: float
    temperature: float
    group: int
    grouping: float
    derated_a: float


class UndergroundResult(BaseModel):
    clause: str
    index: str | None
    conditions: dict[str, float]
    ratings: list[Derating]
    kiosks: int
    issues: list[Issue]
    lowest: Traced | None = None
    placeholders: list[str] = []


def interp(table: list[list[float]], x: float) -> float:
    pts = sorted((float(a), float(b)) for a, b in table)
    if x <= pts[0][0]:
        return pts[0][1]
    for (x0, y0), (x1, y1) in itertools.pairwise(pts):
        if x <= x1:
            return y0 + (y1 - y0) * (x - x0) / (x1 - x0) if x1 > x0 else y1
    return pts[-1][1]


def kiosk_params(rules: RuleSet) -> LoadParams:
    ug = rules.section("underground", "eskom/0.8.0")
    k = ug["kiosk"]
    lv_loads = rules.section("lv_loads", "eskom/0.4.0")
    return LoadParams(max_service_m=float(k["max_service_m"]), attach="pole_boxes", box_loads=int(k["box_loads"]),
                      boxes_per_pole=int(k["boxes"]), second_box_other_phase=True,
                      single_phase_max_kva=float(lv_loads["single_phase_max_kva"]), clause=ug.get("clause", ""))


def place_kiosks(net: LvNetwork, rules: RuleSet, loads: list[LoadIn] = ()) -> list[CandidateIn]:
    """Kiosk sites where the customers are: along each route, a kiosk for each run of customers it can take.

    Each customer is taken to the route nearest it, within the kiosk's service reach. Along each route, customers are
    grouped in order while the group fits one kiosk (`boxes` × `box_loads`), spans no more than `spacing_m` and every
    customer stays within `max_service_m` of the kiosk, which stands at the middle of the group. A route with no
    customers near it has no kiosk; customers beyond reach of every route are left for the load allocation to report.
    """
    k = rules.section("underground", "eskom/0.8.0")["kiosk"]
    spacing, reach = float(k["spacing_m"]), float(k["max_service_m"])
    capacity = int(k["boxes"]) * int(k["box_loads"])
    frame = Frame([n.coordinates for n in net.nodes] or [(27.0, -26.0)])
    routes = [b for b in net.branches if b.kind == "route"]
    lines = [frame.project(LineString(b.coordinates)) for b in routes]
    along: dict[int, list[tuple[float, Point]]] = {}
    for x in loads:
        if x.kva is None:
            continue
        pt = Point(frame.xy(x.coordinates))
        best = min(range(len(lines)), key=lambda i: lines[i].distance(pt), default=None)
        if best is not None and lines[best].distance(pt) <= reach:
            along.setdefault(best, []).append((lines[best].project(pt), pt))
    out: list[CandidateIn] = []

    def site(line: LineString, group: list[tuple[float, Point]]) -> Point:
        return line.interpolate((group[0][0] + group[-1][0]) / 2)

    for i in sorted(along):
        line = lines[i]
        group: list[tuple[float, Point]] = []
        for item in sorted(along[i], key=lambda t: t[0]):
            trial = group + [item]
            spot = site(line, trial)
            if group and (len(trial) > capacity or trial[-1][0] - trial[0][0] > spacing
                          or any(spot.distance(p) > reach for _, p in trial)):
                out.append(_kiosk(frame, site(line, group), len(out) + 1))
                trial = [item]
            group = trial
        if group:
            out.append(_kiosk(frame, site(line, group), len(out) + 1))
    return out


def _kiosk(frame: Frame, pt: Point, n: int) -> CandidateIn:
    return CandidateIn(id=f"auto-kiosk-{n}", kind="pole", label=f"K{n}",
                       geometry={"type": "Point", "coordinates": list(frame.lonlat((pt.x, pt.y)))})


def derate(net: LvNetwork, rules: RuleSet, conductors: dict[str, str], default_conductor: str,
           design: dict[str, float] | None = None) -> UndergroundResult:
    ug = rules.section("underground", "eskom/0.8.0")
    std, f = ug["standard"], ug["factors"]
    cond = {**ug["design"], **(design or {})}
    pipe = float(ug["pipe_share_pct"]) > float(ug["pipe_threshold_pct"])
    shared = float(ug["trench_shared_m"])

    def rel(table: str, key: str) -> float:
        return interp(f[table], float(cond[key])) / interp(f[table], float(std[key]))

    soil, depth, temp = rel("soil_resistivity", "soil_resistivity_kmw"), rel("depth", "depth_m"), rel("ground_temp", "ground_temp_c")
    nodes = {n.id: n for n in net.nodes}
    feeders_of: dict[str, int] = {}
    for fd in net.feeders:
        feeders_of[fd.source] = feeders_of.get(fd.source, 0) + 1
    source_of = {fd.id: fd.source for fd in net.feeders}
    board: dict[str, float] = {}
    for b in net.branches:
        if b.feeder and b.kind == "route":
            s = source_of.get(b.feeder)
            d = nodes[b.from_node].distance_m or 0.0
            if s is not None:
                board[s] = min(board.get(s, math.inf), d)

    ratings: list[Derating] = []
    missing: set[str] = set()
    for b in net.branches:
        if b.kind not in ("route", "link"):
            continue
        code = conductors.get(b.id, default_conductor)
        c = rules.conductor(code)
        avail = dict(c.ratings_a)
        installation: Literal["ground", "pipe"] = "pipe" if pipe and "pipe" in avail else "ground"
        if installation not in avail:
            missing.add(code)
        base = avail.get(installation, c.rating_a)
        group = 1
        s = source_of.get(b.feeder or "")
        if b.kind == "route" and s is not None and (nodes[b.from_node].distance_m or 0.0) - board.get(s, 0.0) < shared:
            group = feeders_of.get(s, 1)
        g = interp(f["grouping"], group)
        ratings.append(Derating(branch=b.id, conductor=code, installation=installation, base_a=base, soil=round(soil, 4), depth=round(depth, 4),
                                temperature=round(temp, 4), group=group, grouping=round(g, 4),
                                derated_a=round(base * soil * depth * temp * g, 2)))
    issues: list[Issue] = []
    if missing:
        issues.append(issue("warning", "no_ground_rating", "These conductors have no rating in ground, so their usual rating was de-rated "
                            "instead. Use underground cables for underground routes.", sorted(missing)))
    clause = ug.get("clause", "")
    low = min(ratings, key=lambda r: r.derated_a / r.base_a, default=None)
    return UndergroundResult(
        clause=clause, index=ug.get("index"), conditions={k: float(v) for k, v in cond.items()}, ratings=ratings,
        kiosks=sum(1 for n in net.nodes if n.kind == "pole"), issues=issues,
        lowest=traced(low.derated_a, "A", formula_id=DERATE_ID, formula=DERATE_FORMULA, clause=clause, rules_hash=rules.hash, inputs={
            "branch": (low.branch, "", "most de-rated"), "conductor": (low.conductor, "", "branch"),
            "I_installation": (low.base_a, "A", f"rules {rules.ref} conductor {low.conductor} ({low.installation})"),
            "f_soil": (low.soil, "", f"{cond['soil_resistivity_kmw']} K·m/W against {std['soil_resistivity_kmw']} (placeholder table)"),
            "f_depth": (low.depth, "", f"{cond['depth_m']} m against {std['depth_m']} m (placeholder table)"),
            "f_temp": (low.temperature, "", f"{cond['ground_temp_c']} °C against {std['ground_temp_c']} °C (placeholder table)"),
            "f_group": (low.grouping, "", f"{low.group} cables in the trench (placeholder table)")}) if low else None,
        placeholders=["underground de-rating factors (SANS 10198-4 and the 240-56030637 annex not transcribed)",
                      "kiosk spacing and service reach"],
    )
