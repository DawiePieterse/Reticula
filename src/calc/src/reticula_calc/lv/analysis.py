"""LV design checks (plan 2.4): voltage drop, thermal loading and fault level on each radial feeder.

The network (plan 2.1) and its connected loads (plan 2.2) are analysed per source. Every service connection becomes
a point on the feeder, so drops are found at every node and every connection.

**Voltage drop (Herman-Beta, per phase).** Each residential consumer's current is a beta variable of its load class
(α, β, c). A single-phase consumer on phase q changes the phase-p voltage at point E by its current times the impedance
of the path that E and the consumer share. That factor is (z_phase + z_neutral) for q = p. For the other phases it is
-½·z_neutral: their neutral current is 120° apart, so half of it opposes phase p's. Here z = R·cosφ + X·sinφ per km.
The drop at E is a weighted sum of independent beta variables. Its mean and variance add up, it is bounded by the
consumers' c, and its value at the confidence level is read from a beta fitted to those moments on those bounds.
Special loads, and three-phase loads split equally over the phases, add their current deterministically.

**Thermal loading.** The current in each section, per phase, is the Herman-Beta design current of the consumers beyond
it, plus their special loads. It is compared with the conductor's rating as normally installed (de-rating is plan 2.6).

**Fault level.** The minimum phase-to-neutral fault current at each point is V_phase / |Z_source + Z_phase + Z_neutral|,
with conductors at their hot resistance. The source is the rules file's transformer until Phase 3 sizes it.

Every input comes from the rules file. Placeholder values are listed in a warning, and in the traces of the results.
"""

from __future__ import annotations

import math
from dataclasses import dataclass, field
from typing import Literal

from pydantic import BaseModel
from scipy.stats import beta as beta_dist
from scipy.stats import norm

from ..calcs.admd import AdmdInputError, _cfg, _load_class
from ..rules import RulesError, RuleSet
from ..rules.loader import Conductor
from ..trace import Traced, traced
from .network import EPS_M, MAX_SAMPLES, Issue, LvNetwork

PHASES: tuple[str, ...] = ("R", "W", "B")
VDROP_ID = "lv.vdrop.herman-beta.v1"
VDROP_FORMULA = ("ΔV_p(E) = Σ_i m_i·I_i, m_i = Σ shared ℓ·(z_ph + z_n) on phase p, Σ shared ℓ·(−½·z_n) on the others, "
                 "z = R·cosφ + X·sinφ; I_i ~ beta(α_i, β_i)·c_i; ΔV at the confidence level of a beta fitted to its mean and "
                 "variance on [Σ m⁻·c, Σ m⁺·c]; plus deterministic special loads")
CURRENT_ID = "lv.current.herman-beta.v1"
CURRENT_FORMULA = "I_p = Herman-Beta design current of the consumers beyond the section on phase p + Σ special-load current"
FAULT_ID = "lv.fault.phase-neutral.v1"
FAULT_FORMULA = "I_f = V_phase / |Z_source + Σ ℓ·(R_hot + jX)_phase + Σ ℓ·(R_hot + jX)_neutral|; Z_source = z% · V_LL² / S"


class LoadAt(BaseModel):
    """A connected load, as plan 2.2 placed it."""

    load_id: str
    branch: str
    offset_m: float
    phase: Literal["R", "W", "B", "RWB"] | None
    kva: float
    kind: Literal["residential", "special"] = "residential"
    load_class: str | None = None
    label: str | None = None


class AnalyseRequest(BaseModel):
    rules: str
    network: LvNetwork
    loads: list[LoadAt]
    conductors: dict[str, str] = {}
    """Conductor per branch id; branches not listed use the rules file's default."""


class PointResult(BaseModel):
    id: str
    """A network node id, or the load id at a service connection."""
    kind: Literal["node", "connection"]
    feeder: str | None
    distance_m: float
    drop_pct: dict[str, float]
    worst_pct: float
    fault_a: float
    passes: bool


