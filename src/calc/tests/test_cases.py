"""Run every hand-worked case under /test-cases."""

import json
from pathlib import Path

import pytest

from reticula_calc.bulk.study import BulkStudyRequest, bulk_study
from reticula_calc.calcs.admd import EstimateRequest, GroupRequest, estimate, group
from reticula_calc.calcs.voltage_drop import VoltageDropRequest, voltage_drop
from reticula_calc.lv.analysis import analyse, derating
from reticula_calc.lv.build import CustomerIn, RouteIn
from reticula_calc.lv.library import library
from reticula_calc.lv.model import LvNetwork
from reticula_calc.lv.overhead import check_overhead
from reticula_calc.mv.network import MvNetwork, analyse_mv, choose_tap
from reticula_calc.mv.placement import SiteIn, allocate
from reticula_calc.opt.lifetime import lifetime_cost
from reticula_calc.opt.search import OptimiseRequest, optimise
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


def _run_lv(inp: dict) -> dict:
    a = analyse(LvNetwork(**inp["network"]), load_rules(inp["rules"]), inp.get("transformer_kva"))
    out: dict = {f"{n.id}.{p}": v for n in a.nodes for p, v in n.vdrop_v.items()}
    out["max_fault_ka"] = a.max_fault_ka.value
    out.update({f"min_fault_a.{e.node_id}": e.min_fault_a for e in a.feeder_ends})
    return out


def _run_oh_sag(inp: dict) -> dict:
    r = check_overhead(LvNetwork(**inp["network"]), load_rules(inp["rules"]))
    return r.spans[0].model_dump()


def _run_ug_derating(inp: dict) -> dict:
    rules = load_rules(inp["rules"])
    cable = library(rules)[inp["conductor"]]
    f, _ = derating(rules, cable, inp["site"])
    return {"factor": f, "rating_a": cable.rating_a * f}


def _run_mv_sizing(inp: dict) -> dict:
    r = allocate([SiteIn(**x) for x in inp["sites"]], [CustomerIn(**x) for x in inp["customers"]], [RouteIn(**x) for x in inp["lv_routes"]],
                 load_rules(inp["rules"]), inp["lv_construction"])
    return r.placements[0].model_dump()


def _run_mv_vdrop(inp: dict) -> dict:
    a = analyse_mv(MvNetwork(**inp["network"]), {k: [CustomerIn(**c) for c in v] for k, v in inp["customers"].items()}, load_rules(inp["rules"]))
    b = a.branches[0]
    return {"current_a": b.current_a, "demand_kva": b.demand_kva, "vdrop_pct": a.site_vdrop_pct["T1"]}


def _run_mv_tap(inp: dict) -> dict:
    tap, lo, hi, reg = choose_tap(load_rules(inp["rules"]), inp["mv_drop_pct"], inp["load_kva"], inp["rating_kva"], inp["z_pct"], inp["x_r"], inp["lv_drop_pct"])
    return {"tap_pct": tap, "v_min_pct": lo, "v_max_pct": hi, "regulation_pct": reg}


def _run_bulk(inp: dict) -> dict:
    r = bulk_study(BulkStudyRequest(**inp), load_rules(inp["rules"]))
    out: dict = {}
    for b in r.buses:
        out[f"{b.id}.v_pct"] = b.v_pct
        out[f"{b.id}.ikss3_max_ka"] = b.ikss3_max_ka
        out[f"{b.id}.ikss1_min_ka"] = b.ikss1_min_ka
    return out


def _run_opt_lifetime(inp: dict) -> dict:
    r = lifetime_cost(LvNetwork(**inp["network"]), load_rules(inp["rules"]), inp["transformer_kva"], inp["capex"], inp["params"])
    return {"line_losses_kw": r.line_losses_kw, "transformer_load_losses_kw": r.transformer_load_losses_kw, "annual_losses_kwh": r.annual_losses_kwh,
            "pv_factor": r.pv_factor, "total": r.total.value}


def _run_opt_bench(inp: dict) -> dict:
    r = optimise(OptimiseRequest(**inp), load_rules(inp["rules"]))
    o = next(x for x in r.options if x.objective == "capex")
    return {"capex": o.option.cost.total, "moved_m": o.design.moved_m, "passed": int(o.option.passed)}


RUNNERS = {"voltage_drop": _run_voltage_drop, "admd": _run_admd, "admd_group": _run_admd_group, "hb_group": _run_hb_group,
           "lv_vdrop": _run_lv, "lv_fault": _run_lv, "oh_sag": _run_oh_sag, "ug_derating": _run_ug_derating,
           "mv_sizing": _run_mv_sizing, "mv_vdrop": _run_mv_vdrop, "mv_tap": _run_mv_tap, "bulk_sc": _run_bulk, "bulk_lf": _run_bulk,
           "opt_lifetime": _run_opt_lifetime, "opt_bench": _run_opt_bench}


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


def test_every_case_folder_has_a_runner():
    """The release gate runs every case: a folder without a runner would be skipped silently."""
    topics = {p.name for p in CASES_DIR.iterdir() if p.is_dir() and any(p.glob("case-*"))}
    assert topics <= set(RUNNERS), f"no runner for {sorted(topics - set(RUNNERS))}"
