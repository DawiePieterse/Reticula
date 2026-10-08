"""Transformer sizing and selection (plans 3.1, 3.2).

Each source of the LV network is sized from the loads its feeders connect (plan 2.2): the Herman-Beta design current
of each phase at the confidence level, loaded by the worst phase (design.demand). The rating is the smallest standard
rating of its mounting with design kVA ≤ rating × (1 − growth) × max loading. The mounting is a mini-sub where the
site was marked as one, where the construction is underground and the rules file feeds underground from mini-subs, or
where the demand is beyond every pole-mounted rating; otherwise a pole-mounted transformer. The engineer may fix a
rating; it is checked like any other. Impedance, X/R and losses come from the rules file by rating.
"""

from __future__ import annotations

from typing import Literal

from pydantic import BaseModel

from ..issues import Issue, issue
from ..lv.loads import LoadAllocation
from ..lv.network import LvNetwork
from ..rules import RuleSet
from ..trace import Traced, traced
from .demand import DEMAND_FORMULA, Demand, DemandModel

SIZE_ID = "tx.size.herman-beta.v1"
SIZE_FORMULA = "rating = min{R ∈ ratings(mounting) : S_design ≤ R·(1 − growth)·max_loading}; " + DEMAND_FORMULA


class TransformerDesign(BaseModel):
    id: str
    """The LV network's source node."""
    label: str | None
    candidate_id: str | None
    coordinates: tuple[float, float]
    marked_kind: str
    mounting: Literal["pole", "minisub"]
    feeders: int
    loads: int
    demand_kva: float
    phase_current_a: dict[str, float]
    rating_kva: float
    loading_pct: float
    """Design kVA as a share of the rating."""
    spare_kva: float
    """Rating × (1 − growth) × max loading less the design kVA."""
    impedance_pct: float
    x_over_r: float
    no_load_loss_w: float
    load_loss_w: float
    fixed: bool
    """The engineer chose the rating."""
    passes: bool
    trace: Traced


class TransformerResult(BaseModel):
    clause: str
    index: str | None
    transformers: list[TransformerDesign]
    issues: list[Issue]
    placeholders: list[str]


def _by_rating(table: dict, rating: float) -> float:
    key = f"{rating:g}"
    if key in table:
        return float(table[key])
    near = min(table, key=lambda k: abs(float(k) - rating))
    return float(table[near])


def source_demands(net: LvNetwork, alloc: LoadAllocation, classes: dict[str, str | None], model: DemandModel) -> dict[str, Demand]:
    """Demand of every source of the network, keyed by source node id."""
    source_of = {f.id: f.source for f in net.feeders}
    out: dict[str, Demand] = {}
    for a in alloc.allocations:
        s = source_of.get(a.feeder or "")
        if s is None:
            continue
        out.setdefault(s, Demand()).add(model.load(a.phase, a.kva, a.kind, classes.get(a.load_id), a.label or a.load_id))
    return out


def size(net: LvNetwork, alloc: LoadAllocation, classes: dict[str, str | None], rules: RuleSet,
         construction: Literal["overhead", "underground"], fixed: dict[str, float] | None = None,
         marked: dict[str, str] | None = None) -> TransformerResult:
    """Sizes every source of the network. `fixed` sets ratings by label; `marked` gives each source's marked kind by node id."""
    sec = rules.section("transformers", "eskom/0.8.0")
    fixed = fixed or {}
    marked = marked or {}
    model = DemandModel(rules)
    demands = source_demands(net, alloc, classes, model)
    growth = float(sec["growth_pct"]) / 100
    max_load = float(sec["max_loading_pct"]) / 100
    pole_r = sorted(float(r) for r in sec["pole_mount_ratings_kva"])
    mini_r = sorted(float(r) for r in sec["minisub_ratings_kva"])
    feeders: dict[str, int] = {}
    for f in net.feeders:
        feeders[f.source] = feeders.get(f.source, 0) + 1
    out: list[TransformerDesign] = []
    issues: list[Issue] = []
    over: list[str] = []
    upgraded: list[str] = []
    for n in net.nodes:
        if n.kind != "source":
            continue
        d = demands.get(n.id, Demand())
        currents = d.currents(model.conf)
        s = 3 * model.v_ph * max(currents.values()) / 1000
        kind = marked.get(n.id, "transformer")
        must_mini = kind == "minisub" or (construction == "underground" and bool(sec["underground_minisub"]))
        usable = s / ((1 - growth) * max_load)
        name = n.label or n.id
        if name in fixed:
            rating = float(fixed[name])
            mounting = "minisub" if must_mini or rating not in pole_r else "pole"
        else:
            if not must_mini and usable > pole_r[-1]:
                upgraded.append(name)
                must_mini = True
            series = mini_r if must_mini else pole_r
            rating = next((r for r in series if r >= usable - 1e-9), series[-1])
            mounting = "minisub" if must_mini else "pole"
        ok = s <= rating * (1 - growth) * max_load + 1e-9
        if not ok:
            over.append(name)
        z, xr = _by_rating(sec["impedance_pct"], rating), _by_rating(sec["x_over_r"], rating)
        clause = sec.get("clause", "")
        trace = traced(rating, "kVA", formula_id=SIZE_ID, formula=SIZE_FORMULA, clause=clause, rules_hash=rules.hash, inputs={
            "transformer": (name, "", "LV source"), "loads": (d.loads, "", "connected (plan 2.2)"),
            **{f"I_{p}": (round(i, 2), "A", "Herman-Beta at the confidence level") for p, i in currents.items()},
            "S_design": (round(s, 2), "kVA", "3·V_ph·max(I_p)"), "growth": (growth * 100, "%", f"rules {rules.ref} transformers (placeholder)"),
            "mounting": (mounting, "", "marked kind, construction and demand"), "fixed": (name in fixed, "", "engineer's choice"),
            "confidence": (model.conf * 100, "%", f"rules {rules.ref} income_admd.diversity")})
        out.append(TransformerDesign(
            id=n.id, label=n.label, candidate_id=n.candidate_id, coordinates=n.coordinates, marked_kind=kind, mounting=mounting,
            feeders=feeders.get(n.id, 0), loads=d.loads, demand_kva=round(s, 2), phase_current_a={p: round(i, 2) for p, i in currents.items()},
            rating_kva=rating, loading_pct=round(s / rating * 100, 1), spare_kva=round(rating * (1 - growth) * max_load - s, 2),
            impedance_pct=z, x_over_r=xr, no_load_loss_w=_by_rating(sec["no_load_loss_w"], rating),
            load_loss_w=_by_rating(sec["load_loss_w"], rating), fixed=name in fixed, passes=ok, trace=trace))
    if over:
        issues.append(issue("error", "transformer_overloaded", "Even the largest rating cannot carry the design demand with the growth "
                            "allowance at these transformers. Split their area with another transformer.", over))
    if upgraded:
        issues.append(issue("warning", "needs_minisub", "These sites were marked as pole-mounted transformers but need more than the "
                            "largest pole-mounted rating, so a mini-sub is proposed.", upgraded))
    if model.unclassed:
        issues.append(issue("warning", "no_load_class", "Residential loads without a load class are taken at their ADMD in the "
                            "transformer demand, without diversity.", model.unclassed))
    return TransformerResult(clause=sec.get("clause", ""), index=sec.get("index"), transformers=out, issues=issues,
                             placeholders=["transformer mounting limits, impedances, X/R and losses (240-56062752 and SANS 780 not held)",
                                           f"growth allowance of {growth * 100:g} %"])
