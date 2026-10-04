"""MV routing, loading, voltage and taps (plans 3.3, 3.4).

Routing. The MV routes are noded and joined like the LV routes; the supply point and every transformer site join the
nearest route (a site further than the rules' tee length is reported). A shortest-path tree from the supply point
makes the network radial, and branches feeding no transformer are removed.

Loading. A branch carries the Herman-Beta demand of every consumer downstream of it (one statistical group, single-
and three-phase connections added), as a balanced three-phase current at the MV voltage.

Voltage drop. ΔV = √3·I·(R cos φ + X sin φ)·L per branch, accumulated from the supply point.

Sizing. Every branch starts on the smallest MV conductor allowed for the construction; overloaded branches, then the
branch contributing most to the worst drop, move up until the checks pass; conductors never get smaller towards the
supply point.

Taps. With the sending voltage V_s, the MV drop to the site ΔV_mv, the transformer's regulation at its design load
ε = (S/S_r)·(R% cos φ + X% sin φ) and the worst LV drop ΔV_lv from the site's LV design, the lowest customer voltage is
V_s − ΔV_mv − ε − ΔV_lv + tap and the highest (no load, nearest customer) is V_s + tap. The tap with the largest
margin inside the supply band (NRS 048-2, ±10 %) is chosen.
"""

from __future__ import annotations

import math
from itertools import pairwise
from typing import Literal

import networkx as nx
from pydantic import BaseModel
from shapely.geometry import LineString, Point

from ..calcs.admd import GroupLoad, GroupRequest, group
from ..geo.routes import Projector, attach_point, route_graph, snap_routes
from ..lv.analysis import Check
from ..lv.build import CustomerIn, RouteIn
from ..lv.library import library
from ..rules import RuleSet
from ..rules.loader import RulesError
from .placement import mv_config


class MvNode(BaseModel):
    id: str
    kind: Literal["supply", "site", "junction"]
    lon: float
    lat: float
    site_id: str | None = None


class MvBranch(BaseModel):
    id: str
    from_id: str
    to_id: str
    kind: Literal["line", "tee"]
    conductor: str
    length_m: float
    geometry: list[tuple[float, float]]
    route_id: str | None = None


class MvBranchResult(BaseModel):
    id: str
    conductor: str
    length_m: float
    demand_kva: float
    current_a: float
    rating_a: float
    loading_pct: float
    vdrop_pct_end: float
    sites: int


class MvNetwork(BaseModel):
    supply_id: str
    nodes: list[MvNode]
    branches: list[MvBranch]


class MvAnalysis(BaseModel):
    branches: list[MvBranchResult]
    site_vdrop_pct: dict[str, float]
    checks: list[Check]