class BranchResult(BaseModel):
    id: str
    feeder: str | None
    conductor: str
    rating_a: float
    current_a: dict[str, float]
    utilisation_pct: float
    passes: bool


class FeederResult(BaseModel):
    feeder: str
    max_drop_pct: float
    max_drop_at: str
    max_utilisation_pct: float
    max_utilisation_branch: str
    min_fault_a: float
    min_fault_at: str
    passes: bool


class Analysis(BaseModel):
    rules_ref: str
    rules_hash: str
    clause: str
    limit_pct: float
    phase_voltage_v: float
    confidence_pct: float
    points: list[PointResult]
    branches: list[BranchResult]
    feeders: list[FeederResult]
    issues: list[Issue]
    worst_drop: Traced | None = None
    worst_current: Traced | None = None
    lowest_fault: Traced | None = None
    placeholders: list[str] = []
    """Inputs that are placeholders, in words; any result that uses them is not fit to submit."""


@dataclass
class _Load:
    phase: str  # R, W, B or RWB
    mu: float = 0.0
    var: float = 0.0
    c: float = 0.0
    det: float = 0.0  # deterministic current per phase it is on, A


@dataclass
class _Agg:
    """Per-phase sums over the consumers in a subtree."""

    mu: dict[str, float] = field(default_factory=lambda: dict.fromkeys(PHASES, 0.0))
    var: dict[str, float] = field(default_factory=lambda: dict.fromkeys(PHASES, 0.0))
    c: dict[str, float] = field(default_factory=lambda: dict.fromkeys(PHASES, 0.0))
    det: dict[str, float] = field(default_factory=lambda: dict.fromkeys(PHASES, 0.0))

    def add(self, other: _Agg, sign: float = 1.0) -> None:
        for p in PHASES:
            self.mu[p] += sign * other.mu[p]
            self.var[p] += sign * other.var[p]
            self.c[p] += sign * other.c[p]
            self.det[p] += sign * other.det[p]

    def put(self, load: _Load) -> None:
        for p in PHASES if load.phase == "RWB" else (load.phase,):
            self.mu[p] += load.mu
            self.var[p] += load.var
            self.c[p] += load.c
            self.det[p] += load.det


@dataclass
class _Vertex:
    id: str
    kind: Literal["node", "connection"]
    parent: int | None
    branch: str | None  # network branch of the edge from the parent
    length_m: float
    feeder: str | None
    zs: float = 0.0  # Σ ℓ·(z_ph + z_n), Ω
    zo: float = 0.0  # Σ ℓ·(−½·z_n), Ω
    loop: complex = 0j  # Σ ℓ·(Z_ph + Z_n), hot, Ω
    distance: float = 0.0
    own: _Agg = field(default_factory=_Agg)
    sub: _Agg = field(default_factory=_Agg)
    children: list[int] = field(default_factory=list)
    loads: list[str] = field(default_factory=list)


def _quantile(mean: float, var: float, lo: float, hi: float, conf: float) -> float:
    """The value at the confidence level of a beta on [lo, hi] with the given mean and variance."""
    span = hi - lo
    if var <= 0 or span <= 0:
        return mean
    m, v = (mean - lo) / span, var / (span * span)
    k = m * (1 - m) / v - 1
    if k <= 0 or not 0 < m < 1:
        # Moments a beta cannot hold on these bounds: a normal estimate, capped at the upper bound.
        return min(hi, mean + float(norm.ppf(conf)) * math.sqrt(var))
    return lo + span * float(beta_dist.ppf(conf, m * k, (1 - m) * k))


