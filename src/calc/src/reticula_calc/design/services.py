"""Service cables (plans 2.2, 2.4, 2.5): each customer's service conductor, its voltage drop, and overhead service spans.

**Conductor.** Each service gets the first conductor of the rules file's `services.<construction>` list for its kind
of load (single-phase, or three-phase above `lv_loads.single_phase_max_kva`) that carries the customer's own demand
and keeps the service drop within `voltage.lv_service_max_drop_pct`. Where none does, it gets the last, and fails.

**Demand.** A service carries one customer, so there is no diversity: a residential customer's design current is its
own Herman-Beta current at the design confidence, c·B⁻¹(conf; α, β) for its load class (the Red Book's "undiversified
ADMD"). A load without a class, or a special load, takes its kVA over the phase voltage; a three-phase load a third of
it on each phase.

**Drop.** ΔV = k·I·ℓ·(R·cosφ + X·sinφ), with k = 2 for a single-phase service (out on the phase, back on a neutral of
the same size) and k = 1 per phase for a balanced three-phase one, and R at the conductor's operating temperature. The
LV analysis adds it to the drop at the service connection (lv/analysis.py), so the voltage limit holds at the meter.

**Overhead spans.** Services are strung to the supplier's installation sag at its maximum working tension
T = `tension_pct` × breaking load, so a span's sag is s = w·L²/(8·T) for the cable's weight w. A span runs from the
feeder pole, where the service clamp sits `below_line_m` under the line's attachment, or from a service pole's top
attachment, to the next service pole or to `house_attachment_m` on the building. Over flat ground its height is the
parabola y(x) = h_a + (h_b − h_a)·x/L − 4s·(x/L)(1 − x/L), whose lowest point must clear `min_clearance_m`. Where a
service is longer than `max_span_m`, or a span's low point is too low, service poles of `pole_height_m`, set as LV
poles, go along the service at equal spacing: the fewest that make every span clear, with no span shorter than the LV
line's `min_span_m`.

Every value comes from the rules file; its `services.placeholder` keys are reported as placeholders.
"""

from __future__ import annotations

import itertools
import math
from typing import Literal

from pydantic import BaseModel
from scipy.stats import beta as beta_dist

from ..calcs.admd import AdmdInputError, _cfg, _load_class
from ..issues import Issue, issue
from ..lv.loads import LoadAllocation
from ..rules import RuleSet
from ..rules.loader import Conductor
from ..trace import Traced, traced
from .overhead import G, OverheadResult, _level

Construction = Literal["overhead", "underground"]

DROP_ID = "lv.service.drop.v1"
DROP_FORMULA = "ΔV% = k·I·ℓ·(R_hot·cosφ + X·sinφ) / V_phase · 100; k = 2 single-phase, 1 three-phase; I = c·B⁻¹(conf; α, β) or kVA / V"
SPAN_ID = "oh.service.span.v1"
SPAN_FORMULA = ("s = w·L² / (8·T), T = p·breaking load; y_min = min over x of h_a + (h_b − h_a)·x/L − 4s·(x/L)(1 − x/L); "
                "n service poles at equal spacing, the fewest with every span ≤ L_max and y_min ≥ clearance")


class ServiceSpan(BaseModel):
    length_m: float
    sag_m: float
    low_m: float
    """Height of the lowest point above ground."""
    passes: bool


class Service(BaseModel):
    load_id: str
    label: str | None = None
    feeder: str | None = None
    phases: Literal[1, 3]
    conductor: str
    length_m: float
    current_a: float
    """The customer's own design current, per phase."""
    rating_a: float
    drop_pct: float
    passes: bool
    """Carries the current and keeps the drop within the service limit."""
    spans: list[ServiceSpan] = []
    poles: list[tuple[float, float]] = []
    """Service poles along an overhead service, lon/lat."""
    clearance_m: float | None = None
    """The lowest point of any of its spans."""
    clears: bool | None = None


class ServiceResult(BaseModel):
    clause: str
    index: str | None
    construction: Construction
    limit_pct: float
    services: list[Service]
    service_poles: int = 0
    pole_height_m: float | None = None
    min_clearance_m: float | None = None
    issues: list[Issue]
    worst_drop: Traced | None = None
    worst_span: Traced | None = None
    placeholders: list[str] = []


