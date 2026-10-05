"""Predict each building's type from OSM tags, municipal zoning, footprint size and, where given, the rooftop signal.

Every value comes from the rules file's `building_prediction` section. Each prediction lists all the
signals that fed it, so the inspector sees why, and low-confidence buildings can be visited first.
"""

from __future__ import annotations

from typing import Any

from pydantic import BaseModel, Field

from ..rules import RulesError, RuleSet

BUILDING_TYPES = ("house", "shop", "school", "other")


class Signal(BaseModel):
    source: str
    type: str
    confidence: float


class BuildingInput(BaseModel):
    id: str
    area_m2: float | None = Field(default=None, ge=0)
    tags: dict[str, str] = {}
    zoning: str | None = None
    #: Signals worked out elsewhere, e.g. the rooftop classifier (plan 1.3).
    extra_signals: list[Signal] | None = None


class PredictRequest(BaseModel):
    rules: str
    buildings: list[BuildingInput]


class Prediction(BaseModel):
    id: str
    type: str
    confidence: float
    source: str
    low_confidence: bool
    signals: list[Signal]


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


def predict(req: PredictRequest, rules: RuleSet) -> PredictResponse:
    cfg = _config(rules)
    boost = float(cfg.get("agreement_boost", 0.0))
    low = float(cfg.get("low_confidence_below", 0.6))
    out: list[Prediction] = []
    for b in req.buildings:
        signals = [s for s in (_tag_signal(b.tags, cfg), _zoning_signal(b.zoning, cfg), _footprint_signal(b.area_m2, cfg)) if s]
        signals += [s for s in b.extra_signals or [] if s.type in BUILDING_TYPES]
        if not signals:
            out.append(Prediction(id=b.id, type="other", confidence=0.0, source="none", low_confidence=True, signals=[]))
            continue
        # Score each candidate type by its strongest signal, plus a boost per extra signal that agrees.
        scored: list[tuple[float, Signal]] = []
        for t in {s.type for s in signals}:
            agreeing = sorted((s for s in signals if s.type == t), key=lambda s: -s.confidence)
            scored.append((min(0.99, agreeing[0].confidence + boost * (len(agreeing) - 1)), agreeing[0]))
        score, lead = max(scored, key=lambda x: x[0])
        out.append(Prediction(id=b.id, type=lead.type, confidence=round(score, 3), source=lead.source,
                              low_confidence=score < low, signals=signals))
    return PredictResponse(rules_hash=rules.hash, method=str(cfg.get("clause", "")), predictions=out)