def analyse(req: AnalyseRequest, rules: RuleSet) -> Analysis:
    sec = rules.data.get("lv_design")
    if not sec:
        raise RulesError(f"rules {rules.ref} has no lv_design section; LV checks need eskom/0.6.0 or later")
    cfg = _cfg(rules)
    conf = float(cfg["diversity"]["confidence_pct"]) / 100
    v_ll = float(rules.data["voltage"]["lv_nominal_v"])
    v_ph = v_ll / math.sqrt(3)
    limit = float(rules.data["voltage"]["lv_max_drop_pct"])
    pf = float(sec["power_factor"])
    sin_phi = math.sqrt(max(0.0, 1 - pf * pf))
    src = sec["source"]
    z_base = v_ll**2 / (float(src["rating_kva"]) * 1000)
    z_src_mag = float(src["impedance_pct"]) / 100 * z_base
    xr = float(src["x_over_r"])
    z_src = complex(z_src_mag / math.sqrt(1 + xr * xr), z_src_mag * xr / math.sqrt(1 + xr * xr))
    temp = float(sec["conductor_temp_c"])
    alpha = sec["temperature_coefficients"]
    net = req.network
    issues: list[Issue] = []
    placeholders: list[str] = ["the LV source transformer (sized in Phase 3)", f"power factor {pf:g}",
                               f"the voltage drop limit of {limit:g} % (Eskom 240-70465489 not held)"]

    conductors: dict[str, Conductor] = {}

    def conductor(branch_id: str) -> Conductor:
        code = req.conductors.get(branch_id, sec["default_conductor"])
        if code not in conductors:
            conductors[code] = rules.conductor(code)
        return conductors[code]

    def r_hot(c: Conductor) -> float:
        if c.r_ac_ohm_per_km is not None:
            return c.r_ac_ohm_per_km
        coeff = float(alpha.get(c.material or "cu", alpha["cu"]))
        return c.r_ohm_per_km * (1 + coeff * (temp - 20))

    # ---- the loads, as currents ----
    no_class: list[str] = []
    loads_on: dict[str, list[LoadAt]] = {}
    for ld in req.loads:
        if ld.phase is None:
            continue  # not on a feeder: plan 2.2 already reported it
        loads_on.setdefault(ld.branch, []).append(ld)

    def as_current(ld: LoadAt) -> _Load:
        if ld.phase == "RWB":
            return _Load("RWB", det=ld.kva * 1000 / (3 * v_ph))
        lc = None
        if ld.kind == "residential" and ld.load_class:
            try:
                lc = _load_class(cfg, rules, ld.load_class, "score")
            except AdmdInputError:
                lc = None  # not a class of the design table (older rules, or a class marked unverified)
        if lc is not None:
            mu = lc.c_amps * lc.alpha / (lc.alpha + lc.beta)
            var = lc.c_amps**2 * lc.alpha * lc.beta / ((lc.alpha + lc.beta) ** 2 * (lc.alpha + lc.beta + 1))
            return _Load(ld.phase, mu=mu, var=var, c=lc.c_amps)
        if ld.kind == "residential":
            no_class.append(ld.label or ld.load_id)
        return _Load(ld.phase, det=ld.kva * 1000 / v_ph)

    # ---- the tree: network nodes plus a vertex at every service connection ----
    branches = {b.id: b for b in net.branches}
    sources = [n for n in net.nodes if n.kind == "source" and any(b.from_node == n.id and (b.feeder or b.kind == "link")
                                                                   for b in net.branches)]
    vertices: list[_Vertex] = []
    index: dict[str, int] = {}  # network node id -> vertex

    def add(v: _Vertex) -> int:
        vertices.append(v)
        if v.parent is not None:
            vertices[v.parent].children.append(len(vertices) - 1)
        return len(vertices) - 1

    out_of: dict[str, list[str]] = {}
    for b in net.branches:
        out_of.setdefault(b.from_node, []).append(b.id)

    for s in sources:
        index[s.id] = add(_Vertex(s.id, "node", None, None, 0.0, None))
        stack = [s.id]
        while stack:
            nid = stack.pop()
            for bid in out_of.get(nid, []):
                b = branches[bid]
                if b.feeder is None and b.kind != "link":
                    continue
                c = conductor(bid)
                z_drop = r_hot(c) * pf + c.x_ohm_per_km * sin_phi  # Ω/km, the same for the neutral (same size)
                z_loop = complex(2 * r_hot(c), 2 * c.x_ohm_per_km)
                prev, pos = index[nid], 0.0
                at_end: list[LoadAt] = []
                for ld in sorted(loads_on.get(bid, []), key=lambda x: (x.offset_m, x.load_id)):
                    at = min(max(ld.offset_m, 0.0), b.length_m)
                    if at >= b.length_m - EPS_M:
                        at_end.append(ld)
                        continue
                    if at - pos > EPS_M:
                        prev = _edge(vertices, add, prev, f"{bid}@{at:.2f}", "connection", bid, at - pos, b.feeder, z_drop, z_loop)
                        pos = at
                    vertices[prev].own.put(as_current(ld))
                    vertices[prev].loads.append(ld.load_id)
                v = _edge(vertices, add, prev, b.to_node, "node", bid, b.length_m - pos, b.feeder, z_drop, z_loop)
                for ld in at_end:
                    vertices[v].own.put(as_current(ld))
                    vertices[v].loads.append(ld.load_id)
                index[b.to_node] = v
                stack.append(b.to_node)

    # Subtree sums, children before parents.
    for i in range(len(vertices) - 1, -1, -1):
        v = vertices[i]
        v.sub.add(v.own)
        if v.parent is not None:
            vertices[v.parent].sub.add(v.sub)

    if no_class:
        issues.append(Issue(severity="warning", code="no_load_class",
                            message="Residential loads without a load class of the design table are taken at their ADMD, without Herman-Beta diversity.",
                            count=len(no_class), samples=no_class[:MAX_SAMPLES]))

    # ---- voltage drop and fault level at every vertex ----
    point_results: list[PointResult] = []
    worst = (-1.0, None, None, None)  # (pct, vertex, phase, parts)
    lowest = (math.inf, None, None)
    min_fault = sec.get("min_end_fault_a")
    for i, v in enumerate(vertices):
        if v.parent is None:
            continue  # the source itself
        path = _path(vertices, i)
        sums = {p: [0.0, 0.0, 0.0, 0.0, 0.0] for p in PHASES}  # mean, var, lo, hi, det
        for j, vi in enumerate(path):
            here = _Agg()
            here.add(vertices[vi].sub)
            if j + 1 < len(path):
                here.add(vertices[path[j + 1]].sub, -1.0)  # consumers whose path leaves E's path here
            for p in PHASES:
                acc = sums[p]
                for q in PHASES:
                    z = vertices[vi].zs if q == p else vertices[vi].zo
                    acc[0] += z * here.mu[q]
                    acc[1] += z * z * here.var[q]
                    acc[2 if z < 0 else 3] += z * here.c[q]
                    acc[4] += z * here.det[q]
        drops, parts = {}, {}
        for p in PHASES:
            mean, var, lo, hi, det = sums[p]
            stoch = _quantile(mean, var, lo, hi, conf)
            drops[p] = (stoch + det) / v_ph * 100
            parts[p] = (mean, var, lo, hi, det, stoch)
        worst_p = max(PHASES, key=lambda p: drops[p])
        fault = v_ph / abs(z_src + v.loop)
        if drops[worst_p] > worst[0]:
            worst = (drops[worst_p], i, worst_p, parts[worst_p])
        if fault < lowest[0]:
            lowest = (fault, i, v.loop)
        ok = drops[worst_p] <= limit and (min_fault is None or fault >= float(min_fault))
        result = {"feeder": v.feeder, "distance_m": round(v.distance, 2), "drop_pct": {p: round(d, 3) for p, d in drops.items()},
                  "worst_pct": round(drops[worst_p], 3), "fault_a": round(fault, 1), "passes": ok}
        if v.kind == "node":
            point_results.append(PointResult(id=v.id, kind="node", **result))
        point_results += [PointResult(id=load_id, kind="connection", **result) for load_id in v.loads]

    # ---- thermal loading per network branch, the worst section of it ----
    branch_results: dict[str, BranchResult] = {}
    worst_current = (-1.0, None, None, None)
    for i, v in enumerate(vertices):
        if v.branch is None:
            continue
        c = conductor(v.branch)
        currents = {}
        for p in PHASES:
            stoch = _quantile(v.sub.mu[p], v.sub.var[p], 0.0, v.sub.c[p], conf)
            currents[p] = stoch + v.sub.det[p]
        util = max(currents.values()) / c.rating_a * 100
        prev = branch_results.get(v.branch)
        if prev is None or util > prev.utilisation_pct:
            branch_results[v.branch] = BranchResult(
                id=v.branch, feeder=v.feeder, conductor=c.code, rating_a=c.rating_a,
                current_a={p: round(x, 2) for p, x in currents.items()}, utilisation_pct=round(util, 1), passes=util <= 100)
        if util > worst_current[0]:
            p = max(PHASES, key=lambda q: currents[q])
            worst_current = (util, v.branch, p, (v.sub.mu[p], v.sub.var[p], v.sub.c[p], v.sub.det[p], currents[p], c))

    # ---- per feeder ----
    feeders: list[FeederResult] = []
    for f in sorted({r.feeder for r in point_results if r.feeder}):
        pts = [r for r in point_results if r.feeder == f]
        brs = [b for b in branch_results.values() if b.feeder == f]
        d = max(pts, key=lambda r: r.worst_pct)
        lo_f = min(pts, key=lambda r: r.fault_a)
        u = max(brs, key=lambda b: b.utilisation_pct) if brs else None
        feeders.append(FeederResult(
            feeder=f, max_drop_pct=d.worst_pct, max_drop_at=d.id, max_utilisation_pct=u.utilisation_pct if u else 0.0,
            max_utilisation_branch=u.id if u else "", min_fault_a=lo_f.fault_a, min_fault_at=lo_f.id,
            passes=all(r.passes for r in pts) and all(b.passes for b in brs)))

    over = [r for r in point_results if r.worst_pct > limit]
    if over:
        issues.append(Issue(severity="error", code="drop_over_limit",
                            message=f"Voltage drop is over {limit:g} % at these points. Use a larger conductor, shorten the feeder or add a source.",
                            count=len(over), samples=[r.id for r in sorted(over, key=lambda r: -r.worst_pct)[:MAX_SAMPLES]]))
    hot = [b for b in branch_results.values() if not b.passes]
    if hot:
        issues.append(Issue(severity="error", code="overload",
                            message="Branches carry more than their conductor's rating. Use a larger conductor or split the feeder.",
                            count=len(hot), samples=[b.id for b in sorted(hot, key=lambda b: -b.utilisation_pct)[:MAX_SAMPLES]]))
    if sec.get("min_end_fault_a") is None:
        issues.append(Issue(severity="warning", code="fault_not_checked",
                            message="Fault levels are reported but not checked: the rules file sets no minimum (LV protection settings not held)."))
    for c in conductors.values():
        if c.placeholder:
            placeholders.append(f"{c.code} {', '.join(c.placeholder)}")
    issues.append(Issue(severity="warning", code="placeholders",
                        message="These results use placeholder values and are not fit to submit: " + "; ".join(placeholders) + "."))

    clause = sec.get("clause", "")
    return Analysis(
        rules_ref=rules.ref, rules_hash=rules.hash, clause=clause, limit_pct=limit, phase_voltage_v=round(v_ph, 2),
        confidence_pct=conf * 100, points=point_results, branches=list(branch_results.values()), feeders=feeders, issues=issues,
        worst_drop=_drop_trace(rules, vertices, worst, v_ph, conf, pf, limit, clause) if worst[1] is not None else None,
        worst_current=_current_trace(rules, worst_current, conf) if worst_current[1] is not None else None,
        lowest_fault=_fault_trace(rules, vertices, lowest, z_src, src, v_ph) if lowest[1] is not None else None,
        placeholders=placeholders,
    )


