"""Pre-design placement (plan 2.0): transformer sites, LV coverage and an MV route proposed before the field visit.

The design used to start from what the inspector marked. This step turns that round: from the loads (each with its
ADMD and load class, by zone or by inspection) and the road network, it proposes where transformers go, which loads
each one feeds, the roads the LV runs along, and the MV route that ties the transformers to the connection point.
The proposals become candidates marked "proposed", for the field to confirm or move.

The problem is capacitated facility location on the road graph:

- **Candidate sites** are the road-graph nodes plus a point every `site_spacing_m` along each road.
- **Demand** is each load, snapped to the nearest candidate site (its service point). Distances are road distances.
- **Capacity** is the largest standard rating × (1 − growth). Whether a set of loads fits is checked with the same
  Herman-Beta sum the load schedule uses (`calcs.admd`), so the ADMD sum is never trusted on its own.
- **Reach** is `lv_reach_m` by road, a stand-in for the voltage drop limit. Plan 2.4's checks are what decide.
- **Cost** is transformers by rating + LV road length + MV road length, at the rules file's indicative rates.

The solver is a constructive pass (open the site that covers the most unassigned loads per rand) followed by a
reassignment pass (each load to its nearest open site with room) and a closing pass (drop a site whose loads fit
elsewhere). That is the start plan 5.3's local search improves on. Every number comes from the rules file.
"""

from __future__ import annotations

import math
from dataclasses import dataclass, field
from itertools import pairwise
from typing import Any, Literal

import networkx as nx
from pydantic import BaseModel, Field
from shapely import STRtree, unary_union
from shapely.geometry import LineString, MultiLineString, Point
from shapely.ops import transform

from ..calcs.admd import AdmdInputError, LoadClassInfo, _cfg, _load_class, _moments
from ..geo import crs as crs_mod
from ..issues import Issue, issue
from ..rules import RulesError, RuleSet
from ..trace import Traced, traced
from .analysis import _quantile
from .network import GEOD

COST_ID = "placement.cost.v1"
COST_FORMULA = "cost = Σ transformer_zar[rating] + L_lv · lv_zar_per_m + L_mv · mv_zar_per_m"
DEMAND_ID = "placement.demand.herman-beta.v1"
DEMAND_FORMULA = "S = phases · V · I_design(Σ n·μ, Σ n·σ², Σ n·c at the confidence level) + Σ special kVA"
REACH_ID = "lv.reach.vdrop.v1"
REACH_FORMULA = "L = 2·ΔV / (I·z); I = R·(1 − growth) / (3·V_ph·n); z = R_ac·cosφ + X·sinφ; ΔV = limit·V_ph (evenly loaded feeder)"


class RoadIn(BaseModel):
    id: str
    coordinates: list[tuple[float, float]]
    """Lon/lat along the road."""


class LoadIn(BaseModel):
    id: str
    coordinates: tuple[float, float]
    kva: float | None = Field(default=None, gt=0)
    """ADMD, or the special load's kVA. A residential load without one, not yet inspected, takes its class's ADMD."""
    kind: Literal["residential", "special"] = "residential"
    load_class: str | None = None
    label: str | None = None


class PlacementRequest(BaseModel):
    rules: str
    roads: list[RoadIn]
    loads: list[LoadIn]
    connection_point: tuple[float, float] | None = None
    """Where the MV comes from (lon/lat). Without it no MV route is proposed."""


class Transformer(BaseModel):
    id: str
    coordinates: tuple[float, float]
    rating_kva: float
    loads: int
    demand_kva: float
    """Herman-Beta demand at the confidence level, plus special loads."""
    utilisation_pct: float
    feeders: int
    lv_length_m: float


class Assignment(BaseModel):
    load_id: str
    transformer: str
    road_m: float
    service_m: float


class Route(BaseModel):
    transformer: str | None
    """The transformer an LV route belongs to; None for the MV route."""
    coordinates: list[tuple[float, float]]
    length_m: float


class ProposedCandidate(BaseModel):
    """The proposal in the shape of a field candidate, so the API can store it as `source: proposed`."""

    kind: Literal["transformer", "lv_route", "mv_route"]
    geometry: dict[str, Any]
    label: str | None = None


class Placement(BaseModel):
    rules_ref: str
    rules_hash: str
    clause: str
    transformers: list[Transformer]
    assignments: list[Assignment]
    lv_routes: list[Route]
    mv_routes: list[Route]
    candidates: list[ProposedCandidate]
    cost: Traced
    worst_demand: Traced | None
    issues: list[Issue]
    placeholders: list[str]
    reach: Traced | None = None
    """The LV reach derived from the voltage drop limit, when the rules file has LV design settings."""


