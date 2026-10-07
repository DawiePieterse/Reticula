"""Optimisation and comparison (plans 5.2, 5.3, 5.4, 5.6): three options from one set of field inputs.

**Objectives (5.2).** Lowest capital cost; lowest lifetime cost (capital plus the present value of losses); most spare
transformer capacity with the capital cost at most `capex_ceiling_pct` above the cheapest option. A design that is less
sound (loads or transformers left unsupplied, a bulk study that stopped: `run.unsound`) is never preferred to a sounder one,
and one with more failed checks never to one with fewer, whatever its cost. Checks that only the field can clear
(elements not yet inspected) are left out of that count, since every move below proposes something the field must confirm.

**Local search (5.3).** From the best starting point, every neighbour is designed in full by `design.run` and so
re-checked against every rule; the best improving neighbour is taken until none improves or the evaluation budget is
spent. The neighbours of a design are:

- the other construction (overhead or underground), where the rules file allows both;
- each transformer one standard rating up, or back down to its sized rating;
- each LV feeder one conductor size up, or back down; the MV conductor one size up, or back down;
- each transformer moved to one of the nearest LV network nodes within `move_radius_m`.

Phasing is balanced by the load allocation in every evaluation. Routes are not invented: re-routing is limited to the
routes marked or proposed, so a different route is the field's or the placement's call (plan 2.0).

**Siting (5.3, MILP).** Where a proven optimum is wanted, transformer sites are chosen by capacitated facility location
over the LV network nodes, solved exactly with HiGHS (scipy.optimize.milp): binary y[s, r] opens site s at rating r and
x[l, s] serves load l from s, minimising Σ y·C_r + Σ y·C_mv(s) + Σ x·kVA_l·d(l, s)·c_lv / S_feeder, subject to each load
served once, Σ kVA·x ≤ Σ r·(1 − growth)·y at each site, at most one rating per site, at most one site per connected LV
network (the network build has no open points, so two sources on one network are tied), only sites within the MV tap
reach of a marked MV route, and d(l, s) ≤ the LV reach. d is
the service distance plus the network distance; c_lv is the default LV conductor's rate per metre and S_feeder its
thermal capacity, so a load pays for the share of a feeder it uses (the load-moment cost). This is a linear model of
the design: it uses the ADMD sum, not the Herman-Beta demand, so its sites are a starting point that the full run
then sizes and checks like any other.

**Compare (5.4).** The options side by side; two options are too close to call when their capital cost ranges (rates
less and plus their uncertainty) overlap.
"""

from __future__ import annotations

import itertools
import math
from typing import Literal

import networkx as nx
import numpy as np
from pydantic import BaseModel
from shapely.geometry import LineString, Point

from .. import cost as cost_mod
from ..issues import Issue
from ..lv.network import BuildRequest, CandidateIn, build_network
from ..mv import network as mv_mod
from ..rules import RuleSet
from ..trace import Traced, traced
from . import sizing as sizing_mod
from .geometry import Frame
from .run import LV_KINDS, Design, DesignCandidate, DesignOptions, DesignRequest, inputs_hash, run, unsound

Objective = Literal["capex", "lifetime", "spare"]
Construction = Literal["overhead", "underground"]
SITING_ID = "opt.siting.milp.v2"
SITING_FORMULA = ("min Σ y[s,r]·(C_r + C_mv(s)) + Σ x[l,s]·kVA_l·d(l,s)·c_lv/S_feeder; Σ_s x[l,s] = 1; "
                  "Σ_l kVA_l·x[l,s] ≤ Σ_r r·(1 − growth)·y[s,r]; Σ_r y[s,r] ≤ 1; Σ_{s∈network} Σ_r y[s,r] ≤ 1; "
                  "x[l,s] = 0 where d > reach")


class OptimiseOptions(BaseModel):
    objectives: list[Objective] = ["capex", "lifetime", "spare"]
    max_evaluations: int = 40
    """Full design runs per objective, shared through a cache."""
    capex_ceiling_pct: float = 15.0
    move_radius_m: float = 80.0
    moves_per_transformer: int = 3
    siting: bool = True
    """Add the MILP siting as a starting point."""
    constructions: list[Construction] | None = None
    """None: every construction the rules file allows."""


