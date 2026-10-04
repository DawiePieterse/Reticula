"""Transformer placement and sizing (plans 3.1, 3.2).

Every building goes to the transformer site nearest along the LV routes (within the rules' reach). Each site that
serves someone is sized from its consumers' Herman-Beta demand (single- and three-phase groups added) plus the growth
allowance, to the smallest standard rating within the utilisation limit. A site marked as a pole-mount transformer
stays pole-mounted when the rating allows it and the LV network is overhead; otherwise it becomes a mini-sub.
"""

from __future__ import annotations

from typing import Literal

import networkx as nx
from pydantic import BaseModel
from shapely.geometry import LineString, Point

from ..calcs.admd import GroupLoad, GroupRequest, group
from ..geo.routes import Projector, attach_point, route_graph, snap_routes
from ..lv.build import CustomerIn, RouteIn
from ..rules import RuleSet
from ..rules.loader import RulesError


class SiteIn(BaseModel):
    id: str
    kind: Literal["transformer", "minisub"]
    lon: float
    lat: float


class Placement(BaseModel):
    site_id: str
    customers: list[str]
    demand_kva: float
    design_kva: float
    unit: Literal["pole_mount", "minisub"]
    rating_kva: float | None
    z_pct: float | None
    x_r: float | None
    utilisation_pct: float | None
    note: str | None = None


class PlacementResult(BaseModel):
    placements: list[Placement]
    unallocated: list[str]
    unused_sites: list[str]


def mv_config(rules: RuleSet) -> dict:
    cfg = rules.data.get("mv_design")
    if not cfg:
        raise RulesError(f"rules {rules.ref} have no mv_design section; use eskom/0.4.0 or later")
    return cfg


def allocate(sites: list[SiteIn], customers: list[CustomerIn], lv_routes: list[RouteIn], rules: RuleSet,
             lv_construction: Literal["overhead", "underground"]) -> PlacementResult:
    cfg = mv_config(rules)
    reach = float(cfg["max_lv_reach_m"])
    max_service = float(rules.data["lv_design"]["max_service_length_m"])
    pts = [c for r in lv_routes for c in r.coordinates] + [(s.lon, s.lat) for s in sites]
    if not pts:
        raise ValueError("No LV routes or transformer sites")
    pr = Projector(sum(p[0] for p in pts) / len(pts), sum(p[1] for p in pts) / len(pts))
    routes = snap_routes({r.id: LineString([pr.xy(*c) for c in r.coordinates]) for r in lv_routes if len(r.coordinates) >= 2})
    if not routes:
        raise ValueError("No LV routes to allocate buildings along; mark LV routes on the field screen first")
    g = route_graph(routes)

    site_nodes = {s.id: attach_point(g, Point(pr.xy(s.lon, s.lat)))[0] for s in sites}
    cust_nodes: dict[str, tuple[tuple, float]] = {}
    for c in customers:
        k, d = attach_point(g, Point(pr.xy(c.lon, c.lat)))
        cust_nodes[c.building_id] = (k, d)
    dist = {sid: nx.single_source_dijkstra_path_length(g, node, cutoff=reach, weight="length") for sid, node in site_nodes.items()}

    assigned: dict[str, list[CustomerIn]] = {s.id: [] for s in sites}
    unallocated: list[str] = []
    for c in customers:
        k, offset = cust_nodes[c.building_id]
        options = [(dist[s.id][k] + offset, i, s.id) for i, s in enumerate(sites) if k in dist[s.id] and offset <= max_service]
        if not options:
            unallocated.append(c.erf or c.building_id)
            continue
        assigned[min(options)[2]].append(c)

    placements: list[Placement] = []
    growth = 1 + float(cfg.get("growth_allowance_pct", 0)) / 100
    max_util = float(cfg.get("max_utilisation_pct", 100)) / 100
    for s in sites:
        cs = assigned[s.id]
        if not cs:
            continue
        loads = [GroupLoad(id=c.building_id, kind=c.kind, kva=max(c.special_kva or 1.0, 0.01), load_class=c.load_class, phases=c.phases) for c in cs]
        demand = group(GroupRequest(rules=rules.ref, loads=loads), rules).total_kva.value
        design = demand * growth
        pole_ok = s.kind == "transformer" and lv_construction == "overhead"
        unit: Literal["pole_mount", "minisub"] = "pole_mount"
        note = None
        sizes = sorted(cfg["pole_mount"], key=lambda t: t["kva"])
        if not pole_ok or design > float(cfg["pole_mount_max_kva"]) * max_util:
            unit = "minisub"
            sizes = sorted(cfg["minisub"], key=lambda t: t["kva"])
            if s.kind == "transformer":
                note = ("the site was marked for a pole-mount transformer, but the LV network is underground" if lv_construction == "underground"
                        else f"the design demand {design:.0f} kVA needs more than a pole-mount transformer")
        chosen = next((t for t in sizes if design <= t["kva"] * max_util), None)
        placements.append(Placement(
            site_id=s.id, customers=[c.building_id for c in cs], demand_kva=round(demand, 2), design_kva=round(design, 2), unit=unit,
            rating_kva=chosen["kva"] if chosen else None, z_pct=chosen["z_pct"] if chosen else None, x_r=chosen.get("x_r") if chosen else None,
            utilisation_pct=round(100 * design / chosen["kva"], 1) if chosen else None,
            note=note if chosen else f"the design demand {design:.0f} kVA exceeds the largest unit in the rules; split the area"))
    return PlacementResult(placements=placements, unallocated=unallocated, unused_sites=[s.id for s in sites if not assigned[s.id]])
