"""LV load allocation and phasing (plan 2.2): each building's load connected to the LV network and given a phase.

The rules file's `lv_loads.attach` says how buildings are connected:

- `pole_boxes`, overhead practice. Services come off LV poles through service distribution boxes. A pole carries
  at most `boxes_per_pole` boxes and each box supplies at most `box_loads` loads, all on the box's phase. A second
  box on a pole is on a different phase from the first (`second_box_other_phase`). Each load goes to the nearest
  pole with room within the service reach, nearest pairs first. A building with no pole within reach, or only full
  ones, is reported for the engineer to mark another pole: it is never connected to a bare route. A pole's loads
  fill its boxes in order around the pole, so neighbours share a box.
- `nearest_point`: each load connects to the nearest point on an LV route and is phased on its own. This is for
  underground networks until kiosks are modelled.

Loads above `single_phase_max_kva` are three-phase. They connect to the nearest pole (or route) on their own
service, use no box, and count a third of their kVA on each phase. Links from a source to its route never take
services. A building with no load estimate yet is reported, not guessed.

A connection is placed on a branch, at a distance along it from the branch's upstream end, so the network itself
is not changed. Boxes (or single loads, with `nearest_point`) are phased per feeder from the far end: each goes to
the phase with the least kVA so far, so the loads beyond any point of the feeder stay as even as they can be.
Phasing balances ADMD, not design current; plan 2.4 works out design currents section by section.
"""

from __future__ import annotations

import math
from dataclasses import dataclass, field
from typing import Literal

from pydantic import BaseModel
from shapely import STRtree
from shapely.geometry import LineString, Point
from shapely.ops import transform

from ..geo import crs as crs_mod
from ..issues import Issue, issue
from ..rules import RulesError, RuleSet
from .network import EPS_M, GEOD, Branch, LvNetwork, Node

PHASES: tuple[str, ...] = ("R", "W", "B")
"""Red, white and blue."""

Phase = Literal["R", "W", "B", "RWB"]
Kind = Literal["residential", "special"]


class LoadIn(BaseModel):
    id: str
    """The load point, or the building when it has no load estimate yet."""
    building_id: str
    label: str | None = None
    """Erf number or other name for messages."""
    coordinates: tuple[float, float]
    kva: float | None = None
    """Design ADMD or special-load kVA; None when the building has no load estimate yet."""
    kind: Kind = "residential"


class AllocateRequest(BaseModel):
    rules: str
    network: LvNetwork
    loads: list[LoadIn]


class Allocation(BaseModel):
    load_id: str
    building_id: str
    label: str | None = None
    kind: Kind
    kva: float
    branch: str
    node: str | None = None
    """The pole or other node the service connects at, when it connects at one."""
    offset_m: float
    """Where the service connects, along the branch from its from_node."""
    at: tuple[float, float]
    service_m: float
    box: str | None = None
    """The service distribution box, e.g. P3-1; None for three-phase loads and with `nearest_point`."""
    feeder: str | None = None
    distance_m: float | None = None
    """Along the network from the source to the connection, on a feeder."""
    phase: Phase | None = None
    location: tuple[float, float] | None = None
    """The building, lon/lat: the service runs from here to `at`."""


class Box(BaseModel):
    id: str
    pole: str
    feeder: str | None = None
    phase: Literal["R", "W", "B"] | None = None
    loads: int
    kva: float
    distance_m: float | None = None


class PhaseLoad(BaseModel):
    customers: int = 0
    kva: float = 0.0
    boxes: int = 0


class FeederPhases(BaseModel):
    feeder: str
    phases: dict[str, PhaseLoad]
    """R, W and B. A three-phase load counts on every phase, with a third of its kVA on each."""
    three_phase: int
    unbalance_pct: float
    """The largest difference between a phase's kVA and the mean of the three, as a percentage of the mean."""


class LoadSummary(BaseModel):
    loads: int
    allocated: int
    unallocated: int
    unestimated: int
    on_feeders: int
    three_phase: int
    boxes: int
    allocated_kva: float
    longest_service_m: float


class LoadAllocation(BaseModel):
    rules_ref: str
    rules_hash: str
    clause: str
    attach: str
    allocations: list[Allocation]
    boxes: list[Box]
    feeders: list[FeederPhases]
    issues: list[Issue]
    summary: LoadSummary


@dataclass(frozen=True)
class Params:
    max_service_m: float
    attach: str
    box_loads: int
    boxes_per_pole: int
    second_box_other_phase: bool
    single_phase_max_kva: float
    clause: str


