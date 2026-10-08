"""Cost model (plan 5.1): bill of quantities, capital cost, losses and lifetime cost.

**Quantities.** Every priced thing in a design maps to an assembly code: poles by network and height, stays, conductor
and cable by code per metre, trench per metre of underground route, kiosks, pole boxes, service connections and their
cable, transformers and mini-subs by rating, MV tee-offs and the connection. An assembly is a list of rate items with
quantities. A quantity whose assembly or item has no rate is listed without a price and reported.

**Rates.** The rate library comes from the API (plan 6.1: imported price lists, the engineer's overrides, each with its
date and source). Without one, the indicative library in `rates/` is used and every cost is marked an estimate. Each
item may carry an uncertainty; the low and high totals use it, so plan 5.4 can tell when two options are too close.

**Losses.** At design demand: I²R in every LV and MV section, transformer load losses (S/S_r)² × P_k and no-load losses
P_0. Energy per year is peak load loss × loss load factor × 8760 h + P_0 × 8760 h. Load losses grow with demand as
(1 + g)^(2t). Lifetime cost is capital plus the present value of the losses' energy over the period.
"""

from __future__ import annotations

import json
import math
import os
from collections import defaultdict
from functools import lru_cache
from pathlib import Path

from pydantic import BaseModel

from .issues import Issue, issue
from .rules import RuleSet
from .trace import Traced, traced

CAPEX_ID = "cost.capex.boq.v1"
CAPEX_FORMULA = "C = Σ qty × Σ(component qty × item rate); low/high = Σ amount × (1 ∓ uncertainty)"
LIFETIME_ID = "cost.lifetime.npv.v1"
LIFETIME_FORMULA = ("LC = C + Σ_t=1..N [E_load·(1+g)^(2(t−1)) + E_0] · price / (1+r)^t; "
                    "E_load = P_loss,peak·LLF·8760, E_0 = P_0·8760")


class RateItem(BaseModel):
    code: str
    description: str
    unit: str
    rate: float
    category: str = "material"
    rate_date: str | None = None
    source: str | None = None
    uncertainty_pct: float | None = None


class AssemblyComponent(BaseModel):
    item: str
    qty: float


class Assembly(BaseModel):
    code: str
    description: str
    unit: str
    components: list[AssemblyComponent]


class RateLibrary(BaseModel):
    name: str
    rate_date: str
    source: str
    currency: str = "ZAR"
    indicative: bool = False
    """Placeholder rates, not a price list: every cost from them is an estimate."""
    items: list[RateItem]
    assemblies: list[Assembly]


class Economics(BaseModel):
    period_years: int
    discount_rate_pct: float
    energy_cost_zar_per_kwh: float
    load_growth_pct: float
    loss_load_factor: float
    rate_uncertainty_pct: float


class BoqLine(BaseModel):
    code: str
    description: str
    unit: str
    qty: float
    rate: float | None
    amount: float | None
    low: float | None
    high: float | None
    group: str
    """Network part: LV, MV, transformers, services."""


class Losses(BaseModel):
    lv_kw: float
    mv_kw: float
    transformer_load_kw: float
    transformer_no_load_kw: float
    energy_mwh_per_year: float
    npv: float


class CostResult(BaseModel):
    library: str
    rate_date: str
    indicative: bool
    currency: str
    lines: list[BoqLine]
    capex: float
    capex_low: float
    capex_high: float
    losses: Losses
    lifetime: float
    lifetime_low: float
    lifetime_high: float
    economics: Economics
    issues: list[Issue]
    capex_trace: Traced
    lifetime_trace: Traced


def rates_dir() -> Path:
    env = os.environ.get("RETICULA_RATES_DIR")
    return Path(env) if env else Path(__file__).resolve().parents[4] / "rates"


@lru_cache(maxsize=4)
def default_library(name: str = "indicative") -> RateLibrary:
    with open(rates_dir() / f"{name}.json", encoding="utf-8") as f:
        return RateLibrary.model_validate(json.load(f))


def assembly_rate(lib: RateLibrary, code: str) -> float | None:
    """Unit rate of an assembly (Σ component qty × item rate), or of a bare item; None when any part has no rate."""
    items = {i.code: i for i in lib.items}
    a = next((x for x in lib.assemblies if x.code == code), None)
    if a is None:
        return items[code].rate if code in items else None
    parts = [(items.get(c.item), c.qty) for c in a.components]
    return sum(p.rate * k for p, k in parts) if all(p is not None for p, _ in parts) else None


def economics(rules: RuleSet, overrides: dict[str, float] | None = None) -> Economics:
    sec = dict(rules.section("economics", "eskom/0.8.0"))
    sec.update({k: v for k, v in (overrides or {}).items() if v is not None})
    return Economics.model_validate({k: sec[k] for k in Economics.model_fields})


class Quantities:
    """Assembly code → quantity, kept by group, in the order first seen."""

    def __init__(self):
        self.qty: dict[tuple[str, str], float] = defaultdict(float)

    def add(self, group: str, code: str, qty: float) -> None:
        if qty > 0:
            self.qty[(group, code)] += qty


