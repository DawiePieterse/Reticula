"""Route geometry shared by the LV and MV designs: a local metric projection, node keys, and joining hand-drawn routes."""

from __future__ import annotations

from itertools import pairwise

import networkx as nx
from pyproj import Transformer
from shapely import STRtree
from shapely.geometry import LineString, Point
from shapely.ops import unary_union


class Projector:
    def __init__(self, lon0: float, lat0: float):
        proj = f"+proj=tmerc +lat_0={lat0} +lon_0={lon0} +k=1 +x_0=0 +y_0=0 +ellps=WGS84 +units=m +no_defs"
        self.fwd = Transformer.from_crs("EPSG:4326", proj, always_xy=True)
        self.inv = Transformer.from_crs(proj, "EPSG:4326", always_xy=True)

    def xy(self, lon: float, lat: float) -> tuple[float, float]:
        return self.fwd.transform(lon, lat)

    def ll(self, x: float, y: float) -> tuple[float, float]:
        lon, lat = self.inv.transform(x, y)
        return round(lon, 8), round(lat, 8)


def key(p: tuple[float, float]) -> tuple[float, float]:
    return (round(p[0] / 0.01) * 0.01, round(p[1] / 0.01) * 0.01)


def snap_routes(routes: dict[str, LineString], tol: float = 1.0) -> dict[str, LineString]:
    """Join routes drawn to end on (or near) another route: a hand-drawn T-junction misses by millimetres once
    projected. Each end within `tol` of another route moves onto it, and that route gets a vertex there."""
    lines = {k: list(v.coords) for k, v in routes.items()}
    for rid, coords in lines.items():
        for end in (0, -1):
            pt = Point(coords[end])
            for oid, other in lines.items():
                if oid == rid or len(other) < 2:
                    continue
                line = LineString(other)
                if 1e-9 < line.distance(pt) <= tol or (line.distance(pt) <= 1e-9 and coords[end] not in other):
                    d = line.project(pt)
                    snap = tuple(line.interpolate(d).coords[0])
                    coords[end] = snap
                    # Insert the junction as a vertex of the other route, in order along it.
                    acc = 0.0
                    for i in range(len(other) - 1):
                        seg = LineString([other[i], other[i + 1]])
                        if acc - 1e-9 <= d <= acc + seg.length + 1e-9:
                            if snap not in (other[i], other[i + 1]):
                                other.insert(i + 1, snap)
                            break
                        acc += seg.length
                    break
    return {k: LineString(v) for k, v in lines.items()}


def route_graph(routes: dict[str, LineString], snap_m: float = 0.5) -> nx.Graph:
    """Routes noded where they cross and split at every bend: nodes keyed by `key()` carry `xy`; edges carry `length`
    (m) and the `route_id` they lie on."""
    tree = STRtree(list(routes.values()))
    ids = list(routes.keys())
    noded = unary_union(list(routes.values()))
    g = nx.Graph()
    for piece in getattr(noded, "geoms", [noded]):
        for a, b in pairwise(list(piece.coords)):
            ka, kb = key(a), key(b)
            if ka == kb:
                continue
            seg = LineString([a, b])
            idx = tree.query_nearest(seg.interpolate(0.5, normalized=True), max_distance=snap_m)
            g.add_node(ka, xy=a)
            g.add_node(kb, xy=b)
            g.add_edge(ka, kb, length=seg.length, route_id=ids[int(idx[0])] if len(idx) else None)
    return g


def attach_point(g: nx.Graph, pt: Point) -> tuple[tuple, float]:
    """Insert the point of the graph nearest `pt` as a node (splitting its edge) and return its key and the distance."""
    best = min(g.edges(data=True), key=lambda e: LineString([g.nodes[e[0]]["xy"], g.nodes[e[1]]["xy"]]).distance(pt))
    u, v, data = best
    seg = LineString([g.nodes[u]["xy"], g.nodes[v]["xy"]])
    xy = tuple(seg.interpolate(seg.project(pt)).coords[0])
    k = key(xy)
    if k not in (u, v):
        g.remove_edge(u, v)
        g.add_node(k, xy=xy)
        g.add_edge(u, k, length=Point(g.nodes[u]["xy"]).distance(Point(xy)), route_id=data["route_id"])
        g.add_edge(k, v, length=Point(xy).distance(Point(g.nodes[v]["xy"])), route_id=data["route_id"])
    return k, pt.distance(Point(xy))

