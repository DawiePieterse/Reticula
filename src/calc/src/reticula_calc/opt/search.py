"""Option search for one transformer site: lowest capital cost, lowest lifetime cost, most spare capacity (plans 5.2–5.4).

Decision variables of a design: the transformer position (a point on the LV routes), the construction (overhead or
underground), the transformer rating (smallest that carries the demand, or larger), the smallest feeder conductor,
the start of the phase rotation, and conductors upsized branch by branch to cut losses.

1. Constructive start. The marked site and the attachment point nearest the load centre (customers weighted by their
   mean current), in every allowed construction, sized by the LV design's own sizing.
2. Local search, per objective. From the best start, every neighbouring design is designed and checked in full
   (layout, Herman-Beta voltage drop, thermal, fault, protection, overhead mechanics) and costed: the transformer moved
   to one of the nearest attachment points within the move radius ("re-route": the radial tree is rebuilt from the new
   position), the other construction, one rating up or down, the smallest feeder one size up or down, the phase
   rotation started further along. The best neighbour that improves the objective is taken; when none does, pairs of
   changes (a move with a new phase start or smallest feeder) are tried before the search stops. It also stops when
   the evaluation budget is spent. A design that fails any check never wins.
3. Lifetime refinement. On the lowest-lifetime design, the feeder branches with the largest losses are tried one size
   up (upstream branches follow, so feeders still taper); a change is kept when the lifetime cost falls and all checks
   still pass.

Objectives: capex = installed cost (indicative rates); lifetime = capex + present value of losses (opt.lifetime);
spare = the smallest of transformer, thermal and voltage-drop headroom, maximised under a capex ceiling (the run's, or
the cheapest feasible design plus the rules' `spare_ceiling_pct`).

Close calls (5.4). Two options whose capex or lifetime cost differ by less than the rate list's uncertainty band are
flagged "too close to call", as is an option whose runner-up for its own objective is that close.
"""

from __future__ import annotations

import math
from typing import Any, Literal

import networkx as nx
from pydantic import BaseModel, Field

from ..geo.routes import Projector
from ..lv.analysis import Check, analyse, customer_loads
from ..lv.build import BuildIssue, BuildRequest, BuildResult, CustomerIn, RouteIn, build_network
from ..lv.costs import RatesRef, estimate, rates_name, rates_of
from ..lv.design import OptionResult, design_option
from ..lv.library import Construction, feeder_options, lv_config
from ..lv.model import LvNetwork
from ..lv.overhead import check_overhead
from ..rules import RuleSet
from .lifetime import LifetimeCost, LifetimeParams, lifetime_cost, opt_config, resolved_params

Objective = Literal["capex", "lifetime", "spare"]
OBJECTIVES: tuple[Objective, ...] = ("capex", "lifetime", "spare")
TITLES = {"capex": "Lowest capital cost", "lifetime": "Lowest lifetime cost", "spare": "Most spare capacity"}
NEIGHBOUR_POSITIONS = 4
PHASE_STEPS = (1, 2, 3)
REFINE_BRANCHES = 6
REFINE_STEPS = 12


class OptimiseRequest(BaseModel):
    rules: str
    source: tuple[float, float] = Field(description="the marked transformer site")
    source_inspected: bool = True
    routes: list[RouteIn]
    customers: list[CustomerIn]
    constructions: list[Construction] = Field(default=["overhead", "underground"], min_length=1)
    objectives: list[Objective] = Field(default=list(OBJECTIVES), min_length=1)
    capex_ceiling: float | None = Field(default=None, gt=0)
    lifetime: LifetimeParams | None = None
    allow_move: bool = True
    move_radius_m: float | None = Field(default=None, ge=0)
    max_evaluations: int | None = Field(default=None, ge=1, le=2000)
    roads: list[list[tuple[float, float]]] = []
    site: dict[str, float] | None = None
    area: dict[str, Any] | None = None
    rates: RatesRef = "indicative/2026-10"
    source_fault_mva_max: float | None = None
    source_fault_mva_min: float | None = None


class Design(BaseModel):
    position_id: int
    position: tuple[float, float]
    position_label: str
    moved_m: float
    construction: Construction
    transformer_kva: float | None = Field(description="None: the smallest rating that carries the demand")
    min_feeder: int = 0
    phase_offset: int = 0
    upsized: list[str] = []


class Spare(BaseModel):
    transformer_pct: float
    thermal_pct: float
    voltage_pct: float
    spare_pct: float


