"""Income band and ADMD from site observations, and after-diversity demand for a group of loads.

All scoring, bands, ADMD values, the diversity constant and special-load defaults come from the
rules file's `income_admd` section. Every number is returned as a Traced record.
"""

from __future__ import annotations

import math
from typing import Any, Literal

from pydantic import BaseModel, Field
from scipy.stats import beta as beta_dist

from ..rules import RulesError, RuleSet
from ..trace import Traced, traced

ESTIMATE_FORMULA_ID = "load.admd.indicator-score.v1"
ESTIMATE_FORMULA = "points = Σ indicator points; band = first income band with points <= max_points; ADMD = band ADMD"
GROUP_FORMULA_ID = "load.admd.group-diversity.v1"
GROUP_FORMULA = "S = Σ ADMD_i × (1 + k / N) over N residential loads + Σ special-load kVA"
HB_FORMULA_ID = "load.group.herman-beta.v1"
HB_FORMULA = ("Per phase: μ = Σ n_i·c_i·α_i/(α_i+β_i), σ² = Σ n_i·σ_i², C = Σ n_i·c_i; beta fitted to (μ/C, σ²/C²); "
              "I = C × BetaInv(confidence); S = phases × V × I + Σ special-load kVA")


class AdmdInputError(ValueError):
    pass


class EstimateRequest(BaseModel):
    rules: str
    kind: Literal["residential", "special"] = "residential"
    observations: dict[str, Any] | None = None
    load_class: str | None = Field(default=None, description="Class chosen by the engineer instead of the score")
    special_load: str | None = None
    override_kva: float | None = Field(default=None, gt=0)
    override_reason: str | None = None


class IndicatorScore(BaseModel):
    indicator: str
    label: str
    value: Any
    points: float


class LoadClassInfo(BaseModel):
    code: str
    description: str
    table: str
    table_source: str
    horizon_years: int
    standard_class: str | None = None
    lsm: str | None = None
    income_min_zar: float | None = None
    income_max_zar: float | None = None
    alpha: float
    beta: float
    c_amps: float
    admd_kva: float
    mean_a: float
    sd_a: float
    chosen_by: Literal["score", "engineer"]


class EstimateResult(BaseModel):
    kind: str
    load_class: LoadClassInfo | None = None
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
    load_class: str | None = None


class GroupRequest(BaseModel):
    rules: str
    loads: list[GroupLoad]


class GroupResult(BaseModel):
    method: str = "admd_factor"
    residential_count: int
    special_count: int
    diversity_factor: Traced | None
    residential_kva: Traced
    special_kva: float
    total_kva: Traced
    rules_hash: str
    phases: int | None = None
    confidence_pct: float | None = None
    design_current_a: Traced | None = None


def _cfg(rules: RuleSet) -> dict[str, Any]:
    cfg = rules.data.get("income_admd")
    if not cfg:
        raise RulesError(f"rules {rules.ref} has no income_admd section")
    return cfg


def _moments(a: float, b: float, c: float) -> tuple[float, float]:
    """Mean and standard deviation of a beta load scaled to c amps."""
    return c * a / (a + b), c * math.sqrt(a * b / ((a + b) ** 2 * (a + b + 1)))


def _design_table(cfg: dict[str, Any], rules: RuleSet) -> tuple[str, dict[str, Any]]:
    table_id = cfg.get("design_table")
    tables = rules.data.get("load_tables", {})
    if not table_id or table_id not in tables:
        raise RulesError(f"rules {rules.ref}: income_admd.design_table {table_id!r} is not a load table")
    return table_id, tables[table_id]