@dataclass
class _Params:
    ratings: list[float]
    growth: float
    reach: float
    spacing: float
    max_feeders: int
    tx_cost: dict[float, float]
    lv_per_m: float
    mv_per_m: float
    clause: str


def _params(rules: RuleSet) -> _Params:
    sec = rules.data.get("mv_design")
    if not sec:
        raise RulesError(f"rules {rules.ref} has no mv_design section; placement needs eskom/0.7.0 or later")
    costs = sec["costs"]
    return _Params(
        ratings=sorted(float(r) for r in sec["transformer_ratings_kva"]), growth=float(sec["growth_pct"]) / 100,
        reach=float(sec["lv_reach_m"]), spacing=float(sec["site_spacing_m"]), max_feeders=int(sec["max_feeders"]),
        tx_cost={float(k): float(v) for k, v in costs["transformer_zar"].items()},
        lv_per_m=float(costs["lv_zar_per_m"]), mv_per_m=float(costs["mv_zar_per_m"]), clause=sec.get("clause", ""),
    )


def _derived_reach(rules: RuleSet, p: _Params) -> Traced | None:
    """The longest LV feeder whose far end stays within the voltage drop limit, or None without LV design settings.

    Model: the largest rating the placement may open, loaded to R·(1 − growth), shared equally by `max_feeders`
    balanced feeders of the default LV conductor, each with its load spread evenly along it. The drop at the end of an
    evenly loaded feeder is I·z·L/2, so L = 2·ΔV / (I·z), with I = R·(1 − growth) / (3·V_ph·n), z = R_ac·cosφ + X·sinφ
    and ΔV = limit × V_ph. The Herman-Beta checks of plan 2.4 decide; this only stops the placement proposing a site
    whose feeders could never pass.
    """
    lvd, volt = rules.data.get("lv_design"), rules.data.get("voltage", {})
    if not lvd or "lv_max_drop_pct" not in volt:
        return None
    c = rules.conductor(lvd["default_conductor"])
    r = c.r_at(float(lvd["conductor_temp_c"]), lvd["temperature_coefficients"])
    pf = float(lvd["power_factor"])
    sin = math.sqrt(max(0.0, 1 - pf * pf))
    z = r * pf + c.x_ohm_per_km * sin
    v_ph = float(volt["lv_nominal_v"]) / math.sqrt(3)
    limit = float(volt["lv_max_drop_pct"])
    rating = max(p.ratings)
    i = rating * (1 - p.growth) * 1000 / (3 * v_ph * p.max_feeders)
    reach = 2 * limit / 100 * v_ph / (i * z) * 1000
    return traced(round(reach, 1), "m", formula_id=REACH_ID, formula=REACH_FORMULA, clause=volt.get("clause", ""), rules_hash=rules.hash,
                  inputs={"R": (rating, "kVA", f"largest rating, rules {rules.ref} mv_design"),
                          "growth": (p.growth * 100, "%", f"rules {rules.ref} mv_design (placeholder)"),
                          "n": (p.max_feeders, "feeders", f"rules {rules.ref} mv_design.max_feeders (placeholder)"),
                          "I": (round(i, 3), "A", "R·(1 − growth) / (3·V_ph·n)"), "V_ph": (round(v_ph, 3), "V", "lv_nominal_v / √3"),
                          "conductor": (c.code, "", f"rules {rules.ref} lv_design.default_conductor"),
                          "R_ac": (r, "Ω/km", "AC resistance at the conductor temperature"), "X": (c.x_ohm_per_km, "Ω/km", c.code),
                          "cos_phi": (pf, "", f"rules {rules.ref} lv_design.power_factor (placeholder)"),
                          "limit": (limit, "%", f"rules {rules.ref} voltage.lv_max_drop_pct")})


@dataclass
class _Demand:
    """Running Herman-Beta sum of a transformer's loads, so adding a load is O(1)."""

    n: int = 0
    mean: float = 0.0
    var: float = 0.0
    cap: float = 0.0
    special: float = 0.0

    def plus(self, lc: LoadClassInfo | None, kva: float) -> _Demand:
        if lc is None:
            return _Demand(self.n, self.mean, self.var, self.cap, self.special + kva)
        mu, sd = _moments(lc.alpha, lc.beta, lc.c_amps)
        return _Demand(self.n + 1, self.mean + mu, self.var + sd * sd, self.cap + lc.c_amps, self.special)

    def kva(self, conf: float, v_ph: float, phases: int) -> float:
        if self.n == 0:
            return self.special
        ph = phases if self.n >= 3 else 1
        current = _quantile(self.mean / ph, self.var / ph, 0.0, self.cap / ph, conf)
        return ph * v_ph * current / 1000 + self.special