class OptimiseRequest(BaseModel):
    design: DesignRequest
    options: OptimiseOptions = OptimiseOptions()


class Move(BaseModel):
    kind: Literal["construction", "transformer_rating", "lv_conductor", "mv_conductor", "move_transformer", "siting"]
    target: str
    detail: str


class OptionResult(BaseModel):
    objective: Objective
    value: float
    design: Design
    request: DesignRequest
    """The design request that gives this option: run it again to reproduce it, or adopt it as the project's design."""
    moves: list[Move]
    """From the starting point to this option."""
    start: str


class CompareRow(BaseModel):
    objective: Objective
    construction: Construction
    capex: float
    capex_low: float
    capex_high: float
    lifetime: float
    lifetime_low: float
    lifetime_high: float
    spare_pct: float
    transformers: int
    transformer_kva: float
    worst_lv_drop_pct: float | None
    worst_mv_drop_pct: float | None
    failures: int
    moves: int
    too_close: list[Objective]
    """Options whose capital cost range overlaps this one's."""
    same_as: list[Objective]
    """Options that ended on the same design."""


class SiteOut(BaseModel):
    node: str
    coordinates: tuple[float, float]
    rating_kva: float
    loads: int
    kva: float
    marked: str | None
    """The marked candidate already at this node."""


class Siting(BaseModel):
    status: str
    optimal: bool
    sites: list[SiteOut]
    cost: float | None
    candidates: int
    loads: int
    unreachable: list[str]
    trace: Traced | None = None


class OptimiseResult(BaseModel):
    rules_ref: str
    rules_hash: str
    inputs_hash: str
    options: list[OptionResult]
    comparison: list[CompareRow]
    siting: Siting | None
    evaluations: int
    issues: list[Issue]


# ------------------------------------------------------------------ the siting MILP


class SitingProblem(BaseModel):
    """A capacitated facility location problem in plain numbers, so it can be solved and benchmarked on its own."""

    sites: list[str]
    loads: list[str]
    kva: list[float]
    distance: list[list[float]]
    """distance[l][s], metres; math.inf where the load cannot be served from the site."""
    ratings: list[float]
    rating_cost: list[float]
    site_cost: list[float]
    """Fixed cost of opening each site (its MV connection)."""
    growth: float
    assign_rate: float
    """Cost per kVA·m."""
    groups: list[int] = []
    """The connected network each site is on; at most one site opens per network. Empty: no such limit."""


class SitingSolution(BaseModel):
    status: str
    optimal: bool
    cost: float | None
    open: dict[str, float]
    """Site → rating."""
    assign: dict[str, str]
    """Load → site."""


def solve_siting(p: SitingProblem, time_limit_s: float = 30.0) -> SitingSolution:
    from scipy.optimize import Bounds, LinearConstraint, milp

    n_l, n_s, n_r = len(p.loads), len(p.sites), len(p.ratings)
    if n_l == 0 or n_s == 0:
        return SitingSolution(status="empty", optimal=True, cost=0.0, open={}, assign={})
    pairs = [(li, si) for li in range(n_l) for si in range(n_s) if math.isfinite(p.distance[li][si])]
    nx_ = len(pairs)
    ny = n_s * n_r
    n = nx_ + ny
    c = np.zeros(n)
    for k, (li, si) in enumerate(pairs):
        c[k] = p.kva[li] * p.distance[li][si] * p.assign_rate
    for si in range(n_s):
        for ri in range(n_r):
            c[nx_ + si * n_r + ri] = p.rating_cost[ri] + p.site_cost[si]
    rows, lo, hi = [], [], []
    for li in range(n_l):  # each load served once
        r = np.zeros(n)
        for k, (lj, _) in enumerate(pairs):
            if lj == li:
                r[k] = 1
        if not r.any():
            continue
        rows.append(r)
        lo.append(1)
        hi.append(1)
    for si in range(n_s):  # capacity, and one rating per site
        r = np.zeros(n)
        for k, (li, sj) in enumerate(pairs):
            if sj == si:
                r[k] = p.kva[li]
        for ri in range(n_r):
            r[nx_ + si * n_r + ri] = -p.ratings[ri] * (1 - p.growth)
        rows.append(r)
        lo.append(-np.inf)
        hi.append(0)
        one = np.zeros(n)
        one[nx_ + si * n_r: nx_ + (si + 1) * n_r] = 1
        rows.append(one)
        lo.append(0)
        hi.append(1)
    for grp in sorted(set(p.groups)):  # one source per connected network
        r = np.zeros(n)
        for si in range(n_s):
            if p.groups[si] == grp:
                r[nx_ + si * n_r: nx_ + (si + 1) * n_r] = 1
        rows.append(r)
        lo.append(0)
        hi.append(1)
    res = milp(c, constraints=LinearConstraint(np.array(rows), lo, hi), integrality=np.ones(n), bounds=Bounds(0, 1),
               options={"time_limit": time_limit_s, "disp": False})
    if res.x is None:
        return SitingSolution(status=res.message, optimal=False, cost=None, open={}, assign={})
    x = np.round(res.x).astype(int)
    opened = {p.sites[si]: p.ratings[ri] for si in range(n_s) for ri in range(n_r) if x[nx_ + si * n_r + ri]}
    assign = {p.loads[li]: p.sites[si] for k, (li, si) in enumerate(pairs) if x[k]}
    return SitingSolution(status=res.message, optimal=res.status == 0, cost=round(float(res.fun), 2), open=opened, assign=assign)


