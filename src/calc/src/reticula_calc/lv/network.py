"""LV network model (plan 2.1): the LV routes and sites marked in the field, joined into a node-branch network.

The engineer marks LV routes as lines and transformer, mini-sub and pole sites as points (plan 1.5). They are drawn
by hand on a tablet, so ends seldom meet exactly. This module joins them within the rules file's tolerances:

- Route ends close together become one node; a route end close to another route tees onto it.
- Routes that cross are joined where they cross.
- A source (transformer or mini-sub) feeds the nearest route within reach, by a link to the nearest point on it.
- A pole site within reach of a route becomes a node on it, splitting the route into spans.

Each connected part should have one source and no loops, because LV networks are run radially. Parts that break
this are flagged, not fixed: the engineer decides where the open point goes. A radial part fed by one source is
oriented away from it and split into feeders, one per route leaving the source's LV board.

The model is plain data (LvNetwork) so the API can store it in Postgres and send it back for later checks;
`LvNetwork.graph()` turns it into a networkx graph again. Geometry work happens in metres in the Lo zone nearest
the routes; lengths are geodesic on WGS84, as for imported layers.
"""

from __future__ import annotations

import math
from collections import defaultdict
from dataclasses import dataclass, field
from itertools import pairwise
from typing import Any, Literal

import networkx as nx
from pydantic import BaseModel
from pyproj import Geod
from shapely import STRtree
from shapely.geometry import LineString, Point, shape
from shapely.geometry.base import BaseGeometry
from shapely.ops import substring, transform

from ..geo import crs as crs_mod
from ..issues import Issue
from ..issues import issue as _issue
from ..rules import RulesError, RuleSet

GEOD = Geod(ellps="WGS84")
# Points closer than this are the same point; also the shortest piece of route kept.
EPS_M = 0.05
SITE_LABELS = {"transformer": "TX", "minisub": "MS", "pole": "P", "connection_point": "CP"}

NodeKind = Literal["source", "pole", "tap", "junction", "joint", "end"]
BranchKind = Literal["route", "link"]


class CandidateIn(BaseModel):
    """A site or route marked in the field (API `candidates`): geometry is GeoJSON in WGS84."""

    id: str
    kind: str
    geometry: dict[str, Any]
    label: str | None = None
    """A label to keep (TX3, K12); otherwise sites are numbered by kind in the order given."""


class BuildRequest(BaseModel):
    rules: str
    candidates: list[CandidateIn]


class Node(BaseModel):
    id: str
    kind: NodeKind
    coordinates: tuple[float, float]
    label: str | None = None
    """TX1, MS1 or P1 for a node at a marked site, numbered in the order the sites were given."""
    candidate_id: str | None = None
    feeder: str | None = None
    distance_m: float | None = None
    """Along the network from the source, when the node is in a radial part fed by one source."""


class Branch(BaseModel):
    id: str
    kind: BranchKind
    """route: part of a marked LV route. link: from a source to the nearest point on its route."""
    from_node: str
    to_node: str
    """In a fed radial part, from_node is the end nearer the source."""
    coordinates: list[tuple[float, float]]
    length_m: float
    candidate_id: str | None = None
    feeder: str | None = None


class Feeder(BaseModel):
    id: str
    source: str
    branches: int
    length_m: float
    ends: int
    farthest_m: float


class Summary(BaseModel):
    routes: int
    sources: int
    sources_connected: int
    poles: int
    poles_placed: int
    feeders: int
    nodes: int
    branches: int
    route_length_m: float
    unfed_length_m: float


class LvNetwork(BaseModel):
    rules_ref: str
    rules_hash: str
    clause: str
    nodes: list[Node]
    branches: list[Branch]
    feeders: list[Feeder]
    issues: list[Issue]
    summary: Summary

    def graph(self) -> nx.MultiGraph:
        """The network as a graph: node and edge attributes are the model's fields, keyed by id, with each
        edge's `geometry` as a shapely LineString in lon/lat."""
        g = nx.MultiGraph()
        for n in self.nodes:
            g.add_node(n.id, **n.model_dump(exclude={"id"}))
        for b in self.branches:
            attrs = b.model_dump(exclude={"id", "coordinates"})
            g.add_edge(b.from_node, b.to_node, key=b.id, geometry=LineString(b.coordinates), **attrs)
        return g