@dataclass
class _Site:
    node: int
    loads: list[int] = field(default_factory=list)
    demand: _Demand = field(default_factory=_Demand)


def place(req: PlacementRequest, rules: RuleSet) -> Placement:
    p = _params(rules)
    cfg = _cfg(rules)
    div = cfg["diversity"]
    conf = float(div["confidence_pct"]) / 100
    phases = int(div.get("phases", 3))
    v_ph = float(rules.data["load_tables"][cfg["design_table"]]["phase_voltage_v"])
    issues: list[Issue] = []

    derived = _derived_reach(rules, p)
    effective_reach = min(p.reach, derived.value) if derived else p.reach

    # A building not yet inspected has no ADMD of its own: it takes its load class's (by zoning, plans 1.3 and 2.0).
    by_class: list[str] = []
    no_estimate: list[LoadIn] = []
    resolved: list[LoadIn] = []
    for ld in req.loads:
        if ld.kva is None and ld.kind == "residential" and ld.load_class:
            try:
                resolved.append(ld.model_copy(update={"kva": _load_class(cfg, rules, ld.load_class, "score").admd_kva}))
                by_class.append(ld.label or ld.id)
            except AdmdInputError:
                no_estimate.append(ld)
        elif ld.kva is None:
            no_estimate.append(ld)
        else:
            resolved.append(ld)
    req = req.model_copy(update={"loads": resolved})
    if by_class:
        issues.append(issue("warning", "class_admd", "Buildings not yet inspected are sized at the ADMD of their load class by zoning. "
                            "Inspect them to confirm.", by_class))
    if no_estimate:
        issues.append(issue("warning", "no_estimate", "Buildings without a load estimate or a load class were left out.",
                            [x.label or x.id for x in no_estimate], [x.coordinates for x in no_estimate]))

    if not req.roads:
        return _empty(rules, p, [issue("error", "no_roads", "No roads to place transformers along. Import the road layout first.")])
    if not req.loads:
        return _empty(rules, p, [issue("error", "no_loads", "No loads to supply. Estimate the loads, by zone or by inspection, first.")])

    # ---- the road graph in metres, with a candidate site every spacing ----
    lon0 = sum(r.coordinates[0][0] for r in req.roads) / len(req.roads)
    fwd = crs_mod.from_wgs84(crs_mod.nearest_lo(lon0))
    inv = crs_mod.to_wgs84(crs_mod.nearest_lo(lon0))
    metres = lambda g: transform(fwd.transform, g)
    merged = unary_union([metres(LineString(r.coordinates)) for r in req.roads if len(r.coordinates) >= 2])
    lines = list(merged.geoms) if isinstance(merged, MultiLineString) else [merged]
    g, xy = _graph(lines, p.spacing)
    sites = list(g.nodes)
    site_pts = [Point(xy[n]) for n in sites]
    tree = STRtree(site_pts)

    def lonlat(n: int) -> tuple[float, float]:
        lon, lat = inv.transform(*xy[n])
        return (round(lon, 7), round(lat, 7))

    # ---- loads snapped to their service point ----
    classes: dict[str, LoadClassInfo | None] = {}
    no_class: list[str] = []

    def load_class(ld: LoadIn) -> LoadClassInfo | None:
        if ld.kind != "residential" or not ld.load_class:
            if ld.kind == "residential":
                no_class.append(ld.label or ld.id)
            return None
        if ld.load_class not in classes:
            try:
                classes[ld.load_class] = _load_class(cfg, rules, ld.load_class, "score")
            except AdmdInputError:
                classes[ld.load_class] = None
        if classes[ld.load_class] is None:
            no_class.append(ld.label or ld.id)
        return classes[ld.load_class]

    load_pt = [metres(Point(ld.coordinates)) for ld in req.loads]
    service_node = [sites[int(tree.nearest(pt))] for pt in load_pt]
    service_m = [pt.distance(Point(xy[n])) for pt, n in zip(load_pt, service_node, strict=True)]
    load_lc = [load_class(ld) for ld in req.loads]

    far = [i for i, s in enumerate(service_m) if s > effective_reach]
    if no_class:
        issues.append(issue("warning", "no_load_class",
                            "Residential loads without a load class of the design table are taken at their ADMD, without Herman-Beta diversity.",
                            no_class))

    # Road distance from every candidate site to the loads it could feed.
    by_node: dict[int, list[int]] = {}
    for i, n in enumerate(service_node):
        if i not in far:
            by_node.setdefault(n, []).append(i)
    reach_of: dict[int, dict[int, float]] = {}
    for s in sites:
        dist = nx.single_source_dijkstra_path_length(g, s, cutoff=effective_reach, weight="length")
        reach_of[s] = {i: d for n, d in dist.items() for i in by_node.get(n, [])}
    load_dist = [{} for _ in req.loads]  # load -> {site: road distance}
    for s, d in reach_of.items():
        for i, m in d.items():
            load_dist[i][s] = m

    capacity = p.ratings[-1] * (1 - p.growth)
    tx_min_cost = min(p.tx_cost.values(), default=0.0)

    def fits(demand: _Demand) -> bool:
        return demand.kva(conf, v_ph, phases) <= capacity

    # ---- constructive pass: open the site that covers the most unassigned loads per rand ----
    unassigned = {i for i in range(len(req.loads)) if i not in far and load_dist[i]}
    unreachable = [i for i in range(len(req.loads)) if i not in far and not load_dist[i]]
    opened: dict[int, _Site] = {}
    while unassigned:
        best: tuple[float, int, list[int], _Demand] | None = None
        for s in sites:
            if s in opened:
                continue
            take: list[int] = []
            demand = _Demand()
            for i, _ in sorted(((i, reach_of[s][i]) for i in reach_of[s] if i in unassigned), key=lambda t: t[1]):
                nxt = demand.plus(load_lc[i], req.loads[i].kva)
                if not fits(nxt):
                    break
                demand, take = nxt, take + [i]
            if not take:
                continue
            score = len(take) / (tx_min_cost + p.lv_per_m * sum(reach_of[s][i] for i in take) + 1.0)
            if best is None or score > best[0]:
                best = (score, s, take, demand)
        if best is None:
            break
        _, s, take, demand = best
        opened[s] = _Site(s, take, demand)
        unassigned -= set(take)
    unreachable += sorted(unassigned)

    # ---- reassignment pass: each load to its nearest open site with room, nearest sites first ----
    for _ in range(3):
        moved = False
        for i in sorted(range(len(req.loads)), key=lambda i: min(load_dist[i].values(), default=math.inf)):
            here = next((s for s, st in opened.items() if i in st.loads), None)
            if here is None:
                continue
            for s, d in sorted(load_dist[i].items(), key=lambda t: t[1]):
                if s == here or s not in opened:
                    continue
                if d >= load_dist[i][here]:
                    break
                nxt = opened[s].demand.plus(load_lc[i], req.loads[i].kva)
                if fits(nxt):
                    opened[s].loads.append(i)
                    opened[s].demand = nxt
                    opened[here].loads.remove(i)
                    opened[here].demand = _sum(opened[here].loads, load_lc, req.loads)
                    moved = True
                    break
        # closing pass: a site whose loads all fit elsewhere is dropped
        for s in sorted(opened, key=lambda s: len(opened[s].loads)):
            trial = {t: _Site(t, list(st.loads), st.demand) for t, st in opened.items() if t != s}
            ok = True
            for i in opened[s].loads:
                home = next((t for t, _ in sorted(load_dist[i].items(), key=lambda t: t[1])
                             if t in trial and fits(trial[t].demand.plus(load_lc[i], req.loads[i].kva))), None)
                if home is None:
                    ok = False
                    break
                trial[home].loads.append(i)
                trial[home].demand = trial[home].demand.plus(load_lc[i], req.loads[i].kva)
            if ok and trial:
                opened = trial
                moved = True
        if not moved:
            break

    # ---- size, route the LV, and route the MV ----
    transformers: list[Transformer] = []
    assignments: list[Assignment] = []
    lv_routes: list[Route] = []
    candidates: list[ProposedCandidate] = []
    lv_total = 0.0
    worst: tuple[float, str, _Demand] | None = None
    for k, s in enumerate(sorted(opened, key=lambda s: (xy[s][1], xy[s][0]), reverse=True), start=1):
        st = opened[s]
        demand = st.demand.kva(conf, v_ph, phases)
        rating = next((r for r in p.ratings if demand <= r * (1 - p.growth)), p.ratings[-1])
        tid = f"TX{k}"
        edges: set[tuple[int, int]] = set()
        for i in st.loads:
            path = nx.shortest_path(g, s, service_node[i], weight="length")
            edges.update((min(a, b), max(a, b)) for a, b in pairwise(path))
            assignments.append(Assignment(load_id=req.loads[i].id, transformer=tid, road_m=round(load_dist[i][s], 1), service_m=round(service_m[i], 1)))
        length = sum(g.edges[a, b]["length"] for a, b in edges)
        lv_total += length
        feeders = sum(1 for a, b in edges if s in (a, b))
        if feeders > p.max_feeders:
            issues.append(issue("warning", "too_many_feeders", f"Transformers need more than {p.max_feeders} LV feeders. Split the area or add a transformer.", [tid]))
        for line in _merge_edges(edges, xy):
            coords = [lonlat_xy(inv, c) for c in line.coords]
            lv_routes.append(Route(transformer=tid, coordinates=coords, length_m=round(GEOD.geometry_length(LineString(coords)), 1)))
            candidates.append(ProposedCandidate(kind="lv_route", geometry={"type": "LineString", "coordinates": coords}, label=tid))
        transformers.append(Transformer(id=tid, coordinates=lonlat(s), rating_kva=rating, loads=len(st.loads), demand_kva=round(demand, 2),
                                        utilisation_pct=round(100 * demand / rating, 1), feeders=feeders, lv_length_m=round(length, 1)))
        candidates.append(ProposedCandidate(kind="transformer", geometry={"type": "Point", "coordinates": list(lonlat(s))}, label=tid))
        if worst is None or demand > worst[0]:
            worst = (demand, tid, st.demand)

    mv_routes: list[Route] = []
    mv_total = 0.0
    if req.connection_point is not None and opened:
        cp = metres(Point(req.connection_point))
        root = sites[int(tree.nearest(cp))]
        for line in _mv_tree(g, root, list(opened), xy):
            coords = [lonlat_xy(inv, c) for c in line.coords]
            length = GEOD.geometry_length(LineString(coords))
            mv_total += length
            mv_routes.append(Route(transformer=None, coordinates=coords, length_m=round(length, 1)))
            candidates.append(ProposedCandidate(kind="mv_route", geometry={"type": "LineString", "coordinates": coords}))
    elif req.connection_point is None:
        issues.append(issue("warning", "no_connection_point", "No connection point was given, so no MV route is proposed."))

    if far:
        issues.append(issue("error", "far_from_road", f"Loads more than {effective_reach:g} m from any road are not placed. Check their positions or add a road.",
                            [req.loads[i].label or req.loads[i].id for i in far], [req.loads[i].coordinates for i in far]))
    if unreachable:
        issues.append(issue("error", "unassigned", f"Loads could not be fed by any transformer within {effective_reach:g} m by road. Add a road or a transformer site near them.",
                            [req.loads[i].label or req.loads[i].id for i in unreachable], [req.loads[i].coordinates for i in unreachable]))

    tx_cost = sum(p.tx_cost.get(t.rating_kva, 0.0) for t in transformers)
    cost = traced(round(tx_cost + lv_total * p.lv_per_m + mv_total * p.mv_per_m, 2), "ZAR", formula_id=COST_ID, formula=COST_FORMULA,
                  clause=p.clause, rules_hash=rules.hash, inputs={
                      "transformers": (len(transformers), "", "proposed"), "transformer_cost": (round(tx_cost, 2), "ZAR", f"rules {rules.ref} mv_design.costs (placeholder)"),
                      "L_lv": (round(lv_total, 1), "m", "road length of the LV routes"), "lv_zar_per_m": (p.lv_per_m, "ZAR/m", f"rules {rules.ref} mv_design.costs (placeholder)"),
                      "L_mv": (round(mv_total, 1), "m", "road length of the MV route"), "mv_zar_per_m": (p.mv_per_m, "ZAR/m", f"rules {rules.ref} mv_design.costs (placeholder)")})
    worst_demand = None
    if worst is not None:
        demand, tid, d = worst
        worst_demand = traced(round(demand, 3), "kVA", formula_id=DEMAND_ID, formula=DEMAND_FORMULA, clause=div.get("clause", cfg["clause"]), rules_hash=rules.hash,
                              inputs={"transformer": (tid, "", "most loaded"), "n": (d.n, "loads", "residential with a class"),
                                      "mean": (round(d.mean, 4), "A", "Σ μ"), "sd": (round(math.sqrt(d.var), 4), "A", "√Σ σ²"), "C": (round(d.cap, 1), "A", "Σ c"),
                                      "special": (round(d.special, 3), "kVA", "special loads and loads without a class"),
                                      "confidence": (conf * 100, "%", f"rules {rules.ref} diversity"), "V_phase": (v_ph, "V", f"rules {rules.ref} load table")})
    reach_note = (f"LV reach {effective_reach:g} m, the shorter of the rules' {p.reach:g} m and {derived.value:g} m from the drop limit"
                  if derived else f"LV reach {p.reach:g} m (rules value, standing in for the voltage drop limit)")
    placeholders = [f"growth allowance {p.growth * 100:g} %", reach_note,
                    f"{p.max_feeders} feeders a transformer", "indicative costs (no rate library yet)"]
    issues.append(issue("warning", "placeholders", "This proposal uses placeholder values and is not a design: " + "; ".join(placeholders) + "."))
    return Placement(rules_ref=rules.ref, rules_hash=rules.hash, clause=p.clause, transformers=transformers, assignments=assignments,
                     lv_routes=lv_routes, mv_routes=mv_routes, candidates=candidates, cost=cost, worst_demand=worst_demand,
                     issues=issues, placeholders=placeholders, reach=derived)