class Evaluation(BaseModel):
    design: Design
    passed: bool
    failed_checks: int
    transformer_kva: float
    capex: float
    lifetime_cost: float
    spare_pct: float


class OptionOut(BaseModel):
    objective: Objective
    title: str
    design: Design
    option: OptionResult
    lifetime: LifetimeCost
    spare: Spare
    trail: list[str]
    notes: list[str]
    runner_up: Evaluation | None
    too_close_to_call: bool


class CloseCall(BaseModel):
    a: Objective
    b: Objective
    measure: Literal["capex", "lifetime cost"]
    difference_pct: float
    band_pct: float


class OptimiseResult(BaseModel):
    rules: str
    rules_hash: str
    issues: list[BuildIssue]
    options: list[OptionOut]
    baseline: Evaluation | None
    evaluations: int
    feasible: int
    positions: int
    capex_ceiling: float | None
    uncertainty_pct: float
    close_calls: list[CloseCall]
    currency: str
    rate_date: str
    assumptions: list[str]
    unverified: list[str]


class _Point(BaseModel):
    id: int
    lonlat: tuple[float, float]
    xy: tuple[float, float]
    label: str
    moved_m: float


class _Run:
    def __init__(self, req: OptimiseRequest, rules: RuleSet):
        self.req, self.rules = req, rules
        cfg = opt_config(rules)
        self.budget = req.max_evaluations or int(cfg.get("max_evaluations", 120))
        self.radius = req.move_radius_m if req.move_radius_m is not None else float(cfg.get("move_radius_m", 150))
        self.params = resolved_params(req.lifetime, req.rates)
        self.source = {"fault_mva_max": req.source_fault_mva_max, "fault_mva_min": req.source_fault_mva_min} if req.source_fault_mva_max else None
        self.sizes = sorted(float(t["kva"]) for t in rules.data["fault"]["transformers"])
        self.rotation = len(lv_config(rules)["phase_rotation"])
        self.builds: dict[tuple, BuildResult | str] = {}
        self.evaluated: dict[tuple, tuple[Evaluation, OptionResult | None, LifetimeCost | None, Spare | None]] = {}
        self.unverified: set[str] = set()
        self.count = 0
        self.points: dict[int, _Point] = {}

    # ---- positions -------------------------------------------------------------------------------------------
    def positions(self) -> tuple[list[_Point], list[BuildIssue]]:
        req = self.req
        base = build_network(BuildRequest(source=req.source, routes=req.routes, customers=req.customers, construction=req.constructions[0],
                                          roads=req.roads), self.rules)
        net = base.network
        pr = Projector(*req.source)
        sx, sy = pr.xy(*req.source)
        pts: list[_Point] = []
        for n in net.nodes:
            if n.kind == "connection":
                continue
            x, y = pr.xy(n.lon, n.lat)
            d = math.hypot(x - sx, y - sy)
            if n.id == "S":
                pts.insert(0, _Point(id=0, lonlat=(n.lon, n.lat), xy=(x, y), label="marked site", moved_m=0.0))
            elif req.allow_move:
                pts.append(_Point(id=0, lonlat=(n.lon, n.lat), xy=(x, y), label=f"attachment point {n.id}", moved_m=round(d, 1)))
        if req.allow_move:
            loads = customer_loads(net, self.rules)
            weights = {c.node_id: sum(m + det for _p, m, _v, _c, det in loads[c.id]) for c in net.customers}
            total = sum(weights.values()) or 1.0
            cx = sum(w * pr.xy(net.node(nid).lon, net.node(nid).lat)[0] for nid, w in weights.items()) / total
            cy = sum(w * pr.xy(net.node(nid).lon, net.node(nid).lat)[1] for nid, w in weights.items()) / total
            centre = min(pts, key=lambda p: math.hypot(p.xy[0] - cx, p.xy[1] - cy))
            if centre.label != "marked site":
                centre.label = "load centre"
            pts = [p for p in pts if p.moved_m <= self.radius + 1e-6 or p is centre]
        for i, p in enumerate(pts):
            p.id = i
        return pts, base.issues

    # ---- evaluation ------------------------------------------------------------------------------------------
    def build(self, p: _Point, construction: Construction, offset: int) -> BuildResult | str:
        key = (p.id, construction, offset)
        if key not in self.builds:
            try:
                self.builds[key] = build_network(BuildRequest(source=p.lonlat, routes=self.req.routes, customers=self.req.customers,
                                                              construction=construction, roads=self.req.roads, phase_offset=offset), self.rules)
            except ValueError as e:
                self.builds[key] = str(e)
        return self.builds[key]

    def key(self, d: Design) -> tuple:
        return (d.position_id, d.construction, d.transformer_kva, d.min_feeder, d.phase_offset, tuple(d.upsized))

    def evaluate(self, d: Design, p: _Point) -> Evaluation | None:
        k = self.key(d)
        if k in self.evaluated:
            return self.evaluated[k][0]
        if self.count >= self.budget:
            return None
        self.count += 1
        built = self.build(p, d.construction, d.phase_offset)
        if isinstance(built, str):
            ev = Evaluation(design=d, passed=False, failed_checks=1, transformer_kva=0, capex=math.inf, lifetime_cost=math.inf, spare_pct=-math.inf)
            self.evaluated[k] = (ev, None, None, None)
            return ev
        option, used = design_option(built, d.construction, self.rules, d.transformer_kva, self.req.site, self.source, self.req.rates, d.min_feeder)
        self.unverified.update(used)
        return self._record(d, option)

    def _record(self, d: Design, option: OptionResult) -> Evaluation:
        kva = option.analysis.transformer_kva
        life = lifetime_cost(option.network, self.rules, kva, option.cost.total, self.params)
        spare = spare_of(option, self.rules)
        ev = Evaluation(design=d, passed=option.passed, failed_checks=option.failed_checks, transformer_kva=kva, capex=option.cost.total,
                        lifetime_cost=life.total.value, spare_pct=spare.spare_pct)
        self.evaluated[self.key(d)] = (ev, option, life, spare)
        return ev

    # ---- search ----------------------------------------------------------------------------------------------
    def score(self, ev: Evaluation, objective: Objective, ceiling: float | None) -> tuple:
        if not ev.passed:
            return (1, ev.failed_checks, ev.capex)
        if objective == "capex":
            return (0, ev.capex, ev.lifetime_cost)
        if objective == "lifetime":
            return (0, ev.lifetime_cost, ev.capex)
        if ceiling is not None and ev.capex > ceiling + 1e-6:
            return (1, 0, ev.capex)
        return (0, -ev.spare_pct, ev.capex)

    def neighbours(self, d: Design, pts: list[_Point]) -> list[tuple[Design, str]]:
        out: list[tuple[Design, str]] = []
        here = pts[d.position_id]
        near = sorted((p for p in pts if p.id != d.position_id), key=lambda p: math.hypot(p.xy[0] - here.xy[0], p.xy[1] - here.xy[1]))
        for p in near[:NEIGHBOUR_POSITIONS]:
            out.append((d.model_copy(update={"position_id": p.id, "position": p.lonlat, "position_label": p.label, "moved_m": p.moved_m, "upsized": []}),
                        f"Transformer moved to the {p.label} ({p.moved_m:.0f} m from the marked site)"))
        for c in self.req.constructions:
            if c != d.construction:
                out.append((d.model_copy(update={"construction": c, "min_feeder": 0, "upsized": []}), f"{c.capitalize()} instead of {d.construction}"))
        kva = d.transformer_kva or self._auto_kva(d)
        if kva is not None:
            i = self.sizes.index(kva) if kva in self.sizes else None
            if i is not None and i + 1 < len(self.sizes):
                out.append((d.model_copy(update={"transformer_kva": self.sizes[i + 1], "upsized": []}), f"Transformer {self.sizes[i + 1]:g} kVA instead of {kva:g} kVA"))
            if i is not None and i > 0 and d.transformer_kva is not None:
                out.append((d.model_copy(update={"transformer_kva": self.sizes[i - 1], "upsized": []}), f"Transformer {self.sizes[i - 1]:g} kVA instead of {kva:g} kVA"))
        n_opts = len(feeder_options(self.rules, d.construction))
        options = [o.code for o in feeder_options(self.rules, d.construction)]
        if d.min_feeder + 1 < n_opts:
            out.append((d.model_copy(update={"min_feeder": d.min_feeder + 1, "upsized": []}), f"Feeders no smaller than {options[d.min_feeder + 1]}"))
        if d.min_feeder > 0:
            out.append((d.model_copy(update={"min_feeder": d.min_feeder - 1, "upsized": []}), f"Feeders no smaller than {options[d.min_feeder - 1]}"))
        for step in PHASE_STEPS:
            off = (d.phase_offset + step) % self.rotation
            out.append((d.model_copy(update={"phase_offset": off, "upsized": []}), f"Phase rotation started {off} place(s) along"))
        return out

    def _auto_kva(self, d: Design) -> float | None:
        hit = self.evaluated.get(self.key(d))
        return hit[0].transformer_kva if hit and hit[0].transformer_kva else None

    def compound(self, d: Design, pts: list[_Point]) -> list[tuple[Design, str]]:
        """Two changes at once (position, phase start, smallest feeder), tried when no single change improves."""
        out: list[tuple[Design, str]] = []
        singles = self.neighbours(d, pts)
        for i, (a, why_a) in enumerate(singles):
            for b, why_b in singles[i + 1:]:
                merged = a.model_copy(update={k: getattr(b, k) for k in ("phase_offset", "min_feeder") if getattr(b, k) != getattr(d, k)})
                changed = {k for k in ("position_id", "construction", "transformer_kva", "min_feeder", "phase_offset") if getattr(merged, k) != getattr(d, k)}
                if len(changed) == 2 and changed & {"phase_offset", "min_feeder"}:
                    out.append((merged, f"{why_a}; {why_b[0].lower()}{why_b[1:]}"))
        return out

    def local_search(self, objective: Objective, starts: list[Evaluation], pts: list[_Point], ceiling: float | None) -> tuple[Evaluation, list[str]]:
        current = min(starts, key=lambda e: self.score(e, objective, ceiling))
        trail = [f"Start: {describe(current.design)}"]
        while self.count < self.budget:
            best = self._best_of(self.neighbours(current.design, pts), pts, objective, ceiling)
            if best is None or not self.score(best[0], objective, ceiling) < self.score(current, objective, ceiling):
                best = self._best_of(self.compound(current.design, pts), pts, objective, ceiling)
                if best is None or not self.score(best[0], objective, ceiling) < self.score(current, objective, ceiling):
                    break
            current = best[0]
            trail.append(best[1])
        return current, trail

    def _best_of(self, moves: list[tuple[Design, str]], pts: list[_Point], objective: Objective, ceiling: float | None) -> tuple[Evaluation, str] | None:
        best: tuple[Evaluation, str] | None = None
        for d, why in moves:
            ev = self.evaluate(d, pts[d.position_id])
            if ev is None:
                break
            if best is None or self.score(ev, objective, ceiling) < self.score(best[0], objective, ceiling):
                best = (ev, why)
        return best

    def refine_losses(self, ev: Evaluation, trail: list[str]) -> Evaluation:
        """Upsize the lossiest feeder branches while the lifetime cost falls and every check passes."""
        _, option, life, _ = self.evaluated[self.key(ev.design)]
        if option is None or not ev.passed:
            return ev
        built = self.build(self.points[ev.design.position_id], ev.design.construction, ev.design.phase_offset)
        assert not isinstance(built, str)
        layout = [i for i in built.issues if i.severity == "error"]
        options = [o.code for o in feeder_options(self.rules, ev.design.construction)]
        rank = {c: i for i, c in enumerate(options)}
        for _ in range(REFINE_STEPS):
            net = option.network
            losses = branch_losses(net, self.rules)
            cands = sorted((b for b in net.branches if b.kind == "feeder" and rank.get(b.conductor, len(options)) < len(options) - 1),
                           key=lambda b: -losses.get(b.id, 0))[:REFINE_BRANCHES]
            best: tuple[float, OptionResult, LifetimeCost, str] | None = None
            for b in cands:
                trial = upsize(net, b.id, options)
                opt2 = self.recheck(trial, ev.design, layout)
                if not opt2.passed:
                    continue
                life2 = lifetime_cost(opt2.network, self.rules, opt2.analysis.transformer_kva, opt2.cost.total, self.params)
                if life2.total.value < life.total.value - 1e-6 and (best is None or life2.total.value < best[0]):
                    best = (life2.total.value, opt2, life2, b.id)
            if best is None:
                break
            _, option, life, bid = best
            d = ev.design.model_copy(update={"upsized": [*ev.design.upsized, bid]})
            new_cond = next(x.conductor for x in option.network.branches if x.id == bid)
            trail.append(f"{new_cond} on branch {bid} to cut losses")
            ev = self._record(d, option)
        return ev

    def recheck(self, net: LvNetwork, d: Design, layout: list[BuildIssue]) -> OptionResult:
        a = analyse(net, self.rules, d.transformer_kva, self.req.site, self.source)
        checks: list[Check] = [Check(code=i.code, subject=s, passed=False, value=1, limit=0, unit="", message=i.message, clause="")
                               for i in layout for s in (i.samples or ["network"])] + a.checks
        oh = None
        if d.construction == "overhead":
            oh = check_overhead(net, self.rules)
            checks += oh.checks
        cost = estimate(net, a.transformer_kva, self.req.rates)
        failed = [c for c in checks if not c.passed]
        return OptionResult(construction=d.construction, network=net, analysis=a.model_copy(update={"checks": checks}), overhead=oh, cost=cost,
                            converged=True, passed=not failed, failed_checks=len(failed))


