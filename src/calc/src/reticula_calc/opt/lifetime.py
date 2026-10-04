"""Lifetime cost of an LV design: capital cost plus the present value of its energy losses (plan 5.1).

Losses at peak. Each consumer's current is the Herman-Beta variable of its load class (mean μ, variance σ²; special
loads constant). Along a branch the expected squared current on phase p is (Σμ_p + I_const,p)² + Σσ²_p and in the
neutral |Σ_p a^k (Σμ_p + I_const,p)|² + Σ_p Σσ²_p (a = 1∠120°, consumers independent); losses are Σ E[I²]·R·L with R
from the conductor library (at operating temperature). The transformer adds its no-load loss and its load loss in
proportion to Σ_p E[I_p²] / (3 I_rated²).

Energy. Annual losses = 8760 h × (peak load losses × LLF + no-load loss), LLF = a·LF + b·LF² (Buller-Woodrow) with
the rules' residential load factor LF.

Present value. Load losses grow with the square of the demand growth g; the cost of year t's losses is discounted at
d: PV factor = Σ_{t=1..N} (1+g)^{2(t-1)} / (1+d)^t. No-load losses do not grow (factor with g = 0).
Lifetime cost = capital cost + annual load-loss cost × PV(g) + annual no-load cost × PV(0).
"""

from __future__ import annotations

import cmath
import math

import networkx as nx
from pydantic import BaseModel, Field

from ..lv.analysis import PHASES, customer_loads
from ..lv.costs import load_rates
from ..lv.library import library, lv_config
from ..lv.model import LvNetwork
from ..rules import RuleSet
from ..rules.loader import RulesError
from ..trace import Traced, traced

FORMULA_ID = "opt.lifetime-cost.v1"
FORMULA = ("C = capex + p·8760·(P_load·LLF·Σ_{t=1..N} (1+g)^{2(t-1)}/(1+d)^t + P_0·Σ_{t=1..N} 1/(1+d)^t); LLF = a·LF + b·LF²; "
           "P_load = Σ_branches (Σ_p E[I_p²]·R + E[|I_n|²]·R_n)·L + P_k·Σ_p E[I_p²]/(3·I_r²)")
_A = {"R": 1 + 0j, "W": cmath.rect(1, -2 * math.pi / 3), "B": cmath.rect(1, 2 * math.pi / 3)}


class LifetimeParams(BaseModel):
    """Overrides for the rate list's lifetime defaults; None keeps the default."""

    period_years: int | None = Field(default=None, ge=1, le=60)
    discount_rate_pct: float | None = Field(default=None, ge=0, le=30)
    energy_cost_per_kwh: float | None = Field(default=None, ge=0)
    load_growth_pct: float | None = Field(default=None, ge=0, le=20)


class LifetimeCost(BaseModel):
    capex: float
    line_losses_kw: float
    transformer_load_losses_kw: float
    transformer_no_load_kw: float
    loss_load_factor: float
    annual_losses_kwh: float
    annual_loss_cost: float
    #: For load losses (with demand growth); no-load losses use the factor without growth.
    pv_factor: float
    losses_pv: float
    total: Traced
    period_years: int
    discount_rate_pct: float
    energy_cost_per_kwh: float
    load_growth_pct: float


def opt_config(rules: RuleSet) -> dict:
    cfg = rules.data.get("optimisation")
    if not cfg:
        raise RulesError(f"rules {rules.ref} have no optimisation section; use eskom/0.5.0 or later")
    return cfg


def resolved_params(params: LifetimeParams | None, rates_ref: str) -> dict[str, float]:
    defaults = load_rates(rates_ref).get("lifetime", {})
    given = (params or LifetimeParams()).model_dump()
    out = {k: (given[k] if given[k] is not None else defaults.get(k)) for k in given}
    missing = [k for k, v in out.items() if v is None]
    if missing:
        raise ValueError(f"lifetime cost needs {', '.join(missing)} (not in the run or rate list {rates_ref})")
    return out


def pv_factor(years: int, discount_pct: float, growth_pct: float) -> float:
    d, g = discount_pct / 100, growth_pct / 100
    return sum((1 + g) ** (2 * (t - 1)) / (1 + d) ** t for t in range(1, years + 1))


