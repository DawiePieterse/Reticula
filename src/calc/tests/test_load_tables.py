"""Every encoded load class must agree with its own beta parameters, so a transcription slip fails CI."""

import math

import pytest

from reticula_calc.rules import load_rules

RULES = load_rules("eskom/0.2.0")
TOLERANCE = 0.015  # published tables round to 2-3 significant figures


def _rows():
    for table_id, table in RULES.data["load_tables"].items():
        for row in table["classes"]:
            yield pytest.param(table_id, table, row, id=f"{table_id}/{row['code']}")


@pytest.mark.parametrize("table_id,table,row", list(_rows()))
def test_class_parameters_are_self_consistent(table_id, table, row):
    a, b, c = row["alpha"], row["beta"], row["c_amps"]
    mean = c * a / (a + b)
    sd = c * math.sqrt(a * b / ((a + b) ** 2 * (a + b + 1)))
    admd = mean * table["phase_voltage_v"] / 1000
    if row.get("status") == "unverified":
        assert abs(admd - row["admd_kva"]) / row["admd_kva"] > TOLERANCE, "unverified class now agrees; review and mark verified"
        return
    assert admd == pytest.approx(row["admd_kva"], rel=TOLERANCE)
    if "mean_a" in row:
        assert mean == pytest.approx(row["mean_a"], rel=TOLERANCE)
    if "sd_a" in row:
        assert sd == pytest.approx(row["sd_a"], rel=TOLERANCE)


def test_income_ranges_increase_with_class():
    classes = RULES.data["load_tables"]["nrs034_15y"]["classes"]
    mins = [c["income_min_zar"] for c in classes]
    assert mins == sorted(mins)


def test_score_bands_point_at_real_classes():
    codes = {c["code"] for c in RULES.data["load_tables"]["nrs034_15y"]["classes"]}
    assert {b["class"] for b in RULES.data["income_admd"]["income_bands"]} <= codes