def build_mv(supply: tuple[float, float], sites: dict[str, tuple[float, float]], mv_routes: list[RouteIn], rules: RuleSet
             ) -> tuple[MvNetwork, list[Check]]:
    cfg = mv_config(rules)
    if not mv_routes:
        raise ValueError("No MV routes; mark the MV route on the field screen first")
    pts = [c for r in mv_routes for c in r.coordinates] + [supply, *sites.values()]
    pr = Projector(sum(p[0] for p in pts) / len(pts), sum(p[1] for p in pts) / len(pts))
    routes = snap_routes({r.id: LineString([pr.xy(*c) for c in r.coordinates]) for r in mv_routes if len(r.coordinates) >= 2})
    g = route_graph(routes)
    checks: list[Check] = []
    clause = cfg.get("clause", "")

    supply_xy = pr.xy(*supply)
    k_supply, d_supply = attach_point(g, Point(supply_xy))
    g.add_node("SUPPLY", xy=supply_xy)
    g.add_edge("SUPPLY", k_supply, length=max(d_supply, 0.01), route_id=None, tee=True)
    max_tee = float(cfg["max_tee_m"])
    for sid, (lon, lat) in sites.items():
        xy = pr.xy(lon, lat)
        k, d = attach_point(g, Point(xy))
        node = f"SITE:{sid}"
        g.add_node(node, xy=xy, site_id=sid)
        g.add_edge(k, node, length=max(d, 0.01), route_id=None, tee=True)
        checks.append(Check(code="mv_tee", subject=sid, passed=d <= max_tee, value=round(d, 1), limit=max_tee, unit="m",
                            message=f"Transformer site {sid} is {d:.0f} m from the MV route", clause=clause))
    if not nx.is_connected(g):
        comp = nx.node_connected_component(g, "SUPPLY")
        missing = [n for n in g.nodes if isinstance(n, str) and n.startswith("SITE:") and n not in comp]
        for n in missing:
            checks.append(Check(code="mv_connected", subject=n[5:], passed=False, value=0, limit=1, unit="",
                                message=f"Transformer site {n[5:]} cannot be reached along the MV routes", clause=clause))
        g = g.subgraph(comp).copy()
    _, paths = nx.single_source_dijkstra(g, "SUPPLY", weight="length")
    tree = nx.DiGraph()
    for n in g.nodes:
        tree.add_node(n, **g.nodes[n])
    for path in paths.values():
        for a, b in pairwise(path):
            if not tree.has_edge(a, b):
                tree.add_edge(a, b, **g.edges[a, b])
    site_nodes = [n for n in tree.nodes if isinstance(n, str) and n.startswith("SITE:")]
    keep = {"SUPPLY"}
    for n in site_nodes:
        keep |= nx.ancestors(tree, n) | {n}
    tree = tree.subgraph(keep).copy()

    ids = {"SUPPLY": "SUPPLY"}
    counter = 0
    nodes: list[MvNode] = []
    for n in nx.topological_sort(tree):
        if n not in ids:
            if isinstance(n, str) and n.startswith("SITE:"):
                ids[n] = n
            else:
                counter += 1
                ids[n] = f"M{counter}"
        lon, lat = pr.ll(*tree.nodes[n]["xy"])
        kind = "supply" if n == "SUPPLY" else "site" if ids[n].startswith("SITE:") else "junction"
        nodes.append(MvNode(id=ids[n], kind=kind, lon=lon, lat=lat, site_id=tree.nodes[n].get("site_id")))
    branches = [
        MvBranch(id=f"MB{i + 1}", from_id=ids[a], to_id=ids[b], kind="tee" if tree.edges[a, b].get("tee") else "line", conductor="",
                 length_m=round(tree.edges[a, b]["length"], 2), geometry=[pr.ll(*tree.nodes[a]["xy"]), pr.ll(*tree.nodes[b]["xy"])],
                 route_id=tree.edges[a, b].get("route_id"))
        for i, (a, b) in enumerate(nx.bfs_edges(tree, "SUPPLY"))
    ]
    return MvNetwork(supply_id="SUPPLY", nodes=nodes, branches=branches), checks


def analyse_mv(net: MvNetwork, customers_by_site: dict[str, list[CustomerIn]], rules: RuleSet) -> MvAnalysis:
    cfg = mv_config(rules)
    lib = library(rules)
    pf = float(cfg["power_factor"])
    sin = math.sqrt(1 - pf * pf)
    un = float(cfg["nominal_kv"]) * 1000
    clause = cfg.get("clause", "")
    g = nx.DiGraph()
    for b in net.branches:
        g.add_edge(b.from_id, b.to_id, branch=b)
    site_of = {n.id: n.site_id for n in net.nodes if n.site_id}

    results: list[MvBranchResult] = []
    checks: list[Check] = []
    vd: dict[str, float] = {net.supply_id: 0.0}
    for b in net.branches:
        below = nx.descendants(g, b.to_id) | {b.to_id}
        sites = [site_of[n] for n in below if n in site_of]
        custs = [c for s in sites for c in customers_by_site.get(s, [])]
        if custs:
            loads = [GroupLoad(id=c.building_id, kind=c.kind, kva=max(c.special_kva or 1.0, 0.01), load_class=c.load_class, phases=c.phases) for c in custs]
            kva = group(GroupRequest(rules=rules.ref, loads=loads), rules).total_kva.value
        else:
            kva = 0.0
        cable = lib.get(b.conductor)
        if cable is None:
            raise RulesError(f"MV conductor {b.conductor!r} is not in rules {rules.ref}")
        current = kva * 1000 / (math.sqrt(3) * un)
        dv = math.sqrt(3) * current * (cable.r_ohm_per_km * pf + cable.x_ohm_per_km * sin) * b.length_m / 1000
        vd[b.to_id] = vd[b.from_id] + 100 * dv / un
        loading = 100 * current / cable.rating_a
        results.append(MvBranchResult(id=b.id, conductor=b.conductor, length_m=b.length_m, demand_kva=round(kva, 1), current_a=round(current, 2),
                                      rating_a=cable.rating_a, loading_pct=round(loading, 1), vdrop_pct_end=round(vd[b.to_id], 3), sites=len(sites)))
        checks.append(Check(code="mv_thermal", subject=b.id, passed=current <= cable.rating_a, value=round(current, 1), limit=cable.rating_a, unit="A",
                            message=f"MV branch {b.id} ({b.conductor}) carries {current:.1f} A ({kva:.0f} kVA)", clause=cable.clause))
    max_drop = float(cfg["max_drop_pct"])
    site_vd = {site_of[n]: round(v, 5) for n, v in vd.items() if n in site_of}
    for sid, v in site_vd.items():
        checks.append(Check(code="mv_vdrop", subject=sid, passed=v <= max_drop + 1e-9, value=round(v, 2), limit=max_drop, unit="%",
                            message=f"MV voltage drop to site {sid} is {v:.2f} %", clause=clause))
    return MvAnalysis(branches=results, site_vdrop_pct=site_vd, checks=checks)