def _siting(req: DesignRequest, rules: RuleSet, construction: Construction, lib: cost_mod.RateLibrary) -> tuple[Siting, list[DesignCandidate]]:
    """Transformer sites for the marked LV routes, as candidates replacing the marked transformer sites."""
    lv = [CandidateIn(id=c.id, kind=c.kind, geometry=c.geometry, label=c.label) for c in req.candidates if c.kind in LV_KINDS]
    net = build_network(BuildRequest(rules=rules.ref, candidates=lv), rules)
    frame = Frame([n.coordinates for n in net.nodes] or [(27.0, -26.0)])
    g = nx.Graph()
    for b in net.branches:
        g.add_edge(b.from_node, b.to_node, weight=b.length_m)
    nodes = {n.id: n for n in net.nodes}
    site_ids = [n.id for n in net.nodes if n.kind in ("end", "junction", "joint", "pole", "source")]
    xy = {nid: frame.xy(nodes[nid].coordinates) for nid in nodes}
    route_nodes = [nid for nid in site_ids if g.has_node(nid)]
    loads = [x for x in req.loads if x.kva]
    sec = rules.section("transformers", "eskom/0.8.0")
    series = sec["minisub_ratings_kva"] if construction == "underground" and sec["underground_minisub"] else sec["pole_mount_ratings_kva"]
    ratings = sorted(float(r) for r in series)
    code = "A-MINISUB-{:g}" if construction == "underground" and sec["underground_minisub"] else "A-TX-POLE-{:g}"
    md = rules.section("mv_design", "eskom/0.7.0")
    rating_cost = [cost_mod.assembly_rate(lib, code.format(r)) or float(md["costs"]["transformer_zar"].get(f"{r:g}", 0)) for r in ratings]
    default_lv = rules.conductor(sizing_mod.default_conductor(rules, construction))
    lv_rate = cost_mod.assembly_rate(lib, f"A-COND-{default_lv.code}") or float(md["costs"]["lv_zar_per_m"])
    s_feeder = math.sqrt(3) * float(rules.data["voltage"]["lv_nominal_v"]) * default_lv.rating_a / 1000
    reach = float(md["lv_reach_m"])
    mv_lines = [frame.project(LineString(c.geometry["coordinates"])) for c in req.candidates if c.kind == "mv_route"]
    mv_code = mv_mod.default_conductor(rules, req.options.mv_construction)
    mv_rate = cost_mod.assembly_rate(lib, f"A-COND-{mv_code}") or float(md["costs"]["mv_zar_per_m"])
    tee = cost_mod.assembly_rate(lib, "A-MV-TEE") or 0.0

    tap_reach = mv_mod.params(rules).tap_reach_m

    def mv_gap(nid: str) -> float:
        return min(line.distance(Point(xy[nid])) for line in mv_lines) if mv_lines else 0.0

    # A site the MV network cannot tap is not supplied, so it is no site at all.
    sites = [nid for nid in route_nodes if mv_gap(nid) <= tap_reach]
    network = {nid: i for i, comp in enumerate(nx.connected_components(g)) for nid in comp}
    lengths = {s: nx.single_source_dijkstra_path_length(g, s, cutoff=reach, weight="weight") for s in sites}
    dist: list[list[float]] = []
    unreachable: list[str] = []
    for x in loads:
        pt = frame.xy(x.coordinates)
        near = min(route_nodes, key=lambda nid: math.dist(pt, xy[nid]))
        service = math.dist(pt, xy[near])
        row = [service + lengths[s][near] if near in lengths[s] else math.inf for s in sites]
        if not any(math.isfinite(d) for d in row):
            unreachable.append(x.id)
        dist.append(row)
    keep = [i for i, x in enumerate(loads) if x.id not in unreachable]
    growth = float(sec["growth_pct"]) / 100
    problem = SitingProblem(sites=sites, loads=[loads[i].id for i in keep], kva=[float(loads[i].kva) for i in keep],
                            distance=[dist[i] for i in keep], ratings=ratings, rating_cost=rating_cost,
                            site_cost=[tee + mv_gap(s) * mv_rate for s in sites], growth=growth,
                            assign_rate=lv_rate / s_feeder, groups=[network[s] for s in sites])
    # One source per connected network: a network that carries more than the largest transformer needs an open point first.
    on_network: dict[int, float] = {}
    for li in keep:
        s = next(si for si, d in enumerate(dist[li]) if math.isfinite(d))
        on_network[problem.groups[s]] = on_network.get(problem.groups[s], 0.0) + float(loads[li].kva)
    over = sorted(k for k, kva in on_network.items() if kva > ratings[-1] * (1 - growth) + 1e-9)
    if over:
        sol = SitingSolution(status=f"{len(over)} connected LV network{'s' if len(over) != 1 else ''} carry more load than the largest "
                             f"transformer ({ratings[-1]:g} kVA); mark an open point to split {'them' if len(over) != 1 else 'it'}",
                             optimal=False, cost=None, open={}, assign={})
    else:
        sol = solve_siting(problem)
    marked = {n.id: n.candidate_id for n in net.nodes if n.kind == "source"}
    served: dict[str, list[str]] = {}
    for lid, s in sol.assign.items():
        served.setdefault(s, []).append(lid)
    kva = {x.id: float(x.kva) for x in loads}
    opened = [SiteOut(node=s, coordinates=nodes[s].coordinates, rating_kva=r, loads=len(served.get(s, [])),
                      kva=round(sum(kva[i] for i in served.get(s, [])), 2), marked=marked.get(s)) for s, r in sol.open.items()]
    trace = traced(sol.cost if sol.cost is not None else 0.0, lib.currency, formula_id=SITING_ID, formula=SITING_FORMULA,
                   clause=md.get("clause", ""), rules_hash=rules.hash, inputs={
                       "sites": (len(sites), "", f"LV network nodes within {tap_reach:g} m of an MV route"),
                       "networks": (len(set(problem.groups)), "", "connected LV networks, one source each"),
                       "loads": (len(keep), "", "with an ADMD, within reach"),
                       "ratings": (", ".join(f"{r:g}" for r in ratings), "kVA", f"rules {rules.ref} transformers ({construction})"),
                       "c_lv": (round(lv_rate, 2), f"{lib.currency}/m", f"{default_lv.code}, rate library {lib.name}"),
                       "S_feeder": (round(s_feeder, 1), "kVA", f"√3·V·I_rating of {default_lv.code}"),
                       "reach": (reach, "m", f"rules {rules.ref} mv_design.lv_reach_m (placeholder)"),
                       "growth": (problem.growth * 100, "%", f"rules {rules.ref} transformers"), "solver": (sol.status, "", "HiGHS")})
    siting = Siting(status=sol.status, optimal=sol.optimal, sites=opened, cost=sol.cost, candidates=len(sites), loads=len(keep),
                    unreachable=unreachable, trace=trace)
    kind = "minisub" if construction == "underground" and sec["underground_minisub"] else "transformer"
    keep_cands = [c for c in req.candidates if c.kind not in ("transformer", "minisub")]
    by_id = {c.id: c for c in req.candidates}
    new: list[DesignCandidate] = []
    for k, s in enumerate(opened, start=1):
        if s.marked and s.marked in by_id:
            new.append(by_id[s.marked])
        else:
            new.append(DesignCandidate(id=f"opt-tx-{k}", kind=kind, source="proposed",
                                       geometry={"type": "Point", "coordinates": list(s.coordinates)}))
    return siting, keep_cands + new