def params(rules: RuleSet) -> Params:
    sec = rules.data.get("lv_loads")
    if not sec:
        raise RulesError(f"rules {rules.ref} has no lv_loads section; connecting loads needs eskom/0.4.0 or later")
    # The schema requires the box settings with pole_boxes; nearest_point has no boxes.
    return Params(max_service_m=float(sec["max_service_m"]), attach=sec["attach"], box_loads=int(sec.get("box_loads", 0)),
                  boxes_per_pole=int(sec.get("boxes_per_pole", 0)), second_box_other_phase=bool(sec.get("second_box_other_phase", False)),
                  single_phase_max_kva=float(sec["single_phase_max_kva"]), clause=sec.get("clause", ""))


@dataclass
class _Unit:
    """What gets a phase: a box of loads, or one load with `nearest_point`."""

    id: str
    pole: str | None
    feeder: str
    distance_m: float
    loads: list[Allocation] = field(default_factory=list)
    box: Box | None = None

    @property
    def kva(self) -> float:
        return sum(a.kva for a in self.loads)


def _name(load: LoadIn | Allocation) -> str:
    return load.label or load.building_id[:8]


def _issue(severity: Literal["error", "warning"], code: str, message: str, loads: list[LoadIn]) -> Issue:
    return issue(severity, code, message, [_name(x) for x in loads], [x.coordinates for x in loads])


class _Geo:
    """The network's route branches and poles in metres, for nearest-neighbour queries."""

    def __init__(self, net: LvNetwork, routes: list[Branch], p: Params):
        lons = [n.coordinates[0] for n in net.nodes]
        self.fwd = crs_mod.from_wgs84(crs_mod.nearest_lo(sum(lons) / len(lons)))
        self.routes = routes
        self.lines = [self.metres(LineString(b.coordinates)) for b in routes]
        # The route into each node and the first route out of it, for placing a connection at a pole.
        self.into: dict[str, Branch] = {}
        self.out_of: dict[str, Branch] = {}
        for b in routes:
            self.into.setdefault(b.to_node, b)
            self.out_of.setdefault(b.from_node, b)
        self.tree = STRtree(self.lines)
        self.poles = [n for n in net.nodes if n.kind == "pole"] if p.attach == "pole_boxes" else []
        self.pole_pts = [self.metres(Point(n.coordinates)) for n in self.poles]
        self.pole_tree = STRtree(self.pole_pts) if self.poles else None

    def metres(self, g):
        return transform(self.fwd.transform, g)

    def poles_within(self, pt: Point, reach: float) -> list[tuple[float, int]]:
        if self.pole_tree is None:
            return []
        idx = self.pole_tree.query(pt, predicate="dwithin", distance=reach)
        return sorted((self.pole_pts[i].distance(pt), int(i)) for i in idx.tolist())

    def route_within(self, pt: Point, reach: float) -> int | None:
        idx = self.tree.query(pt, predicate="dwithin", distance=reach)
        return min(idx.tolist(), key=lambda i: (self.lines[i].distance(pt), i)) if len(idx) else None