def _edge(vertices: list[_Vertex], add, parent: int, vid: str, kind, branch: str, length_m: float, feeder: str | None,
          z_drop: float, z_loop: complex) -> int:
    p = vertices[parent]
    km = max(length_m, 0.0) / 1000
    return add(_Vertex(vid, kind, parent, branch, length_m, feeder, zs=p.zs + 2 * z_drop * km, zo=p.zo - 0.5 * z_drop * km,
                       loop=p.loop + z_loop * km, distance=p.distance + max(length_m, 0.0)))


def _path(vertices: list[_Vertex], i: int) -> list[int]:
    out = []
    while i is not None:
        out.append(i)
        i = vertices[i].parent
    return out[::-1]


def _drop_trace(rules: RuleSet, vertices, worst, v_ph: float, conf: float, pf: float, limit: float, clause: str) -> Traced:
    pct, vi, phase, (mean, var, lo, hi, det, stoch) = worst
    return traced(pct, "%", formula_id=VDROP_ID, formula=VDROP_FORMULA, clause=clause, rules_hash=rules.hash, inputs={
        "point": (vertices[vi].id, "", "worst point"), "phase": (phase, "", "worst phase"),
        "mean": (round(mean, 4), "V", "Σ m·μ"), "sd": (round(math.sqrt(var), 4), "V", "√Σ m²·σ²"),
        "lower": (round(lo, 4), "V", "Σ m⁻·c"), "upper": (round(hi, 4), "V", "Σ m⁺·c"),
        "at_confidence": (round(stoch, 4), "V", f"beta at {conf * 100:g} %"), "special": (round(det, 4), "V", "special and three-phase loads"),
        "V_phase": (round(v_ph, 2), "V", f"rules {rules.ref} voltage"), "cos_phi": (pf, "", f"rules {rules.ref} lv_design (placeholder)"),
        "limit": (limit, "%", f"rules {rules.ref} voltage (placeholder)"),
    })