def peak_losses(network: LvNetwork, rules: RuleSet, transformer_kva: float) -> tuple[float, float, float]:
    """Expected losses at peak: (lines kW, transformer load kW, transformer no-load kW)."""
    cfg = opt_config(rules)
    lib = library(rules)
    loads = customer_loads(network, rules)
    g = network.graph()
    by_node: dict[str, list[tuple[str, float, float, float, float]]] = {}
    for c in network.customers:
        by_node.setdefault(c.node_id, []).extend(loads[c.id])
    mean: dict[str, dict[str, float]] = {}
    var: dict[str, dict[str, float]] = {}
    for n in reversed(list(nx.topological_sort(g))):
        m = dict.fromkeys(PHASES, 0.0)
        v = dict.fromkeys(PHASES, 0.0)
        for p, mu, s2, _c, det in by_node.get(n, []):
            m[p] += mu + det
            v[p] += s2
        for ch in g.successors(n):
            for p in PHASES:
                m[p] += mean[ch][p]
                v[p] += var[ch][p]
        mean[n], var[n] = m, v
    line_w = 0.0
    for b in network.branches:
        cable = lib[b.conductor]
        m, v = mean[b.to_id], var[b.to_id]
        phase_sq = sum(m[p] ** 2 + v[p] for p in PHASES)
        neutral_sq = abs(sum(_A[p] * m[p] for p in PHASES)) ** 2 + sum(v.values())
        line_w += (phase_sq * cable.r_ohm_per_km + neutral_sq * cable.neutral_r_ohm_per_km) * b.length_m / 1000
    losses = sorted(cfg["transformer_losses"], key=lambda t: t["kva"])
    t = next((x for x in losses if x["kva"] >= transformer_kva), losses[-1])
    v_ph = float(lv_config(rules)["phase_voltage_v"])
    i_rated = transformer_kva * 1000 / (3 * v_ph)
    m, v = mean[network.source_id], var[network.source_id]
    load_kw = float(t["load_kw"]) * sum(m[p] ** 2 + v[p] for p in PHASES) / (3 * i_rated**2)
    return line_w / 1000, load_kw, float(t["no_load_kw"])


def lifetime_cost(network: LvNetwork, rules: RuleSet, transformer_kva: float, capex: float, params: dict[str, float]) -> LifetimeCost:
    cfg = opt_config(rules)
    lf = float(cfg["load_factor"])
    llf = float(cfg["loss_load_factor"]["a"]) * lf + float(cfg["loss_load_factor"]["b"]) * lf**2
    line_kw, load_kw, no_load_kw = peak_losses(network, rules, transformer_kva)
    load_kwh, no_load_kwh = 8760 * (line_kw + load_kw) * llf, 8760 * no_load_kw
    annual_kwh = load_kwh + no_load_kwh
    price = float(params["energy_cost_per_kwh"])
    cost = annual_kwh * price
    pv = pv_factor(int(params["period_years"]), float(params["discount_rate_pct"]), float(params["load_growth_pct"]))
    pv0 = pv_factor(int(params["period_years"]), float(params["discount_rate_pct"]), 0.0)
    losses_pv = price * (load_kwh * pv + no_load_kwh * pv0)
    total = capex + losses_pv
    return LifetimeCost(
        capex=round(capex, 2), line_losses_kw=round(line_kw, 4), transformer_load_losses_kw=round(load_kw, 4), transformer_no_load_kw=no_load_kw,
        loss_load_factor=round(llf, 4), annual_losses_kwh=round(annual_kwh, 1), annual_loss_cost=round(cost, 2), pv_factor=round(pv, 4),
        losses_pv=round(losses_pv, 2),
        total=traced(round(total, 2), "currency", formula_id=FORMULA_ID, formula=FORMULA, clause=str(cfg.get("clause", "")), rules_hash=rules.hash,
                     inputs={"capex": (round(capex, 2), "currency"), "P_load": (round(line_kw + load_kw, 4), "kW"), "P_0": (no_load_kw, "kW"),
                             "LF": (lf, ""), "LLF": (round(llf, 4), ""), "p": (float(params["energy_cost_per_kwh"]), "currency/kWh"),
                             "N": (int(params["period_years"]), "years"), "d": (float(params["discount_rate_pct"]), "%"),
                             "g": (float(params["load_growth_pct"]), "%")}),
        period_years=int(params["period_years"]), discount_rate_pct=float(params["discount_rate_pct"]),
        energy_cost_per_kwh=float(params["energy_cost_per_kwh"]), load_growth_pct=float(params["load_growth_pct"]),
    )