def _load_class(cfg: dict[str, Any], rules: RuleSet, code: str, chosen_by: str) -> LoadClassInfo:
    table_id, table = _design_table(cfg, rules)
    row = next((c for c in table["classes"] if c["code"] == code), None)
    if row is None:
        known = ", ".join(c["code"] for c in table["classes"])
        raise AdmdInputError(f"unknown load class {code!r} in {table_id}; known: {known}")
    if row.get("status") == "unverified":
        raise AdmdInputError(f"load class {code!r} in {table_id} is marked unverified: {row.get('note', 'check the source table')}")
    mean, sd = _moments(row["alpha"], row["beta"], row["c_amps"])
    return LoadClassInfo(
        code=row["code"], description=row["description"], table=table_id, table_source=table["source"],
        horizon_years=table["horizon_years"], standard_class=row.get("standard_class"), lsm=row.get("lsm"),
        income_min_zar=row.get("income_min_zar"), income_max_zar=row.get("income_max_zar"),
        alpha=row["alpha"], beta=row["beta"], c_amps=row["c_amps"], admd_kva=row["admd_kva"],
        mean_a=row.get("mean_a", round(mean, 3)), sd_a=row.get("sd_a", round(sd, 3)), chosen_by=chosen_by,  # type: ignore[arg-type]
    )


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

    load_class: LoadClassInfo | None = None
    if req.load_class:
        load_class = _load_class(cfg, rules, req.load_class, "engineer")
    elif "class" in band:
        load_class = _load_class(cfg, rules, band["class"], "score")

    admd_inputs: dict[str, Any]
    if load_class is not None:
        estimated = float(load_class.admd_kva)
        category = load_class.code
        admd_inputs = {
            "points": (total, "points", "indicator score"),
            "band": (band["band"], "", f"rules {rules.ref} income_bands"),
            "load_class": (load_class.code, "", f"{load_class.description} ({load_class.chosen_by})"),
            "alpha": (load_class.alpha, "", load_class.table_source),
            "beta": (load_class.beta, "", load_class.table_source),
            "c": (load_class.c_amps, "A", load_class.table_source),
            "class_admd": (estimated, "kVA", load_class.table_source),
        }
        formula = "ADMD = published ADMD of the load class (chosen by the engineer, or from the indicator score band)"
        clause = f"{clause}; {load_class.table_source}"
    else:
        estimated = float(band["admd_kva"])
        category = band["category"]
        admd_inputs = {"points": (total, "points", "indicator score"),
                       "band": (band["band"], "", f"rules {rules.ref} income_bands"),
                       "band_admd": (estimated, "kVA", f"rules {rules.ref} income_bands")}
        formula = ESTIMATE_FORMULA
    value = req.override_kva or estimated
    if req.override_kva:
        admd_inputs["override_kva"] = (req.override_kva, "kVA", req.override_reason or "override")
    admd = traced(value, "kVA", formula_id=ESTIMATE_FORMULA_ID, formula=formula, clause=clause,
                  rules_hash=rules.hash, inputs=admd_inputs)
    return EstimateResult(kind="residential", load_class=load_class, points=points, breakdown=breakdown, missing=missing,
                          income_band=band["band"], category=category, admd_kva=admd, estimated_kva=estimated,
                          overridden=req.override_kva is not None, rules_hash=rules.hash)


def group(req: GroupRequest, rules: RuleSet) -> GroupResult:
    cfg = _cfg(rules)
    div = cfg["diversity"]
    if div.get("method") == "herman_beta":
        return _group_herman_beta(req, rules, cfg, div)
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


