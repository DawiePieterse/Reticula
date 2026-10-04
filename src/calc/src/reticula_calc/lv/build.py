"""Lay out an LV network on the candidate routes and allocate the loads (plan 2.2).

1. The candidate LV routes are noded at crossings and split at every bend, in a local metric projection.
2. The transformer site joins the nearest route; a shortest-path tree from it makes the network radial (routes that
   close loops are opened and reported).
3. Attachment nodes are placed along the tree: poles at bends and at no more than the maximum span (overhead), or
   joints/kiosks at the same spacing (underground).
4. Each building connects to the nearest attachment node within the maximum service length; others are reported.
5. Branches with no customer downstream are removed.
6. Phases follow the rules' rotation (ReticMaster's paired W W R R B B) in feeder order; 3-phase customers take all
   three phases.
"""

from __future__ import annotations

import math
from itertools import pairwise
from typing import Literal

import networkx as nx
from pydantic import BaseModel
from shapely import STRtree
from shapely.geometry import LineString, Point

from ..geo.routes import Projector, attach_point, route_graph, snap_routes
from ..rules import RuleSet
from .library import lv_config
from .model import Branch, Customer, LvNetwork, Node

Construction = Literal["overhead", "underground"]
SNAP_M = 0.5


class RouteIn(BaseModel):
    id: str
    coordinates: list[tuple[float, float]]


class CustomerIn(BaseModel):
    building_id: str
    lon: float
    lat: float
    phases: Literal[1, 3] = 1
    kind: Literal["residential", "special"] = "residential"
    load_class: str | None = None
    special_kva: float | None = None
    inspected: bool = True
    erf: str | None = None


class BuildRequest(BaseModel):
    source: tuple[float, float]
    routes: list[RouteIn]
    customers: list[CustomerIn]
    construction: Construction
    roads: list[list[tuple[float, float]]] = []


class BuildIssue(BaseModel):
    severity: Literal["error", "warning"]
    code: str
    message: str
    count: int = 1
    samples: list[str] = []


class BuildResult(BaseModel):
    network: LvNetwork
    issues: list[BuildIssue]