# ------------------------------------------------------------------ local search


class _Variant(BaseModel, frozen=True):
    construction: Construction
    ratings: tuple[tuple[str, float], ...] = ()
    lv: tuple[tuple[str, str], ...] = ()
    mv: str | None = None
    positions: tuple[tuple[str, tuple[float, float]], ...] = ()
    candidates: str = "marked"
    """'marked' or 'siting': which set of transformer sites the variant starts from."""


class _Search:
    def __init__(self, req: OptimiseRequest, rules: RuleSet):
        self.req, self.rules = req, rules
        self.base = req.design
        self.cache: dict[_Variant, Design] = {}
        self.requests: dict[_Variant, DesignRequest] = {}
        self.sited: dict[Construction, list[DesignCandidate]] = {}
        self.evaluations = 0

    def candidates(self, v: _Variant) -> list[DesignCandidate]:
        cands = self.sited[v.construction] if v.candidates == "siting" else self.base.candidates
        moved = dict(v.positions)
        out = []
        for c in cands:
            if c.id in moved:
                out.append(c.model_copy(update={"geometry": {"type": "Point", "coordinates": list(moved[c.id])}, "source": "proposed"}))
            else:
                out.append(c)
        return out

    def design(self, v: _Variant) -> Design:
        if v not in self.cache:
            o = self.base.options
            opts = DesignOptions(construction=v.construction, mv_construction=o.mv_construction, objective=o.objective,
                                 lv_conductors={**o.lv_conductors, **dict(v.lv)}, mv_conductor=v.mv or o.mv_conductor,
                                 transformer_ratings={**o.transformer_ratings, **dict(v.ratings)}, economics=o.economics,
                                 underground_conditions=o.underground_conditions)
            self.requests[v] = self.base.model_copy(update={"candidates": self.candidates(v), "options": opts})
            self.cache[v] = run(self.requests[v], self.rules)
            self.evaluations += 1
        return self.cache[v]

    def neighbours(self, v: _Variant, d: Design, allowed: list[Construction]) -> list[tuple[_Variant, Move]]:
        out: list[tuple[_Variant, Move]] = []
        for c in allowed:
            if c != v.construction:
                out.append((v.model_copy(update={"construction": c, "ratings": (), "lv": (), "mv": None}),
                            Move(kind="construction", target="all", detail=f"{v.construction} to {c}")))
        sec = self.rules.data["transformers"]
        ratings = dict(v.ratings)
        lib = [c.code for c in sizing_mod.library(self.rules, v.construction)]
        for t in d.transformers.transformers:
            series = sorted(float(r) for r in (sec["minisub_ratings_kva"] if t.mounting == "minisub" else sec["pole_mount_ratings_kva"]))
            i = series.index(t.rating_kva) if t.rating_kva in series else -1
            name = t.label or t.id
            if 0 <= i < len(series) - 1:
                up = tuple(sorted({**ratings, name: series[i + 1]}.items()))
                out.append((v.model_copy(update={"ratings": up}),
                            Move(kind="transformer_rating", target=name, detail=f"{t.rating_kva:g} to {series[i + 1]:g} kVA")))
                # A larger transformer raises the fault level; its feeders may need the next size to withstand it.
                bumped = {f: lib[lib.index(c) + 1] for f, c in d.lv.sizing.feeders.items()
                          if f.startswith(f"{name}-") and c in lib and lib.index(c) < len(lib) - 1}
                if bumped:
                    out.append((v.model_copy(update={"ratings": up, "lv": tuple(sorted({**dict(v.lv), **bumped}.items()))}),
                                Move(kind="transformer_rating", target=name,
                                     detail=f"{t.rating_kva:g} to {series[i + 1]:g} kVA with its feeders one size up")))
            if name in ratings:
                rest = {k: r for k, r in ratings.items() if k != name}
                out.append((v.model_copy(update={"ratings": tuple(sorted(rest.items()))}),
                            Move(kind="transformer_rating", target=name, detail=f"{t.rating_kva:g} kVA back to sized")))
        lv = dict(v.lv)
        for f, code in sorted(d.lv.sizing.feeders.items()):
            if f.startswith("link:") or code not in lib:
                continue
            i = lib.index(code)
            if i < len(lib) - 1:
                out.append((v.model_copy(update={"lv": tuple(sorted({**lv, f: lib[i + 1]}.items()))}),
                            Move(kind="lv_conductor", target=f, detail=f"{code} to {lib[i + 1]}")))
            if f in lv:
                rest = {k: x for k, x in lv.items() if k != f}
                out.append((v.model_copy(update={"lv": tuple(sorted(rest.items()))}), Move(kind="lv_conductor", target=f, detail=f"{code} back to sized")))
        if d.mv is not None:
            mv_lib = [c.code for c in mv_mod.library(self.rules, self.base.options.mv_construction)]
            used = max((b.conductor for b in d.mv.branches), key=lambda c: mv_lib.index(c) if c in mv_lib else -1, default=None)
            if used in mv_lib and mv_lib.index(used) < len(mv_lib) - 1:
                nxt = mv_lib[mv_lib.index(used) + 1]
                out.append((v.model_copy(update={"mv": nxt}), Move(kind="mv_conductor", target="MV", detail=f"{used} to {nxt}")))
            if v.mv is not None:
                out.append((v.model_copy(update={"mv": None}), Move(kind="mv_conductor", target="MV", detail=f"{v.mv} back to sized")))
        out += self._moves(v, d)
        return out

    def _moves(self, v: _Variant, d: Design) -> list[tuple[_Variant, Move]]:
        o = self.req.options
        net = d.lv.network
        frame = Frame([n.coordinates for n in net.nodes] or [(27.0, -26.0)])
        spots = [n for n in net.nodes if n.kind in ("end", "junction", "joint", "pole")]
        pos = dict(v.positions)
        out: list[tuple[_Variant, Move]] = []
        for t in d.transformers.transformers:
            if t.candidate_id is None:
                continue
            here = frame.xy(t.coordinates)
            near = sorted((math.dist(here, frame.xy(n.coordinates)), n) for n in spots if 1.0 < math.dist(here, frame.xy(n.coordinates)) <= o.move_radius_m)
            for dist, n in near[: o.moves_per_transformer]:
                out.append((v.model_copy(update={"positions": tuple(sorted({**pos, t.candidate_id: tuple(n.coordinates)}.items()))}),
                            Move(kind="move_transformer", target=t.label or t.candidate_id, detail=f"{dist:.0f} m to node {n.id}")))
        return out


