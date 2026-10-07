"""The design run: every stage from the marked routes and sites to a priced, checked design (plans 2.5 to 5.1).

1. **LV network** from the LV routes and sites (plan 2.1), then poles placed along it (overhead, plan 2.5) or kiosks
   (underground, plan 2.6), and the network built again with them, so every span is a branch and every load has a pole
   box or kiosk within reach.
2. **Loads** connected and phased (plan 2.2).
3. **Transformers** sized from the loads each feeds (plans 3.1, 3.2).
4. **LV conductors** sized per feeder against voltage drop and loading, with the sized transformers as sources and,
   underground, the de-rated ratings (plans 2.3, 2.4, 2.6).
5. **Overhead checks** on the spans and poles, or the underground de-rating record (plans 2.5, 2.6).
6. **MV network** from the MV routes, the connection point and the transformers; sized, with drops and taps; and its
   overhead line checked (plans 3.3, 3.4).
7. **Bulk supply**: load flow, fault study and supply size, or a hard stop without the connection point's data (Phase 4).
8. **Cost**: bill of quantities, capital, losses and lifetime cost (plan 5.1).

Every check of every stage is listed once in `checks`, with its value, limit, clause and formula, for the results table
(plan 2.9). Proposed candidates the field has not confirmed are listed in `not_inspected` (plan 2.8). A design is fit to
submit only when every check passes, nothing is uninspected, the bulk studies ran and no value used is a placeholder.

With `construction: compare` the run is done overhead and underground, and the option the objective prefers is returned
with both summarised side by side (plan 2.7).
"""

from __future__ import annotations

import hashlib
import json
from typing import Literal

from pydantic import BaseModel

from .. import bulk as bulk_mod
from .. import cost as cost_mod
from ..issues import Issue, issue
from ..lv.analysis import Analysis, LoadAt, SourceIn
from ..lv.loads import AllocateRequest, LoadAllocation, LoadIn, allocate
from ..lv.network import BuildRequest, CandidateIn, LvNetwork, build_network
from ..mv import network as mv_mod
from ..rules import RuleSet
from . import overhead as oh_mod
from . import sizing as sizing_mod
from . import transformers as tx_mod
from . import underground as ug_mod
from .demand import Demand, DemandModel
from .geometry import ContourIn, Frame, Ground

Construction = Literal["overhead", "underground"]
LV_KINDS = ("lv_route", "transformer", "minisub", "pole")


class DesignCandidate(CandidateIn):
    source: Literal["field", "proposed", "imported"] = "field"


class DesignOptions(BaseModel):
    construction: Literal["overhead", "underground", "compare"] | None = None
    """None takes the rules file's default."""
    mv_construction: Construction = "overhead"
    objective: Literal["capex", "lifetime", "spare"] = "lifetime"
    """Which option `compare` returns."""
    lv_conductors: dict[str, str] = {}
    """Conductor per LV feeder id, fixed by the engineer."""
    mv_conductor: str | None = None
    transformer_ratings: dict[str, float] = {}
    """Rating per transformer label, fixed by the engineer."""
    economics: dict[str, float] = {}
    underground_conditions: dict[str, float] = {}
    """Soil resistivity, depth and ground temperature at the site, overriding the rules file's design conditions."""


class DesignRequest(BaseModel):
    rules: str
    candidates: list[DesignCandidate]
    loads: list[LoadIn]
    classes: dict[str, str | None] = {}
    """Herman-Beta load class per load id."""
    contours: list[ContourIn] = []
    connection_point: mv_mod.ConnectionPointIn | None = None
    options: DesignOptions = DesignOptions()
    rates: cost_mod.RateLibrary | None = None


class Check(BaseModel):
    id: str
    category: str
    element: str
    label: str | None = None
    value: float | None = None
    limit: float | None = None
    unit: str = ""
    passes: bool
    clause: str = ""
    index: str | None = None
    formula_id: str | None = None


class NotInspected(BaseModel):
    candidate_id: str
    kind: str
    label: str | None
    elements: list[str]