class ServiceSizer:
    """The rules a service is sized by: its conductor lists, the drop limit, the power factor and the design confidence."""

    def __init__(self, rules: RuleSet, construction: Construction):
        self.rules = rules
        sec = rules.data["services"]
        volt = rules.data["voltage"]
        self.limit = float(volt["lv_service_max_drop_pct"])
        lvd = rules.section("lv_design", "eskom/0.6.0")
        self.pf = float(lvd["power_factor"])
        self.sin_phi = math.sqrt(max(0.0, 1 - self.pf * self.pf))
        self.v_ph = float(volt["lv_nominal_v"]) / math.sqrt(3)
        self.cfg = _cfg(rules)
        self.conf = float(self.cfg["diversity"]["confidence_pct"]) / 100
        self.temp, self.alpha = float(lvd["conductor_temp_c"]), lvd["temperature_coefficients"]
        self.options = {n: [rules.conductor(code) for code in sec[construction][n]] for n in ("single_phase", "three_phase")}
        self._classes: dict[str, float | None] = {}

    def current(self, kind: str, kva: float, load_class: str | None, three: bool) -> float:
        """The customer's own design current per phase: its class's Herman-Beta current at the confidence, else kVA / V."""
        if kind == "residential" and load_class:
            if load_class not in self._classes:
                try:
                    lc = _load_class(self.cfg, self.rules, load_class, "score")
                    self._classes[load_class] = lc.c_amps * float(beta_dist.ppf(self.conf, lc.alpha, lc.beta))
                except AdmdInputError:
                    self._classes[load_class] = None
            if self._classes[load_class] is not None:
                return self._classes[load_class]
        return kva * 1000 / ((3 if three else 1) * self.v_ph)

    def drop(self, c: Conductor, current: float, length_m: float, three: bool) -> float:
        z = c.r_at(self.temp, self.alpha) * self.pf + c.x_ohm_per_km * self.sin_phi
        return (1 if three else 2) * current * length_m / 1000 * z / self.v_ph * 100

    def choose(self, current: float, length_m: float, three: bool) -> tuple[Conductor, float, bool]:
        """The first conductor of the list that carries the current within the drop limit, its drop and whether it passes."""
        pick = self.options["three_phase" if three else "single_phase"]
        c = next((x for x in pick if current <= x.rating_a and self.drop(x, current, length_m, three) <= self.limit + 1e-9), pick[-1])
        pct = self.drop(c, current, length_m, three)
        return c, pct, current <= c.rating_a and pct <= self.limit + 1e-9


def size_services(alloc: LoadAllocation, classes: dict[str, str | None], rules: RuleSet, construction: Construction) -> ServiceResult | None:
    """The service conductor and drop of every allocated load; None when the rules file has no services section."""
    sec = rules.data.get("services")
    if not sec:
        return None
    z = ServiceSizer(rules, construction)
    services: list[Service] = []
    worst: tuple = (-1.0, None, None)  # (pct, service, conductor)
    for a in alloc.allocations:
        three = a.phase == "RWB"
        current = z.current(a.kind, a.kva, classes.get(a.load_id), three)
        c, pct, ok = z.choose(current, a.service_m, three)
        sv = Service(load_id=a.load_id, label=a.label, feeder=a.feeder, phases=3 if three else 1, conductor=c.code, length_m=round(a.service_m, 2),
                     current_a=round(current, 2), rating_a=c.rating_a, drop_pct=round(pct, 3), passes=ok)
        services.append(sv)
        if pct > worst[0]:
            worst = (pct, sv, c)

    issues: list[Issue] = []
    failed = [x for x in services if not x.passes]
    if failed:
        issues.append(issue("error", "service_over_limit", f"No service conductor of the rules file carries these customers within the "
                            f"{z.limit:g} % service drop limit. Move the service to a nearer pole or add a pole.",
                            [x.label or x.load_id for x in sorted(failed, key=lambda x: -x.drop_pct)]))
    volt = rules.data["voltage"]
    marked = [k for k in sec.get("placeholder", []) if k.split(".")[0] in (construction, k)]
    placeholders = [f"services ({construction}): {', '.join(marked)}"] if marked else []
    if "lv_service_max_drop_pct" in volt.get("placeholder", []):
        placeholders.append(f"the service drop limit of {z.limit:g} % (CSIR Red Book guidance, not confirmed)")
    for c in {c.code: c for c in (*z.options["single_phase"], *z.options["three_phase"])}.values():
        if c.placeholder:
            placeholders.append(f"{c.code} {', '.join(c.placeholder)}")
    return ServiceResult(
        clause=sec.get("clause", ""), index=sec.get("index"), construction=construction, limit_pct=z.limit, services=services, issues=issues,
        worst_drop=_drop_trace(rules, worst, z) if worst[1] is not None else None, placeholders=placeholders)


