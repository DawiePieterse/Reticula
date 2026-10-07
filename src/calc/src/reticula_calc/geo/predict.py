"""Predict each building's type from OSM tags, municipal zoning and footprint size.

Every value comes from the rules file's `building_prediction` section. Each prediction lists all the
signals that fed it, so the inspector sees why, and low-confidence buildings can be visited first.
"""

from __future__ import annotations

from typing import Any

from pydantic import BaseModel, Field

from ..rules import RulesError, RuleSet

BUILDING_TYPES = ("house", "shop", "school", "other")


class BuildingInput(BaseModel):
    id: str
    area_m2: float | None = Field(default=None, ge=0)
    tags: dict[str, str] = {}
    zoning: str | None = None


class PredictRequest(BaseModel):
    rules: str
    buildings: list[BuildingInput]


class Signal(BaseModel):
    source: str
    type: str
    confidence: float


class Prediction(BaseModel):
    id: str
    type: str
    confidence: float
    source: str
    low_confidence: bool
    signals: list[Signal]
    load_class: str | None = None
    load_class_source: str | None = None


class PredictResponse(BaseModel):
    rules_hash: str
    method: str
    predictions: list[Prediction]


def _config(rules: RuleSet) -> dict[str, Any]:
    cfg = rules.data.get("building_prediction")
    if not cfg:
        raise RulesError(f"rules {rules.ref} has no building_prediction section")
    return cfg


def _tag_signal(tags: dict[str, str], cfg: dict[str, Any]) -> Signal | None:
    best: Signal | None = None
    for rule in cfg.get("osm_tags", []):
        key, _, value = rule["match"].partition("=")
        actual = tags.get(key)
        if actual is None or (value != "*" and actual != value):
            continue
        s = Signal(source=f"osm:{key}={actual}", type=rule["type"], confidence=float(rule["confidence"]))
        if best is None or s.confidence > best.confidence:
            best = s
    return best


def _zoning_signal(zoning: str | None, cfg: dict[str, Any]) -> Signal | None:
    if not zoning:
        return None
    z = zoning.lower()
    for rule in cfg.get("zoning", []):
        if rule["match"].lower() in z:
            return Signal(source=f"zoning:{zoning}", type=rule["type"], confidence=float(rule["confidence"]))
    return None


def _footprint_signal(area: float | None, cfg: dict[str, Any]) -> Signal | None:
    if area is None:
        return None
    for band in cfg.get("footprint_m2", []):
        lo, hi = band.get("min", 0), band.get("max")
        if area >= lo and (hi is None or area < hi):
            return Signal(source=f"footprint:{area:.0f} m²", type=band["type"], confidence=float(band["confidence"]))
    return None


def load_class_for(btype: str, zoning: str | None, rules: RuleSet) -> tuple[str | None, str | None]:
    """The load class of a building not yet inspected, from its predicted type and its stand's zoning (plans 1.3, 2.0).

    Only residential types get a class. The rules file's `load_classes.by_zone` entries are matched within the zoning
    text, case-insensitively, and the longest match wins; otherwise the section's default applies. Returns the class
    code and where it came from, or (None, None) when the rules file has no such section or the type is not residential.
    """
    sec = rules.data.get("load_classes")
    if not sec or btype not in sec["residential_types"]:
        return None, None
    z = (zoning or "").lower()
    hits = [m for m in sec["by_zone"] if m["zone"].lower() in z] if z else []
    if hits:
        best = max(hits, key=lambda m: len(m["zone"]))
        return best["load_class"], f"zoning '{zoning}' (rules {rules.ref} load_classes: '{best['zone']}')"
    if sec.get("default"):
        return sec["default"], f"default for a {btype} (rules {rules.ref} load_classes)"
    return None, None


def predict(req: PredictRequest, rules: RuleSet) -> PredictResponse:
    cfg = _config(rules)
    boost = float(cfg.get("agreement_boost", 0.0))
    low = float(cfg.get("low_confidence_below", 0.6))
    out: list[Prediction] = []
    for b in req.buildings:
        signals = [s for s in (_tag_signal(b.tags, cfg), _zoning_signal(b.zoning, cfg), _footprint_signal(b.area_m2, cfg)) if s]
        if not signals:
            lc, lc_src = load_class_for("other", b.zoning, rules)
            out.append(Prediction(id=b.id, type="other", confidence=0.0, source="none", low_confidence=True, signals=[],
                                load_class=lc, load_class_source=lc_src))
            continue
        # Score each candidate type by its strongest signal, plus a boost per extra signal that agrees.
        scored: list[tuple[float, Signal]] = []
        for t in {s.type for s in signals}:
            agreeing = sorted((s for s in signals if s.type == t), key=lambda s: -s.confidence)
            scored.append((min(0.99, agreeing[0].confidence + boost * (len(agreeing) - 1)), agreeing[0]))
        score, lead = max(scored, key=lambda x: x[0])
        lc, lc_src = load_class_for(lead.type, b.zoning, rules)
        out.append(Prediction(id=b.id, type=lead.type, confidence=round(score, 3), source=lead.source,
                              low_confidence=score < low, signals=signals, load_class=lc, load_class_source=lc_src))
    return PredictResponse(rules_hash=rules.hash, method=str(cfg.get("clause", "")), predictions=out)