class OptionSummary(BaseModel):
    construction: Construction
    capex: float
    capex_low: float
    capex_high: float
    lifetime: float
    worst_lv_drop_pct: float | None
    transformers: int
    transformer_kva: float
    spare_pct: float
    failures: int
    chosen: bool = False
    too_close: bool = False
    """The capital cost ranges overlap with the chosen option's: rate uncertainty cannot separate them."""


class LvDesign(BaseModel):
    network: LvNetwork
    allocation: LoadAllocation
    analysis: Analysis
    sizing: sizing_mod.Sizing
    generated: int
    """Poles or kiosks the design placed."""


class DesignSummary(BaseModel):
    construction: Construction
    loads: int
    connected: int
    transformers: int
    transformer_kva: float
    poles: int
    stays: int
    kiosks: int
    lv_km: float
    mv_km: float
    worst_lv_drop_pct: float | None
    worst_mv_drop_pct: float | None
    nmd_kva: float | None
    capex: float
    lifetime: float
    spare_pct: float
    checks: int
    failures: int


class Design(BaseModel):
    rules_ref: str
    rules_hash: str
    inputs_hash: str
    construction: Construction
    lv: LvDesign
    overhead: oh_mod.OverheadResult | None = None
    underground: ug_mod.UndergroundResult | None = None
    transformers: tx_mod.TransformerResult
    mv_network: LvNetwork | None = None
    mv: mv_mod.MvAnalysis | None = None
    mv_overhead: oh_mod.OverheadResult | None = None
    bulk: bulk_mod.BulkResult
    cost: cost_mod.CostResult
    comparison: list[OptionSummary] = []
    checks: list[Check]
    not_inspected: list[NotInspected]
    issues: list[Issue]
    placeholders: list[str]
    fit_to_submit: bool
    summary: DesignSummary


def inputs_hash(req: DesignRequest) -> str:
    """A hash of everything the run depends on, so a revision can be reproduced and compared (plan 7.2)."""
    blob = json.dumps(req.model_dump(mode="json"), sort_keys=True, separators=(",", ":"), ensure_ascii=False)
    return hashlib.sha256(blob.encode()).hexdigest()[:16]


def run(req: DesignRequest, rules: RuleSet) -> Design:
    allowed = rules.section("construction", "eskom/0.8.0")
    choice = req.options.construction or allowed["default"]
    if choice != "compare":
        return _run(req, rules, choice)
    options = [_run(req, rules, c) for c in allowed["allowed"]]
    key = {"capex": lambda d: d.cost.capex, "lifetime": lambda d: d.cost.lifetime, "spare": lambda d: -d.summary.spare_pct}[req.options.objective]
    # A design that cannot be built, or that fails its checks, is never preferred to one that can, or that passes.
    best = min(options, key=lambda d: (unsound(d), d.summary.failures > 0, key(d)))
    summaries = [_option(d, d is best) for d in options]
    for s, d in zip(summaries, options, strict=True):
        if d is not best:
            s.too_close = not (d.cost.capex_low > best.cost.capex_high or d.cost.capex_high < best.cost.capex_low)
    best.comparison = summaries
    return best


def unsound(d: Design) -> int:
    """What the design leaves unsupplied or unstudied: loads with no service or no feeder, transformers the MV network does
    not tap, and a bulk study that did not run.

    Ranking designs by failed checks alone rewards breaking them: a load nothing feeds, or a study that stops, fails no check
    because no check runs on it. So a less sound design is never preferred to a sounder one, whatever its checks or cost.
    Failed checks (and the error issues that report them) are not counted here; they rank next.
    """
    unfed = sum(1 for a in d.lv.allocation.allocations if a.feeder is None)
    untapped = sum(i.count for i in d.issues if i.code == "tap_unconnected")
    return (d.summary.loads - d.summary.connected) + unfed + untapped + (1 if d.bulk.stopped else 0)


def _option(d: Design, chosen: bool) -> OptionSummary:
    return OptionSummary(construction=d.construction, capex=d.cost.capex, capex_low=d.cost.capex_low, capex_high=d.cost.capex_high,
                         lifetime=d.cost.lifetime, worst_lv_drop_pct=d.summary.worst_lv_drop_pct, transformers=d.summary.transformers,
                         transformer_kva=d.summary.transformer_kva, spare_pct=d.summary.spare_pct, failures=d.summary.failures, chosen=chosen)


