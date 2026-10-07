"""MV network (plans 3.3, 3.4): the marked MV routes joined to the connection point and the transformers, then checked.

**Network.** The LV builder (lv.network) joins the MV routes too, with the rules file's `mv_network` tolerances: the
connection point is the source, linked to the nearest MV route within `source_reach_m`; each transformer or mini-sub
is a tap, tee'd off the nearest MV route within `tap_reach_m` and keeping its LV label (TX1, MS2). The network must be
radial; the open points of a ring are the engineer's call.

**Loading.** Every section carries the Herman-Beta demand of all the consumers beyond it (design.demand), so diversity
grows towards the connection point as it does in the load schedule. The MV current is S / (√3 · V_MV).

**Voltage.** The drop to each tap is Σ √3 · I · (R·cosφ + X·sinφ) · ℓ over the sections between, as a share of the
nominal voltage, against `voltage.mv_max_drop_pct`. Each transformer's regulation at its design load is
(S/S_r)·(ε_R·cosφ + ε_X·sinφ). Its off-load tap is the largest boost that does not exceed the MV drop plus regulation
and keeps the LV voltage at no load within `voltage.lv_max_rise_pct`.

**Sizing.** Each MV feeder (route leaving the connection point) starts on the default conductor of its construction
and steps up the MV library until it passes loading and drop, as LV feeders do.
"""

from __future__ import annotations

import math
from typing import Literal

from pydantic import BaseModel

from ..design.demand import Demand, DemandModel
from ..design.transformers import TransformerDesign
from ..issues import Issue, issue
from ..lv.network import BuildRequest, CandidateIn, LvNetwork, Params, build_network
from ..rules import RuleSet
from ..rules.loader import Conductor
from ..trace import Traced, traced

DROP_ID = "mv.vdrop.balanced.v1"
DROP_FORMULA = "ΔV% = Σ √3·I·(R·cosφ + X·sinφ)·ℓ / V_LL × 100; I = S_design / (√3·V_LL); S_design Herman-Beta of all consumers beyond"
TAP_ID = "mv.tap.offload.v1"
TAP_FORMULA = "tap = max{t ∈ taps : t ≤ ΔV_MV% + reg%, t ≤ rise limit}; reg% = (S/S_r)(ε_R·cosφ + ε_X·sinφ)"


class ConnectionPointIn(BaseModel):
    coordinates: tuple[float, float]
    voltage_kv: float | None = None
    capacity_kva: float | None = None
    """What the authority can supply at this point."""
    fault_3ph_ka: float | None = None
    """Three-phase fault level at the point, maximum."""
    fault_1ph_ka: float | None = None
    x_over_r: float | None = None
    fault_3ph_min_ka: float | None = None


class MvBranchResult(BaseModel):
    id: str
    feeder: str | None
    conductor: str
    length_m: float
    demand_kva: float
    current_a: float
    rating_a: float
    utilisation_pct: float
    drop_pct: float
    """At the far end, from the connection point."""
    passes: bool


class MvTapResult(BaseModel):
    id: str
    label: str | None
    feeder: str | None
    distance_m: float
    drop_pct: float
    regulation_pct: float
    tap_pct: float
    lv_full_load_pct: float
    """LV voltage at the board at design load, % of nominal, with MV at nominal at the connection point."""
    lv_no_load_pct: float
    passes: bool


class MvAnalysis(BaseModel):
    rules_ref: str
    rules_hash: str
    clause: str
    construction: Literal["overhead", "underground"]
    voltage_kv: float
    limit_pct: float
    total_kva: float
    branches: list[MvBranchResult]
    taps: list[MvTapResult]
    conductors: dict[str, str]
    issues: list[Issue]
    worst_drop: Traced | None = None
    worst_tap: Traced | None = None
    placeholders: list[str] = []


def params(rules: RuleSet, pole_reach_m: float | None = None) -> Params:
    s = rules.section("mv_network", "eskom/0.8.0")
    return Params(join_m=float(s["join_m"]), near_miss_m=float(s["near_miss_m"]), source_reach_m=float(s["source_reach_m"]),
                  pole_reach_m=pole_reach_m if pole_reach_m is not None else float(s["join_m"]), sources=("connection_point",),
                  clause=s.get("clause", ""), taps=("transformer", "minisub"), tap_reach_m=float(s["tap_reach_m"]), route_kind="mv_route")


def build(routes: list[CandidateIn], taps: list[CandidateIn], cp: ConnectionPointIn | None, rules: RuleSet,
          poles: list[CandidateIn] = ()) -> LvNetwork:
    cands = list(routes) + list(taps) + list(poles)
    if cp is not None:
        cands.append(CandidateIn(id="connection-point", kind="connection_point", label="CP",
                                 geometry={"type": "Point", "coordinates": list(cp.coordinates)}))
    return build_network(BuildRequest(rules=rules.ref, candidates=cands), rules, params(rules))