def _group_herman_beta(req: GroupRequest, rules: RuleSet, cfg: dict[str, Any], div: dict[str, Any]) -> GroupResult:
    """Herman-Beta demand for a group of residential loads, with special loads added at their kVA.

    Each consumer's current is a beta variable scaled to its class's c. Consumers are shared equally over
    the phases (balanced allocation; fewer than three consumers stay on one phase). Per phase, the sum's
    mean and variance are matched by a beta on [0, Σc], and the design current is its quantile at the
    confidence level.
    """
    conf = float(div["confidence_pct"])
    clause = div.get("clause", cfg["clause"])
    _, table = _design_table(cfg, rules)
    voltage = float(table["phase_voltage_v"])

    res = [load for load in req.loads if load.kind == "residential"]
    spec = [load for load in req.loads if load.kind == "special"]
    special = sum(load.kva for load in spec)
    missing = [load.id for load in res if not load.load_class]
    if missing:
        raise AdmdInputError(f"Herman-Beta needs a load class for every residential load; missing for {', '.join(missing[:5])}")

    counts: dict[str, int] = {}
    for load in res:
        counts[load.load_class] = counts.get(load.load_class, 0) + 1  # type: ignore[index]
    classes = {code: _load_class(cfg, rules, code, "score") for code in counts}

    n = len(res)
    phases = int(div.get("phases", 3)) if n >= 3 else 1
    mean = var = cap = 0.0
    for code, count in counts.items():
        lc = classes[code]
        mu, sd = _moments(lc.alpha, lc.beta, lc.c_amps)
        share = count / phases
        mean += share * mu
        var += share * sd * sd
        cap += share * lc.c_amps

    inputs: dict[str, Any] = {f"n_{code}": (count, "loads", classes[code].description) for code, count in counts.items()}
    inputs.update({"phases": (phases, "", "balanced allocation" if phases > 1 else "single phase (fewer than 3 loads)"),
                   "confidence": (conf, "%", f"rules {rules.ref} diversity"), "V": (voltage, "V", table["source"])})

    if n == 0:
        current = 0.0
    else:
        m, v = mean / cap, var / (cap * cap)
        k = m * (1 - m) / v - 1
        current = cap * float(beta_dist.ppf(conf / 100, m * k, (1 - m) * k))
    design = traced(current, "A", formula_id=HB_FORMULA_ID, formula=HB_FORMULA, clause=clause, rules_hash=rules.hash,
                    inputs={**inputs, "mean_per_phase": (round(mean, 4), "A", "Σ n·μ"), "sd_per_phase": (round(math.sqrt(var), 4), "A", "√Σ n·σ²"),
                            "c_per_phase": (cap, "A", "Σ n·c")})
    res_kva_value = phases * voltage * current / 1000
    res_kva = traced(res_kva_value, "kVA", formula_id=HB_FORMULA_ID, formula=HB_FORMULA, clause=clause, rules_hash=rules.hash,
                     inputs={**inputs, "I_design": (round(current, 4), "A", "per phase")})
    sum_admd = sum(classes[c].admd_kva * k for c, k in counts.items())
    factor = None
    if n:
        factor = traced(res_kva_value / sum_admd, "", formula_id=HB_FORMULA_ID, formula="factor = S_residential / Σ ADMD",
                        clause=clause, rules_hash=rules.hash,
                        inputs={"S_residential": (round(res_kva_value, 4), "kVA", "Herman-Beta"), "sum_admd": (round(sum_admd, 4), "kVA", table["source"])})
    total = traced(res_kva_value + special, "kVA", formula_id=HB_FORMULA_ID, formula=HB_FORMULA, clause=clause, rules_hash=rules.hash,
                   inputs={**inputs, "S_residential": (round(res_kva_value, 4), "kVA", "Herman-Beta"), "special_kva": (special, "kVA", "Σ special loads")})
    return GroupResult(method="herman_beta", residential_count=n, special_count=len(spec), diversity_factor=factor,
                       residential_kva=res_kva, special_kva=special, total_kva=total, rules_hash=rules.hash,
                       phases=phases, confidence_pct=conf, design_current_a=design)


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
        "load_classes": _form_classes(cfg, rules),
    }


def _form_classes(cfg: dict[str, Any], rules: RuleSet) -> list[dict[str, Any]]:
    if not cfg.get("design_table"):
        return []
    table_id, table = _design_table(cfg, rules)
    return [{"code": c["code"], "description": c["description"], "table": table_id, "admd_kva": c["admd_kva"],
             "income_min_zar": c.get("income_min_zar"), "income_max_zar": c.get("income_max_zar"),
             "usable": c.get("status") != "unverified"} for c in table["classes"]]
