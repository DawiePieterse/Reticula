"""Run every hand-worked case under /test-cases."""

import json
from pathlib import Path

import pytest

from reticula_calc.calcs.admd import EstimateRequest, GroupRequest, estimate, group
from reticula_calc.calcs.voltage_drop import VoltageDropRequest, voltage_drop
from reticula_calc.lv.analysis import analyse, derating
from reticula_calc.lv.library import library
from reticula_calc.lv.model import LvNetwork
from reticula_calc.lv.overhead import check_overhead
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


RUNNERS = {"voltage_drop": _run_voltage_drop, "admd": _run_admd, "admd_group": _run_admd_group, "hb_group": _run_hb_group,
           "lv_vdrop": _run_lv, "lv_fault": _run_lv, "oh_sag": _run_oh_sag, "ug_derating": _run_ug_derating}


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
