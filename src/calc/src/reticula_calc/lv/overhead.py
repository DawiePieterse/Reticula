"""Overhead line checks (plan 2.5): span length, conductor tension, sag and ground clearance, pole choice and stays.

Each span is strung at the everyday tension (a share of the conductor's breaking load at the everyday temperature,
no wind). The parabolic change-of-state equation gives the horizontal tension in the other cases:

    H2²·(H2 − H1 + E·A·w1²·L²/(24·H1²) + E·A·α·(t2 − t1)) = E·A·w2²·L²/24

where w is the resultant load per metre (weight, and wind on the conductor's projected area). The maximum-load case
must stay within its share of the breaking load; the sag in the maximum-sag case, sag = w·L²/(8·H), sets the lowest
point, which must clear the ground (more over roads). Ground is taken as level between poles.

Poles: the shortest pole whose attachment height gives the clearance on both sides, then the first pole of that
length whose tip load carries the horizontal resultant of the conductor tensions (and wind on the conductors).
Terminal poles and deviations above the rules' angle get a stay; stays act at 45°, so each carries √2 times its share
of the resultant. A second stay is added where one is not enough; more than two is reported as a failure.
"""

from __future__ import annotations

import math

import numpy as np
from pydantic import BaseModel

from ..rules import RuleSet
from ..rules.loader import RulesError
from .analysis import Check
from .build import deviation_deg
from .library import CableType, library
from .model import LvNetwork

G = 9.81


class SpanResult(BaseModel):
    branch_id: str
    length_m: float
    everyday_kn: float
    max_load_kn: float
    max_load_pct_uts: float
    sag_m: float
    clearance_m: float
    required_m: float
    crosses_road: bool


class PoleResult(BaseModel):
    node_id: str
    pole: str
    stay: bool
    deviation_deg: float
    resultant_kn: float
    terminal: bool
    stays: int = 0


class OverheadResult(BaseModel):
    spans: list[SpanResult]
    poles: list[PoleResult]
    checks: list[Check]
    stays: int


def tension_after(h1: float, w1: float, t1: float, w2: float, t2: float, length: float, ea: float, alpha: float) -> float:
    """Horizontal tension (N) in state 2 from state 1, by the parabolic change-of-state equation."""
    b = ea * w1 * w1 * length * length / (24 * h1 * h1) - h1 + ea * alpha * (t2 - t1)
    d = ea * w2 * w2 * length * length / 24
    roots = np.roots([1.0, b, 0.0, -d])
    real = [r.real for r in roots if abs(r.imag) < 1e-6 * max(1.0, abs(r.real)) and r.real > 0]
    if not real:
        raise ValueError("change-of-state equation has no positive root")
    return max(real)


def _loads(cable: CableType, wind_pa: float) -> float:
    weight = (cable.mass_kg_per_m or 0) * G
    wind = wind_pa * (cable.diameter_mm or 0) / 1000
    return math.hypot(weight, wind)


