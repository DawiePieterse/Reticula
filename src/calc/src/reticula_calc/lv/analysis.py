"""Herman-Beta voltage drop, thermal loading and LV fault level for a radial LV network (plan 2.4).

Voltage drop. Each residential consumer's current is a beta variable on [0, c] (its load class). The voltage drop
from the transformer to a node, on phase p, is a linear combination of all consumers' currents. Along each branch
the phase conductor carries phase p's downstream current and the neutral carries the phasor sum of all three phases;
projected onto phase p's voltage (unity-angle loads, 120° apart) the neutral current is I_p − (I_q + I_r)/2. So a
consumer on phase p adds (z_phase + z_neutral) per metre of shared path, and one on another phase adds −z_neutral/2,
where z = R cos φ + X sin φ. The sum's mean, variance and bounds give a fitted beta whose quantile at the confidence
level (90 %) is the design voltage drop (Herman & Gaunt's direct statistical method; NRS 034-1). A 3-phase connection
counts as one consumer on each phase. Special loads are constant currents at their kVA.

The moments are accumulated in one pass from the source: for a node reached from its parent n over branch b,
mean and bounds add coef_b(q)·(downstream sums on phase q), and the variance adds
(2·w_q(n)·coef_b(q) + coef_b(q)²)·(downstream variance on phase q), where w_q(n) is the weight every consumer
below b already carries at n.

Thermal. A branch's design current is the beta quantile of its downstream current per phase; the worst phase is
compared with the conductor rating (underground ratings derated, plan 2.6).

Fault level. IEC 60909 short-circuit currents from the MV source and transformer: the maximum three-phase current at
the LV terminals and the minimum phase-neutral current at every feeder end, which must reach the rules' multiple of
the feeder fuse.
"""

from __future__ import annotations

import math
from dataclasses import dataclass, field
from itertools import pairwise
from typing import Any

import networkx as nx
from pydantic import BaseModel
from scipy.special import betaincinv

from ..calcs.admd import AdmdInputError, _cfg, _load_class
from ..rules import RuleSet
from ..rules.loader import RulesError
from ..trace import Traced, traced
from .library import CableType, library, lv_config
from .model import LvNetwork

PHASES = ("R", "W", "B")
VD_FORMULA_ID = "lv.vdrop.herman-beta.v1"
VD_FORMULA = ("ΔV_p = Σ_k w_k I_k; w_k = Σ_shared (z_ph+z_n) if consumer k is on phase p else −Σ_shared z_n/2, z = R cos φ + X sin φ; "
              "I_k ~ c_k·Beta(α_k, β_k); beta fitted to (mean, variance) on [L, U]; ΔV = L + (U−L)·BetaInv(conf)")
I_FORMULA_ID = "lv.current.herman-beta.v1"
FAULT_FORMULA_ID = "lv.fault.iec60909.v1"


def bounded_beta_quantile(mean: float, var: float, lo: float, hi: float, q: float) -> float:
    """Quantile of a variable on [lo, hi] approximated by a beta with the same mean and variance."""
    span = hi - lo
    if span <= 1e-12 or var <= 1e-18:
        return mean
    m = min(max((mean - lo) / span, 1e-9), 1 - 1e-9)
    v = var / (span * span)
    k = m * (1 - m) / v - 1
    if k <= 0:  # variance at its theoretical maximum: a two-point distribution
        return lo + span * (1.0 if q > 1 - m else 0.0)
    return lo + span * float(betaincinv(m * k, (1 - m) * k, q))  # the beta quantile, without scipy.stats overhead


@dataclass
class _Sums:
    """Downstream sums per phase: mean, variance, beta scale c, and constant current."""

    mean: dict[str, float] = field(default_factory=lambda: dict.fromkeys(PHASES, 0.0))
    var: dict[str, float] = field(default_factory=lambda: dict.fromkeys(PHASES, 0.0))
    cap: dict[str, float] = field(default_factory=lambda: dict.fromkeys(PHASES, 0.0))
    det: dict[str, float] = field(default_factory=lambda: dict.fromkeys(PHASES, 0.0))

    def add(self, other: _Sums) -> None:
        for p in PHASES:
            self.mean[p] += other.mean[p]
            self.var[p] += other.var[p]
            self.cap[p] += other.cap[p]
            self.det[p] += other.det[p]