@dataclass(frozen=True)
class Params:
    join_m: float
    near_miss_m: float
    source_reach_m: float
    pole_reach_m: float
    sources: tuple[str, ...]
    clause: str
    taps: tuple[str, ...] = ()
    """Site kinds linked to a route like a source but fed by it: transformers on the MV network."""
    tap_reach_m: float = 0.0
    route_kind: str = "lv_route"


def params(rules: RuleSet) -> Params:
    sec = rules.data.get("lv_network")
    if not sec:
        raise RulesError(f"rules {rules.ref} has no lv_network section; the LV network needs eskom/0.3.0 or later")
    return Params(
        join_m=float(sec["join_m"]),
        near_miss_m=float(sec["near_miss_m"]),
        source_reach_m=float(sec["source_reach_m"]),
        pole_reach_m=float(sec["pole_reach_m"]),
        sources=tuple(sec["sources"]),
        clause=sec.get("clause", ""),
    )


@dataclass
class _Site:
    kind: str
    label: str
    candidate_id: str


@dataclass
class _Piece:
    kind: BranchKind
    a: int
    b: int
    coords: list[tuple[float, float]]
    candidate_id: str | None


@dataclass
class _Builder:
    """Joins projected routes into nodes and pieces. Node ids are ints merged with a union-find."""

    p: Params
    lines: list[LineString]
    parent: list[int] = field(default_factory=list)
    xy: list[tuple[float, float]] = field(default_factory=list)
    markers: list[list[tuple[float, int]]] = field(default_factory=list)
    sites: dict[int, _Site] = field(default_factory=dict)
    # Route ends that came within the near-miss distance of another route without joining it.
    near: list[int] = field(default_factory=list)

    def __post_init__(self) -> None:
        self.markers = [[] for _ in self.lines]

    def new_node(self, xy: tuple[float, float]) -> int:
        self.parent.append(len(self.parent))
        self.xy.append(xy)
        return len(self.parent) - 1

    def find(self, n: int) -> int:
        while self.parent[n] != n:
            self.parent[n] = self.parent[self.parent[n]]
            n = self.parent[n]
        return n

    def union(self, a: int, b: int) -> int:
        """Merges two nodes; the older one keeps its position."""
        ra, rb = self.find(a), self.find(b)
        if ra == rb:
            return ra
        keep, drop = min(ra, rb), max(ra, rb)
        self.parent[drop] = keep
        if drop in self.sites and keep not in self.sites:
            self.sites[keep] = self.sites.pop(drop)
        return keep

    def nearby(self, r: int, s: float) -> int | None:
        """A node already on route r within the join distance of position s along it."""
        best = min(((abs(ms - s), n) for ms, n in self.markers[r]), default=None)
        return self.find(best[1]) if best and best[0] <= self.p.join_m else None

    def node_on(self, r: int, s: float) -> int:
        """The node at position s along route r, reusing one within the join distance."""
        n = self.nearby(r, s)
        if n is None:
            pt = self.lines[r].interpolate(s)
            n = self.new_node((pt.x, pt.y))
            self.markers[r].append((s, n))
        return n

    def pieces(self, refs: list[str]) -> list[_Piece]:
        out: list[_Piece] = []
        for r, line in enumerate(self.lines):
            ms = sorted((s, self.find(n)) for s, n in self.markers[r])
            for (s0, a), (s1, b) in pairwise(ms):
                if s1 - s0 < EPS_M or (a == b and s1 - s0 <= 2 * self.p.join_m):
                    continue  # the same point twice, or an overshoot folded back onto one node
                coords = [(x, y) for x, y, *_ in substring(line, s0, s1).coords]
                coords[0], coords[-1] = self.xy[a], self.xy[b]
                out.append(_Piece("route", a, b, coords, refs[r]))
        return out


def _route_lines(candidates: list[CandidateIn], kind: str = "lv_route") -> list[tuple[str, BaseGeometry]]:
    return [(c.id, shape(c.geometry)) for c in candidates if c.kind == kind]


