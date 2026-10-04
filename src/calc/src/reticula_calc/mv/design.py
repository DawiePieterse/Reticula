"""One MV design run (plans 3.1–3.4): place and size transformers, design each site's LV network, lay and size the MV
network, and set each transformer's tap. Costs are indicative."""

from __future__ import annotations

from typing import Any, Literal

from pydantic import BaseModel, Field

from ..lv.analysis import Check
from ..lv.build import BuildIssue, CustomerIn, RouteIn
from ..lv.costs import CostLine, load_rates
from ..lv.design import LvDesignRequest, OptionResult, design_lv
from ..rules import RuleSet
from .network import MvAnalysis, MvNetwork, build_mv, choose_tap, size_mv
from .placement import Placement, SiteIn, allocate, mv_config


class MvDesignRequest(BaseModel):
    rules: str
    supply: tuple[float, float]
    supply_note: str | None = None
    sites: list[SiteIn] = Field(min_length=1)
    lv_routes: list[RouteIn]
    mv_routes: list[RouteIn]
    customers: list[CustomerIn]
    lv_construction: Literal["overhead", "underground"] = "overhead"
    mv_construction: Literal["overhead", "underground"] = "overhead"
    roads: list[list[tuple[float, float]]] = []
    area: dict[str, Any] | None = None
    rates: str = "indicative/2026-10"
    source_fault_mva_max: float | None = None
    source_fault_mva_min: float | None = None


class SiteResult(BaseModel):
    placement: Placement
    lon: float
    lat: float
    lv: OptionResult | None
    lv_passed: bool
    lv_worst_vdrop_pct: float | None
    mv_vdrop_pct: float | None
    regulation_pct: float | None
    tap_pct: float | None
    v_min_pct: float | None
    v_max_pct: float | None


class MvDesignResult(BaseModel):
    rules: str
    rules_hash: str
    issues: list[BuildIssue]
    sites: list[SiteResult]
    mv_network: MvNetwork | None
    mv_analysis: MvAnalysis | None
    checks: list[Check]
    passed: bool
    cost_lines: list[CostLine]
    cost_total: float
    currency: str
    rate_date: str
    unverified: list[str]