class BranchResult(BaseModel):
    id: str
    conductor: str
    length_m: float
    design_current_a: float
    current_by_phase: dict[str, float]
    rating_a: float
    derating: float
    loading_pct: float
    customers: int


class NodeResult(BaseModel):
    id: str
    vdrop_v: dict[str, float]
    vdrop_pct: float
    worst_phase: str


class CustomerResult(BaseModel):
    id: str
    phases: list[str]
    feeder_vdrop_pct: float
    service_vdrop_pct: float
    total_vdrop_pct: float


class FeederEnd(BaseModel):
    node_id: str
    feeder: str
    min_fault_a: float
    fuse_a: float | None
    required_a: float | None


class Check(BaseModel):
    code: str
    subject: str
    passed: bool
    value: float
    limit: float
    unit: str
    message: str
    clause: str


class Analysis(BaseModel):
    branches: list[BranchResult]
    nodes: list[NodeResult]
    customers: list[CustomerResult]
    feeder_ends: list[FeederEnd]
    demand_kva: Traced
    transformer_kva: float
    max_fault_ka: Traced
    worst_vdrop_pct: Traced
    checks: list[Check]
    unverified: list[str]

    @property
    def passed(self) -> bool:
        return all(c.passed for c in self.checks)


def customer_loads(network: LvNetwork, rules: RuleSet) -> dict[str, list[tuple[str, float, float, float, float]]]:
    """Per customer: (phase, mean, variance, c, constant current) entries, one per connected phase."""
    cfg = _cfg(rules)
    v = float(lv_config(rules)["phase_voltage_v"])
    out: dict[str, list[tuple[str, float, float, float, float]]] = {}
    for c in network.customers:
        entries = []
        if c.kind == "special":
            amps = (c.special_kva or 0) * 1000 / v / len(c.phases)
            entries = [(p, 0.0, 0.0, 0.0, amps) for p in c.phases]
        else:
            if not c.load_class:
                raise AdmdInputError(f"building {c.erf or c.building_id} has no load class; record its load first")
            lc = _load_class(cfg, rules, c.load_class, "score")
            mu = lc.c_amps * lc.alpha / (lc.alpha + lc.beta)
            sd2 = lc.c_amps ** 2 * lc.alpha * lc.beta / ((lc.alpha + lc.beta) ** 2 * (lc.alpha + lc.beta + 1))
            entries = [(p, mu, sd2, lc.c_amps, 0.0) for p in c.phases]
        out[c.id] = entries
    return out


def derating(rules: RuleSet, cable: CableType, site: dict[str, float] | None = None) -> tuple[float, dict[str, float]]:
    """Underground derating factor (soil resistivity × depth × ground temperature × grouping); 1 for overhead."""
    if cable.kind != "underground":
        return 1.0, {}
    ug = rules.data.get("underground")
    if not ug:
        return 1.0, {}
    d = {**ug["design"], **(site or {})}
    factors = {
        "soil_resistivity": _interp(ug["soil_resistivity_factor"], float(d["soil_resistivity_kmw"])),
        "depth": _interp(ug["depth_factor"], float(d["depth_m"])),
        "ground_temp": _interp(ug["ground_temp_factor"], float(d["ground_temp_c"])),
        "grouping": _interp(ug["grouping_factor"], float(d["circuits_in_trench"])),
    }
    return math.prod(factors.values()), factors


def _interp(table: dict[str, float], x: float) -> float:
    pts = sorted((float(k), float(v)) for k, v in table.items())
    if x <= pts[0][0]:
        return pts[0][1]
    if x >= pts[-1][0]:
        return pts[-1][1]
    for (x0, y0), (x1, y1) in pairwise(pts):
        if x0 <= x <= x1:
            return y0 + (y1 - y0) * (x - x0) / (x1 - x0)
    return pts[-1][1]


def feeders(network: LvNetwork) -> dict[str, list[str]]:
    """Feeder name -> its branch ids. A feeder starts at each branch leaving the transformer (after its link)."""
    g = network.graph()
    start = network.source_id
    out_edges = list(g.out_edges(start, data=True))
    if len(out_edges) == 1 and out_edges[0][2]["branch"].route_id is None and out_edges[0][2]["branch"].kind == "feeder":
        start = out_edges[0][1]
    result: dict[str, list[str]] = {}
    for i, (_, child, data) in enumerate(sorted(g.out_edges(start, data=True), key=lambda e: e[2]["branch"].id)):
        name = f"F{i + 1}"
        below = nx.descendants(g, child) | {child}
        result[name] = [data["branch"].id] + [g.edges[u, v]["branch"].id for u, v in g.edges if v in below and u in below]
    if start != network.source_id:
        result["link"] = [out_edges[0][2]["branch"].id]  # the transformer's link to the routes, common to all feeders
    return result


