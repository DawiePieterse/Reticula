"""Herman-Beta demand of a group of connected loads, per phase: the one sum the transformer, MV and bulk stages share.

Each residential load with a class of the design table adds its beta moments (mean, variance, scaling current c) to
its phase; special loads, residential loads without a class, and three-phase loads add a fixed current (three-phase
loads a third of their kVA on each phase). The design current of a phase is the beta fitted to the summed moments on
[0, Σc] at the confidence level, plus the fixed current. A three-phase supply is loaded by its worst phase, so its
design kVA is 3 · V_phase · max(I_p).
"""

from __future__ import annotations

import math
from dataclasses import dataclass, field

from ..calcs.admd import AdmdInputError, _cfg, _load_class, _moments
from ..lv.analysis import PHASES, _quantile
from ..rules import RuleSet

DEMAND_ID = "design.demand.herman-beta.v1"
DEMAND_FORMULA = ("S = 3·V_ph·max_p(I_p); I_p = beta(Σμ, Σσ² on [0, Σc]) at the confidence level + Σ fixed current; "
                  "three-phase and special loads fixed, three-phase split equally")


@dataclass
class Demand:
    mu: dict[str, float] = field(default_factory=lambda: dict.fromkeys(PHASES, 0.0))
    var: dict[str, float] = field(default_factory=lambda: dict.fromkeys(PHASES, 0.0))
    c: dict[str, float] = field(default_factory=lambda: dict.fromkeys(PHASES, 0.0))
    det: dict[str, float] = field(default_factory=lambda: dict.fromkeys(PHASES, 0.0))
    loads: int = 0

    def add(self, other: Demand) -> None:
        for p in PHASES:
            self.mu[p] += other.mu[p]
            self.var[p] += other.var[p]
            self.c[p] += other.c[p]
            self.det[p] += other.det[p]
        self.loads += other.loads

    def currents(self, conf: float) -> dict[str, float]:
        return {p: _quantile(self.mu[p], self.var[p], 0.0, self.c[p], conf) + self.det[p] for p in PHASES}

    def kva(self, conf: float, v_ph: float) -> float:
        return 3 * v_ph * max(self.currents(conf).values()) / 1000


class DemandModel:
    """Turns connected loads into per-phase demand, from the rules file's design load table and confidence level."""

    def __init__(self, rules: RuleSet):
        self.rules = rules
        self.cfg = _cfg(rules)
        self.conf = float(self.cfg["diversity"]["confidence_pct"]) / 100
        self.v_ph = float(rules.data["voltage"]["lv_nominal_v"]) / math.sqrt(3)
        self._classes: dict[str, tuple[float, float, float] | None] = {}
        self.unclassed: list[str] = []

    def _class(self, code: str) -> tuple[float, float, float] | None:
        if code not in self._classes:
            try:
                lc = _load_class(self.cfg, self.rules, code, "score")
                mu, sd = _moments(lc.alpha, lc.beta, lc.c_amps)
                self._classes[code] = (mu, sd * sd, lc.c_amps)
            except AdmdInputError:
                self._classes[code] = None
        return self._classes[code]

    def load(self, phase: str | None, kva: float, kind: str, load_class: str | None, name: str = "") -> Demand:
        d = Demand(loads=1)
        if phase is None:
            return d
        if phase == "RWB":
            for p in PHASES:
                d.det[p] += kva * 1000 / (3 * self.v_ph)
            return d
        cls = self._class(load_class) if kind == "residential" and load_class else None
        if cls is not None:
            d.mu[phase], d.var[phase], d.c[phase] = cls
        else:
            if kind == "residential":
                self.unclassed.append(name)
            d.det[phase] += kva * 1000 / self.v_ph
        return d