def describe(d: Design) -> str:
    kva = f"{d.transformer_kva:g} kVA" if d.transformer_kva else "smallest rating that carries the demand"
    return f"{d.construction}, transformer at the {d.position_label}, {kva}"


def upsize(net: LvNetwork, branch_id: str, options: list[str]) -> LvNetwork:
    """One size up on a feeder branch and on any upstream branch smaller than it (feeders taper outwards)."""
    out = net.model_copy(deep=True)
    rank = {c: i for i, c in enumerate(options)}
    by_to = {b.to_id: b for b in out.branches}
    b = next(x for x in out.branches if x.id == branch_id)
    b.conductor = options[rank[b.conductor] + 1]
    need = rank[b.conductor]
    node = b.from_id
    while node in by_to:
        up = by_to[node]
        if up.kind == "feeder" and rank.get(up.conductor, 0) < need:
            up.conductor = options[need]
        node = up.from_id
    return out


def branch_losses(net: LvNetwork, rules: RuleSet) -> dict[str, float]:
    """Expected peak losses per branch (W), from downstream Herman-Beta moments; only used to rank refinement moves."""
    from ..lv.analysis import PHASES
    from ..lv.library import library

    lib = library(rules)
    loads = customer_loads(net, rules)
    g = net.graph()
    own: dict[str, dict[str, list[float]]] = {}
    for c in net.customers:
        for p, m, v, _c, det in loads[c.id]:
            own.setdefault(c.node_id, {}).setdefault(p, [0.0, 0.0])
            own[c.node_id][p][0] += m + det
            own[c.node_id][p][1] += v
    acc: dict[str, dict[str, list[float]]] = {}
    for n in reversed(list(nx.topological_sort(g))):
        s = {p: list(own.get(n, {}).get(p, [0.0, 0.0])) for p in PHASES}
        for ch in g.successors(n):
            for p in PHASES:
                s[p][0] += acc[ch][p][0]
                s[p][1] += acc[ch][p][1]
        acc[n] = s
    out = {}
    for b in net.branches:
        s = acc[b.to_id]
        out[b.id] = sum(s[p][0] ** 2 + s[p][1] for p in PHASES) * lib[b.conductor].r_ohm_per_km * b.length_m / 1000
    return out


