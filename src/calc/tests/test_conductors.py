"""LV conductor library (plan 2.3), checked against Eskom 240-56030637 Rev 2 Tables 1, 2, 6 and 7."""

import pytest

from reticula_calc.lv.conductors import library, rating, withstand
from reticula_calc.rules import RulesError, load_rules

RULES = "eskom/0.5.0"


@pytest.fixture
def rs():
    return load_rules(RULES)


@pytest.mark.parametrize(("code", "ground", "pipe", "air"), [
    ("CU-2C-16", 107, 88, 96), ("CU-4C-16", 91, 75, 82), ("CU-2C-35", 171, 139, 156), ("CU-4C-70", 210, 171, 205),
    ("CU-4C-240", 416, 344, 451), ("AL-2C-25", 106, 86, 92), ("AL-4C-120", 219, 179, 216), ("AL-4C-240", 324, 268, 342),
])
def test_ratings_are_transcribed_from_tables_6_and_7(rs, code, ground, pipe, air):
    c = rs.conductor(code)
    assert dict(c.ratings_a) == {"ground": ground, "pipe": pipe, "air": air}
    assert c.rating_a == ground  # underground cables are normally laid direct in ground


# Table 1 (Cu) and Table 2 (Al): the K·A/√t formula with K 0,115 and 0,076 reproduces the published fault levels.
@pytest.mark.parametrize(("code", "t", "table_ka"), [
    ("CU-4C-16", 0.3, 3.359365), ("CU-4C-70", 0.2, 18.00035), ("CU-4C-120", 1, 13.8), ("CU-4C-240", 3, 15.93487),
    ("AL-4C-35", 1, 2.66), ("AL-4C-120", 0.6, 11.77387), ("AL-4C-240", 0.4, 28.83997),
])
def test_withstand_matches_the_fault_level_tables(rs, code, t, table_ka):
    w = withstand(rs, code, t)
    assert w.value == pytest.approx(table_ka, abs=1e-4)
    assert w.unit == "kA" and "Table" in w.clause and {i.name for i in w.inputs} == {"K", "A", "t"}


def test_the_library_is_complete_and_says_what_is_a_placeholder(rs):
    lib = library(rs)
    codes = [c.code for c in lib.conductors]
    assert len(codes) == len(set(codes)) == 35
    for c in lib.conductors:
        if c.kind == "underground":
            # Ratings and K come from the standard; impedances do not yet.
            assert c.placeholder == ["r_ohm_per_km", "x_ohm_per_km"]
            assert c.ratings_a["ground"] >= c.ratings_a["pipe"]
            assert c.one_second_ka == pytest.approx(c.fault_k * c.size_mm2)
            assert ("feeder" in c.uses) == (c.cores == 4)
            assert c.index == "ESKOM-LVCABLE-RATING"
    three = [c for c in lib.conductors if c.code.startswith("ABC-3C-")]
    assert len(three) == 7 and all(c.placeholder == ["rating_a", "ratings_a"] and c.cores == 4 for c in three)


# M-TEC data sheet AS3x, R06 2022-01-19: size, DC R 20 °C, AC R 90 °C, X, Z, rating in air, 1 s short circuit.
ABC_3C = [
    (25, 1.200, 1.535, 0.091, 1.541, 105, 2.3), (35, 0.868, 1.113, 0.090, 1.117, 144, 3.2), (50, 0.641, 0.822, 0.084, 0.826, 183, 4.6),
    (70, 0.443, 0.568, 0.083, 0.574, 228, 6.4), (95, 0.320, 0.411, 0.080, 0.419, 277, 8.7), (120, 0.253, 0.325, 0.078, 0.334, 322, 11.0),
    (150, 0.206, 0.265, 0.077, 0.276, 340, 12.7),
]


@pytest.mark.parametrize(("size", "r20", "r90", "x", "z", "amps", "isc"), ABC_3C)
def test_three_phase_abc_is_transcribed_and_agrees_with_the_single_phase_sheet(rs, size, r20, r90, x, z, amps, isc):
    import math

    c = rs.conductor(f"ABC-3C-{size}")
    assert (c.r_ohm_per_km, c.r_ac_ohm_per_km, c.x_ohm_per_km, c.rating_a) == (r20, r90, x, amps)
    assert withstand(rs, c.code, 1).value == pytest.approx(isc, abs=0.001)
    assert math.hypot(r90, x) == pytest.approx(z, abs=0.004)  # 25 mm²: 1.535 gives 1.538 against the sheet's 1.541
    one = rs.conductor(f"ABC-1C-{size}")
    # Same conductors, two manufacturers: resistance and reactance agree within a rounding step.
    assert c.r_ohm_per_km == one.r_ohm_per_km and c.x_ohm_per_km == pytest.approx(one.x_ohm_per_km, abs=0.0011)
    assert c.r_ac_ohm_per_km == pytest.approx(one.r_ac_ohm_per_km, abs=0.005)


def test_a_rating_is_traced_and_flags_placeholders(rs):
    r = rating(rs, "CU-4C-70", "pipe")
    assert (r.value, r.unit, r.formula_id) == (171, "A", "lv.cable.rating.v1")
    assert "Table 6" in r.inputs[1].source and "placeholder" not in r.inputs[1].source
    abc = rating(rs, "ABC-3C-70")
    assert abc.value == 228 and "(placeholder)" in abc.inputs[1].source
    with pytest.raises(RulesError, match="no rating for installation"):
        rating(rs, "ABC-3C-70", "ground")
    with pytest.raises(RulesError, match="no fault constant"):
        withstand(load_rules("eskom/0.4.0"), "CU-PVC-35", 1)


def test_older_rules_still_load_their_simple_conductors():
    old = load_rules("eskom/0.4.0").conductor("CU-PVC-35")
    assert (old.ratings_a, old.fault_k, old.placeholder) == ((), None, ())


def test_endpoint(client):
    r = client.get(f"/rules/{RULES}/conductors")
    assert r.status_code == 200
    body = r.json()
    assert body["rules_ref"] == RULES and len(body["conductors"]) == 35
    assert next(c for c in body["conductors"] if c["code"] == "AL-4C-70")["ratings_a"] == {"ground": 158, "pipe": 130, "air": 151}
    assert client.get("/rules/eskom/9.9.9/conductors").status_code == 404