def build_network(req: BuildRequest, rules: RuleSet, p: Params | None = None) -> LvNetwork:
    """Joins routes and sites into a network. `p` overrides the rules file's LV tolerances (the MV network uses its own)."""
    p = p or params(rules)
    issues: list[Issue] = []

    raw_routes = _route_lines(req.candidates, p.route_kind)
    sites = [(c, shape(c.geometry)) for c in req.candidates if c.kind in (*p.sources, *p.taps, "pole")]
    all_coords = [xy for _, g in raw_routes for xy in _coords(g)] + [xy for _, g in sites for xy in _coords(g)]
    if not all_coords:
        return _empty(rules, p, [_issue("error", "no_routes", f"No {_what(p)} routes are marked. Mark them in the field.", [], [])])

    zone = crs_mod.nearest_lo(sum(x for x, _ in all_coords) / len(all_coords))
    fwd, inv = crs_mod.from_wgs84(zone), crs_mod.to_wgs84(zone)

    def project(g: BaseGeometry) -> BaseGeometry:
        return transform(fwd.transform, g)

    def lonlat(xy: tuple[float, float]) -> tuple[float, float]:
        lon, lat = inv.transform(*xy)
        return (round(lon, 7), round(lat, 7))

    refs: list[str] = []
    lines: list[LineString] = []
    bad: list[str] = []
    for ref, g in raw_routes:
        line = project(g) if isinstance(g, LineString) else None
        if line is None or line.length < 2 * EPS_M:
            bad.append(ref)
            continue
        refs.append(ref)
        lines.append(line)
    if bad:
        issues.append(_issue("warning", "route_unusable", f"Some {_what(p)} routes are not lines with length and were left out.", bad, []))
    if not lines:
        issues.append(_issue("error", "no_routes", f"No {_what(p)} routes are marked. Mark them in the field.", [], []))
        return _empty(rules, p, issues)

    b = _Builder(p, lines)
    tree = STRtree(lines)
    _join_ends(b, tree)
    _join_crossings(b, tree, issues, lonlat)

    links: list[_Piece] = []
    counters: dict[str, int] = defaultdict(int)
    labels: list[tuple[_Site, BaseGeometry]] = []
    for c, g in sites:
        counters[c.kind] += 1
        labels.append((_Site(c.kind, c.label or f"{SITE_LABELS[c.kind]}{counters[c.kind]}", c.id), g))
    unconnected, untapped, off_route, doubled = [], [], [], []
    placed = 0
    # Sources first, so that a pole marked at a pole-mounted transformer becomes that transformer's pole.
    for site, g in sorted(labels, key=lambda t: t[0].kind == "pole"):
        if not isinstance(g, Point):
            continue
        pt = project(g)
        reach = p.pole_reach_m if site.kind == "pole" else p.tap_reach_m if site.kind in p.taps else p.source_reach_m
        hit = _nearest(tree, pt, reach)
        if hit is None:
            (off_route if site.kind == "pole" else untapped if site.kind in p.taps else unconnected).append((site.label, (g.x, g.y)))
            continue
        r, d = hit
        tee = b.node_on(r, lines[r].project(pt))
        if site.kind == "pole":
            here = b.sites.get(b.find(tee))
            if here is None:
                b.sites[b.find(tee)] = site
            elif here.kind == "pole":
                doubled.append((site.label, (g.x, g.y)))
                continue
            placed += 1
            continue
        if d <= EPS_M and b.find(tee) not in b.sites:
            b.sites[b.find(tee)] = site
            continue
        src = b.new_node((pt.x, pt.y))
        b.sites[src] = site
        links.append(_Piece("link", src, tee, [(pt.x, pt.y), b.xy[b.find(tee)]], site.candidate_id))
    if untapped:
        issues.append(_issue("warning", "tap_unconnected",
                             f"Transformers more than {p.tap_reach_m:g} m from any {_what(p)} route are not supplied.",
                             [s for s, _ in untapped], [a for _, a in untapped]))
    if unconnected:
        issues.append(_issue("warning", "source_unconnected",
                             f"Sources more than {p.source_reach_m:g} m from any LV route feed nothing.",
                             [s for s, _ in unconnected], [a for _, a in unconnected]))
    if off_route:
        issues.append(_issue("warning", "pole_off_route", f"Pole sites more than {p.pole_reach_m:g} m from any LV route were left out.",
                             [s for s, _ in off_route], [a for _, a in off_route]))
    if doubled:
        issues.append(_issue("warning", "pole_doubled", "Pole sites fall on a node that already has a pole; only the first is kept.",
                             [s for s, _ in doubled], [a for _, a in doubled]))

    if not any(s.kind in p.sources for s, _ in labels):
        what = "connection point" if "connection_point" in p.sources else "transformer or mini-sub site"
        issues.append(_issue("error", "no_sources", f"No {what} is marked, so nothing feeds the {_what(p)} routes.", [], []))
    pieces = b.pieces(refs) + [_Piece(lk.kind, b.find(lk.a), b.find(lk.b), lk.coords, lk.candidate_id) for lk in links]
    return _assemble(rules, p, b, pieces, issues, lonlat, len(refs),
                     sum(1 for s, _ in labels if s.kind in p.sources), sum(1 for s, _ in labels if s.kind == "pole"), placed)


