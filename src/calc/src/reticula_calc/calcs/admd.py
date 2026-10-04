"""Income band and ADMD from site observations, and after-diversity demand for a group of loads.

All scoring, bands, ADMD values, the diversity constant and special-load defaults come from the
rules file's `income_admd` section. Every number is returned as a Traced record.
"""

from __future__ import annotations

from typing import Any, Literal

from pydantic import BaseModel, Field

from ..rules import RulesError, RuleSet
from ..trace import Traced, traced

ESTIMATE_FORMULA_ID = "load.admd.indicator-score.v1"
ESTIMATE_FORMULA = "points = Σ indicator points; band = first income band with points <= max_points; ADMD = band ADMD"
GROUP_FORMULA_ID = "load.admd.group-diversity.v1"
GROUP_FORMULA = "S = Σ ADMD_i × (1 + k / N) over N residential loads + Σ special-load kVA"


class AdmdInputError(ValueError):
    pass


class EstimateRequest(BaseModel):
    rules: str
    kind: Literal["residential", "special"] = "residential"
    observations: dict[str, Any] | None = None
    special_load: str | None = None
    override_kva: float | None = Field(default=None, gt=0)
    override_reason: str | None = None


class IndicatorScore(BaseModel):
    indicator: str
    label: str
    value: Any
    points: float


class EstimateResult(BaseModel):
    kind: str
    points: Traced | None
    breakdown: list[IndicatorScore]
    missing: list[str]
    income_band: str | None
    category: str | None
    admd_kva: Traced
    estimated_kva: float
    overridden: bool
    rules_hash: str


class GroupLoad(BaseModel):
    id: str
    kind: Literal["residential", "special"]
    kva: float = Field(gt=0)


class GroupRequest(BaseModel):
    rules: str
    loads: list[GroupLoad]


class GroupResult(BaseModel):
    residential_count: int
    special_count: int
    diversity_factor: Traced | None
    residential_kva: Traced
    special_kva: float
    total_kva: Traced
    rules_hash: str


def _cfg(rules: RuleSet) -> dict[str, Any]:
    cfg = rules.data.get("income_admd")
    if not cfg:
        raise RulesError(f"rules {rules.ref} has no income_admd section")
    return cfg


def _band_points(value: float, bands: list[dict[str, Any]]) -> float:
    for b in bands:
        if b.get("max") is None or value < b["max"]:
            return float(b["points"])
    return float(bands[-1]["points"])


def estimate(req: EstimateRequest, rules: RuleSet) -> EstimateResult:
    cfg = _cfg(rules)
    clause = cfg["clause"]

    if req.kind == "special":
        name = req.special_load or "other"
        defaults = cfg["special_loads"]
        if name not in defaults:
            raise AdmdInputError(f"unknown special load {name!r}; known: {', '.join(sorted(defaults))}")
        estimated = float(defaults[name])
        value = req.override_kva or estimated
        kva = traced(value, "kVA", formula_id="load.special.default.v1",
                     formula="special load kVA = rules default, or the engineer's override",
                     clause=clause, rules_hash=rules.hash,
                     inputs={"special_load": (name, "", f"rules {rules.ref}"), "default_kva": (estimated, "kVA", f"rules {rules.ref}"),
                             **({"override_kva": (req.override_kva, "kVA", req.override_reason or "override")} if req.override_kva else {})})
        return EstimateResult(kind="special", points=None, breakdown=[], missing=[], income_band=None, category=name,
                              admd_kva=kva, estimated_kva=estimated, overridden=req.override_kva is not None, rules_hash=rules.hash)

    obs = req.observations or {}
    breakdown: list[IndicatorScore] = []
    missing: list[str] = []

    for key, ind in cfg.get("indicators", {}).items():
        v = obs.get(key)
        if v in (None, ""):
            missing.append(key)
            continue
        v = str(v)
        if v not in ind["options"]:
            raise AdmdInputError(f"{key}: unknown option {v!r}; choose one of {', '.join(ind['options'])}")
        breakdown.append(IndicatorScore(indicator=key, label=ind.get("label", key), value=v, points=float(ind["options"][v])))

    for key, ind in cfg.get("multi_indicators", {}).items():
        v = obs.get(key)
        if v is None:
            missing.append(key)
            continue
        chosen = [str(x) for x in (v if isinstance(v, list) else [v])]
        unknown = [c for c in chosen if c not in ind["options"]]
        if unknown:
            raise AdmdInputError(f"{key}: unknown option(s) {', '.join(unknown)}")
        breakdown.append(IndicatorScore(indicator=key, label=ind.get("label", key), value=chosen,
                                        points=float(sum(ind["options"][c] for c in chosen))))

    for key, ind in cfg.get("band_indicators", {}).items():
        v = obs.get(key)
        if v in (None, ""):
            missing.append(key)
            continue
        try:
            num = float(v)
        except (TypeError, ValueError) as e:
            raise AdmdInputError(f"{key}: expected a number, got {v!r}") from e
        if num < 0:
            raise AdmdInputError(f"{key}: must not be negative")
        breakdown.append(IndicatorScore(indicator=key, label=ind.get("label", key), value=num, points=_band_points(num, ind["bands"])))

    total = sum(b.points for b in breakdown)
    band = next(b for b in cfg["income_bands"] if b["max_points"] is None or total <= b["max_points"])
    inputs = {b.indicator: (b.points, "points", f"{b.label} = {b.value}") for b in breakdown}
    points = traced(total, "points", formula_id=ESTIMATE_FORMULA_ID, formula=ESTIMATE_FORMULA, clause=clause,
                    rules_hash=rules.hash, inputs=inputs)
    estimated = float(band["admd_kva"])
    value = req.override_kva or estimated
    admd_inputs: dict[str, Any] = {"points": (total, "points", "indicator score"),
                                   "band": (band["band"], "", f"rules {rules.ref} income_bands"),
                                   "band_admd": (estimated, "kVA", f"rules {rules.ref} income_bands")}
    if req.override_kva:
        admd_inputs["override_kva"] = (req.override_kva, "kVA", req.override_reason or "override")
    admd = traced(value, "kVA", formula_id=ESTIMATE_FORMULA_ID, formula=ESTIMATE_FORMULA, clause=clause,
                  rules_hash=rules.hash, inputs=admd_inputs)
    return EstimateResult(kind="residential", points=points, breakdown=breakdown, missing=missing,
                          income_band=band["band"], category=band["category"], admd_kva=admd, estimated_kva=estimated,
                          overridden=req.override_kva is not None, rules_hash=rules.hash)