def design_mv(req: MvDesignRequest, rules: RuleSet) -> MvDesignResult:
    cfg = mv_config(rules)
    clause = cfg.get("clause", "")
    issues: list[BuildIssue] = []
    if req.supply_note:
        issues.append(BuildIssue(severity="warning", code="supply_assumed", message=req.supply_note))
    unverified: set[str] = set()
    if cfg.get("status") == "unverified":
        unverified.add("mv_design")

    placement = allocate(req.sites, req.customers, req.lv_routes, rules, req.lv_construction)
    checks: list[Check] = []
    for erf in placement.unallocated:
        checks.append(Check(code="customer_unallocated", subject=erf, passed=False, value=1, limit=0, unit="",
                            message=f"Building {erf} is not within {cfg['max_lv_reach_m']} m of any transformer site along the LV routes", clause=clause))
    if placement.unused_sites:
        issues.append(BuildIssue(severity="warning", code="site_unused", message="Some transformer sites serve no building and were left out.",
                                 count=len(placement.unused_sites), samples=placement.unused_sites[:10]))

    by_id = {c.building_id: c for c in req.customers}
    site_xy = {s.id: (s.lon, s.lat) for s in req.sites}
    customers_by_site = {p.site_id: [by_id[b] for b in p.customers] for p in placement.placements}
    rates = load_rates(req.rates)
    lines: list[CostLine] = []

    # Each site's LV network, with its chosen transformer.
    sites: list[SiteResult] = []
    for p in placement.placements:
        if p.note:
            issues.append(BuildIssue(severity="warning" if p.rating_kva else "error", code="transformer_unit",
                                     message=f"Site {p.site_id}: {p.note}."))
        checks.append(Check(code="transformer_rating", subject=p.site_id, passed=p.rating_kva is not None,
                            value=p.design_kva, limit=p.rating_kva or 0, unit="kVA",
                            message=f"Site {p.site_id}: design demand {p.design_kva:.1f} kVA" + (f" on a {p.rating_kva:g} kVA {p.unit.replace('_', '-')}" if p.rating_kva else ""),
                            clause=clause))
        lv_opt = None
        if p.rating_kva:
            lv = design_lv(LvDesignRequest(rules=req.rules, source=site_xy[p.site_id], routes=req.lv_routes, customers=customers_by_site[p.site_id],
                                           constructions=[req.lv_construction], roads=req.roads, transformer_kva=p.rating_kva, area=req.area, rates=req.rates,
                                           source_fault_mva_max=req.source_fault_mva_max, source_fault_mva_min=req.source_fault_mva_min), rules)
            lv_opt = lv.options[0]
            unverified.update(lv.unverified)
            lines += [CostLine(item=f"Site {p.site_id}: {line.item}", quantity=line.quantity, unit=line.unit, rate=line.rate, amount=line.amount)
                      for line in lv_opt.cost.lines if not line.item.startswith("Transformer")]
            unit_rates = rates.get("pole_mount_each" if p.unit == "pole_mount" else "minisub_each", {})
            rate = unit_rates.get(f"{p.rating_kva:g}")
            if rate is not None:
                lines.append(CostLine(item=f"Site {p.site_id}: {p.unit.replace('_', '-')} {p.rating_kva:g} kVA", quantity=1, unit="each", rate=float(rate), amount=float(rate)))
        lv_worst = max((c.total_vdrop_pct for c in lv_opt.analysis.customers), default=0.0) if lv_opt else None
        sites.append(SiteResult(placement=p, lon=site_xy[p.site_id][0], lat=site_xy[p.site_id][1], lv=lv_opt, lv_passed=bool(lv_opt and lv_opt.passed),
                                lv_worst_vdrop_pct=lv_worst, mv_vdrop_pct=None, regulation_pct=None, tap_pct=None, v_min_pct=None, v_max_pct=None))
        if lv_opt:
            checks.append(Check(code="lv_design", subject=p.site_id, passed=lv_opt.passed, value=lv_opt.failed_checks, limit=0, unit="failed",
                                message=f"Site {p.site_id}: LV design {'passes' if lv_opt.passed else f'fails {lv_opt.failed_checks} checks'}",
                                clause=rules.data["lv_design"].get("clause", "")))

    # MV network to the sites that have a transformer.
    mv_net = mv_an = None
    usable = {s.placement.site_id: site_xy[s.placement.site_id] for s in sites if s.placement.rating_kva}
    if usable:
        net, tee_checks = build_mv(req.supply, usable, req.mv_routes, rules)
        checks += tee_checks
        mv_net, mv_an, _ = size_mv(net, customers_by_site, rules, req.mv_construction)
        checks += mv_an.checks
        lib_mv = rates.get("mv_conductor_per_m", {})
        by_cond: dict[str, float] = {}
        for b in mv_net.branches:
            by_cond[b.conductor] = by_cond.get(b.conductor, 0) + b.length_m
        for code, length in sorted(by_cond.items()):
            if code in lib_mv:
                lines.append(CostLine(item=f"MV {code}", quantity=round(length, 1), unit="m", rate=float(lib_mv[code]), amount=round(length * float(lib_mv[code]), 2)))
        if req.mv_construction == "overhead" and rates.get("mv_pole_per_km"):
            km = sum(b.length_m for b in mv_net.branches) / 1000
            lines.append(CostLine(item="MV poles and hardware", quantity=round(km, 3), unit="km", rate=float(rates["mv_pole_per_km"]),
                                  amount=round(km * float(rates["mv_pole_per_km"]), 2)))

    # Taps: lowest and highest customer voltage at each site.
    band = float(cfg["supply_voltage_band_pct"])
    for s in sites:
        p = s.placement
        if not (p.rating_kva and mv_an and p.site_id in mv_an.site_vdrop_pct and s.lv_worst_vdrop_pct is not None):
            continue
        tap, lo, hi, reg = choose_tap(rules, mv_an.site_vdrop_pct[p.site_id], p.design_kva, p.rating_kva, p.z_pct or 4.0, p.x_r or 2.0, s.lv_worst_vdrop_pct)
        s.mv_vdrop_pct, s.tap_pct, s.v_min_pct, s.v_max_pct, s.regulation_pct = mv_an.site_vdrop_pct[p.site_id], tap, lo, hi, reg
        checks.append(Check(code="supply_voltage_band", subject=p.site_id, passed=tap is not None, value=lo, limit=100 - band, unit="%",
                            message=(f"Site {p.site_id}: tap {tap:+g} % keeps customers between {lo:.1f} % and {hi:.1f} % of nominal" if tap is not None
                                     else f"Site {p.site_id}: no tap keeps every customer within ±{band:g} % (lowest {lo:.1f} %, highest {hi:.1f} %)"),
                            clause=clause))

    total = round(sum(x.amount for x in lines), 2)
    return MvDesignResult(rules=rules.ref, rules_hash=rules.hash, issues=issues, sites=sites, mv_network=mv_net, mv_analysis=mv_an, checks=checks,
                          passed=all(c.passed for c in checks), cost_lines=lines, cost_total=total, currency=rates.get("currency", "ZAR"),
                          rate_date=str(rates.get("rate_date")), unverified=sorted(unverified))