def _empty(rules: RuleSet, p: _Params, issues: list[Issue]) -> Placement:
    cost = traced(0.0, "ZAR", formula_id=COST_ID, formula=COST_FORMULA, clause=p.clause, rules_hash=rules.hash, inputs={})
    return Placement(rules_ref=rules.ref, rules_hash=rules.hash, clause=p.clause, transformers=[], assignments=[], lv_routes=[], mv_routes=[],
                     candidates=[], cost=cost, worst_demand=None, issues=issues, placeholders=[])


def _sum(loads: list[int], lc: list[LoadClassInfo | None], req_loads: list[LoadIn]) -> _Demand:
    d = _Demand()
    for i in loads:
        d = d.plus(lc[i], req_loads[i].kva)
    return d


def _graph(lines: list[LineString], spacing: float) -> tuple[nx.Graph, dict[int, tuple[float, float]]]:
    """The roads as a graph of candidate sites: every vertex, plus a site every `spacing` along each road."""
    g = nx.Graph()
    xy: dict[int, tuple[float, float]] = {}
    ids: dict[tuple[float, float], int] = {}

    def node(pt: tuple[float, float]) -> int:
        key = (round(pt[0], 2), round(pt[1], 2))
        if key not in ids:
            ids[key] = len(ids)
            xy[ids[key]] = key
        return ids[key]

    for line in lines:
        steps = max(1, math.ceil(line.length / spacing))
        prev = node(line.coords[0])
        at = 0.0
        vertices = [(line.project(Point(c)), c) for c in line.coords[1:]]
        marks = sorted({*(line.length * k / steps for k in range(1, steps + 1)), *(s for s, _ in vertices)})
        for s in marks:
            if s - at < 0.5:
                continue
            pt = line.interpolate(s)
            n = node((pt.x, pt.y))
            if n != prev:
                g.add_edge(prev, n, length=s - at)
            prev, at = n, s
    return g, xy