def build_network(req: BuildRequest, rules: RuleSet) -> BuildResult:
    cfg = lv_config(rules)
    spacing = float(rules.data["overhead"]["max_span_m"]) if req.construction == "overhead" else float(cfg.get("kiosk_spacing_m", 60))
    max_service = float(cfg["max_service_length_m"])
    issues: list[BuildIssue] = []

    pts = [c for r in req.routes for c in r.coordinates] + [req.source]
    lon0 = sum(p[0] for p in pts) / len(pts)
    lat0 = sum(p[1] for p in pts) / len(pts)
    pr = Projector(lon0, lat0)

    routes = snap_routes({r.id: LineString([pr.xy(*c) for c in r.coordinates]) for r in req.routes if len(r.coordinates) >= 2})
    if not routes:
        raise ValueError("No LV routes to lay the network on; mark LV routes on the field screen first")

    # 1. Node at crossings, then split every bend so each edge is straight.
    g = route_graph(routes, SNAP_M)

    # 2. Join the transformer to the nearest route.
    sx, sy = pr.xy(*req.source)
    src_pt = Point(sx, sy)
    k_proj, _ = attach_point(g, src_pt)
    proj_xy = g.nodes[k_proj]["xy"]
    # The transformer stands on the route: it is moved to the nearest point and feeds every direction from there.
    source_key = k_proj
    moved = src_pt.distance(Point(proj_xy))
    sx, sy = proj_xy
    if moved > 1:
        issues.append(BuildIssue(severity="warning" if moved <= max_service else "error", code="source_moved_to_route",
                                 message=f"The transformer site is {moved:.0f} m from the nearest LV route; it was placed on the route."))

    # 3. Radial: shortest-path tree from the source.
    if not nx.is_connected(g):
        comp = nx.node_connected_component(g, source_key)
        dropped = g.number_of_nodes() - len(comp)
        g = g.subgraph(comp).copy()
        issues.append(BuildIssue(severity="warning", code="routes_not_connected",
                                 message="Some LV routes do not connect to the transformer's network and were left out.", count=dropped))
    _, paths = nx.single_source_dijkstra(g, source_key, weight="length")
    tree = nx.DiGraph()
    for path in paths.values():
        for a, b in pairwise(path):
            if not tree.has_edge(a, b):
                tree.add_edge(a, b, **g.edges[a, b])
    for n in tree.nodes:
        tree.nodes[n]["xy"] = g.nodes[n]["xy"]
    opened = g.number_of_edges() - tree.number_of_edges()
    if opened:
        issues.append(BuildIssue(severity="warning", code="route_loop_opened",
                                 message="Routes formed loops; the network was made radial by leaving out the longer way round.", count=opened))

    # 4. Attachment nodes along each straight edge, no further apart than the span/kiosk spacing.
    net = nx.DiGraph()
    counter = {"n": 0}

    def new_node(xy: tuple[float, float]) -> str:
        counter["n"] += 1
        nid = f"N{counter['n']}"
        net.add_node(nid, xy=xy)
        return nid

    ids: dict[tuple, str] = {source_key: "S"}  # type: ignore[dict-item]
    net.add_node("S", xy=(sx, sy))
    for a, b in nx.bfs_edges(tree, source_key):
        for k in (a, b):
            if k not in ids:
                ids[k] = new_node(tree.nodes[k]["xy"])
        e = tree.edges[a, b]
        line = LineString([tree.nodes[a]["xy"], tree.nodes[b]["xy"]])
        n_spans = max(1, math.ceil(line.length / spacing - 1e-9)) if e["route_id"] is not None else 1
        prev = ids[a]
        for i in range(1, n_spans):
            mid = new_node(tuple(line.interpolate(i / n_spans, normalized=True).coords[0]))
            net.add_edge(prev, mid, route_id=e["route_id"])
            prev = mid
        net.add_edge(prev, ids[b], route_id=e["route_id"])

    # 5. Connect each building to the nearest attachment node within the service length.
    attach_ids = [n for n in net.nodes]
    attach_tree = STRtree([Point(net.nodes[n]["xy"]) for n in attach_ids])
    customers: list[Customer] = []
    services: list[tuple[str, str, tuple, tuple]] = []
    unconnected: list[str] = []
    for c in req.customers:
        cxy = pr.xy(c.lon, c.lat)
        idx = attach_tree.query_nearest(Point(cxy), max_distance=max_service)
        if not len(idx):
            unconnected.append(c.erf or c.building_id)
            continue
        at = attach_ids[int(idx[0])]
        cid = f"C-{c.building_id}"
        net.add_node(cid, xy=cxy, customer=True)
        services.append((at, cid, net.nodes[at]["xy"], cxy))
        customers.append(Customer(id=c.building_id, building_id=c.building_id, node_id=cid, phases=["R"], kind=c.kind,
                                  load_class=c.load_class, special_kva=c.special_kva, inspected=c.inspected, erf=c.erf))
    for at, cid, _, _ in services:
        net.add_edge(at, cid, route_id=None, service=True)
    if unconnected:
        issues.append(BuildIssue(severity="error", code="customer_unconnected",
                                 message=f"Some buildings are more than {max_service:g} m from any LV route and cannot be connected.",
                                 count=len(unconnected), samples=unconnected[:10]))
    if not customers:
        raise ValueError("No building is within reach of the LV routes")

    # 6. Remove branches that feed no customer.
    keep = {"S"}
    for c in customers:
        keep.update(nx.ancestors(net, c.node_id) | {c.node_id})
    pruned = [n for n in net.nodes if n not in keep]
    net.remove_nodes_from(pruned)

    # Underground: drop plain joints between two straight lengths of the same route.
    if req.construction == "underground":
        for n in list(nx.topological_sort(net)):
            if n == "S" or net.nodes[n].get("customer") or net.in_degree(n) != 1 or net.out_degree(n) != 1:
                continue
            (p,), (ch,) = list(net.predecessors(n)), list(net.successors(n))
            if net.nodes[ch].get("customer"):
                continue
            a, b, c2 = net.nodes[p]["xy"], net.nodes[n]["xy"], net.nodes[ch]["xy"]
            if deviation_deg(a, b, c2) < 1 and net.edges[p, n]["route_id"] == net.edges[n, ch]["route_id"]:
                rid = net.edges[p, n]["route_id"]
                via = [*net.edges[p, n].get("via", []), b, *net.edges[n, ch].get("via", [])]
                net.remove_node(n)
                net.add_edge(p, ch, route_id=rid, via=via)

    # 7. Phases in feeder order (depth-first from the transformer, nearest first).
    rotation = list(cfg["phase_rotation"])
    by_node = {c.node_id: c for c in customers}
    order = [n for n in nx.dfs_preorder_nodes(net, "S", sort_neighbors=lambda ns: sorted(ns, key=lambda m: _dist(net, "S", m)))]
    single = 0
    phase_of: dict[str, list[str]] = {}
    want3 = {f"C-{c.building_id}" for c in req.customers if c.phases == 3}
    for n in order:
        if n not in by_node:
            continue
        if n in want3:
            phase_of[n] = ["R", "W", "B"]
        else:
            phase_of[n] = [rotation[single % len(rotation)]]
            single += 1
    for c in customers:
        c.phases = phase_of[c.node_id]  # type: ignore[assignment]

    # Output: WGS84 geometry, metric lengths.
    road_lines = [LineString([pr.xy(*p) for p in r]) for r in req.roads if len(r) >= 2]
    road_tree = STRtree(road_lines) if road_lines else None
    kind_attach = "pole" if req.construction == "overhead" else "kiosk"
    nodes: list[Node] = []
    for n in net.nodes:
        x, y = net.nodes[n]["xy"]
        lon, lat = pr.ll(x, y)
        kind = "source" if n == "S" else "connection" if net.nodes[n].get("customer") else (
            kind_attach if req.construction == "overhead" or any(net.nodes[s].get("customer") for s in net.successors(n)) else "junction")
        nodes.append(Node(id=n, kind=kind, lon=lon, lat=lat))
    feeder_cond, service_cond = "", str(cfg["service_conductor"])
    branches: list[Branch] = []
    for i, (a, b) in enumerate(nx.bfs_edges(net, "S")):
        e = net.edges[a, b]
        xy = [net.nodes[a]["xy"], *e.get("via", []), net.nodes[b]["xy"]]
        line = LineString(xy)
        is_service = bool(e.get("service"))
        crosses = bool(road_tree is not None and any(line.crosses(road_lines[int(j)]) for j in road_tree.query(line)))
        branches.append(Branch(id=f"B{i + 1}", from_id=a, to_id=b, kind="service" if is_service else "feeder",
                               construction=req.construction, conductor=service_cond if is_service else feeder_cond,
                               length_m=round(line.length, 2), geometry=[pr.ll(*p) for p in xy], route_id=e.get("route_id"),
                               crosses_road=crosses))
    network = LvNetwork(source_id="S", nodes=nodes, branches=branches, customers=customers)
    problems = network.validate_radial()
    if problems:
        raise ValueError("The network could not be made radial: " + "; ".join(problems))

    not_inspected = [c.erf or c.building_id for c in customers if not c.inspected]
    if not_inspected:
        issues.append(BuildIssue(severity="warning", code="not_inspected",
                                 message="Some connected buildings were not inspected on site; their loads are estimates.",
                                 count=len(not_inspected), samples=not_inspected[:10]))
    return BuildResult(network=network, issues=issues)


def _dist(net: nx.DiGraph, a: str, b: str) -> float:
    (x1, y1), (x2, y2) = net.nodes[a]["xy"], net.nodes[b]["xy"]
    return math.hypot(x2 - x1, y2 - y1)


def deviation_deg(a, b, c) -> float:
    """Change of direction at b, in degrees (0 = straight on)."""
    h1 = math.atan2(b[1] - a[1], b[0] - a[0])
    h2 = math.atan2(c[1] - b[1], c[0] - b[0])
    d = abs(math.degrees(h2 - h1)) % 360
    return min(d, 360 - d)