def size_mv(net: MvNetwork, customers_by_site: dict[str, list[CustomerIn]], rules: RuleSet,
            construction: Literal["overhead", "underground"]) -> tuple[MvNetwork, MvAnalysis, bool]:
    cfg = mv_config(rules)
    lib = library(rules)
    options = list(cfg["mv_conductors"].get(construction, []))
    if not options or any(o not in lib for o in options):
        raise RulesError(f"mv_design.mv_conductors.{construction} must list conductors in the library")
    options.sort(key=lambda o: lib[o].rating_a)
    rank = {o: i for i, o in enumerate(options)}
    net = net.model_copy(deep=True)
    for b in net.branches:
        b.conductor = options[0]
    by_id = {b.id: b for b in net.branches}
    parent = {b.to_id: b for b in net.branches}
    g = nx.DiGraph()
    for b in net.branches:
        g.add_edge(b.from_id, b.to_id, branch=b)
    site_node = {n.site_id: n.id for n in net.nodes if n.site_id}

    def taper() -> None:
        for n in reversed(list(nx.topological_sort(g))):
            b = parent.get(n)
            if b is None:
                continue
            below = [g.edges[n, ch]["branch"] for ch in g.successors(n)]
            biggest = max((rank[x.conductor] for x in below), default=0)
            if rank[b.conductor] < biggest:
                b.conductor = options[biggest]

    for _ in range(200):
        a = analyse_mv(net, customers_by_site, rules)
        failing = [c for c in a.checks if not c.passed and c.code in ("mv_thermal", "mv_vdrop")]
        if not failing:
            return net, a, True
        thermal = [by_id[c.subject] for c in failing if c.code == "mv_thermal" and rank[by_id[c.subject].conductor] < len(options) - 1]
        if thermal:
            for b in thermal:
                b.conductor = options[rank[b.conductor] + 1]
        else:
            worst = max((c for c in failing if c.code == "mv_vdrop"), key=lambda c: c.value, default=None)
            if worst is None:
                return net, a, False
            node = site_node[worst.subject]
            path = []
            while node in parent:
                path.append(parent[node])
                node = parent[node].from_id
            current = {r.id: r.current_a for r in a.branches}
            cands = [b for b in path if rank[b.conductor] < len(options) - 1]
            if not cands:
                return net, a, False
            best = max(cands, key=lambda b: current[b.id] * b.length_m * (lib[b.conductor].r_ohm_per_km - lib[options[rank[b.conductor] + 1]].r_ohm_per_km))
            best.conductor = options[rank[best.conductor] + 1]
        taper()
    return net, analyse_mv(net, customers_by_site, rules), False


def choose_tap(rules: RuleSet, mv_drop_pct: float, load_kva: float, rating_kva: float, z_pct: float, x_r: float,
               lv_drop_pct: float) -> tuple[float | None, float, float, float]:
    """Returns (tap %, lowest customer voltage %, highest customer voltage %, regulation %). Tap None if none fits."""
    cfg = mv_config(rules)
    pf = float(cfg["power_factor"])
    sin = math.sqrt(1 - pf * pf)
    send = float(cfg["sending_voltage_pct"])
    band = float(cfg["supply_voltage_band_pct"])
    r_pct = z_pct / math.sqrt(1 + x_r * x_r)
    x_pct = r_pct * x_r
    reg = (load_kva / rating_kva) * (r_pct * pf + x_pct * sin)
    best: tuple[float, float | None, float, float] = (-math.inf, None, 0.0, 0.0)
    for tap in cfg["taps_pct"]:
        lo = send - mv_drop_pct - reg - lv_drop_pct + tap
        hi = send + tap
        margin = min(lo - (100 - band), (100 + band) - hi)
        if margin > best[0]:
            best = (margin, float(tap), lo, hi)
    margin, tap, lo, hi = best
    return (tap if margin >= 0 else None), round(lo, 4), round(hi, 4), round(reg, 4)