def _what(p: Params) -> str:
    return "MV" if p.route_kind == "mv_route" else "LV"


def _coords(g: BaseGeometry) -> list[tuple[float, float]]:
    if isinstance(g, Point):
        return [(g.x, g.y)]
    if isinstance(g, LineString):
        return [(x, y) for x, y, *_ in g.coords]
    return []


def _nearest(tree: STRtree, pt: Point, reach: float, exclude: set[int] | None = None) -> tuple[int, float] | None:
    idx = tree.query(pt, predicate="dwithin", distance=reach)
    found = sorted((tree.geometries[i].distance(pt), int(i)) for i in idx if not exclude or int(i) not in exclude)
    return (found[0][1], found[0][0]) if found else None


def _join_ends(b: _Builder, tree: STRtree) -> None:
    """Route ends within the join distance become one node; a group of ends near another route tees onto it.
    Ends that come close to another route without joining it are noted in `b.near`."""
    ends = [(r, s, Point(line.coords[i])) for r, line in enumerate(b.lines) for i, s in ((0, 0.0), (-1, line.length))]
    end_tree = STRtree([pt for _, _, pt in ends])
    a, c = end_tree.query([pt for _, _, pt in ends], predicate="dwithin", distance=b.p.join_m)
    groups = nx.utils.UnionFind(range(len(ends)))
    for i, j in zip(a.tolist(), c.tolist(), strict=True):
        groups.union(i, j)
    members = {min(g): sorted(g) for g in groups.to_sets()}

    for first, idx in sorted(members.items()):
        rep = ends[first][2]
        node = b.new_node((rep.x, rep.y))
        own = {ends[i][0] for i in idx}
        for i in idx:
            b.markers[ends[i][0]].append((ends[i][1], node))
        hit = _nearest(tree, rep, b.p.join_m, own)
        if hit is not None:
            r = hit[0]
            s = b.lines[r].project(rep)
            existing = b.nearby(r, s)
            if existing is not None:
                b.union(node, existing)
            else:
                b.markers[r].append((s, node))
                q = b.lines[r].interpolate(s)
                b.xy[b.find(node)] = (q.x, q.y)
        elif _nearest(tree, rep, b.p.near_miss_m, own) is not None:
            b.near.append(node)


def _join_crossings(b: _Builder, tree: STRtree, issues: list[Issue], lonlat) -> None:
    """Routes that cross or touch are joined where they meet."""
    left, right = tree.query(b.lines, predicate="intersects")
    overlaps: list[tuple[float, float]] = []
    for i, j in sorted({(int(x), int(y)) for x, y in zip(left.tolist(), right.tolist(), strict=True) if x < y}):
        meet = b.lines[i].intersection(b.lines[j])
        points: list[Point] = []
        for part in getattr(meet, "geoms", [meet]):
            if isinstance(part, Point):
                points.append(part)
            elif isinstance(part, LineString) and part.length > 0:
                points += [Point(part.coords[0]), Point(part.coords[-1])]
                overlaps.append(lonlat(part.interpolate(0.5, normalized=True).coords[0]))
        for pt in points:
            si, sj = b.lines[i].project(pt), b.lines[j].project(pt)
            ni, nj = b.nearby(i, si), b.nearby(j, sj)
            if ni is not None and nj is not None:
                b.union(ni, nj)
            elif ni is not None:
                b.markers[j].append((sj, ni))
            elif nj is not None:
                b.markers[i].append((si, nj))
            else:
                n = b.new_node((pt.x, pt.y))
                b.markers[i].append((si, n))
                b.markers[j].append((sj, n))
    if overlaps:
        issues.append(_issue("warning", "route_overlap", "LV routes are drawn on top of each other for part of their length.",
                             [], overlaps))