def spare_of(option: OptionResult, rules: RuleSet) -> Spare:
    a = option.analysis
    t = 100 * (1 - a.demand_kva.value / a.transformer_kva) if a.transformer_kva else 0.0
    th = 100 - max((b.loading_pct for b in a.branches if any(x.id == b.id and x.kind == "feeder" for x in option.network.branches)), default=0.0)
    limit = float(rules.data["voltage"]["lv_max_drop_pct"])
    v = 100 * (1 - max((c.total_vdrop_pct for c in a.customers), default=0.0) / limit)
    return Spare(transformer_pct=round(t, 1), thermal_pct=round(th, 1), voltage_pct=round(v, 1), spare_pct=round(min(t, th, v), 1))


def optimise(req: OptimiseRequest, rules: RuleSet) -> OptimiseResult:
    run = _Run(req, rules)
    rates = rates_of(req.rates)
    band = float(rates.get("uncertainty_pct", 15))
    pts, issues = run.positions()
    run.points = {p.id: p for p in pts}
    if not req.source_inspected:
        issues.append(BuildIssue(severity="warning", code="source_not_inspected", message="The marked transformer site was not inspected on site."))

    def design(p: _Point, c: Construction) -> Design:
        return Design(position_id=p.id, position=p.lonlat, position_label=p.label, moved_m=p.moved_m, construction=c, transformer_kva=None)

    starts_pts = [p for p in pts if p.label in ("marked site", "load centre")]
    starts = [ev for p in starts_pts for c in dict.fromkeys(req.constructions) if (ev := run.evaluate(design(p, c), p)) is not None]
    baseline = starts[0] if starts else None
    assumptions = [
        (f"Lifetime: {run.params['period_years']} years, discount rate {run.params['discount_rate_pct']} %, losses at "
         f"{run.params['energy_cost_per_kwh']} per kWh, demand growth {run.params['load_growth_pct']} % a year."),
        "Losses use the expected (mean and variance) Herman-Beta currents at peak and conductor resistance at operating temperature.",
        f"Costs are indicative (rates {rates_name(req.rates)}, ±{band:g} %); differences inside that band are too close to call.",
    ]
    if req.allow_move:
        assumptions.append(f"The transformer may move up to {run.radius:g} m from the marked site along the LV routes (plus the load centre); "
                           "a moved position must be confirmed on site.")

    results: dict[Objective, tuple[Evaluation, list[str]]] = {}
    ceiling = req.capex_ceiling
    for obj in OBJECTIVES:
        if obj not in req.objectives or not starts:
            continue
        if obj == "spare" and ceiling is None:
            feasible = [e for e, *_ in run.evaluated.values() if e.passed]
            if feasible:
                ceiling = round(min(e.capex for e in feasible) * (1 + float(opt_config(rules).get("spare_ceiling_pct", 20)) / 100), 2)
                assumptions.append(f"No capex ceiling given: most spare capacity is sought within {ceiling:,.0f} "
                                   f"(the cheapest design found plus {opt_config(rules).get('spare_ceiling_pct', 20)} %).")
        best, trail = run.local_search(obj, starts, pts, ceiling)
        if obj == "lifetime":
            best = run.refine_losses(best, trail)
        results[obj] = (best, trail)

    options: list[OptionOut] = []
    for obj, (best, trail) in results.items():
        ev, option, life, spare = run.evaluated[run.key(best.design)]
        assert option is not None and life is not None and spare is not None
        notes: list[str] = []
        if not ev.passed:
            notes.append(f"No design found that passes every check; this is the one with fewest failures ({ev.failed_checks}).")
        if ev.design.position_label != "marked site":
            notes.append("The transformer is not at the marked site: confirm the new position on site.")
        if option.cost.missing_rates:
            notes.append(f"No rate for {', '.join(option.cost.missing_rates)}; the cost leaves them out.")
        measure = (lambda e: e.capex) if obj != "lifetime" else (lambda e: e.lifetime_cost)
        others = [e for e, *_ in run.evaluated.values() if e.passed and _material(e) != _material(ev)]
        runner = min(others, key=lambda e: run.score(e, obj, ceiling)) if others else None
        close = False
        if runner is not None and ev.passed:
            if obj == "spare":
                close = abs(runner.spare_pct - ev.spare_pct) < 1.0 and abs(runner.capex - ev.capex) <= band / 100 * max(runner.capex, ev.capex)
            else:
                close = abs(measure(runner) - measure(ev)) <= band / 100 * max(measure(runner), measure(ev))
        if obj == "spare" and ceiling is not None and ev.passed and ev.capex > ceiling * (1 - band / 100):
            notes.append("Its cost is within the rate uncertainty of the capex ceiling.")
        options.append(OptionOut(objective=obj, title=TITLES[obj], design=ev.design, option=option, lifetime=life, spare=spare, trail=trail,
                                 notes=notes, runner_up=runner, too_close_to_call=close))

    close_calls: list[CloseCall] = []
    for i, a in enumerate(options):
        for b in options[i + 1:]:
            if run.key(a.design) == run.key(b.design):
                continue
            for name, va, vb in (("capex", a.option.cost.total, b.option.cost.total), ("lifetime cost", a.lifetime.total.value, b.lifetime.total.value)):
                diff = abs(va - vb) / max(va, vb, 1e-9) * 100
                if diff <= band:
                    close_calls.append(CloseCall(a=a.objective, b=b.objective, measure=name, difference_pct=round(diff, 2), band_pct=band))  # type: ignore[arg-type]

    if rules.data.get("optimisation", {}).get("status") == "unverified":
        run.unverified.add("optimisation")
    return OptimiseResult(rules=rules.ref, rules_hash=rules.hash, issues=issues, options=options, baseline=baseline, evaluations=run.count,
                          feasible=sum(1 for e, *_ in run.evaluated.values() if e.passed), positions=len(pts), capex_ceiling=ceiling,
                          uncertainty_pct=band, close_calls=close_calls, currency=str(rates.get("currency", "ZAR")),
                          rate_date=str(rates.get("rate_date")), assumptions=assumptions, unverified=sorted(run.unverified))


def _material(e: Evaluation) -> tuple:
    """What makes two designs different options (not just a phase or conductor tweak)."""
    return (e.design.position_id, e.design.construction, e.transformer_kva)