def _run(req: DesignRequest, rules: RuleSet, construction: Construction) -> Design:
    opts = req.options
    issues: list[Issue] = []
    lv_cands = [CandidateIn(id=c.id, kind=c.kind, geometry=c.geometry, label=c.label) for c in req.candidates if c.kind in LV_KINDS]
    marked_kind = {c.id: c.kind for c in req.candidates}
    all_coords = [tuple(x[:2]) for c in req.candidates for x in _coords(c.geometry)]
    frame = Frame(all_coords or [(27.0, -26.0)])
    ground = Ground(req.contours, frame) if req.contours else None

    # 1. LV network, with the poles or kiosks the design places.
    net0 = build_network(BuildRequest(rules=rules.ref, candidates=lv_cands), rules)
    generated = oh_mod.place_poles(net0, rules, "lv") if construction == "overhead" else ug_mod.place_kiosks(net0, rules, req.loads)
    net = build_network(BuildRequest(rules=rules.ref, candidates=lv_cands + generated), rules)
    gen_nodes = {n.id for n in net.nodes if (n.candidate_id or "").startswith("auto-")}
    for i in net.issues:
        if i.code != "pole_doubled":
            issues.append(i)

    # 2. Loads.
    alloc = allocate(AllocateRequest(rules=rules.ref, network=net, loads=req.loads), rules,
                     None if construction == "overhead" else ug_mod.kiosk_params(rules))
    issues += alloc.issues

    # 3. Transformers.
    source_kind = {n.id: marked_kind.get(n.candidate_id or "", "transformer") for n in net.nodes if n.kind == "source"}
    tx = tx_mod.size(net, alloc, req.classes, rules, construction, opts.transformer_ratings, source_kind)
    issues += tx.issues
    sources = {t.id: SourceIn(rating_kva=t.rating_kva, impedance_pct=t.impedance_pct, x_over_r=t.x_over_r) for t in tx.transformers}

    # 4. LV conductors.
    loads_at = [LoadAt(load_id=a.load_id, branch=a.branch, offset_m=a.offset_m, phase=a.phase, kva=a.kva, kind=a.kind,
                       load_class=req.classes.get(a.load_id), label=a.label) for a in alloc.allocations]
    default_lv = sizing_mod.default_conductor(rules, construction)
    derating = None
    if construction == "underground":
        def derating(conds: dict[str, str]) -> dict[str, float]:
            return {r.branch: r.derated_a for r in ug_mod.derate(net, rules, conds, default_lv, opts.underground_conditions).ratings}
    if net.feeders:
        sizing, analysis = sizing_mod.size_lv(net, loads_at, rules, construction, sources, opts.lv_conductors, derating)
    else:
        sizing = sizing_mod.Sizing(conductors={}, feeders={}, steps=[], runs=0)
        from ..lv.analysis import AnalyseRequest, analyse
        analysis = analyse(AnalyseRequest(rules=rules.ref, network=net, loads=loads_at, sources=sources), rules)
    issues += [i for i in analysis.issues if i.code != "placeholders"]

    # 5. Overhead line or underground record.
    oh = ug = None
    if construction == "overhead":
        oh = oh_mod.check(net, rules, "lv", sizing.conductors, default_lv, gen_nodes, ground)
        issues += oh.issues
    else:
        ug = ug_mod.derate(net, rules, sizing.conductors, default_lv, opts.underground_conditions)
        issues += ug.issues

    # 6. MV.
    model = DemandModel(rules)
    by_source = tx_mod.source_demands(net, alloc, req.classes, model)
    labels = {n.id: n.label for n in net.nodes if n.kind == "source"}
    demands = {labels[k]: d for k, d in by_source.items() if labels.get(k)}
    mv_routes = [CandidateIn(id=c.id, kind=c.kind, geometry=c.geometry) for c in req.candidates if c.kind == "mv_route"]
    taps = [CandidateIn(id=n.candidate_id or n.id, kind=source_kind.get(n.id, "transformer"), label=n.label,
                        geometry={"type": "Point", "coordinates": list(n.coordinates)}) for n in net.nodes if n.kind == "source"]
    mv_net = mv_res = mv_oh = None
    cp = req.connection_point
    if not mv_routes:
        issues.append(Issue(severity="warning", code="no_mv_routes", message="No MV routes are marked, so the MV network was not designed."))
    elif cp is None:
        issues.append(Issue(severity="error", code="no_connection_point",
                            message="The authority's connection point is not given, so the MV network has no source."))
    else:
        mv_net = mv_mod.build(mv_routes, taps, cp, rules)
        mv_gen: set[str] = set()
        if opts.mv_construction == "overhead":
            mv_poles = oh_mod.place_poles(mv_net, rules, "mv", prefix="auto-mvpole")
            mv_net = mv_mod.build(mv_routes, taps, cp, rules, mv_poles)
            mv_gen = {n.id for n in mv_net.nodes if (n.candidate_id or "").startswith("auto-")}
        issues += [i for i in mv_net.issues if i.code != "pole_doubled"]
        mv_res = mv_mod.size_mv(mv_net, demands, tx.transformers, rules, opts.mv_construction, model, cp.voltage_kv, opts.mv_conductor)
        issues += mv_res.issues
        if opts.mv_construction == "overhead":
            mv_oh = oh_mod.check(mv_net, rules, "mv", mv_res.conductors, mv_mod.default_conductor(rules, "overhead"), mv_gen, ground)
            issues += mv_oh.issues

    # 7. Bulk supply.
    total = Demand()
    for d in by_source.values():
        total.add(d)
    tap_pct = {t.label: t.tap_pct for t in mv_res.taps} if mv_res else {}
    bulk = bulk_mod.study(rules, cp, model, total, mv_net, mv_res, net, sizing.conductors, alloc, tx.transformers, tap_pct)
    issues += bulk.issues

    # 8. Cost.
    econ = cost_mod.economics(rules, opts.economics)
    q = _quantities(construction, opts.mv_construction, net, alloc, sizing, oh, tx, mv_net, mv_res, mv_oh, cp)
    losses = _losses(rules, net, analysis, sizing, tx, mv_net, mv_res, econ)
    cost = cost_mod.price(q, req.rates or cost_mod.default_library(), econ, losses, rules)
    issues += cost.issues

    # Checks, uninspected proposals, placeholders.
    checks = _checks(rules, analysis, oh, tx, mv_res, mv_oh, bulk)
    proposed = {c.id: c for c in req.candidates if c.source == "proposed"}
    uninspected: list[NotInspected] = []
    for cid, c in proposed.items():
        elements = [n.id for n in net.nodes if n.candidate_id == cid] + [b.id for b in net.branches if b.candidate_id == cid]
        if mv_net is not None:
            elements += [f"MV:{b.id}" for b in mv_net.branches if b.candidate_id == cid]
        uninspected.append(NotInspected(candidate_id=cid, kind=c.kind, label=c.label, elements=elements))
    if uninspected:
        issues.append(issue("warning", "not_inspected", "These sites and routes were proposed by the design and not yet confirmed in the "
                            "field. Check them on the ground before the design is submitted.", [u.label or u.kind for u in uninspected]))
        checks += [Check(id=f"inspect:{u.candidate_id}", category="not_inspected", element=u.candidate_id, label=u.label or u.kind,
                         passes=False, clause="Plan 2.8: every element must be inspected or marked in the field") for u in uninspected]
    placeholders = list(dict.fromkeys(analysis.placeholders + (oh.placeholders if oh else []) + (ug.placeholders if ug else [])
                                      + tx.placeholders + (mv_res.placeholders if mv_res else []) + (mv_oh.placeholders if mv_oh else [])
                                      + bulk.placeholders))
    issues.append(Issue(severity="warning", code="placeholders",
                        message="This design uses placeholder values and is not fit to submit: " + "; ".join(placeholders) + "."))
    failures = sum(1 for c in checks if not c.passes)
    fit = failures == 0 and not uninspected and bulk.stopped is None and not placeholders and not any(i.severity == "error" for i in issues)

    lv_km = sum(b.length_m for b in net.branches if b.kind == "route") / 1000
    mv_km = sum(b.length_m for b in mv_net.branches if b.kind == "route") / 1000 if mv_net else 0.0
    t_kva = sum(t.rating_kva for t in tx.transformers)
    spare = sum(t.spare_kva for t in tx.transformers) / t_kva * 100 if t_kva else 0.0
    summary = DesignSummary(
        construction=construction, loads=len(req.loads), connected=alloc.summary.allocated, transformers=len(tx.transformers), transformer_kva=t_kva,
        poles=len(oh.poles) if oh else 0, stays=sum(p.stays for p in oh.poles) if oh else 0, kiosks=ug.kiosks if ug else 0,
        lv_km=round(lv_km, 3), mv_km=round(mv_km, 3),
        worst_lv_drop_pct=max((f.max_drop_pct for f in analysis.feeders), default=None),
        worst_mv_drop_pct=max((t.drop_pct for t in mv_res.taps), default=None) if mv_res else None,
        nmd_kva=bulk.supply.nmd_kva if bulk.supply else None, capex=cost.capex, lifetime=cost.lifetime, spare_pct=round(spare, 1),
        checks=len(checks), failures=failures)
    return Design(
        rules_ref=rules.ref, rules_hash=rules.hash, inputs_hash=inputs_hash(req), construction=construction,
        lv=LvDesign(network=net, allocation=alloc, analysis=analysis, sizing=sizing, generated=len(gen_nodes)),
        overhead=oh, underground=ug, transformers=tx, mv_network=mv_net, mv=mv_res, mv_overhead=mv_oh, bulk=bulk, cost=cost,
        checks=checks, not_inspected=uninspected, issues=issues, placeholders=placeholders, fit_to_submit=fit, summary=summary)