def allocate(req: AllocateRequest, rules: RuleSet, p: Params | None = None) -> LoadAllocation:
    """Connects loads to the network. `p` overrides the rules file's service settings (kiosks on underground routes)."""
    p = p or params(rules)
    net = req.network
    estimated = [x for x in req.loads if x.kva is not None]
    unestimated = [x for x in req.loads if x.kva is None]
    routes = [b for b in net.branches if b.kind == "route"]

    allocations: list[Allocation] = []
    units: list[_Unit] = []
    far: list[LoadIn] = []
    no_pole: list[LoadIn] = []
    full: list[LoadIn] = []
    if routes and estimated:
        geo = _Geo(net, routes, p)
        pts = {x.id: geo.metres(Point(x.coordinates)) for x in estimated}
        near_poles = {x.id: geo.poles_within(pts[x.id], p.max_service_m) for x in estimated}

        # Every load needs the network within reach; with pole boxes, a pole too.
        reachable: list[LoadIn] = []
        for x in estimated:
            if geo.route_within(pts[x.id], p.max_service_m) is None and not near_poles[x.id]:
                far.append(x)
            else:
                reachable.append(x)
        three = [x for x in reachable if x.kva > p.single_phase_max_kva]
        single = [x for x in reachable if x.kva <= p.single_phase_max_kva]

        for x in three:
            if p.attach == "pole_boxes":
                near = near_poles[x.id]
                a = _at_pole(x, geo.poles[near[0][1]], geo) if near else None
            else:
                a = _on_route(x, pts[x.id], geo, p.max_service_m)
            if a is None:
                no_pole.append(x)
            else:
                a.phase = "RWB"
                allocations.append(a)

        if p.attach == "pole_boxes":
            capacity = p.box_loads * p.boxes_per_pole
            pairs = sorted((d, i, x.id) for x in single for d, i in near_poles[x.id])
            by_id = {x.id: x for x in single}
            on_pole: dict[int, list[LoadIn]] = {}
            placed: set[str] = set()
            for _, i, lid in pairs:
                if lid not in placed and len(on_pole.get(i, [])) < capacity:
                    on_pole.setdefault(i, []).append(by_id[lid])
                    placed.add(lid)
            for x in single:
                if x.id not in placed:
                    (full if near_poles[x.id] else no_pole).append(x)
            for i in sorted(on_pole):
                pole = geo.poles[i]
                for k, group in enumerate(_boxes(pole, on_pole[i], p.box_loads), start=1):
                    box_id = f"{pole.label or pole.id}-{k}"
                    loads = [_at_pole(x, pole, geo) for x in group]
                    for a in loads:
                        a.box = box_id
                    allocations += loads
                    box = Box(id=box_id, pole=pole.id, feeder=loads[0].feeder, loads=len(loads), kva=round(sum(a.kva for a in loads), 2))
                    units.append(_Unit(box_id, pole.id, loads[0].feeder or "", 0.0, loads, box))
        else:
            for x in single:
                a = _on_route(x, pts[x.id], geo, p.max_service_m)
                if a is None:
                    far.append(x)
                    continue
                allocations.append(a)
                units.append(_Unit(x.id, None, a.feeder or "", 0.0, [a]))
    else:
        far = estimated

    feeders = _phase(allocations, units, net, p)

    issues: list[Issue] = []
    if not routes:
        issues.append(Issue(severity="error", code="no_network", message="There is no LV network to connect loads to. Build it first."))
    if far and routes:
        issues.append(_issue("error", "unallocated", f"Buildings more than {p.max_service_m:g} m from the LV network have no "
                             "service connection. Mark a route closer to them.", far))
    if no_pole:
        issues.append(_issue("error", "no_pole", f"Buildings have no LV pole within {p.max_service_m:g} m to take their service. "
                             "Mark a pole near them.", no_pole))
    if full:
        issues.append(_issue("error", "poles_full", f"Poles within reach of these buildings are full ({p.boxes_per_pole} boxes "
                             f"of {p.box_loads} loads each). Mark another pole near them.", full))
    off = [x for x in allocations if x.feeder is None]
    if off:
        issues.append(issue("warning", "loads_unfed",
                            "Loads connect to routes that are not on a feeder (nothing feeds them, or they are in a loop "
                            "or between two sources). They get no phase until that is fixed.",
                            [_name(x) for x in off], [x.at for x in off]))
    if unestimated:
        issues.append(_issue("warning", "no_load", "Buildings have no load estimate yet, so they are not connected. "
                             "Estimate their loads in the field.", unestimated))

    unallocated = len(far) + len(no_pole) + len(full)
    boxes = [u.box for u in units if u.box is not None]
    return LoadAllocation(
        rules_ref=rules.ref, rules_hash=rules.hash, clause=p.clause, attach=p.attach, allocations=allocations, boxes=boxes,
        feeders=feeders, issues=issues,
        summary=LoadSummary(
            loads=len(req.loads), allocated=len(allocations), unallocated=unallocated, unestimated=len(unestimated),
            on_feeders=sum(1 for x in allocations if x.feeder), three_phase=sum(1 for x in allocations if x.phase == "RWB"),
            boxes=len(boxes), allocated_kva=round(sum(x.kva for x in allocations), 2),
            longest_service_m=round(max((x.service_m for x in allocations), default=0.0), 1),
        ),
    )


def _service_m(a: tuple[float, float], b: tuple[float, float]) -> float:
    return round(GEOD.inv(a[0], a[1], b[0], b[1])[2], 1)


def _bearing(a: tuple[float, float], b: tuple[float, float]) -> float:
    """Degrees clockwise from north, from a to b (lon/lat, short distances)."""
    dx = (b[0] - a[0]) * math.cos(math.radians(a[1]))
    return math.degrees(math.atan2(dx, b[1] - a[1])) % 360