def library(rules: RuleSet, construction: Literal["overhead", "underground"]) -> list[Conductor]:
    return sorted((c for c in rules.mv_conductors() if c.kind == construction), key=lambda c: (c.rating_a, c.code))


def default_conductor(rules: RuleSet, construction: Literal["overhead", "underground"]) -> str:
    s = rules.section("mv_network", "eskom/0.8.0")
    return s["default_cable"] if construction == "underground" else s["default_conductor"]


def analyse(net: LvNetwork, demands: dict[str, Demand], transformers: list[TransformerDesign], rules: RuleSet,
            conductors: dict[str, str], construction: Literal["overhead", "underground"], model: DemandModel,
            voltage_kv: float | None = None) -> MvAnalysis:
    """`demands` and `transformers` are keyed and labelled as the LV network's sources."""
    s = rules.section("mv_network", "eskom/0.8.0")
    tx = rules.section("transformers", "eskom/0.8.0")
    v_kv = voltage_kv or float(rules.data["voltage"].get("mv_nominal_kv", 11))
    limit = float(rules.data["voltage"].get("mv_max_drop_pct", 5))
    rise = float(rules.data["voltage"].get("lv_max_rise_pct", 10))
    pf = float(s["power_factor"])
    sin = math.sqrt(max(0.0, 1 - pf * pf))
    taps_pct = sorted(float(t) for t in tx["taps_pct"])
    by_label = {t.label: t for t in transformers if t.label}
    default = default_conductor(rules, construction)
    nodes = {n.id: n for n in net.nodes}
    children: dict[str, list] = {}
    for b in net.branches:
        children.setdefault(b.from_node, []).append(b)

    sub: dict[str, Demand] = {}

    def total(nid: str) -> Demand:
        if nid in sub:
            return sub[nid]
        d = Demand()
        n = nodes[nid]
        if n.kind == "tap" and n.label in demands:
            d.add(demands[n.label])
        for b in children.get(nid, []):
            d.add(total(b.to_node))
        sub[nid] = d
        return d

    roots = [n.id for n in net.nodes if n.kind == "source"]
    drop: dict[str, float] = {r: 0.0 for r in roots}
    results: list[MvBranchResult] = []
    worst = None
    stack = list(roots)
    order: list[str] = []
    while stack:
        nid = stack.pop()
        order.append(nid)
        for b in children.get(nid, []):
            if b.feeder is None and b.kind != "link":
                continue
            c = rules.conductor(conductors.get(b.id, default))
            d = total(b.to_node)
            kva = d.kva(model.conf, model.v_ph)
            i = kva * 1000 / (math.sqrt(3) * v_kv * 1000)
            dv = math.sqrt(3) * i * (c.r_ohm_per_km * pf + c.x_ohm_per_km * sin) * b.length_m / 1000 / (v_kv * 1000) * 100
            drop[b.to_node] = drop[nid] + dv
            util = i / c.rating_a * 100
            ok = util <= 100 + 1e-9 and drop[b.to_node] <= limit + 1e-9
            results.append(MvBranchResult(id=b.id, feeder=b.feeder, conductor=c.code, length_m=b.length_m, demand_kva=round(kva, 2),
                                          current_a=round(i, 3), rating_a=c.rating_a, utilisation_pct=round(util, 2),
                                          drop_pct=round(drop[b.to_node], 4), passes=ok))
            if worst is None or drop[b.to_node] > worst[0]:
                worst = (drop[b.to_node], b.to_node, i, c, b.id)
            stack.append(b.to_node)

    taps: list[MvTapResult] = []
    worst_tap = None
    for n in net.nodes:
        if n.kind != "tap":
            continue
        t = by_label.get(n.label)
        mv_drop = drop.get(n.id)
        if t is None or mv_drop is None:
            continue
        z, xr = t.impedance_pct, t.x_over_r
        er, ex = z / math.sqrt(1 + xr * xr), z * xr / math.sqrt(1 + xr * xr)
        reg = t.demand_kva / t.rating_kva * (er * pf + ex * sin)
        allowed = [x for x in taps_pct if x <= rise + 1e-9 and x <= mv_drop + reg + 1e-9]
        tap = max(allowed) if allowed else min(taps_pct)
        full = 100 - mv_drop - reg + tap
        none = 100 + tap
        ok = mv_drop <= limit + 1e-9 and none <= 100 + rise + 1e-9
        taps.append(MvTapResult(id=n.id, label=n.label, feeder=n.feeder, distance_m=n.distance_m or 0.0, drop_pct=round(mv_drop, 4),
                                regulation_pct=round(reg, 4), tap_pct=tap, lv_full_load_pct=round(full, 3), lv_no_load_pct=round(none, 3),
                                passes=ok))
        if worst_tap is None or mv_drop > worst_tap[0]:
            worst_tap = (mv_drop, n.label, reg, tap, full, none, t)

    issues: list[Issue] = []
    over = [r.id for r in results if r.utilisation_pct > 100 + 1e-9]
    if over:
        issues.append(issue("error", "mv_overload", "MV sections carry more than their conductor's rating.", over))
    far = [t.label or t.id for t in taps if t.drop_pct > limit + 1e-9]
    if far:
        issues.append(issue("error", "mv_drop_over_limit", f"MV voltage drop is over {limit:g} % at these transformers.", far))
    clause = s.get("clause", "")
    root_total = Demand()
    for r in roots:
        root_total.add(total(r))
    return MvAnalysis(
        rules_ref=rules.ref, rules_hash=rules.hash, clause=clause, construction=construction, voltage_kv=v_kv, limit_pct=limit,
        total_kva=round(root_total.kva(model.conf, model.v_ph), 2), branches=results, taps=taps,
        conductors={r.id: r.conductor for r in results}, issues=issues,
        worst_drop=traced(worst[0], "%", formula_id=DROP_ID, formula=DROP_FORMULA, clause=clause, rules_hash=rules.hash, inputs={
            "node": (worst[1], "", "largest MV drop"), "V_LL": (v_kv, "kV", "connection point or rules voltage.mv_nominal_kv"),
            "cos_phi": (pf, "", f"rules {rules.ref} mv_network (placeholder)"), "last_section": (worst[4], "", worst[3].code),
            "I_last": (round(worst[2], 3), "A", "Herman-Beta demand beyond / (√3·V)"),
            "limit": (limit, "%", f"rules {rules.ref} voltage.mv_max_drop_pct (placeholder)")}) if worst else None,
        worst_tap=traced(worst_tap[3], "%", formula_id=TAP_ID, formula=TAP_FORMULA, clause=tx.get("clause", ""), rules_hash=rules.hash, inputs={
            "transformer": (worst_tap[1], "", "largest MV drop"), "mv_drop": (round(worst_tap[0], 4), "%", "to the tap"),
            "regulation": (round(worst_tap[2], 4), "%", (f"{worst_tap[6].demand_kva:g} of {worst_tap[6].rating_kva:g} kVA, "
                                                         f"{worst_tap[6].impedance_pct:g} %, X/R {worst_tap[6].x_over_r:g}")),
            "taps": (", ".join(f"{t:g}" for t in taps_pct), "%", f"rules {rules.ref} transformers.taps_pct (placeholder)"),
            "rise_limit": (rise, "%", f"rules {rules.ref} voltage.lv_max_rise_pct (placeholder)"),
            "lv_full_load": (round(worst_tap[4], 3), "%", "100 − ΔV_MV − reg + tap")}) if worst_tap else None,
        placeholders=["MV conductor data (typical values)", f"MV power factor {pf:g}", f"MV drop limit {limit:g} %",
                      "transformer taps and LV rise limit"],
    )