def string_services(res: ServiceResult, alloc: LoadAllocation, oh: OverheadResult, rules: RuleSet) -> ServiceResult:
    """Overhead services: spans, sag and clearance, and the service poles where the sag would be too low."""
    o = rules.data["services"]["overhead"]
    lv = _level(rules, "lv")
    heights = {p.id: p.height_m for p in oh.poles}
    by_load = {a.load_id: a for a in alloc.allocations}
    worst: tuple = (math.inf, None, None)  # (clearance, service, span inputs)
    for s in res.services:
        a = by_load[s.load_id]
        if s.conductor not in rules.data["overhead"]["mechanical"] or a.location is None:
            continue
        span = span_service(rules, s.conductor, s.length_m, heights.get(a.node or "", lv.heights[0]))
        s.spans, s.clearance_m, s.clears = span.spans, span.clearance_m, span.clears
        s.poles = [_along(a.at, a.location, (i + 1) / (span.poles + 1)) for i in range(span.poles)]
        res.service_poles += span.poles
        if span.clearance_m < worst[0]:
            worst = (span.clearance_m, s, span)
    res.pole_height_m, res.min_clearance_m = float(o["pole_height_m"]), float(o["min_clearance_m"])
    low = [s for s in res.services if s.clears is False]
    if low:
        res.issues.append(issue("error", "service_too_low", f"These overhead services do not clear {res.min_clearance_m:g} m with service "
                                f"poles {lv.min_span:g} m apart. Raise the house attachment or move the service.", [s.label or s.load_id for s in low]))
    if worst[1] is not None:
        res.worst_span = _span_trace(rules, worst, o)
    return res


class SpannedService(BaseModel):
    spans: list[ServiceSpan]
    poles: int
    clearance_m: float
    clears: bool
    w_n_per_m: float
    tension_n: float
    h_pole_m: float
    h_service_pole_m: float


def span_service(rules: RuleSet, conductor: str, length_m: float, feeder_pole_m: float) -> SpannedService:
    """One overhead service of length_m from a feeder pole of feeder_pole_m: the fewest service poles that make every span clear."""
    o = rules.data["services"]["overhead"]
    lv = _level(rules, "lv")
    m = rules.data["overhead"]["mechanical"][conductor]

    def top(h: float) -> float:
        return h - (lv.depth_fraction * h + lv.depth_fixed) - lv.attach_below_top

    w = float(m["mass_kg_per_km"]) / 1000 * G
    t = float(o["tension_pct"]) / 100 * float(m["breaking_load_kn"]) * 1000
    h_pole, h_mid = top(feeder_pole_m) - float(o["below_line_m"]), top(float(o["pole_height_m"]))
    most = max(0, math.floor(length_m / lv.min_span) - 1)
    n = 0
    while True:
        spans = _spans(length_m, n, h_pole, h_mid, float(o["house_attachment_m"]), w, t, float(o["min_clearance_m"]), float(o["max_span_m"]))
        if all(x.passes for x in spans) or n >= most:
            break
        n += 1
    return SpannedService(spans=spans, poles=n, clearance_m=round(min(x.low_m for x in spans), 3), clears=all(x.passes for x in spans),
                          w_n_per_m=w, tension_n=t, h_pole_m=h_pole, h_service_pole_m=h_mid)


