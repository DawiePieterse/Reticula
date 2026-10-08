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

**Service cables.** A load may carry the drop in its own service cable (plan 2.2, design/services.py). The drop at the
customer is then the drop at its connection, on its phase (the worst phase for a three-phase load), plus that.

**Fault level.** The minimum phase-to-neutral fault current at each point is V_phase / |Z_source + Z_phase + Z_neutral|,
with conductors at their hot resistance. The source is the rules file's transformer until Phase 3 sizes it.

**Protection.** Where the rules file sets `lv_design.protection`, each feeder gets a fuse: the smallest standard rating
I_n at or above its design current I_B (the most loaded section's worst phase), which must not be above the lowest
rating I_z of its conductors. Every point of the feeder must then see a fault current of at least k·I_n.

Every input comes from the rules file. Placeholder values are listed in a warning, and in the traces of the results.
"""

from __future__ import annotations

import math
from dataclasses import dataclass, field
from typing import Literal

from pydantic import BaseModel
from scipy.stats import beta as beta_dist
from scipy.stats import norm

from ..calcs.admd import AdmdInputError, LoadClassInfo, _cfg, _load_class, _moments
from ..issues import Issue, issue
from ..rules import RulesError, RuleSet
from ..rules.loader import Conductor
from ..trace import Traced, traced
from .conductors import _source
from .network import EPS_M, LvNetwork

PHASES: tuple[str, ...] = ("R", "W", "B")
VDROP_ID = "lv.vdrop.herman-beta.v1"
VDROP_FORMULA = ("ΔV_p(E) = Σ_i m_i·I_i, m_i = Σ shared ℓ·(z_ph + z_n) on phase p, Σ shared ℓ·(−½·z_n) on the others, "
                 "z = R·cosφ + X·sinφ; I_i ~ beta(α_i, β_i)·c_i; ΔV at the confidence level of a beta fitted to its mean and "
                 "variance on [Σ m⁻·c, Σ m⁺·c]; plus deterministic special loads")
CURRENT_ID = "lv.current.herman-beta.v1"
CURRENT_FORMULA = "I_p = Herman-Beta design current of the consumers beyond the section on phase p + Σ special-load current"
FAULT_ID = "lv.fault.phase-neutral.v1"
FAULT_FORMULA = "I_f = V_phase / |Z_source + Σ ℓ·(R_hot + jX)_phase + Σ ℓ·(R_hot + jX)_neutral|; Z_source = z% · V_LL² / S"
CUSTOMER_ID = "lv.vdrop.customer.v1"
CUSTOMER_FORMULA = "ΔV_customer = ΔV_p(connection), p the customer's phase (worst for three-phase) + ΔV_service"
FUSE_ID = "lv.protection.fuse.v1"
FUSE_FORMULA = "I_n = min{standard ratings ≥ I_B}; I_n ≤ I_z = min conductor rating on the feeder; I_f,min ≥ k·I_n"


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
    service_pct: float = 0.0
    """The drop in the load's service cable at its own demand, %; added to the drop at its connection."""


class SourceIn(BaseModel):
    """A sized transformer feeding the LV network (Phase 3.1)."""

    rating_kva: float
    impedance_pct: float
    x_over_r: float


class AnalyseRequest(BaseModel):
    rules: str
    network: LvNetwork
    loads: list[LoadAt]
    conductors: dict[str, str] = {}
    """Conductor per branch id; branches not listed use the rules file's default."""
    sources: dict[str, SourceIn] = {}
    """The transformer at each source, by source node id or label; sources not listed use the rules file's placeholder."""
    ratings: dict[str, float] = {}
    """Rating per branch id where it differs from the conductor's as normally installed (de-rated, plan 2.6), A."""


class PointResult(BaseModel):
    id: str
    """A network node id, or the load id at a service connection."""
    kind: Literal["node", "connection"]
    feeder: str | None
    distance_m: float
    drop_pct: dict[str, float]
    worst_pct: float
    """At a node, the worst phase; at a connection, the drop at the customer: its phase's drop plus service_pct."""
    fault_a: float
    passes: bool
    service_pct: float | None = None
    """At a connection, the drop in the service cable."""


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
    design_current_a: float | None = None
    """I_B: the most loaded section's worst phase current, with protection set."""
    conductor_rating_a: float | None = None
    """I_z: the lowest rating of the feeder's conductors."""
    fuse_a: float | None = None
    """I_n: the smallest standard fuse rating at or above I_B; None when none is."""
    min_fault_required_a: float | None = None
    """k·I_n, the least fault current every point of the feeder must see."""
    protected: bool | None = None
    """I_B ≤ I_n ≤ I_z and the least fault at least k·I_n; None when the rules set no protection."""


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
    worst_customer: Traced | None = None
    """The drop at the customer, service cable included, where it is largest."""
    protection: Traced | None = None
    """The feeder whose fuse is least well covered: the lowest ratio of its least fault to k·I_n."""
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
    root: int = 0  # the source vertex this one is fed from
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
    z_src = source_impedance(v_ll, float(src["rating_kva"]), float(src["impedance_pct"]), float(src["x_over_r"]))
    temp = float(sec["conductor_temp_c"])
    alpha = sec["temperature_coefficients"]
    net = req.network
    issues: list[Issue] = []
    volt = rules.data["voltage"]
    limit_placeholder = "lv_max_drop_pct" in volt.get("placeholder", ["lv_max_drop_pct"])
    placeholders: list[str] = [f"power factor {pf:g}"]
    if limit_placeholder:
        placeholders.append(f"the voltage drop limit of {limit:g} % (Eskom 240-70465489 not held)")
    root_z: dict[int, complex] = {}
    root_src: dict[int, tuple[complex, dict, bool]] = {}  # impedance, settings, sized
    unsized: list[str] = []

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

    classes: dict[str, LoadClassInfo | None] = {}

    def load_class(code: str) -> LoadClassInfo | None:
        if code not in classes:
            try:
                classes[code] = _load_class(cfg, rules, code, "score")
            except AdmdInputError:
                classes[code] = None  # not a class of the design table (older rules, or a class marked unverified)
        return classes[code]

    def as_current(ld: LoadAt) -> _Load:
        if ld.phase == "RWB":
            return _Load("RWB", det=ld.kva * 1000 / (3 * v_ph))
        lc = load_class(ld.load_class) if ld.kind == "residential" and ld.load_class else None
        if lc is not None:
            mu, sd = _moments(lc.alpha, lc.beta, lc.c_amps)
            return _Load(ld.phase, mu=mu, var=sd * sd, c=lc.c_amps)
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
        vertices[index[s.id]].root = index[s.id]
        given = req.sources.get(s.id) or (req.sources.get(s.label) if s.label else None)
        if given is None:
            unsized.append(s.label or s.id)
        root_z[index[s.id]] = source_impedance(v_ll, given.rating_kva, given.impedance_pct, given.x_over_r) if given else z_src
        root_src[index[s.id]] = (root_z[index[s.id]], given.model_dump() if given else src, given is not None)
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
        issues.append(issue("warning", "no_load_class",
                            "Residential loads without a load class of the design table are taken at their ADMD, without Herman-Beta diversity.",
                            no_class))

    # ---- voltage drop and fault level at every vertex ----
    point_results: list[PointResult] = []
    worst = (-1.0, None, None, None)  # (pct, vertex, phase, parts)
    lowest = (math.inf, None, None)
    min_fault = sec.get("min_end_fault_a")
    load_by_id = {ld.load_id: ld for ld in req.loads}
    worst_customer: tuple = (-1.0, None, None, 0.0)  # (pct, vertex, load, drop at the connection)
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
        fault = v_ph / abs(root_z[v.root] + v.loop)
        if drops[worst_p] > worst[0]:
            worst = (drops[worst_p], i, worst_p, parts[worst_p])
        if fault < lowest[0]:
            lowest = (fault, i, v.loop)
        fault_ok = min_fault is None or fault >= float(min_fault)
        result = {"feeder": v.feeder, "distance_m": round(v.distance, 2), "drop_pct": {p: round(d, 3) for p, d in drops.items()},
                  "fault_a": round(fault, 1)}
        if v.kind == "node":
            point_results.append(PointResult(id=v.id, kind="node", worst_pct=round(drops[worst_p], 3), passes=drops[worst_p] <= limit and fault_ok,
                                             **result))
        for load_id in v.loads:
            ld = load_by_id[load_id]
            at_customer = (drops[worst_p] if ld.phase == "RWB" else drops[ld.phase]) + ld.service_pct
            point_results.append(PointResult(id=load_id, kind="connection", worst_pct=round(at_customer, 3), passes=at_customer <= limit and fault_ok,
                                             service_pct=round(ld.service_pct, 3), **result))
            if at_customer > worst_customer[0]:
                worst_customer = (at_customer, i, ld, drops[worst_p] if ld.phase == "RWB" else drops[ld.phase])

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
        rating = req.ratings.get(v.branch, c.rating_a)
        util = max(currents.values()) / rating * 100
        prev = branch_results.get(v.branch)
        if prev is None or util > prev.utilisation_pct:
            branch_results[v.branch] = BranchResult(
                id=v.branch, feeder=v.feeder, conductor=c.code, rating_a=round(rating, 1),
                current_a={p: round(x, 2) for p, x in currents.items()}, utilisation_pct=round(util, 1), passes=util <= 100)
        if util > worst_current[0]:
            p = max(PHASES, key=lambda q: currents[q])
            worst_current = (util, v.branch, p, (v.sub.mu[p], v.sub.var[p], v.sub.c[p], v.sub.det[p], currents[p], c, rating,
                                                 v.branch in req.ratings))

    # ---- per feeder, with its fuse ----
    prot = sec.get("protection")
    fuse_ratings = sorted(float(x) for x in prot["fuse_ratings_a"]) if prot else []
    k_fault = float(prot["min_fault_multiple"]) if prot else 0.0
    feeders: list[FeederResult] = []
    least_covered: tuple = (math.inf, None)  # (least fault / required, feeder)
    for f in sorted({r.feeder for r in point_results if r.feeder}):
        pts = [r for r in point_results if r.feeder == f]
        brs = [b for b in branch_results.values() if b.feeder == f]
        d = max(pts, key=lambda r: r.worst_pct)
        lo_f = min(pts, key=lambda r: r.fault_a)
        u = max(brs, key=lambda b: b.utilisation_pct) if brs else None
        fr = FeederResult(
            feeder=f, max_drop_pct=d.worst_pct, max_drop_at=d.id, max_utilisation_pct=u.utilisation_pct if u else 0.0,
            max_utilisation_branch=u.id if u else "", min_fault_a=lo_f.fault_a, min_fault_at=lo_f.id,
            passes=all(r.passes for r in pts) and all(b.passes for b in brs))
        if prot and brs:
            i_b = max(max(b.current_a.values()) for b in brs)
            i_z = min(b.rating_a for b in brs)
            i_n = next((r for r in fuse_ratings if r >= i_b - 1e-9), None)
            fr.design_current_a, fr.conductor_rating_a, fr.fuse_a = round(i_b, 2), i_z, i_n
            covered = 0.0  # no fuse carries the feeder: the least covered of all
            if i_n is not None:
                fr.min_fault_required_a = round(k_fault * i_n, 1)
                for r in pts:
                    if r.fault_a < fr.min_fault_required_a:
                        r.passes = False
                covered = lo_f.fault_a / fr.min_fault_required_a
            fr.protected = i_n is not None and i_n <= i_z + 1e-9 and covered >= 1.0
            fr.passes = fr.passes and fr.protected
            if covered < least_covered[0]:
                least_covered = (covered, fr)
        feeders.append(fr)

    over = [r for r in point_results if r.worst_pct > limit]
    if over:
        issues.append(issue("error", "drop_over_limit",
                            f"Voltage drop is over {limit:g} % at these points. Use a larger conductor, shorten the feeder or add a source.",
                            [r.id for r in sorted(over, key=lambda r: -r.worst_pct)]))
    hot = [b for b in branch_results.values() if not b.passes]
    if hot:
        issues.append(issue("error", "overload",
                            "Branches carry more than their conductor's rating. Use a larger conductor or split the feeder.",
                            [b.id for b in sorted(hot, key=lambda b: -b.utilisation_pct)]))
    if prot:
        unfused = [f.feeder for f in feeders if f.design_current_a is not None and (f.fuse_a is None or f.fuse_a > f.conductor_rating_a + 1e-9)]
        if unfused:
            issues.append(issue("error", "no_fuse", "No standard fuse rating carries these feeders' design current without exceeding their "
                                "conductor's rating. Use a larger conductor or split the feeder.", unfused))
        uncovered = [f.feeder for f in feeders if f.min_fault_required_a is not None and f.min_fault_a < f.min_fault_required_a]
        if uncovered:
            issues.append(issue("error", "fault_below_fuse", f"The far end of these feeders sees less than {k_fault:g} times the fuse "
                                "rating, so the fuse may not clear a fault there. Use a larger conductor or shorten the feeder.", uncovered))
        if prot.get("placeholder"):
            placeholders.append(f"LV feeder fuses: {', '.join(prot['placeholder'])} (engineering assumptions; Eskom 240-57649065 not held)")
    elif min_fault is None:
        issues.append(Issue(severity="warning", code="fault_not_checked",
                            message="Fault levels are reported but not checked: the rules file sets no minimum (LV protection settings not held)."))
    if unsized:
        placeholders.insert(0, "the LV source transformer of " + ", ".join(unsized) + " (not sized: the rules file's placeholder)")
    for c in conductors.values():
        if c.placeholder:
            placeholders.append(f"{c.code} {', '.join(c.placeholder)}")
    issues.append(Issue(severity="warning", code="placeholders",
                        message="These results use placeholder values and are not fit to submit: " + "; ".join(placeholders) + "."))

    clause = sec.get("clause", "")
    return Analysis(
        rules_ref=rules.ref, rules_hash=rules.hash, clause=clause, limit_pct=limit, phase_voltage_v=round(v_ph, 2),
        confidence_pct=conf * 100, points=point_results, branches=list(branch_results.values()), feeders=feeders, issues=issues,
        worst_drop=_drop_trace(rules, vertices, worst, v_ph, conf, pf, limit, limit_placeholder, clause) if worst[1] is not None else None,
        worst_current=_current_trace(rules, worst_current, conf) if worst_current[1] is not None else None,
        lowest_fault=_fault_trace(rules, vertices, lowest, root_src[vertices[lowest[1]].root], v_ph, clause) if lowest[1] is not None else None,
        worst_customer=_customer_trace(rules, vertices, worst_customer, limit, limit_placeholder, clause) if worst_customer[1] is not None else None,
        protection=_fuse_trace(rules, least_covered[1], prot, k_fault) if least_covered[1] is not None else None,
        placeholders=placeholders,
    )


def _edge(vertices: list[_Vertex], add, parent: int, vid: str, kind, branch: str, length_m: float, feeder: str | None,
          z_drop: float, z_loop: complex) -> int:
    p = vertices[parent]
    km = max(length_m, 0.0) / 1000
    return add(_Vertex(vid, kind, parent, branch, length_m, feeder, root=p.root, zs=p.zs + 2 * z_drop * km, zo=p.zo - 0.5 * z_drop * km,
                       loop=p.loop + z_loop * km, distance=p.distance + max(length_m, 0.0)))


def _path(vertices: list[_Vertex], i: int) -> list[int]:
    out = []
    while i is not None:
        out.append(i)
        i = vertices[i].parent
    return out[::-1]


def _limit_source(rules: RuleSet, placeholder: bool) -> str:
    return f"rules {rules.ref} voltage" + (" (placeholder)" if placeholder else "")


def _drop_trace(rules: RuleSet, vertices, worst, v_ph: float, conf: float, pf: float, limit: float, limit_placeholder: bool,
                clause: str) -> Traced:
    pct, vi, phase, (mean, var, lo, hi, det, stoch) = worst
    return traced(pct, "%", formula_id=VDROP_ID, formula=VDROP_FORMULA, clause=clause, rules_hash=rules.hash, inputs={
        "point": (vertices[vi].id, "", "worst point"), "phase": (phase, "", "worst phase"),
        "mean": (round(mean, 4), "V", "Σ m·μ"), "sd": (round(math.sqrt(var), 4), "V", "√Σ m²·σ²"),
        "lower": (round(lo, 4), "V", "Σ m⁻·c"), "upper": (round(hi, 4), "V", "Σ m⁺·c"),
        "at_confidence": (round(stoch, 4), "V", f"beta at {conf * 100:g} %"), "special": (round(det, 4), "V", "special and three-phase loads"),
        "V_phase": (round(v_ph, 2), "V", f"rules {rules.ref} voltage"), "cos_phi": (pf, "", f"rules {rules.ref} lv_design (placeholder)"),
        "limit": (limit, "%", _limit_source(rules, limit_placeholder)),
    })


def _customer_trace(rules: RuleSet, vertices, worst, limit: float, limit_placeholder: bool, clause: str) -> Traced:
    pct, vi, ld, at_connection = worst
    return traced(pct, "%", formula_id=CUSTOMER_ID, formula=CUSTOMER_FORMULA, clause=clause, rules_hash=rules.hash, inputs={
        "load": (ld.label or ld.load_id, "", "worst customer"), "connection": (vertices[vi].id, "", "service connection"),
        "phase": (ld.phase, "", "the customer's phase"), "drop_connection": (round(at_connection, 3), "%", VDROP_ID),
        "drop_service": (round(ld.service_pct, 3), "%", "service cable at the customer's own demand (design/services.py)"),
        "limit": (limit, "%", _limit_source(rules, limit_placeholder))})


def _fuse_trace(rules: RuleSet, f: FeederResult, prot: dict, k: float) -> Traced:
    src = f"rules {rules.ref} lv_design.protection" + (" (placeholder)" if prot.get("placeholder") else "")
    return traced(f.fuse_a or 0.0, "A", formula_id=FUSE_ID, formula=FUSE_FORMULA, clause=prot.get("clause", ""), rules_hash=rules.hash, inputs={
        "feeder": (f.feeder, "", "least covered"), "I_B": (f.design_current_a, "A", CURRENT_ID),
        "I_z": (f.conductor_rating_a, "A", "lowest conductor rating on the feeder"),
        "ratings": (", ".join(f"{float(x):g}" for x in sorted(prot["fuse_ratings_a"])), "A", src),
        "k": (k, "", src), "I_f_min": (f.min_fault_a, "A", f"{FAULT_ID} at {f.min_fault_at}"),
        "required": (f.min_fault_required_a if f.min_fault_required_a is not None else "no fuse", "A", "k·I_n")})


def _current_trace(rules: RuleSet, worst, conf: float) -> Traced:
    util, branch, phase, (mu, var, c_sum, det, current, cond, rating, derated) = worst
    return traced(current, "A", formula_id=CURRENT_ID, formula=CURRENT_FORMULA, clause=cond.rating_clause or cond.clause, rules_hash=rules.hash,
                  inputs={"branch": (branch, "", "most loaded"), "phase": (phase, "", "most loaded phase"),
                          "mean": (round(mu, 4), "A", "Σ μ"), "sd": (round(math.sqrt(var), 4), "A", "√Σ σ²"), "C": (c_sum, "A", "Σ c"),
                          "special": (round(det, 4), "A", "special and three-phase loads"),
                          "rating": (round(rating, 1), "A", f"de-rated for installation (plan 2.6) from {cond.rating_a:g} A" if derated
                                     else _source(rules, cond, "rating_a")),
                          "utilisation": (round(util, 2), "%", "I / rating"), "confidence": (conf * 100, "%", f"rules {rules.ref} diversity")})


def source_impedance(v_ll: float, rating_kva: float, impedance_pct: float, x_over_r: float) -> complex:
    """A transformer's impedance referred to the LV side, Ω: z% · V_LL² / S, split by X/R."""
    z = impedance_pct / 100 * v_ll**2 / (rating_kva * 1000)
    return complex(z / math.sqrt(1 + x_over_r * x_over_r), z * x_over_r / math.sqrt(1 + x_over_r * x_over_r))


def _fault_trace(rules: RuleSet, vertices, lowest, source: tuple[complex, dict, bool], v_ph: float, clause: str) -> Traced:
    fault, vi, loop = lowest
    z_src, src, sized = source
    how = "sized transformer (plan 3.1)" if sized else f"rules {rules.ref} lv_design.source (placeholder)"
    return traced(fault, "A", formula_id=FAULT_ID, formula=FAULT_FORMULA, clause=clause, rules_hash=rules.hash,
                  inputs={"point": (vertices[vi].id, "", "lowest fault level"), "V_phase": (round(v_ph, 2), "V", f"rules {rules.ref} voltage"),
                          "R_source": (round(z_src.real, 5), "Ω", how),
                          "X_source": (round(z_src.imag, 5), "Ω", f"{src['rating_kva']} kVA, {src['impedance_pct']} %, X/R {src['x_over_r']}"),
                          "R_loop": (round(loop.real, 5), "Ω", "phase + neutral, hot"), "X_loop": (round(loop.imag, 5), "Ω", "phase + neutral")})