def size_mv(net: LvNetwork, demands: dict[str, Demand], transformers: list[TransformerDesign], rules: RuleSet,
            construction: Literal["overhead", "underground"], model: DemandModel, voltage_kv: float | None = None,
            fixed: str | None = None) -> MvAnalysis:
    """Steps each MV feeder up the library until it passes; `fixed` puts every section on one conductor."""
    lib = library(rules, construction)
    groups: dict[str, list[str]] = {}
    for b in net.branches:
        if b.feeder or b.kind == "link":
            groups.setdefault(b.feeder or f"link:{b.id}", []).append(b.id)
    default = fixed or default_conductor(rules, construction)
    start = next((i for i, c in enumerate(lib) if c.code == default), 0)
    pick = dict.fromkeys(groups, start)
    for _ in range(len(lib) * max(len(groups), 1) + 1):
        conds = {bid: lib[pick[g]].code for g, bids in groups.items() for bid in bids}
        result = analyse(net, demands, transformers, rules, conds, construction, model, voltage_kv)
        if fixed:
            return result
        failing = {g for g, bids in groups.items() if any(not r.passes for r in result.branches if r.id in bids)}
        changed = False
        for g in failing:
            if pick[g] < len(lib) - 1:
                pick[g] += 1
                changed = True
        if not changed:
            return result
    return result