def _coords(g: dict) -> list[list[float]]:
    c = g.get("coordinates") or []
    return [c] if g.get("type") == "Point" else list(c)


def _quantities(construction: Construction, mv_construction: Construction, net: LvNetwork, alloc: LoadAllocation, sizing: sizing_mod.Sizing,
                oh: oh_mod.OverheadResult | None, tx: tx_mod.TransformerResult, mv_net: LvNetwork | None, mv: mv_mod.MvAnalysis | None,
                mv_oh: oh_mod.OverheadResult | None, cp) -> cost_mod.Quantities:
    q = cost_mod.Quantities()
    for b in net.branches:
        code = sizing.conductors.get(b.id)
        if code is None:
            continue
        q.add("LV", f"A-COND-{code}", b.length_m)
        if construction == "underground" and b.kind == "route":
            q.add("LV", "A-TRENCH-LV", b.length_m)
    if oh:
        for p in oh.poles:
            if p.kind != "source":
                q.add("LV", f"A-POLE-LV-{p.height_m:g}", 1)
            q.add("LV", "A-STAY", p.stays)
        q.add("Services", "A-SDB", len(alloc.boxes))
    else:
        q.add("LV", "A-KIOSK", sum(1 for n in net.nodes if n.kind == "pole"))
    service = "OH" if construction == "overhead" else "UG"
    q.add("Services", f"A-SERVICE-{service}", len(alloc.allocations))
    q.add("Services", f"A-SERVICE-CABLE-{service}", sum(a.service_m for a in alloc.allocations))
    for t in tx.transformers:
        if t.mounting == "pole":
            q.add("Transformers", f"A-TX-POLE-{t.rating_kva:g}", 1)
            q.add("Transformers", "A-POLE-MV-12", 1)
            q.add("MV", "A-MV-TEE", 1)
        else:
            q.add("Transformers", f"A-MINISUB-{t.rating_kva:g}", 1)
    if mv_net is not None and mv is not None:
        for b in mv_net.branches:
            code = mv.conductors.get(b.id)
            if code is not None:
                q.add("MV", f"A-COND-{code}", b.length_m)
                if mv_construction == "underground" and b.kind == "route":
                    q.add("MV", "A-TRENCH-MV", b.length_m)
        if mv_oh:
            for p in mv_oh.poles:
                if p.kind != "source":
                    q.add("MV", f"A-POLE-MV-{p.height_m:g}", 1)
                q.add("MV", "A-STAY", p.stays)
    if cp is not None:
        q.add("MV", "A-MV-CONNECTION", 1)
    return q


