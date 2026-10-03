"""Run every hand-worked case under /test-cases."""

import json
from pathlib import Path

import pytest

from reticula_calc.calcs.voltage_drop import VoltageDropRequest, voltage_drop
from reticula_calc.rules import load_rules

CASES_DIR = Path(__file__).resolve().parents[3] / "test-cases"

def _run_voltage_drop(inp: dict) -> dict:
    r = voltage_drop(VoltageDropRequest(**inp), load_rules(inp["rules"]))
    return {"drop_v": r.drop_v.value, "drop_pct": r.drop_pct.value, "passes": r.passes}


RUNNERS = {"voltage_drop": _run_voltage_drop}


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
        if isinstance(exp, bool):
            assert got[key] == exp, key
        else:
            assert got[key] == pytest.approx(exp, rel=tol), key