def _spans(length: float, n: int, h_pole: float, h_mid: float, h_house: float, w: float, t: float, clearance: float,
           max_span: float) -> list[ServiceSpan]:
    """The n + 1 equal spans of a service with n service poles, from the feeder pole to the house."""
    span = length / (n + 1)
    supports = [h_pole] + [h_mid] * n + [h_house]
    sag = w * span * span / (8 * t)
    out = []
    for h_a, h_b in itertools.pairwise(supports):
        low = _low_point(h_a, h_b, sag)
        out.append(ServiceSpan(length_m=round(span, 2), sag_m=round(sag, 3), low_m=round(low, 3),
                               passes=low >= clearance - 1e-6 and span <= max_span + 1e-6))
    return out


def _low_point(h_a: float, h_b: float, sag: float) -> float:
    """The lowest height of y(u) = h_a + (h_b − h_a)·u − 4·sag·u·(1 − u) on 0 ≤ u ≤ 1."""
    if sag <= 0:
        return min(h_a, h_b)
    u = min(1.0, max(0.0, 0.5 - (h_b - h_a) / (8 * sag)))
    return h_a + (h_b - h_a) * u - 4 * sag * u * (1 - u)


def _along(a: tuple[float, float], b: tuple[float, float], f: float) -> tuple[float, float]:
    return (round(a[0] + (b[0] - a[0]) * f, 7), round(a[1] + (b[1] - a[1]) * f, 7))


def _drop_trace(rules: RuleSet, worst, z: ServiceSizer) -> Traced:
    pct, s, c = worst
    volt = rules.data["voltage"]
    limit_src = f"rules {rules.ref} voltage" + (" (placeholder)" if "lv_service_max_drop_pct" in volt.get("placeholder", []) else "")
    return traced(pct, "%", formula_id=DROP_ID, formula=DROP_FORMULA, clause=volt.get("clause", ""), rules_hash=rules.hash,
                  inputs={"load": (s.label or s.load_id, "", "largest service drop"), "conductor": (c.code, "", c.clause),
                          "I": (s.current_a, "A", f"the customer's own demand at {z.conf * 100:g} %"), "length": (s.length_m, "m", "building to pole"),
                          "k": (2 if s.phases == 1 else 1, "", f"{s.phases}-phase service"),
                          "R_hot": (round(c.r_at(z.temp, z.alpha), 4), "Ω/km", c.clause), "X": (c.x_ohm_per_km, "Ω/km", c.clause),
                          "cos_phi": (z.pf, "", f"rules {rules.ref} lv_design"), "V_phase": (round(z.v_ph, 2), "V", f"rules {rules.ref} voltage"),
                          "limit": (z.limit, "%", limit_src)})


def _span_trace(rules: RuleSet, worst, o: dict) -> Traced:
    low, s, x = worst
    src = f"rules {rules.ref} services.overhead"
    ph = set(rules.data["services"].get("placeholder", []))

    def mark(key: str) -> str:
        return src + (" (placeholder)" if f"overhead.{key}" in ph else "")

    return traced(low, "m", formula_id=SPAN_ID, formula=SPAN_FORMULA, clause=rules.data["services"].get("clause", ""), rules_hash=rules.hash,
                  inputs={"load": (s.label or s.load_id, "", "lowest service span"), "conductor": (s.conductor, "", "services"),
                          "length": (s.length_m, "m", "building to pole"), "w": (round(x.w_n_per_m, 3), "N/m", "mass × g, overhead.mechanical"),
                          "T": (round(x.tension_n, 1), "N", f"{o['tension_pct']:g} % of breaking load, {src}"),
                          "h_pole": (round(x.h_pole_m, 3), "m", "feeder pole attachment less " + mark("below_line_m")),
                          "h_service_pole": (round(x.h_service_pole_m, 3), "m", f"{o['pole_height_m']:g} m pole set as an LV pole"),
                          "h_house": (float(o["house_attachment_m"]), "m", mark("house_attachment_m")), "service_poles": (x.poles, "", "added"),
                          "L_max": (float(o["max_span_m"]), "m", src), "clearance": (float(o["min_clearance_m"]), "m", mark("min_clearance_m"))})