def _current_trace(rules: RuleSet, worst, conf: float) -> Traced:
    util, branch, phase, (mu, var, c_sum, det, current, cond) = worst
    flag = " (placeholder)" if "rating_a" in cond.placeholder else ""
    return traced(current, "A", formula_id=CURRENT_ID, formula=CURRENT_FORMULA, clause=cond.rating_clause or cond.clause, rules_hash=rules.hash,
                  inputs={"branch": (branch, "", "most loaded"), "phase": (phase, "", "most loaded phase"),
                          "mean": (round(mu, 4), "A", "Σ μ"), "sd": (round(math.sqrt(var), 4), "A", "√Σ σ²"), "C": (c_sum, "A", "Σ c"),
                          "special": (round(det, 4), "A", "special and three-phase loads"),
                          "rating": (cond.rating_a, "A", f"rules {rules.ref} conductor {cond.code}{flag}"),
                          "utilisation": (round(util, 2), "%", "I / rating"), "confidence": (conf * 100, "%", f"rules {rules.ref} diversity")})


def _fault_trace(rules: RuleSet, vertices, lowest, z_src: complex, src: dict, v_ph: float) -> Traced:
    fault, vi, loop = lowest
    return traced(fault, "A", formula_id=FAULT_ID, formula=FAULT_FORMULA, clause=rules.data["lv_design"].get("clause", ""), rules_hash=rules.hash,
                  inputs={"point": (vertices[vi].id, "", "lowest fault level"), "V_phase": (round(v_ph, 2), "V", f"rules {rules.ref} voltage"),
                          "R_source": (round(z_src.real, 5), "Ω", f"rules {rules.ref} lv_design.source (placeholder)"),
                          "X_source": (round(z_src.imag, 5), "Ω", f"{src['rating_kva']} kVA, {src['impedance_pct']} %, X/R {src['x_over_r']}"),
                          "R_loop": (round(loop.real, 5), "Ω", "phase + neutral, hot"), "X_loop": (round(loop.imag, 5), "Ω", "phase + neutral")})
