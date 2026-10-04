"""Conductor and cable library from the rules file (plan 2.3)."""

from __future__ import annotations

from dataclasses import dataclass
from typing import Literal

from ..rules import RuleSet
from ..rules.loader import RulesError

Construction = Literal["overhead", "underground"]


@dataclass(frozen=True)
class CableType:
    code: str
    description: str
    kind: Construction
    role: str
    r_ohm_per_km: float
    x_ohm_per_km: float
    neutral_r_ohm_per_km: float
    neutral_x_ohm_per_km: float
    rating_a: float
    clause: str
    status: str
    material: str | None = None
    area_mm2: float | None = None
    k: float | None = None
    fuse_a: float | None = None
    mass_kg_per_m: float | None = None
    diameter_mm: float | None = None
    uts_kn: float | None = None
    e_gpa: float | None = None
    alpha_per_c: float | None = None

    def z_eff(self, pf: float) -> tuple[float, float]:
        """Effective resistance-plus-reactance per metre for phase and neutral: R cos φ + X sin φ (Ω/m)."""
        sin = (1 - pf * pf) ** 0.5
        return ((self.r_ohm_per_km * pf + self.x_ohm_per_km * sin) / 1000,
                (self.neutral_r_ohm_per_km * pf + self.neutral_x_ohm_per_km * sin) / 1000)


def library(rules: RuleSet) -> dict[str, CableType]:
    out: dict[str, CableType] = {}
    for c in rules.data["conductors"]:
        r, x = float(c["r_ohm_per_km"]), float(c["x_ohm_per_km"])
        out[c["code"]] = CableType(
            code=c["code"], description=c.get("description", ""), kind=c["kind"], role=c.get("role", "feeder"),
            r_ohm_per_km=r, x_ohm_per_km=x,
            neutral_r_ohm_per_km=float(c.get("neutral_r_ohm_per_km", r)), neutral_x_ohm_per_km=float(c.get("neutral_x_ohm_per_km", x)),
            rating_a=float(c["rating_a"]), clause=c.get("clause", ""), status=c.get("status", "unverified"),
            material=c.get("material"), area_mm2=c.get("area_mm2"), k=c.get("k"), fuse_a=c.get("fuse_a"),
            mass_kg_per_m=c.get("mass_kg_per_m"), diameter_mm=c.get("diameter_mm"), uts_kn=c.get("uts_kn"),
            e_gpa=c.get("e_gpa"), alpha_per_c=c.get("alpha_per_c"),
        )
    return out


def lv_config(rules: RuleSet) -> dict:
    cfg = rules.data.get("lv_design")
    if not cfg:
        raise RulesError(f"rules {rules.ref} have no lv_design section; use eskom/0.3.0 or later")
    return cfg


def feeder_options(rules: RuleSet, construction: Construction) -> list[CableType]:
    """The conductors the rules allow for LV feeders of one construction, smallest first."""
    lib = library(rules)
    codes = lv_config(rules)["feeder_conductors"].get(construction, [])
    missing = [c for c in codes if c not in lib]
    if missing:
        raise RulesError(f"lv_design lists conductors not in the library: {', '.join(missing)}")
    if not codes:
        raise RulesError(f"rules {rules.ref} allow no {construction} LV feeder conductors")
    return sorted((lib[c] for c in codes), key=lambda c: (c.rating_a, c.area_mm2 or 0))