def analyse(network: LvNetwork, rules: RuleSet, transformer_kva: float | None = None,
            site: dict[str, float] | None = None, source: dict[str, float] | None = None) -> Analysis:
    """`source` may give the MV fault levels at the connection point (`fault_mva_max`, `fault_mva_min`, MVA),
    which replace the rules' default."""
    cfg = lv_config(rules)
    lib = library(rules)
    pf = float(cfg["power_factor"])
    v_ph = float(cfg["phase_voltage_v"])
    conf = float(cfg["confidence_pct"]) / 100
    clause = cfg.get("clause", "")
    unverified: set[str] = set()
    if cfg.get("status") == "unverified":
        unverified.add("lv_design")

    g = network.graph()
    order = list(nx.topological_sort(g))
    branch_of = {b.to_id: b for b in network.branches}
    loads = customer_loads(network, rules)
    at_node: dict[str, list[tuple[str, float, float, float, float]]] = {}
    count_at: dict[str, int] = {}
    for c in network.customers:
        at_node.setdefault(c.node_id, []).extend(loads[c.id])
        count_at[c.node_id] = count_at.get(c.node_id, 0) + 1

    # Downstream sums per node (post-order).
    sums: dict[str, _Sums] = {}
    n_below: dict[str, int] = {}
    for n in reversed(order):
        s = _Sums()
        for p, mu, var, cap, det in at_node.get(n, []):
            s.mean[p] += mu
            s.var[p] += var
            s.cap[p] += cap
            s.det[p] += det
        cnt = count_at.get(n, 0)
        for ch in g.successors(n):
            s.add(sums[ch])
            cnt += n_below[ch]
        sums[n] = s
        n_below[n] = cnt

    # Branch currents and loading.
    branches: list[BranchResult] = []
    checks: list[Check] = []
    for b in network.branches:
        cable = lib.get(b.conductor)
        if cable is None:
            raise RulesError(f"branch {b.id} uses conductor {b.conductor!r}, which is not in rules {rules.ref}")
        if cable.status == "unverified":
            unverified.add(f"conductor {cable.code}")
        s = sums[b.to_id]
        by_phase = {p: bounded_beta_quantile(s.mean[p] + s.det[p], s.var[p], s.det[p], s.det[p] + s.cap[p], conf) for p in PHASES}
        design = max(by_phase.values())
        f, factors = derating(rules, cable, site)
        if factors and rules.data["underground"].get("status") == "unverified":
            unverified.add("underground derating factors")
        rating = cable.rating_a * f
        loading = 100 * design / rating
        branches.append(BranchResult(id=b.id, conductor=b.conductor, length_m=b.length_m, design_current_a=round(design, 2),
                                     current_by_phase={p: round(i, 2) for p, i in by_phase.items()}, rating_a=round(rating, 1),
                                     derating=round(f, 3), loading_pct=round(loading, 1), customers=n_below[b.to_id]))
        checks.append(Check(code="thermal", subject=b.id, passed=design <= rating + 1e-9, value=round(design, 1), limit=round(rating, 1), unit="A",
                            message=f"{b.kind} {b.id} ({b.conductor}) carries {design:.0f} A at {conf:.0%} confidence; rating {rating:.0f} A"
                                    + (f" after derating ×{f:.2f}" if factors else ""),
                            clause=cable.clause))

    # Voltage drop: one pass from the source per measured phase.
    z: dict[str, tuple[float, float]] = {}
    for b in network.branches:
        zp, zn = lib[b.conductor].z_eff(pf)
        z[b.id] = (zp * b.length_m, zn * b.length_m)
    vd: dict[str, dict[str, float]] = {n: {} for n in order}
    for p in PHASES:
        mean = {network.source_id: 0.0}
        var = {network.source_id: 0.0}
        lo = {network.source_id: 0.0}
        hi = {network.source_id: 0.0}
        z_same = {network.source_id: 0.0}
        z_neu = {network.source_id: 0.0}
        for n in order:
            if n == network.source_id:
                vd[n][p] = 0.0
                continue
            b = branch_of[n]
            par = b.from_id
            zp, zn = z[b.id]
            s = sums[n]
            m_, v_, l_, h_ = mean[par], var[par], lo[par], hi[par]
            for q in PHASES:
                coef = (zp + zn) if q == p else -zn / 2
                w_par = z_same[par] if q == p else -z_neu[par] / 2
                m_ += coef * (s.mean[q] + s.det[q])
                v_ += (2 * w_par * coef + coef * coef) * s.var[q]
                if coef >= 0:
                    h_ += coef * (s.cap[q] + s.det[q])
                    l_ += coef * s.det[q]
                else:
                    l_ += coef * (s.cap[q] + s.det[q])
                    h_ += coef * s.det[q]
            mean[n], var[n], lo[n], hi[n] = m_, v_, l_, h_
            z_same[n] = z_same[par] + zp + zn
            z_neu[n] = z_neu[par] + zn
            vd[n][p] = bounded_beta_quantile(m_, v_, l_, h_, conf)

    feeder_limit = float(cfg["feeder_max_drop_pct"])
    service_limit = float(cfg["service_max_drop_pct"])
    total_limit = float(rules.data["voltage"]["lv_max_drop_pct"])
    nodes: list[NodeResult] = []
    connection_nodes = {c.node_id for c in network.customers}
    for n in order:
        worst = max(vd[n], key=lambda q: vd[n][q])
        pct = 100 * vd[n][worst] / v_ph
        nodes.append(NodeResult(id=n, vdrop_v={q: round(x, 3) for q, x in vd[n].items()}, vdrop_pct=round(pct, 3), worst_phase=worst))
        if n not in connection_nodes and n != network.source_id:
            checks.append(Check(code="feeder_vdrop", subject=n, passed=pct <= feeder_limit + 1e-9, value=round(pct, 2), limit=feeder_limit, unit="%",
                                message=f"Feeder voltage drop at {n} is {pct:.2f} % (phase {worst}) at {conf:.0%} confidence", clause=clause))

    customers: list[CustomerResult] = []
    for c in network.customers:
        sb = branch_of[c.node_id]
        at = sb.from_id
        feeder_pct = max(100 * vd[at][q] / v_ph for q in c.phases)
        total_pct = max(100 * vd[c.node_id][q] / v_ph for q in c.phases)
        service_pct = max(total_pct - feeder_pct, 0.0)
        customers.append(CustomerResult(id=c.id, phases=list(c.phases), feeder_vdrop_pct=round(feeder_pct, 3),
                                        service_vdrop_pct=round(service_pct, 3), total_vdrop_pct=round(total_pct, 3)))
        name = c.erf or c.building_id
        checks.append(Check(code="service_vdrop", subject=c.id, passed=service_pct <= service_limit + 1e-9, value=round(service_pct, 2),
                            limit=service_limit, unit="%", message=f"Service voltage drop to {name} is {service_pct:.2f} %", clause=clause))
        checks.append(Check(code="supply_vdrop", subject=c.id, passed=total_pct <= total_limit + 1e-9, value=round(total_pct, 2),
                            limit=total_limit, unit="%", message=f"Voltage drop to the point of supply of {name} is {total_pct:.2f} %",
                            clause=rules.data["voltage"].get("clause", clause)))

    worst_node = max(nodes, key=lambda r: r.vdrop_pct)
    worst_vd = traced(worst_node.vdrop_pct, "%", formula_id=VD_FORMULA_ID, formula=VD_FORMULA, clause=clause, rules_hash=rules.hash,
                      inputs={"node": (worst_node.id, ""), "phase": (worst_node.worst_phase, ""), "confidence": (conf * 100, "%"),
                              "power_factor": (pf, "", f"rules {rules.ref} lv_design"), "V_phase": (v_ph, "V", f"rules {rules.ref} lv_design")})

    # Demand and transformer.
    src = sums[network.source_id]
    per_phase = {p: bounded_beta_quantile(src.mean[p] + src.det[p], src.var[p], src.det[p], src.det[p] + src.cap[p], conf) for p in PHASES}
    demand = 3 * v_ph * max(per_phase.values()) / 1000
    demand_t = traced(round(demand, 2), "kVA", formula_id=I_FORMULA_ID, formula="S = 3 × V_ph × max_p I_p(conf); I_p beta quantile of all consumers on phase p",
                      clause=clause, rules_hash=rules.hash,
                      inputs={**{f"I_{p}": (round(i, 2), "A") for p, i in per_phase.items()}, "V_phase": (v_ph, "V", f"rules {rules.ref}")})
    fault = rules.data.get("fault")
    if not fault:
        raise RulesError(f"rules {rules.ref} have no fault section")
    if fault.get("status") == "unverified":
        unverified.add("fault parameters")
    sizes = sorted(fault["transformers"], key=lambda t: t["kva"])
    chosen = next((t for t in sizes if t["kva"] == transformer_kva), None) if transformer_kva else next((t for t in sizes if t["kva"] >= demand), None)
    if chosen is None:
        chosen = sizes[-1]
        checks.append(Check(code="transformer_size", subject=network.source_id, passed=False, value=round(demand, 1), limit=chosen["kva"], unit="kVA",
                            message=f"Design demand {demand:.0f} kVA exceeds the largest transformer in the rules ({chosen['kva']} kVA)",
                            clause=fault.get("clause", "")))
    else:
        checks.append(Check(code="transformer_size", subject=network.source_id, passed=demand <= chosen["kva"] + 1e-9, value=round(demand, 1),
                            limit=chosen["kva"], unit="kVA", message=f"Design demand {demand:.1f} kVA on a {chosen['kva']} kVA transformer",
                            clause=fault.get("clause", "")))

    # Fault levels (IEC 60909-0, values referred to the LV side). Source: ZQ = cQ·Un²/S''kQ with cQ for the MV network
    # (R/X 0.1). Network transformer: impedance corrected by KT = 0.95·cmax/(1 + 0.6·xT) for the maximum fault (§6.3.3).
    un = float(rules.data["voltage"]["lv_nominal_v"])
    c_max, c_min = float(fault["voltage_factor_max"]), float(fault["voltage_factor_min"])
    c_q = float(fault.get("voltage_factor_mv", 1.1))
    sk_max = float((source or {}).get("fault_mva_max") or fault["mv_fault_mva"])
    sk_min = float((source or {}).get("fault_mva_min") or fault.get("mv_fault_mva_min") or sk_max)

    def zq_of(sk: float) -> complex:
        mag = c_q * un ** 2 / (sk * 1e6)
        return complex(0.1 * mag / math.sqrt(1.01), mag / math.sqrt(1.01))

    zt_mag = float(chosen["z_pct"]) / 100 * un ** 2 / (float(chosen["kva"]) * 1e3)
    xr = float(chosen.get("x_r", 3.0))
    rt = zt_mag / math.sqrt(1 + xr * xr)
    zt = complex(rt, rt * xr)
    xt_pu = float(chosen["z_pct"]) / 100 * xr / math.sqrt(1 + xr * xr)
    kt = 0.95 * c_max / (1 + 0.6 * xt_pu)
    zq = zq_of(sk_min)
    ik3 = c_max * un / (math.sqrt(3) * abs(zq_of(sk_max) + kt * zt))
    max_fault = traced(round(ik3 / 1000, 3), "kA", formula_id=FAULT_FORMULA_ID, formula="I''k3 = c·Un / (√3·|ZQ + KT·ZT|); ZQ = cQ·Un²/S''kQ; KT = 0.95·cmax/(1 + 0.6·xT)",
                       clause=fault.get("clause", ""), rules_hash=rules.hash,
                       inputs={"c_max": (c_max, ""), "c_Q": (c_q, ""), "Un": (round(un, 1), "V"),
                               "S_k_MV": (sk_max, "MVA", "connection point" if (source or {}).get("fault_mva_max") else f"rules {rules.ref} fault"),
                               "S_T": (chosen["kva"], "kVA"), "u_k": (chosen["z_pct"], "%"), "X/R": (xr, ""), "K_T": (round(kt, 4), "")})
    if fault.get("max_lv_terminal_fault_ka"):
        lim = float(fault["max_lv_terminal_fault_ka"])
        checks.append(Check(code="max_fault", subject=network.source_id, passed=ik3 / 1000 <= lim, value=round(ik3 / 1000, 2), limit=lim, unit="kA",
                            message=f"Maximum three-phase fault at the LV terminals is {ik3 / 1000:.1f} kA", clause=fault.get("clause", "")))

    # Minimum phase-neutral fault at every feeder end: Ik1 = √3·c·Un / |2·Z1 + Z0|, Dyn transformer (Z0T = Z1T, no Zq in Z0).
    feeder_map = feeders(network)
    feeder_of = {bid: name for name, ids in feeder_map.items() if name != "link" for bid in ids}
    rc = float(fault.get("contact_resistance_ohm", 0))
    z1 = {network.source_id: zq + zt}
    z0 = {network.source_id: zt}
    for n in order:
        if n == network.source_id:
            continue
        b = branch_of[n]
        cable = lib[b.conductor]
        zph = complex(cable.r_ohm_per_km, cable.x_ohm_per_km) * b.length_m / 1000
        zne = complex(cable.neutral_r_ohm_per_km, cable.neutral_x_ohm_per_km) * b.length_m / 1000
        z1[n] = z1[b.from_id] + zph
        z0[n] = z0[b.from_id] + zph + 3 * zne
    # Feeder fuses: the smallest standard rating that carries the feeder's design current, no larger than the
    # largest fuse that still protects the feeder's first conductor.
    mult = float(fault["min_fault_multiple_of_fuse"])
    ratings = sorted(float(x) for x in fault.get("fuse_ratings_a", []))
    design_of = {br.id: br.design_current_a for br in branches}
    fuse_of: dict[str, float | None] = {}
    by_id = {x.id: x for x in network.branches}
    for name, ids in feeder_map.items():
        if name == "link" or not ids:
            continue
        first_b = by_id[ids[0]]
        cable = lib[first_b.conductor]
        need_i = design_of[first_b.id]
        fuse = next((r for r in ratings if r >= need_i), None) if ratings else cable.fuse_a
        fuse_of[name] = fuse
        if ratings:
            ok = fuse is not None and (cable.fuse_a is None or fuse <= cable.fuse_a)
            checks.append(Check(code="fuse_protection", subject=first_b.id, passed=ok, value=fuse or need_i, limit=cable.fuse_a or (fuse or 0), unit="A",
                                message=(f"Feeder {name}: {fuse:.0f} A fuse for {need_i:.0f} A design current on {cable.code}"
                                         + (f" (largest protecting fuse {cable.fuse_a:.0f} A)" if cable.fuse_a else ""))
                                if fuse else f"Feeder {name}: no fuse in the rules carries {need_i:.0f} A",
                                clause=fault.get("clause", "")))
    ends: list[FeederEnd] = []
    for n in order:
        b = branch_of.get(n)
        if b is None or b.kind != "feeder" or any(g.edges[n, ch]["branch"].kind == "feeder" for ch in g.successors(n)):
            continue
        name = feeder_of.get(b.id, "link")
        fuse = fuse_of.get(name)
        ik1 = math.sqrt(3) * c_min * un / abs(2 * z1[n] + z0[n] + 3 * rc)
        need = mult * fuse if fuse else None
        ends.append(FeederEnd(node_id=n, feeder=name, min_fault_a=round(ik1, 1), fuse_a=fuse, required_a=need))
        if need is not None:
            checks.append(Check(code="min_fault", subject=n, passed=ik1 >= need, value=round(ik1, 0), limit=round(need, 0), unit="A",
                                message=f"Minimum phase-neutral fault at the end of {name} ({n}) is {ik1:.0f} A; the {fuse:.0f} A fuse needs {need:.0f} A",
                                clause=fault.get("clause", "")))

    return Analysis(branches=branches, nodes=nodes, customers=customers, feeder_ends=ends, demand_kva=demand_t,
                    transformer_kva=float(chosen["kva"]), max_fault_ka=max_fault, worst_vdrop_pct=worst_vd, checks=checks,
                    unverified=sorted(unverified))


def summary(a: Analysis) -> dict[str, Any]:
    failed = [c for c in a.checks if not c.passed]
    return {"passed": not failed, "failed": len(failed), "worst_vdrop_pct": a.worst_vdrop_pct.value,
            "max_loading_pct": max((b.loading_pct for b in a.branches), default=0), "transformer_kva": a.transformer_kva}