def _merge_edges(edges: set[tuple[int, int]], xy: dict[int, tuple[float, float]]) -> list[LineString]:
    from shapely.ops import linemerge

    if not edges:
        return []
    merged = linemerge([LineString([xy[a], xy[b]]) for a, b in edges])
    return list(merged.geoms) if isinstance(merged, MultiLineString) else [merged]


def _mv_tree(g: nx.Graph, root: int, targets: list[int], xy: dict[int, tuple[float, float]]) -> list[LineString]:
    """A road-following tree from the connection point to every transformer: each transformer joins the tree by its
    shortest path to any node already on it, nearest first (a Steiner tree approximation)."""
    on_tree = {root}
    edges: set[tuple[int, int]] = set()
    left = set(targets) - on_tree
    while left:
        dist, paths = nx.multi_source_dijkstra(g, on_tree, weight="length")
        t = min((t for t in left if t in dist), key=lambda t: dist[t], default=None)
        if t is None:
            break
        path = paths[t]
        edges.update((min(a, b), max(a, b)) for a, b in pairwise(path))
        on_tree.update(path)
        left.discard(t)
    return _merge_edges(edges, xy)


def lonlat_xy(inv, c: tuple[float, ...]) -> tuple[float, float]:
    lon, lat = inv.transform(c[0], c[1])
    return (round(lon, 7), round(lat, 7))
