"""One LV design run: lay out, size, check and cost each requested construction, then compare (plans 2.2–2.8)."""

from __future__ import annotations

from typing import Any

from pydantic import BaseModel, Field
from shapely.geometry import LineString, shape

from ..rules import RuleSet
from .analysis import Analysis, Check
from .build import BuildIssue, BuildRequest, BuildResult, CustomerIn, RouteIn, build_network
from .costs import CostEstimate, RatesRef, estimate
from .library import Construction
from .model import LvNetwork
from .overhead import OverheadResult, check_overhead
from .sizing import size_network


class LvDesignRequest(BaseModel):
    rules: str = Field(description="authority/version")
    source: tuple[float, float]
    #: False when the transformer position is not a site marked during the field visit (plan 2.8).
    source_inspected: bool = True
    routes: list[RouteIn]
    customers: list[CustomerIn]
    constructions: list[Construction] = Field(default=["overhead"], min_length=1)
    roads: list[list[tuple[float, float]]] = []
    transformer_kva: float | None = None
    site: dict[str, float] | None = None
    area: dict[str, Any] | None = None
    rates: RatesRef = "indicative/2026-10"
    #: The authority's MV fault levels at the connection point (plan 4.1); the rules' default when absent.
    source_fault_mva_max: float | None = None
    source_fault_mva_min: float | None = None


class OptionResult(BaseModel):
    construction: Construction
    network: LvNetwork
    analysis: Analysis
    overhead: OverheadResult | None
    cost: CostEstimate
    converged: bool
    passed: bool
    failed_checks: int


class Comparison(BaseModel):
    construction: Construction
    passed: bool
    worst_vdrop_pct: float
    max_loading_pct: float
    transformer_kva: float
    min_end_fault_a: float | None
    route_length_m: float
    poles: int
    stays: int
    kiosks: int
    cost_total: float
    currency: str


class LvDesignResult(BaseModel):
    rules: str
    rules_hash: str
    issues: list[BuildIssue]
    options: list[OptionResult]
    comparison: list[Comparison]
    unverified: list[str]


def design_option(built: BuildResult, construction: Construction, rules: RuleSet, transformer_kva: float | None, site: dict[str, float] | None,
                  source: dict[str, float] | None, rates: RatesRef, min_feeder: int = 0) -> tuple[OptionResult, set[str]]:
    """Size, check and cost one laid-out network; returns the option and the unverified rules sections it used."""
    unverified: set[str] = set()
    # A layout error (a building that cannot be connected) fails the design like any check.
    layout_checks = [Check(code=i.code, subject=sample, passed=False, value=1, limit=0, unit="", message=i.message, clause="")
                     for i in built.issues if i.severity == "error" for sample in (i.samples or ["network"])]
    sized = size_network(built.network, rules, construction, transformer_kva, site, source, min_feeder)
    net = sized.network
    checks: list[Check] = [*layout_checks, *sized.analysis.checks]
    oh = None
    if construction == "overhead":
        oh = check_overhead(net, rules)
        checks += oh.checks
        if rules.data["overhead"].get("status") == "unverified":
            unverified.add("overhead rules")
    unverified.update(sized.analysis.unverified)
    cost = estimate(net, sized.analysis.transformer_kva, rates)
    failed = [c for c in checks if not c.passed]
    analysis = sized.analysis.model_copy(update={"checks": checks})
    return OptionResult(construction=construction, network=net, analysis=analysis, overhead=oh, cost=cost, converged=sized.converged,
                        passed=not failed, failed_checks=len(failed)), unverified


def design_lv(req: LvDesignRequest, rules: RuleSet) -> LvDesignResult:
    issues: list[BuildIssue] = []
    if not req.source_inspected:
        issues.append(BuildIssue(severity="warning", code="source_not_inspected",
                                 message="The transformer is not at a site marked during the field visit."))
    options: list[OptionResult] = []
    unverified: set[str] = set()
    source = {"fault_mva_max": req.source_fault_mva_max, "fault_mva_min": req.source_fault_mva_min} if req.source_fault_mva_max else None
    for construction in dict.fromkeys(req.constructions):
        built = build_network(BuildRequest(source=req.source, routes=req.routes, customers=req.customers,
                                           construction=construction, roads=req.roads), rules)
        if not options:
            issues += built.issues
        option, used = design_option(built, construction, rules, req.transformer_kva, req.site, source, req.rates)
        unverified.update(used)
        options.append(option)

    if req.area and options:
        area = shape(req.area)
        outside = [b.id for b in options[0].network.branches if not area.intersects(LineString(b.geometry))]
        if outside:
            issues.append(BuildIssue(severity="warning", code="outside_area", message="Some branches run outside the project area.",
                                     count=len(outside), samples=outside[:10]))

    comparison = [
        Comparison(
            construction=o.construction, passed=o.passed, worst_vdrop_pct=o.analysis.worst_vdrop_pct.value,
            max_loading_pct=max((b.loading_pct for b in o.analysis.branches), default=0.0), transformer_kva=o.analysis.transformer_kva,
            min_end_fault_a=min((e.min_fault_a for e in o.analysis.feeder_ends), default=None),
            route_length_m=round(sum(b.length_m for b in o.network.branches if b.kind == "feeder"), 1),
            poles=sum(1 for n in o.network.nodes if n.pole), stays=sum(n.stays for n in o.network.nodes),
            kiosks=sum(1 for n in o.network.nodes if n.kind == "kiosk"), cost_total=o.cost.total, currency=o.cost.currency,
        )
        for o in options
    ]
    return LvDesignResult(rules=rules.ref, rules_hash=rules.hash, issues=issues, options=options, comparison=comparison,
                          unverified=sorted(unverified))