def _bearing(a: tuple[float, float], b: tuple[float, float]) -> float:
    return math.degrees(math.atan2(b[0] - a[0], b[1] - a[1])) % 360


def _assemble(rules: RuleSet, p: Params, b: _Builder, pieces: list[_Piece], issues: list[Issue], lonlat,
              routes: int, sources: int, poles: int, poles_placed: int) -> LvNetwork:
    used = sorted({n for pc in pieces for n in (pc.a, pc.b)})
    node_id = {n: f"N{i + 1}" for i, n in enumerate(used)}
    g = nx.MultiGraph()
    g.add_nodes_from(used)
    branches: list[Branch] = []
    for k, pc in enumerate(pieces):
        ll = [lonlat(xy) for xy in pc.coords]
        branch = Branch(id=f"B{k + 1}", kind=pc.kind, from_node=node_id[pc.a], to_node=node_id[pc.b], coordinates=ll,
                        length_m=round(GEOD.geometry_length(LineString(ll)), 2), candidate_id=pc.candidate_id)
        branches.append(branch)
        g.add_edge(pc.a, pc.b, key=k)

    def site(n: int) -> _Site | None:
        return b.sites.get(n)

    nodes = {n: Node(id=node_id[n], kind=_node_kind(site(n), g.degree(n), p), coordinates=lonlat(b.xy[n]),
                     label=site(n).label if site(n) else None, candidate_id=site(n).candidate_id if site(n) else None)
             for n in used}

    # A gap is flagged once, and only where an end is left dangling: an end joined some other way is fine.
    near: list[int] = []
    for n in dict.fromkeys(b.find(x) for x in b.near):
        if n in nodes and g.degree(n) == 1 and all(math.dist(b.xy[n], b.xy[m]) > p.near_miss_m for m in near):
            near.append(n)
    if near:
        issues.append(_issue(
            "warning", "near_miss",
            f"Route ends stop short of another route by {p.join_m:g} to {p.near_miss_m:g} m and were not joined. "
            "Redraw them if they should meet.", [nodes[n].id for n in near], [nodes[n].coordinates for n in near]))

    feeders: list[Feeder] = []
    unfed: list[tuple[str, tuple[float, float]]] = []
    unfed_length = 0.0
    tied: list[tuple[str, tuple[float, float]]] = []
    loops: list[tuple[str, tuple[float, float]]] = []
    for comp in sorted(nx.connected_components(g), key=min):
        sub = g.subgraph(comp)
        srcs = sorted(n for n in comp if site(n) and site(n).kind in p.sources)
        cyclomatic = sub.number_of_edges() - sub.number_of_nodes() + 1
        if cyclomatic > 0:
            tree_edges = {frozenset((u, v)) for u, v in nx.bfs_edges(nx.Graph(sub), min(comp))}
            seen: set[frozenset[int]] = set()
            for u, v, k in sorted(sub.edges(keys=True), key=lambda e: e[2]):
                pair = frozenset((u, v))
                if pair in tree_edges and pair not in seen and u != v:
                    seen.add(pair)
                    continue
                br = branches[k]
                loops.append((br.id, br.coordinates[len(br.coordinates) // 2]))
        if not srcs:
            length = sum(branches[k].length_m for _, _, k in sub.edges(keys=True))
            unfed_length += length
            k0 = min(k for _, _, k in sub.edges(keys=True))
            unfed.append((branches[k0].id, branches[k0].coordinates[len(branches[k0].coordinates) // 2]))
        elif len(srcs) > 1:
            tied += [(site(n).label, nodes[n].coordinates) for n in srcs]
        elif cyclomatic == 0:
            feeders += _orient(srcs[0], sub, pieces, branches, nodes, site(srcs[0]).label, node_id, b)

    if unfed:
        issues.append(_issue("warning", "unfed", f"Parts of the {_what(p)} network have no source within reach.",
                             [s for s, _ in unfed], [a for _, a in unfed]))
    if tied:
        issues.append(_issue("error", "sources_tied", "Sources are joined by LV routes. Mark where the open point goes.",
                             [s for s, _ in tied], [a for _, a in tied]))
    if loops:
        issues.append(_issue("error", "loop", f"The {_what(p)} routes form a loop; the network must be radial. "
                             "Break each loop at one of the branches listed.", [s for s, _ in loops], [a for _, a in loops]))

    ordered_nodes = [nodes[n] for n in used]
    return LvNetwork(
        rules_ref=rules.ref, rules_hash=rules.hash, clause=p.clause, nodes=ordered_nodes, branches=branches,
        feeders=feeders, issues=issues,
        summary=Summary(
            routes=routes, sources=sources,
            sources_connected=sum(1 for n in ordered_nodes if n.kind == "source"),
            poles=poles, poles_placed=poles_placed,
            feeders=len(feeders), nodes=len(ordered_nodes), branches=len(branches),
            route_length_m=round(sum(br.length_m for br in branches if br.kind == "route"), 1),
            unfed_length_m=round(unfed_length, 1),
        ),
    )


def _node_kind(site: _Site | None, degree: int, p: Params) -> NodeKind:
    if site:
        return "pole" if site.kind == "pole" else "tap" if site.kind in p.taps else "source"
    return "junction" if degree >= 3 else "joint" if degree == 2 else "end"


def _orient(src: int, sub: nx.MultiGraph, pieces: list[_Piece], branches: list[Branch], nodes: dict[int, Node],
            label: str, node_id: dict[int, str], b: _Builder) -> list[Feeder]:
    """Points a radial part away from its source, measures distances and numbers its feeders.

    A source linked to its route has an LV board where the link meets the route; otherwise the board is the source
    itself. Each route branch leaving the board starts a feeder, numbered clockwise from north.
    """
    incident = list(sub.edges(src, keys=True))
    board, start = src, 0.0
    if len(incident) == 1 and pieces[incident[0][2]].kind == "link":
        k = incident[0][2]
        board = incident[0][1] if incident[0][0] == src else incident[0][0]
        start = branches[k].length_m
        _point(branches[k], src, node_id)
    nodes[src].distance_m = 0.0
    nodes[board].distance_m = round(start, 2)

    outgoing = []
    for u, v, k in sub.edges(board, keys=True):
        if pieces[k].kind == "link":
            continue
        far = v if u == board else u
        pc = pieces[k]
        coords = pc.coords if pc.a == board else pc.coords[::-1]
        outgoing.append((_bearing(b.xy[board], coords[1]), k, far))
    feeders: list[Feeder] = []
    for i, (_, k0, first) in enumerate(sorted(outgoing)):
        fid = f"{label}-F{i + 1}"
        count, length, ends, farthest = 0, 0.0, 0, 0.0
        stack = [(board, k0, first)]
        while stack:
            parent, k, child = stack.pop()
            br = branches[k]
            _point(br, parent, node_id)
            br.feeder = fid
            dist = (nodes[parent].distance_m or 0.0) + br.length_m
            nodes[child].distance_m = round(dist, 2)
            nodes[child].feeder = fid
            count, length, farthest = count + 1, length + br.length_m, max(farthest, dist)
            onward = [(child, kk, nxt) for _, nxt, kk in sub.edges(child, keys=True) if kk != k]
            if not onward:
                ends += 1
            stack += onward
        feeders.append(Feeder(id=fid, source=nodes[src].id, branches=count, length_m=round(length, 1), ends=ends,
                              farthest_m=round(farthest, 1)))
    return feeders


def _point(br: Branch, parent: int, node_id: dict[int, str]) -> None:
    """Makes the branch run away from `parent`."""
    if br.from_node != node_id[parent]:
        br.from_node, br.to_node = br.to_node, br.from_node
        br.coordinates = br.coordinates[::-1]


def _empty(rules: RuleSet, p: Params, issues: list[Issue]) -> LvNetwork:
    return LvNetwork(rules_ref=rules.ref, rules_hash=rules.hash, clause=p.clause, nodes=[], branches=[], feeders=[],
                     issues=issues, summary=Summary(routes=0, sources=0, sources_connected=0, poles=0, poles_placed=0,
                                                    feeders=0, nodes=0, branches=0, route_length_m=0, unfed_length_m=0))