def _failures(d: Design) -> int:
    return sum(1 for c in d.checks if not c.passes and c.category != "not_inspected")


def _value(d: Design, objective: Objective) -> float:
    return {"capex": d.cost.capex, "lifetime": d.cost.lifetime, "spare": -d.summary.spare_pct}[objective]


def optimise(req: OptimiseRequest, rules: RuleSet) -> OptimiseResult:
    o = req.options
    allowed: list[Construction] = o.constructions or list(rules.section("construction", "eskom/0.8.0")["allowed"])
    search = _Search(req, rules)
    lib = req.design.rates or cost_mod.default_library()
    issues: list[Issue] = []
    starts: list[tuple[_Variant, str, list[Move]]] = [(_Variant(construction=c), f"marked sites, {c}", []) for c in allowed]
    siting: Siting | None = None
    if o.siting and req.design.loads and any(c.kind == "lv_route" for c in req.design.candidates):
        for c in allowed:
            s, cands = _siting(req.design, rules, c, lib)
            if not s.sites:
                issues.append(Issue(severity="warning", code="siting_failed", message=f"The siting model found no solution ({s.status})."))
                continue
            search.sited[c] = cands
            starts.append((_Variant(construction=c, candidates="siting"), f"MILP siting, {c}",
                           [Move(kind="siting", target="transformers", detail=f"{len(s.sites)} sites, {'optimal' if s.optimal else s.status}")]))
            if siting is None or c == (req.design.options.construction or allowed[0]):
                siting = s
            if s.unreachable:
                issues.append(Issue(severity="warning", code="siting_unreachable", message=f"{len(s.unreachable)} loads are beyond the LV "
                                    "reach of every site the MV network can tap and were left out of the siting model."))

    ceiling: float | None = None
    results: list[OptionResult] = []
    order = sorted(o.objectives, key=lambda x: {"capex": 0, "lifetime": 1, "spare": 2}[x])
    for objective in order:
        if objective == "spare" and ceiling is None:
            ceiling = min(search.design(v).cost.capex for v, _, _ in starts) * (1 + o.capex_ceiling_pct / 100)

        def key(d: Design, objective=objective, ceiling=ceiling) -> tuple:
            over = objective == "spare" and ceiling is not None and d.cost.capex > ceiling + 1e-6
            return (unsound(d), _failures(d), over, _value(d, objective))

        best_v, best_label, best_moves = min(starts, key=lambda s: key(search.design(s[0])))
        budget = search.evaluations + o.max_evaluations
        improved = True
        while improved and search.evaluations < budget:
            improved = False
            current = search.design(best_v)
            pick = None
            for nv, move in search.neighbours(best_v, current, allowed):
                if search.evaluations >= budget and nv not in search.cache:
                    break
                k = key(search.design(nv))
                if k < key(current) and (pick is None or k < pick[0]):
                    pick = (k, nv, move)
            if pick:
                best_v, best_moves, improved = pick[1], [*best_moves, pick[2]], True
        d = search.design(best_v)
        if objective == "capex":
            ceiling = d.cost.capex * (1 + o.capex_ceiling_pct / 100)
        results.append(OptionResult(objective=objective, value=_value(d, objective), design=d, request=search.requests[best_v],
                                    moves=best_moves, start=best_label))

    results.sort(key=lambda r: o.objectives.index(r.objective))
    return OptimiseResult(rules_ref=rules.ref, rules_hash=rules.hash, inputs_hash=inputs_hash(req.design), options=results,
                          comparison=compare(results), siting=siting, evaluations=search.evaluations, issues=issues)


