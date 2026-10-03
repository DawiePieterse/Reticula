"""Three-phase balanced voltage drop. Sample calc proving the traceability pipeline (Phase 0.7)."""

from __future__ import annotations

import math

from pydantic import BaseModel, Field

from ..rules import RuleSet
from ..trace import Traced, traced

FORMULA_ID = "lv.vdrop.3ph.balanced.v1"
FORMULA = "dV = sqrt(3) * I * L * (R*cos(phi) + X*sin(phi)); dV% = dV / V_nominal * 100"


class VoltageDropRequest(BaseModel):
    rules: str = Field(description="authority/version")
    conductor: str
    current_a: float = Field(gt=0)
    length_km: float = Field(gt=0)
    power_factor: float = Field(gt=0, le=1)


class VoltageDropResult(BaseModel):
    drop_v: Traced
    drop_pct: Traced
    limit_pct: float
    passes: bool
    rules_hash: str


def voltage_drop(req: VoltageDropRequest, rules: RuleSet) -> VoltageDropResult:
    c = rules.conductor(req.conductor)
    v_nom = float(rules.data["voltage"]["lv_nominal_v"])
    limit = float(rules.data["voltage"]["lv_max_drop_pct"])
    clause = rules.data["voltage"].get("clause", "")
    sin_phi = math.sqrt(1 - req.power_factor**2)
    dv = math.sqrt(3) * req.current_a * req.length_km * (c.r_ohm_per_km * req.power_factor + c.x_ohm_per_km * sin_phi)
    pct = dv / v_nom * 100

    inputs = {
        "I": (req.current_a, "A", "request"),
        "L": (req.length_km, "km", "request"),
        "cos_phi": (req.power_factor, "", "request"),
        "R": (c.r_ohm_per_km, "ohm/km", f"rules {rules.ref} conductor {c.code} ({c.clause})"),
        "X": (c.x_ohm_per_km, "ohm/km", f"rules {rules.ref} conductor {c.code} ({c.clause})"),
        "V_nominal": (v_nom, "V", f"rules {rules.ref} voltage"),
    }
    drop_v = traced(dv, "V", formula_id=FORMULA_ID, formula=FORMULA, clause=clause, rules_hash=rules.hash, inputs=inputs)
    drop_pct = traced(pct, "%", formula_id=FORMULA_ID, formula=FORMULA, clause=clause, rules_hash=rules.hash, inputs=inputs)
    return VoltageDropResult(drop_v=drop_v, drop_pct=drop_pct, limit_pct=limit, passes=pct <= limit, rules_hash=rules.hash)
