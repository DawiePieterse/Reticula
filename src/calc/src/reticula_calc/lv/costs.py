"""Indicative installed cost of an LV design, for comparing overhead and underground (plan 2.7).

Rates come from `rates/<name>/<date>.yaml`; every figure is labelled an estimate with its rate date. A rate is either
a number or `{assembly: CODE}`: an assembly is the sum of its materials and labour from the material library
(plan 5.1), and its cost line lists them.
"""

from __future__ import annotations

import os
from functools import lru_cache
from pathlib import Path

import yaml
from pydantic import BaseModel

from .model import LvNetwork


class Component(BaseModel):
    material: str
    description: str
    quantity: float
    unit: str
    rate: float


class CostLine(BaseModel):
    item: str
    quantity: float
    unit: str
    rate: float
    amount: float
    assembly: str | None = None
    components: list[Component] = []


class CostEstimate(BaseModel):
    rates: str
    rate_date: str
    currency: str
    lines: list[CostLine]
    total: float
    missing_rates: list[str]
    note: str = "Indicative estimate for comparing options; not a tender price."


def rates_dir() -> Path:
    env = os.environ.get("RETICULA_RATES_DIR")
    return Path(env) if env else Path(__file__).resolve().parents[5] / "rates"


@lru_cache(maxsize=8)
def load_rates(ref: str = "indicative/2026-10") -> dict:
    path = rates_dir() / f"{ref}.yaml"
    if not path.is_file():
        raise FileNotFoundError(f"rate list not found: {path}")
    return yaml.safe_load(path.read_text(encoding="utf-8"))


def resolve_rate(rates: dict, value) -> tuple[float | None, str | None, list[Component]]:
    """A rate entry as (rate, assembly code, components); an assembly's rate is the sum of its components."""
    if value is None or isinstance(value, int | float):
        return (None if value is None else float(value)), None, []
    code = value["assembly"]
    assembly = rates.get("assemblies", {}).get(code)
    if assembly is None:
        return None, code, []
    materials = rates.get("materials", {})
    parts: list[Component] = []
    for c in assembly["components"]:
        m = materials.get(c["material"])
        if m is None:
            return None, code, []
        parts.append(Component(material=c["material"], description=m["description"], quantity=float(c["qty"]), unit=m["unit"], rate=float(m["rate"])))
    return round(sum(p.quantity * p.rate for p in parts), 2), code, parts


def estimate(network: LvNetwork, transformer_kva: float, ref: str = "indicative/2026-10") -> CostEstimate:
    r = load_rates(ref)
    lines: list[CostLine] = []
    missing: set[str] = set()

    def add(item: str, qty: float, unit: str, value) -> None:
        if qty <= 0:
            return
        rate, assembly, parts = resolve_rate(r, value)
        if rate is None:
            missing.add(item)
            return
        lines.append(CostLine(item=item, quantity=round(qty, 2), unit=unit, rate=rate, amount=round(qty * rate, 2), assembly=assembly, components=parts))

    by_conductor: dict[str, float] = {}
    for b in network.branches:
        by_conductor[b.conductor] = by_conductor.get(b.conductor, 0) + b.length_m
    for code, length in sorted(by_conductor.items()):
        add(f"Conductor {code}", length, "m", r["conductor_per_m"].get(code))

    feeders = [b for b in network.branches if b.kind == "feeder"]
    services = [b for b in network.branches if b.kind == "service"]
    if feeders and feeders[0].construction == "overhead":
        poles: dict[str, int] = {}
        for n in network.nodes:
            if n.pole:
                poles[n.pole] = poles.get(n.pole, 0) + 1
        for code, count in sorted(poles.items()):
            add(f"Pole {code}", count, "each", r["pole_each"].get(code))
        add("Stay", sum(n.stays for n in network.nodes), "each", r.get("stay_each"))
        add("Service connection (overhead)", len(services), "each", r.get("service_overhead_each"))
    else:
        add("Trench", sum(b.length_m for b in feeders) + sum(b.length_m for b in services), "m", r.get("trench_per_m"))
        add("Distribution kiosk", sum(1 for n in network.nodes if n.kind == "kiosk"), "each", r.get("kiosk_each"))
        add("Service connection (underground)", len(services), "each", r.get("service_underground_each"))
    add(f"Transformer {transformer_kva:g} kVA", 1, "each", r.get("transformer_each", {}).get(f"{transformer_kva:g}"))

    return CostEstimate(rates=ref, rate_date=str(r.get("rate_date")), currency=r.get("currency", "ZAR"), lines=lines,
                        total=round(sum(x.amount for x in lines), 2), missing_rates=sorted(missing))