def compare(results: list[OptionResult]) -> list[CompareRow]:
    rows = []
    for r in results:
        d = r.design
        close = [x.objective for x in results if x is not r and not (x.design.cost.capex_low > d.cost.capex_high
                                                                      or x.design.cost.capex_high < d.cost.capex_low)]
        same = [x.objective for x in results if x is not r and x.design is d]
        rows.append(CompareRow(
            objective=r.objective, construction=d.construction, capex=d.cost.capex, capex_low=d.cost.capex_low, capex_high=d.cost.capex_high,
            lifetime=d.cost.lifetime, lifetime_low=d.cost.lifetime_low, lifetime_high=d.cost.lifetime_high, spare_pct=d.summary.spare_pct,
            transformers=d.summary.transformers, transformer_kva=d.summary.transformer_kva, worst_lv_drop_pct=d.summary.worst_lv_drop_pct,
            worst_mv_drop_pct=d.summary.worst_mv_drop_pct, failures=_failures(d), moves=len(r.moves), too_close=close, same_as=same))
    return rows


def brute_force_siting(p: SitingProblem) -> float:
    """The optimum by enumeration, for benchmarking `solve_siting` on small problems (plan 5.6)."""
    best = math.inf
    options = [None, *range(len(p.ratings))]
    for opened in itertools.product(options, repeat=len(p.sites)):
        if p.groups and any(sum(1 for s, r in enumerate(opened) if r is not None and p.groups[s] == grp) > 1 for grp in set(p.groups)):
            continue
        fixed = sum(p.rating_cost[r] + p.site_cost[s] for s, r in enumerate(opened) if r is not None)
        if fixed >= best:
            continue
        cap = [p.ratings[r] * (1 - p.growth) if r is not None else -1.0 for r in opened]
        choices = [[s for s in range(len(p.sites)) if opened[s] is not None and math.isfinite(p.distance[li][s])] for li in range(len(p.loads))]
        if any(not ch for ch in choices):
            continue
        for assign in itertools.product(*choices):
            used = [0.0] * len(p.sites)
            for li, s in enumerate(assign):
                used[s] += p.kva[li]
            if any(used[s] > cap[s] + 1e-9 for s in range(len(p.sites)) if opened[s] is not None):
                continue
            total = fixed + sum(p.kva[li] * p.distance[li][s] * p.assign_rate for li, s in enumerate(assign))
            best = min(best, total)
    return best