def group(req: GroupRequest, rules: RuleSet) -> GroupResult:
    cfg = _cfg(rules)
    div = cfg["diversity"]
    clause = div.get("clause", cfg["clause"])
    k = float(div["k"])
    res = [load for load in req.loads if load.kind == "residential"]
    spec = [load for load in req.loads if load.kind == "special"]
    n = len(res)
    sum_admd = sum(load.kva for load in res)
    special = sum(load.kva for load in spec)

    factor = None
    res_kva_value = 0.0
    if n:
        f = 1 + k / n
        factor = traced(f, "", formula_id=GROUP_FORMULA_ID, formula="diversity factor = 1 + k / N", clause=clause,
                        rules_hash=rules.hash, inputs={"k": (k, "", f"rules {rules.ref} diversity"), "N": (n, "loads", "request")})
        res_kva_value = sum_admd * f
    inputs = {"sum_admd": (sum_admd, "kVA", "Σ residential ADMD"), "N": (n, "loads", "request"), "k": (k, "", f"rules {rules.ref} diversity")}
    res_kva = traced(res_kva_value, "kVA", formula_id=GROUP_FORMULA_ID, formula=GROUP_FORMULA, clause=clause,
                     rules_hash=rules.hash, inputs=inputs)
    total = traced(res_kva_value + special, "kVA", formula_id=GROUP_FORMULA_ID, formula=GROUP_FORMULA, clause=clause,
                   rules_hash=rules.hash, inputs={**inputs, "special_kva": (special, "kVA", "Σ special loads")})
    return GroupResult(residential_count=n, special_count=len(spec), diversity_factor=factor, residential_kva=res_kva,
                       special_kva=special, total_kva=total, rules_hash=rules.hash)


def form_definition(rules: RuleSet) -> dict[str, Any]:
    """The observation form the field app renders, so options never get hard-coded in the client."""
    cfg = _cfg(rules)
    return {
        "rules_hash": rules.hash,
        "indicators": [{"key": k, "label": v.get("label", k), "type": "choice", "options": list(v["options"])} for k, v in cfg.get("indicators", {}).items()],
        "multi_indicators": [{"key": k, "label": v.get("label", k), "type": "multi", "options": list(v["options"])} for k, v in cfg.get("multi_indicators", {}).items()],
        "band_indicators": [{"key": k, "label": v.get("label", k), "type": "number", "unit": v.get("unit", "")} for k, v in cfg.get("band_indicators", {}).items()],
        "special_loads": cfg["special_loads"],
        "income_bands": cfg["income_bands"],
    }