def price(q: Quantities, lib: RateLibrary, econ: Economics, losses: Losses, rules: RuleSet) -> CostResult:
    items = {i.code: i for i in lib.items}
    assemblies = {a.code: a for a in lib.assemblies}
    lines: list[BoqLine] = []
    no_rate: list[str] = []
    for (group, code), qty in q.qty.items():
        a = assemblies.get(code)
        rate = low = high = None
        desc, unit = code, "each"
        if a is not None:
            desc, unit = a.description, a.unit
            parts = [(items.get(c.item), c.qty) for c in a.components]
            if all(p is not None for p, _ in parts):
                rate = sum(p.rate * k for p, k in parts)
                low = sum(p.rate * k * (1 - (p.uncertainty_pct if p.uncertainty_pct is not None else econ.rate_uncertainty_pct) / 100) for p, k in parts)
                high = sum(p.rate * k * (1 + (p.uncertainty_pct if p.uncertainty_pct is not None else econ.rate_uncertainty_pct) / 100) for p, k in parts)
        elif code in items:
            i = items[code]
            desc, unit, rate = i.description, i.unit, i.rate
            u = (i.uncertainty_pct if i.uncertainty_pct is not None else econ.rate_uncertainty_pct) / 100
            low, high = rate * (1 - u), rate * (1 + u)
        if rate is None:
            no_rate.append(code)
        lines.append(BoqLine(code=code, description=desc, unit=unit, qty=round(qty, 2), rate=_r(rate), amount=_r(rate * qty if rate is not None else None),
                             low=_r(low * qty if low is not None else None), high=_r(high * qty if high is not None else None), group=group))
    capex = sum(x.amount or 0 for x in lines)
    lo = sum(x.low or 0 for x in lines)
    hi = sum(x.high or 0 for x in lines)
    issues: list[Issue] = []
    if no_rate:
        issues.append(issue("warning", "no_rate", "These items have no rate in the library, so they are left out of the cost. "
                            "Add them to the rate library.", sorted(set(no_rate))))
    if lib.indicative:
        issues.append(Issue(severity="warning", code="indicative_rates",
                            message=f"Costs use the indicative rate library '{lib.name}' and are estimates, not a price."))
    clause = rules.data.get("economics", {}).get("clause", "")
    capex_trace = traced(capex, lib.currency, formula_id=CAPEX_ID, formula=CAPEX_FORMULA, clause=f"rate library {lib.name} ({lib.rate_date})",
                         rules_hash=rules.hash, inputs={"lines": (len(lines), "", "bill of quantities"), "low": (round(lo, 2), lib.currency, "rates − uncertainty"),
                                                       "high": (round(hi, 2), lib.currency, "rates + uncertainty"),
                                                       "rate_date": (lib.rate_date, "", lib.source), "unpriced": (len(set(no_rate)), "", "items without a rate")})
    life = capex + losses.npv
    lifetime_trace = traced(life, lib.currency, formula_id=LIFETIME_ID, formula=LIFETIME_FORMULA, clause=clause, rules_hash=rules.hash, inputs={
        "capex": (round(capex, 2), lib.currency, "bill of quantities"), "loss_npv": (round(losses.npv, 2), lib.currency, "present value of losses"),
        "P_lv": (losses.lv_kw, "kW", "I²R at design demand"), "P_mv": (losses.mv_kw, "kW", "I²R at design demand"),
        "P_tx_load": (losses.transformer_load_kw, "kW", "(S/S_r)²·P_k"), "P_0": (losses.transformer_no_load_kw, "kW", "no-load"),
        "period": (econ.period_years, "years", "economics"), "discount": (econ.discount_rate_pct, "%", "economics"),
        "price": (econ.energy_cost_zar_per_kwh, "ZAR/kWh", "economics"), "growth": (econ.load_growth_pct, "%", "economics"),
        "LLF": (econ.loss_load_factor, "", "economics")})
    return CostResult(library=lib.name, rate_date=lib.rate_date, indicative=lib.indicative, currency=lib.currency, lines=lines,
                      capex=round(capex, 2), capex_low=round(lo, 2), capex_high=round(hi, 2), losses=losses, lifetime=round(life, 2),
                      lifetime_low=round(lo + losses.npv, 2), lifetime_high=round(hi + losses.npv, 2), economics=econ, issues=issues,
                      capex_trace=capex_trace, lifetime_trace=lifetime_trace)


def losses(lv_kw: float, mv_kw: float, tx_load_kw: float, tx_nl_kw: float, econ: Economics) -> Losses:
    e_load = (lv_kw + mv_kw + tx_load_kw) * econ.loss_load_factor * 8760
    e_0 = tx_nl_kw * 8760
    g, r = econ.load_growth_pct / 100, econ.discount_rate_pct / 100
    npv = sum((e_load * (1 + g) ** (2 * (t - 1)) + e_0) * econ.energy_cost_zar_per_kwh / (1 + r) ** t for t in range(1, econ.period_years + 1))
    return Losses(lv_kw=round(lv_kw, 3), mv_kw=round(mv_kw, 3), transformer_load_kw=round(tx_load_kw, 3), transformer_no_load_kw=round(tx_nl_kw, 3),
                  energy_mwh_per_year=round((e_load + e_0) / 1000, 3), npv=round(npv, 2))


def _r(v: float | None) -> float | None:
    return None if v is None or math.isnan(v) else round(v, 2)