def _losses(rules: RuleSet, net: LvNetwork, analysis: Analysis, sizing: sizing_mod.Sizing, tx: tx_mod.TransformerResult,
            mv_net: LvNetwork | None, mv: mv_mod.MvAnalysis | None, econ: cost_mod.Economics) -> cost_mod.Losses:
    lvd = rules.data["lv_design"]
    lengths = {b.id: b.length_m for b in net.branches}
    lv_w = 0.0
    for b in analysis.branches:
        c = rules.conductor(b.conductor)
        r = c.r_at(float(lvd["conductor_temp_c"]), lvd["temperature_coefficients"])
        lv_w += sum(i * i for i in b.current_a.values()) * r * lengths.get(b.id, 0.0) / 1000
    mv_w = 0.0
    if mv is not None:
        for b in mv.branches:
            mv_w += 3 * b.current_a**2 * rules.conductor(b.conductor).r_ohm_per_km * b.length_m / 1000
    tx_load = sum(t.load_loss_w * (t.demand_kva / t.rating_kva) ** 2 for t in tx.transformers)
    tx_nl = sum(t.no_load_loss_w for t in tx.transformers)
    return cost_mod.losses(lv_w / 1000, mv_w / 1000, tx_load / 1000, tx_nl / 1000, econ)


def _checks(rules: RuleSet, a: Analysis, oh: oh_mod.OverheadResult | None, tx: tx_mod.TransformerResult, mv: mv_mod.MvAnalysis | None,
            mv_oh: oh_mod.OverheadResult | None, bulk: bulk_mod.BulkResult) -> list[Check]:
    lvd = rules.data.get("lv_design", {})
    v_clause, v_index = rules.data["voltage"].get("clause", ""), rules.data["voltage"].get("index")
    out: list[Check] = []
    worst_conn: dict[str, object] = {}
    for p in a.points:
        if p.kind == "node":
            out.append(Check(id=f"lv_drop:{p.id}", category="lv_drop", element=p.id, value=p.worst_pct, limit=a.limit_pct, unit="%",
                             passes=p.worst_pct <= a.limit_pct + 1e-9, clause=v_clause, index=v_index, formula_id="lv.vdrop.herman-beta.v1"))
        elif p.feeder and (p.feeder not in worst_conn or p.worst_pct > worst_conn[p.feeder].worst_pct):
            worst_conn[p.feeder] = p
    for f, p in sorted(worst_conn.items()):
        out.append(Check(id=f"lv_drop_service:{f}", category="lv_drop", element=p.id, label=f"worst service on {f}", value=p.worst_pct,
                         limit=a.limit_pct, unit="%", passes=p.worst_pct <= a.limit_pct + 1e-9, clause=v_clause, index=v_index,
                         formula_id="lv.vdrop.herman-beta.v1"))
    for b in a.branches:
        out.append(Check(id=f"lv_loading:{b.id}", category="lv_loading", element=b.id, label=b.conductor, value=b.utilisation_pct, limit=100,
                         unit="%", passes=b.passes, clause=lvd.get("clause", ""), index=lvd.get("index"), formula_id="lv.current.herman-beta.v1"))
    if lvd.get("min_end_fault_a") is not None:
        for f in a.feeders:
            out.append(Check(id=f"lv_fault:{f.feeder}", category="lv_fault", element=f.min_fault_at, label=f.feeder, value=f.min_fault_a,
                             limit=float(lvd["min_end_fault_a"]), unit="A", passes=f.min_fault_a >= float(lvd["min_end_fault_a"]),
                             clause=lvd.get("clause", ""), index=lvd.get("index"), formula_id="lv.fault.phase-neutral.v1"))
    for res, net in ((oh, "lv"), (mv_oh, "mv")):
        if res is None:
            continue
        lim = float(rules.data["overhead"][net]["min_ground_clearance_m"])
        for s in res.spans:
            if s.clearance_m is not None:
                out.append(Check(id=f"oh_clearance:{net}:{s.id}", category="oh_clearance", element=s.id, label=f"{net.upper()} span",
                                 value=s.clearance_m, limit=lim, unit="m", passes=s.clearance_m >= lim - 1e-6, clause=res.clause, index=res.index,
                                 formula_id=oh_mod.CLEARANCE_ID))
        for sec in res.sections:
            out.append(Check(id=f"oh_tension:{sec.id}", category="oh_tension", element=sec.id, label=sec.conductor,
                             value=max(sec.everyday_tension_kn, sec.hot_tension_kn, sec.cold_tension_kn), limit=sec.max_tension_kn, unit="kN",
                             passes=sec.passes, clause=res.clause, index=res.index, formula_id=oh_mod.TENSION_ID))
        for p in res.poles:
            out.append(Check(id=f"oh_pole:{net}:{p.id}", category="oh_pole", element=p.id, label=p.label or f"{net.upper()} pole",
                             value=p.tip_load_kn, unit="kN", passes=p.passes, clause=res.clause, index=res.index, formula_id=oh_mod.POLE_ID))
    growth = float(rules.data["transformers"]["growth_pct"]) / 100
    max_load = float(rules.data["transformers"]["max_loading_pct"]) / 100
    for t in tx.transformers:
        out.append(Check(id=f"tx_loading:{t.id}", category="tx_loading", element=t.id, label=t.label, value=t.demand_kva,
                         limit=round(t.rating_kva * (1 - growth) * max_load, 2), unit="kVA", passes=t.passes, clause=tx.clause, index=tx.index,
                         formula_id=tx_mod.SIZE_ID))
    if mv is not None:
        for b in mv.branches:
            out.append(Check(id=f"mv_loading:{b.id}", category="mv_loading", element=b.id, label=b.conductor, value=b.utilisation_pct, limit=100,
                             unit="%", passes=b.utilisation_pct <= 100 + 1e-9, clause=mv.clause, formula_id=mv_mod.DROP_ID))
        for t in mv.taps:
            out.append(Check(id=f"mv_drop:{t.label}", category="mv_drop", element=t.id, label=t.label, value=t.drop_pct, limit=mv.limit_pct,
                             unit="%", passes=t.drop_pct <= mv.limit_pct + 1e-9, clause=v_clause, index=v_index, formula_id=mv_mod.DROP_ID))
    if bulk.supply is not None:
        s = bulk.supply
        out.append(Check(id="supply_capacity", category="bulk_supply", element="connection point", value=s.nmd_kva or s.required_kva,
                         limit=s.capacity_kva, unit="kVA", passes=s.passes, clause=bulk.clause, index=bulk.index, formula_id=bulk_mod.SUPPLY_ID))
    if bulk.stopped:
        out.append(Check(id="bulk_studies", category="bulk_fault", element="connection point", passes=False, clause=bulk.stopped,
                         index=bulk.index))
    else:
        switchgear = float(rules.data["bulk"]["mv_switchgear_ka"])
        mv_max = max((x.ik3_max_ka or 0.0 for x in bulk.buses if x.level == "mv"), default=0.0)
        out.append(Check(id="mv_fault", category="bulk_fault", element="MV", value=round(mv_max, 3), limit=switchgear, unit="kA",
                         passes=mv_max <= switchgear + 1e-9, clause=bulk.clause, index=bulk.index, formula_id=bulk_mod.FAULT_ID))
        for b in bulk.branches:
            if b.withstand_ka is not None and b.ik_max_ka is not None:
                out.append(Check(id=f"withstand:{b.level}:{b.id}", category="bulk_withstand", element=b.id, label=b.conductor, value=b.ik_max_ka,
                                 limit=b.withstand_ka, unit="kA", passes=b.ik_max_ka <= b.withstand_ka + 1e-9, clause=bulk.clause, index=bulk.index,
                                 formula_id=bulk_mod.FAULT_ID))
        rise = float(rules.data["voltage"].get("lv_max_rise_pct", 10)) / 100
        if bulk.min_vm_pu is not None:
            out.append(Check(id="voltage_band", category="bulk_voltage", element="all buses", value=bulk.min_vm_pu, limit=round(1 - rise, 4),
                             unit="pu", passes=bulk.min_vm_pu >= 1 - rise - 1e-9 and (bulk.max_vm_pu or 1) <= 1 + rise + 1e-9,
                             clause=v_clause, index=v_index))
    return out