def check_overhead(network: LvNetwork, rules: RuleSet) -> OverheadResult:
    oh = rules.data.get("overhead")
    if not oh:
        raise RulesError(f"rules {rules.ref} have no overhead section")
    lib = library(rules)
    clause = oh.get("clause", "")
    ed, ml, ms = oh["everyday"], oh["max_load"], oh["max_sag"]
    poles = sorted(oh["poles"], key=lambda p: (p["length_m"], p["max_tip_load_kn"]))
    below_top = float(oh.get("attachment_below_top_m", 0.3))

    def attach_height(pole: dict) -> float:
        return pole["length_m"] - (0.1 * pole["length_m"] + 0.6) - below_top

    g = network.graph()
    nodes = {n.id: n for n in network.nodes}
    spans: list[SpanResult] = []
    checks: list[Check] = []
    need_height: dict[str, float] = {}
    tension_at: dict[str, list[tuple[float, float, float, float]]] = {}  # node -> (dx, dy, max-load tension N, wind N)

    def xy(nid: str) -> tuple[float, float]:
        n = nodes[nid]
        return (n.lon * 111_320 * math.cos(math.radians(n.lat)), n.lat * 110_540)

    for b in network.branches:
        if b.kind != "feeder" or b.construction != "overhead":
            continue
        cable = lib[b.conductor]
        if not (cable.uts_kn and cable.mass_kg_per_m and cable.area_mm2 and cable.e_gpa and cable.alpha_per_c):
            raise RulesError(f"conductor {cable.code} lacks the mechanical data (mass, diameter, breaking load, E, α) for sag checks")
        ea = cable.e_gpa * 1e9 * 4 * cable.area_mm2 * 1e-6  # all four cores carry the tension
        uts = cable.uts_kn * 1000
        length = b.length_m
        h_ed = ed["max_pct_uts"] / 100 * uts
        w_ed = _loads(cable, ed["wind_pa"])
        h_ml = tension_after(h_ed, w_ed, ed["temp_c"], _loads(cable, ml["wind_pa"]), ml["temp_c"], length, ea, cable.alpha_per_c)
        w_ms = _loads(cable, ms["wind_pa"])
        h_ms = tension_after(h_ed, w_ed, ed["temp_c"], w_ms, ms["temp_c"], length, ea, cable.alpha_per_c)
        sag = w_ms * length * length / (8 * h_ms)
        required = float(oh["min_road_clearance_m"] if b.crosses_road else oh["min_ground_clearance_m"])
        for nid in (b.from_id, b.to_id):
            need_height[nid] = max(need_height.get(nid, 0.0), required + sag)
        (x1, y1), (x2, y2) = xy(b.from_id), xy(b.to_id)
        dx, dy = x2 - x1, y2 - y1
        norm = math.hypot(dx, dy) or 1.0
        wind_n = ml["wind_pa"] * (cable.diameter_mm or 0) / 1000 * length / 2
        tension_at.setdefault(b.from_id, []).append((dx / norm, dy / norm, h_ml, wind_n))
        tension_at.setdefault(b.to_id, []).append((-dx / norm, -dy / norm, h_ml, wind_n))
        pct = 100 * h_ml / uts
        spans.append(SpanResult(branch_id=b.id, length_m=length, everyday_kn=round(h_ed / 1000, 2), max_load_kn=round(h_ml / 1000, 2),
                                max_load_pct_uts=round(pct, 1), sag_m=round(sag, 3), clearance_m=0.0, required_m=required, crosses_road=b.crosses_road))
        checks.append(Check(code="span_length", subject=b.id, passed=length <= float(oh["max_span_m"]) + 0.01, value=round(length, 1),
                            limit=float(oh["max_span_m"]), unit="m", message=f"Span {b.id} is {length:.1f} m", clause=clause))
        checks.append(Check(code="conductor_tension", subject=b.id, passed=pct <= ml["max_pct_uts"] + 1e-9, value=round(pct, 1),
                            limit=float(ml["max_pct_uts"]), unit="% UTS",
                            message=f"Span {b.id} tension at {ml['temp_c']} °C with {ml['wind_pa']} Pa wind is {h_ml / 1000:.1f} kN ({pct:.0f} % of breaking load)",
                            clause=clause))

    # Poles: height for clearance, then strength; stays where needed.
    pole_results: list[PoleResult] = []
    chosen: dict[str, dict] = {}
    for nid, forces in tension_at.items():
        need = need_height.get(nid, 0.0)
        tall_enough = [p for p in poles if attach_height(p) >= need]
        terminal = len(forces) == 1
        fx = sum(f[0] * f[2] for f in forces)
        fy = sum(f[1] * f[2] for f in forces)
        resultant = math.hypot(fx, fy) + sum(f[3] for f in forces)
        dev = 0.0
        if len(forces) == 2:
            preds = list(g.predecessors(nid))
            succs = [s for s in g.successors(nid) if g.edges[nid, s]["branch"].kind == "feeder"]
            if preds and succs:
                dev = deviation_deg(xy(preds[0]), xy(nid), xy(succs[0]))
        stay = terminal or dev > float(oh["stay_angle_deg"])
        if not tall_enough:
            pole = poles[-1]
            checks.append(Check(code="pole_height", subject=nid, passed=False, value=round(attach_height(pole), 2), limit=round(need, 2), unit="m",
                                message=f"No pole in the rules is tall enough at {nid} (needs {need:.1f} m attachment height)", clause=clause))
        else:
            length = tall_enough[0]["length_m"]
            same = [p for p in tall_enough if p["length_m"] == length]
            strong = [p for p in same if p["max_tip_load_kn"] * 1000 >= resultant]
            pole = strong[0] if strong else same[-1]
            if not strong and not stay:
                stay = True
        chosen[nid] = pole
        n_stays = 0
        if stay:
            cap = float(oh.get("stay_capacity_kn", 0)) * 1000
            pull = resultant * math.sqrt(2)
            n_stays = max(1, math.ceil(pull / cap - 1e-9)) if cap else 1
            if cap:
                per = pull / min(n_stays, 2)
                checks.append(Check(code="stay_capacity", subject=nid, passed=n_stays <= 2, value=round(per / 1000, 2), limit=cap / 1000, unit="kN",
                                    message=f"{min(n_stays, 2)} stay(s) at {nid} each carry {per / 1000:.1f} kN", clause=clause))
                n_stays = min(n_stays, 2)
        else:
            checks.append(Check(code="pole_tip_load", subject=nid, passed=pole["max_tip_load_kn"] * 1000 >= resultant,
                                value=round(resultant / 1000, 2), limit=pole["max_tip_load_kn"], unit="kN",
                                message=f"Pole {nid} ({pole['code']}) carries {resultant / 1000:.2f} kN at the top", clause=clause))
        pole_results.append(PoleResult(node_id=nid, pole=pole["code"], stay=stay, deviation_deg=round(dev, 1),
                                       resultant_kn=round(resultant / 1000, 2), terminal=terminal, stays=n_stays))
        node = nodes[nid]
        node.pole, node.stay, node.stays = pole["code"], stay, n_stays

    for sp in spans:
        b = next(x for x in network.branches if x.id == sp.branch_id)
        low = min(attach_height(chosen[b.from_id]), attach_height(chosen[b.to_id]))
        sp.clearance_m = round(low - sp.sag_m, 2)
        checks.append(Check(code="ground_clearance", subject=b.id, passed=sp.clearance_m >= sp.required_m - 1e-9, value=sp.clearance_m,
                            limit=sp.required_m, unit="m",
                            message=f"Span {b.id} clears the {'road' if sp.crosses_road else 'ground'} by {sp.clearance_m:.2f} m at {ms['temp_c']} °C (sag {sp.sag_m:.2f} m)",
                            clause=clause))
    return OverheadResult(spans=spans, poles=pole_results, checks=checks, stays=sum(p.stays for p in pole_results))