def _boxes(pole: Node, loads: list[LoadIn], size: int) -> list[list[LoadIn]]:
    """A pole's loads in boxes of up to `size`, in order around the pole starting after the widest gap, so neighbours share."""
    ordered = sorted(loads, key=lambda x: (_bearing(pole.coordinates, x.coordinates), x.id))
    if len(ordered) > size:
        angles = [_bearing(pole.coordinates, x.coordinates) for x in ordered]
        gaps = [((angles[(i + 1) % len(angles)] - angles[i]) % 360, i) for i in range(len(angles))]
        start = (max(gaps)[1] + 1) % len(ordered)
        ordered = ordered[start:] + ordered[:start]
    return [ordered[i:i + size] for i in range(0, len(ordered), size)]


def _at_pole(load: LoadIn, pole: Node, geo: _Geo) -> Allocation:
    # Place the connection at the pole's end of the branch that feeds it, or at the start of one leaving it.
    into = geo.into.get(pole.id)
    branch, offset = (into, into.length_m) if into else (geo.out_of[pole.id], 0.0)
    return Allocation(load_id=load.id, building_id=load.building_id, label=load.label, kind=load.kind, kva=load.kva or 0.0,
                      branch=branch.id, node=pole.id, offset_m=round(offset, 2), at=pole.coordinates,
                      service_m=_service_m(load.coordinates, pole.coordinates), feeder=branch.feeder, location=load.coordinates)


def _on_route(load: LoadIn, pt: Point, geo: _Geo, reach: float) -> Allocation | None:
    k = geo.route_within(pt, reach)
    if k is None:
        return None
    line, branch = geo.lines[k], geo.routes[k]
    s = line.project(pt)
    offset = s / line.length * branch.length_m if line.length else 0.0
    # The connection point in lon/lat, along the stored geometry in the same proportion.
    at = LineString(branch.coordinates).interpolate(s / line.length if line.length else 0.0, normalized=True)
    at_ll = (round(at.x, 7), round(at.y, 7))
    node = branch.from_node if offset < EPS_M else branch.to_node if branch.length_m - offset < EPS_M else None
    return Allocation(load_id=load.id, building_id=load.building_id, label=load.label, kind=load.kind, kva=load.kva or 0.0,
                      branch=branch.id, node=node, offset_m=round(offset, 2), at=at_ll,
                      service_m=_service_m(load.coordinates, at_ll), feeder=branch.feeder, location=load.coordinates)


def _phase(allocations: list[Allocation], units: list[_Unit], net: LvNetwork, p: Params) -> list[FeederPhases]:
    nodes = {n.id: n for n in net.nodes}
    upstream = {b.id: b.from_node for b in net.branches}
    for a in allocations:
        if a.feeder is not None:
            a.distance_m = round((nodes[upstream[a.branch]].distance_m or 0.0) + a.offset_m, 2)
    for u in units:
        u.distance_m = u.loads[0].distance_m or 0.0
        if u.box is not None:
            u.box.distance_m = u.loads[0].distance_m

    feeders = sorted({a.feeder for a in allocations if a.feeder})
    out: list[FeederPhases] = []
    for feeder in feeders:
        phases = {ph: PhaseLoad() for ph in PHASES}
        three = [a for a in allocations if a.feeder == feeder and a.phase == "RWB"]
        for a in three:
            for ph in PHASES:
                phases[ph].customers += 1
                phases[ph].kva += a.kva / 3
        used: dict[str, set[str]] = {}
        # Farthest first, so that the loads beyond every point along the feeder stay balanced.
        for u in sorted((u for u in units if u.feeder == feeder), key=lambda u: (-u.distance_m, u.id)):
            taken = used.get(u.pole or "", set()) if p.second_box_other_phase and u.pole else set()
            choices = [ph for ph in PHASES if ph not in taken] or list(PHASES)
            ph = min(choices, key=lambda x: (round(phases[x].kva, 6), phases[x].customers, PHASES.index(x)))
            for a in u.loads:
                a.phase = ph  # type: ignore[assignment]
            if u.box is not None:
                u.box.phase = ph  # type: ignore[assignment]
                phases[ph].boxes += 1
            if u.pole:
                used.setdefault(u.pole, set()).add(ph)
            phases[ph].customers += len(u.loads)
            phases[ph].kva += u.kva
        mean = sum(x.kva for x in phases.values()) / 3
        unbalance = max(abs(x.kva - mean) for x in phases.values()) / mean * 100 if mean else 0.0
        for x in phases.values():
            x.kva = round(x.kva, 2)
        out.append(FeederPhases(feeder=feeder, phases=phases, three_phase=len(three), unbalance_pct=round(unbalance, 1)))
    return out
