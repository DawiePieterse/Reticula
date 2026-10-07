"""Run every hand-worked case under /test-cases."""

import json
from pathlib import Path

import pytest
from test_lv_network import build, route, site

from reticula_calc.calcs.admd import EstimateRequest, GroupRequest, estimate, group
from reticula_calc.calcs.voltage_drop import VoltageDropRequest, voltage_drop
from reticula_calc.design import overhead, underground
from reticula_calc.design.run import DesignRequest, run
from reticula_calc.lv import placement
from reticula_calc.lv.analysis import AnalyseRequest, LoadAt, analyse
from reticula_calc.rules import load_rules

CASES_DIR = Path(__file__).resolve().parents[3] / "test-cases"

def _run_voltage_drop(inp: dict) -> dict:
    r = voltage_drop(VoltageDropRequest(**inp), load_rules(inp["rules"]))
    return {"drop_v": r.drop_v.value, "drop_pct": r.drop_pct.value, "passes": r.passes}


def _run_admd(inp: dict) -> dict:
    r = estimate(EstimateRequest(**inp), load_rules(inp["rules"]))
    return {"points": r.points.value, "admd_kva": r.admd_kva.value, "category": r.category}


def _run_admd_group(inp: dict) -> dict:
    r = group(GroupRequest(**inp), load_rules(inp["rules"]))
    return {"diversity_factor": r.diversity_factor.value, "residential_kva": r.residential_kva.value, "total_kva": r.total_kva.value}


def _run_hb_group(inp: dict) -> dict:
    r = group(GroupRequest(**inp), load_rules(inp["rules"]))
    return {"design_current_a": r.design_current_a.value, "residential_kva": r.residential_kva.value}


def _run_oh_span(inp: dict) -> dict:
    """An LV or MV route of route_m east of a transformer: poles placed, then the overhead checks."""
    rs = load_rules(inp["rules"])
    cands = [route("a", (0, 0), (inp["route_m"], 0)), site("t", "transformer", 0, 0)]
    net = build(*cands, *overhead.place_poles(build(*cands, rules=inp["rules"]), rs, inp["level"]), rules=inp["rules"])
    r = overhead.check(net, rs, inp["level"], {}, inp["conductor"], set())
    [sec] = r.sections
    return {"spans": sec.spans, "ruling_span_m": sec.ruling_span_m, "everyday_tension_kn": sec.everyday_tension_kn,
            "hot_tension_kn": sec.hot_tension_kn, "cold_tension_kn": sec.cold_tension_kn,
            "sag_m": max(x.sag_m for x in r.spans), "pole_height_m": max(p.height_m for p in r.poles),
            "clearance_m": min(x.clearance_m for x in r.spans)}


def _run_ug_derate(inp: dict) -> dict:
    """One underground feeder of route_m from a mini-sub, de-rated at the given conditions."""
    rs = load_rules(inp["rules"])
    net = build(route("a", (0, 0), (inp["route_m"], 0)), site("t", "minisub", 0, 0), rules=inp["rules"])
    r = underground.derate(net, rs, {}, inp["conductor"], inp["design"])
    d = next(x for x in r.ratings if x.branch in {b.id for b in net.branches if b.kind == "route"})
    return {"base_a": d.base_a, "soil": d.soil, "depth": d.depth, "temperature": d.temperature, "grouping": d.grouping, "derated_a": d.derated_a}


def _run_lv_drop(inp: dict) -> dict:
    """A load at the end of a route_m feeder, the transformer link_m off its start."""
    rs = load_rules(inp["rules"])
    net = build(route("a", (0, 0), (inp["route_m"], 0)), site("t", "transformer", 0, -inp["link_m"]), rules=inp["rules"])
    b = next(x for x in net.branches if x.kind == "route")
    load = LoadAt(load_id="L", branch=b.id, offset_m=b.length_m, phase=inp["phase"], kva=inp["kva"], kind="special")
    conds = {x.id: inp["conductor"] for x in net.branches}
    r = analyse(AnalyseRequest(rules=inp["rules"], network=net, loads=[load], conductors=conds), rs)
    worst = max(r.points, key=lambda p: p.worst_pct)
    return {"current_a": max(r.branches[0].current_a.values()), "drop_pct": worst.worst_pct, "passes": worst.passes}


def _run_design(inp: dict) -> dict:
    """The whole design run on a small layout: transformer, MV drop and tap, supply and fault level."""
    d = run(DesignRequest(**inp), load_rules(inp["rules"]))
    [t] = d.transformers.transformers
    [tap] = d.mv.taps
    cp_bus = "MV:" + next(n.id for n in d.mv_network.nodes if n.kind == "source")
    [tl] = d.bulk.transformers
    return {"tx_demand_kva": t.demand_kva, "tx_rating_kva": t.rating_kva, "tx_spare_kva": t.spare_kva, "tx_mounting": t.mounting,
            "mv_drop_pct": tap.drop_pct, "mv_regulation_pct": tap.regulation_pct, "tap_pct": tap.tap_pct,
            "lv_full_load_pct": tap.lv_full_load_pct, "nmd_kva": d.bulk.supply.nmd_kva,
            "ik3_cp_ka": next(b.ik3_max_ka for b in d.bulk.buses if b.id == cp_bus), "ik3_lv_ka": tl.ik3_lv_ka}


def _run_lv_reach(inp: dict) -> dict:
    """The LV reach the placement derives from the drop limit."""
    rs = load_rules(inp["rules"])
    return {"reach_m": placement._derived_reach(rs, placement._params(rs)).value}


RUNNERS = {"voltage_drop": _run_voltage_drop, "admd": _run_admd, "admd_group": _run_admd_group, "hb_group": _run_hb_group,
           "oh_span": _run_oh_span, "ug_derate": _run_ug_derate, "lv_drop": _run_lv_drop, "design": _run_design, "lv_reach": _run_lv_reach}


def _cases():
    for topic, runner in RUNNERS.items():
        for case in sorted((CASES_DIR / topic).glob("case-*")):
            yield pytest.param(runner, case, id=f"{topic}/{case.name}")


@pytest.mark.parametrize("runner,case", list(_cases()))
def test_hand_worked_case(runner, case: Path):
    inputs = json.loads((case / "inputs.json").read_text())
    expected = json.loads((case / "expected.json").read_text())
    tol = expected.pop("tolerance_pct", 0.5) / 100
    got = runner(inputs)
    for key, exp in expected.items():
        if isinstance(exp, (bool, str)):
            assert got[key] == exp, key
        else:
            assert got[key] == pytest.approx(exp, rel=tol), key
