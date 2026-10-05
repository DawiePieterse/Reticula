"""LV conductor library (plan 2.3): the rules file's conductors and cables, with ratings and short-circuit withstand.

Every value comes from the rules file. Each conductor says which of its values are still placeholders, so a
result that uses one can be flagged. Rating and withstand are traced like any other calculated value.
"""

from __future__ import annotations

import math
from typing import Literal

from pydantic import BaseModel

from ..rules import RulesError, RuleSet
from ..rules.loader import Conductor
from ..trace import Traced, traced

Installation = Literal["ground", "pipe", "air"]

RATING_ID = "lv.cable.rating.v1"
RATING_FORMULA = "I_rated = rating for the installation at standard conditions (de-rating is plan 2.6)"
WITHSTAND_ID = "lv.cable.withstand.v1"
WITHSTAND_FORMULA = "I_sc = K × A / √t (kA; A in mm², t in s)"


class ConductorOut(BaseModel):
    code: str
    description: str
    kind: str
    material: str | None
    size_mm2: float | None
    cores: int | None
    uses: list[str]
    r_ohm_per_km: float
    x_ohm_per_km: float
    rating_a: float
    ratings_a: dict[str, float]
    fault_k: float | None
    one_second_ka: float | None
    """Short-circuit withstand for 1 s, from fault_k: a convenience for comparing cables."""
    placeholder: list[str]
    clause: str
    rating_clause: str
    index: str


class Library(BaseModel):
    rules_ref: str
    rules_hash: str
    conductors: list[ConductorOut]


def library(rules: RuleSet) -> Library:
    return Library(rules_ref=rules.ref, rules_hash=rules.hash, conductors=[_out(c) for c in rules.conductors()])


def _out(c: Conductor) -> ConductorOut:
    return ConductorOut(
        code=c.code, description=c.description, kind=c.kind, material=c.material, size_mm2=c.size_mm2, cores=c.cores,
        uses=list(c.uses), r_ohm_per_km=c.r_ohm_per_km, x_ohm_per_km=c.x_ohm_per_km, rating_a=c.rating_a,
        ratings_a=dict(c.ratings_a), fault_k=c.fault_k,
        one_second_ka=round(c.fault_k * c.size_mm2, 3) if c.fault_k and c.size_mm2 else None,
        placeholder=list(c.placeholder), clause=c.clause, rating_clause=c.rating_clause, index=c.index,
    )


def _source(rules: RuleSet, c: Conductor, field: str) -> str:
    flag = " (placeholder)" if field in c.placeholder else ""
    return f"rules {rules.ref} conductor {c.code} ({c.rating_clause or c.clause}){flag}"


def rating(rules: RuleSet, code: str, installation: Installation | None = None) -> Traced:
    """The conductor's continuous rating, as normally installed or for the given installation."""
    c = rules.conductor(code)
    ratings = dict(c.ratings_a)
    if installation is None:
        value, field, how = c.rating_a, "rating_a", "as normally installed"
    elif installation in ratings:
        value, field, how = ratings[installation], "ratings_a", f"installed {installation}"
    else:
        raise RulesError(f"conductor {code} in rules {rules.ref} has no rating for installation {installation!r}")
    return traced(value, "A", formula_id=RATING_ID, formula=RATING_FORMULA, clause=c.rating_clause or c.clause, rules_hash=rules.hash,
                  inputs={"installation": (how, "", "request"), "I_rated": (value, "A", _source(rules, c, field))})


def withstand(rules: RuleSet, code: str, duration_s: float) -> Traced:
    """The largest short-circuit current the conductor withstands for the duration, from its K constant."""
    c = rules.conductor(code)
    if c.fault_k is None or c.size_mm2 is None:
        raise RulesError(f"conductor {code} in rules {rules.ref} has no fault constant")
    if duration_s <= 0:
        raise RulesError("duration must be positive")
    value = c.fault_k * c.size_mm2 / math.sqrt(duration_s)
    return traced(value, "kA", formula_id=WITHSTAND_ID, formula=WITHSTAND_FORMULA, clause=c.rating_clause or c.clause, rules_hash=rules.hash,
                  inputs={"K": (c.fault_k, "kA·√s/mm²", _source(rules, c, "fault_k")), "A": (c.size_mm2, "mm²", f"rules {rules.ref} conductor {c.code}"),
                          "t": (duration_s, "s", "request")})
