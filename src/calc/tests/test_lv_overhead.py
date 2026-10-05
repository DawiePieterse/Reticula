"""LV overhead line checks (plan 2.5): spans, sag and tension, clearance, pole loads and stays, against hand-worked values."""

import copy
import math
from dataclasses import replace

import numpy as np
import pytest
from fastapi.testclient import TestClient
from test_lv_network import build, route, site

from reticula_calc.app import app
from reticula_calc.lv.overhead import OverheadRequest, check_overhead, state_change
from reticula_calc.rules import RulesError, load_rules

RULES = "eskom/0.7.0"
# ABC-3C-70 (M-TEC AS3x): 1,01 kg/m, 34 mm, 8,9 kN max pull; four 70 mm² Al cores at 59 GPa and 23e-6 /°C.
W = 1.01 * 9.81
D = 0.034
EA = 59e9 * 4 * 70e-6
ALPHA = 23e-6
H1 = 0.45 * 8900  # everyday: 45 % of max pull at 15 °C, no wind
H_ATTACH = 9.0 - 1.5 - 0.3


def h_case(rs_m: float, temp: float, wind_pa: float) -> float:
    """The state change worked independently: the positive root of H³ + (K − H₁)·H² − C = 0."""
    w2 = math.hypot(W, wind_pa * D)
    k = EA * W**2 * rs_m**2 / (24 * H1**2) + EA * ALPHA * (temp - 15)
    c = EA * w2**2 * rs_m**2 / 24
    roots = np.roots([1, k - H1, 0, -c])
    return max(r.real for r in roots if abs(r.imag) < 1e-6 and r.real > 0)


@pytest.fixture
def rs():
    return load_rules(RULES)


def run(rs, net, **conductors):
    return check_overhead(OverheadRequest(rules=RULES, network=net, conductors=conductors), rs)


def support(r, label):
    return next(p for p in r.supports if p.label == label)


def span(r, a, b):
    return next(s for s in r.spans if {s.from_label, s.to_label} == {a, b})


def straight(n_spans=3, length=40.0):
    """A transformer at the start of a straight route with a pole every `length` metres."""
    end = n_spans * length
    poles = [site(f"p{i}", "pole", i * length, 0) for i in range(1, n_spans + 1)]
    return build(route("a", (0, 0), (end, 0)), site("t", "transformer", 0, 0), *poles, rules=RULES)


def test_state_change_matches_the_cubic():
    for temp, wind in ((80, 0), (-5, 0), (10, 700), (15, 0)):
        got = state_change(H1, W, 15, math.hypot(W, wind * D), temp, 40.0, EA, ALPHA)
        assert got == pytest.approx(h_case(40.0, temp, wind), rel=1e-9)
    assert state_change(H1, W, 15, W, 15, 40.0, EA, ALPHA) == pytest.approx(H1)  # the same state


def test_a_straight_line_one_section_sag_and_clearance(rs):
    r = run(rs, straight())
    assert [s.length_m for s in r.spans] == pytest.approx([40, 40, 40], abs=0.05)
    [t] = r.sections
    assert t.ruling_span_m == pytest.approx(40, abs=0.05)
    h_hot = h_case(t.ruling_span_m, 80, 0)
    assert t.tension_kn["hot"] == pytest.approx(h_hot / 1000, abs=5e-4)
    assert t.tension_kn["everyday"] == pytest.approx(4.005)
    s = span(r, "P1", "P2")
    sag = W * s.length_m**2 / (8 * h_hot)
    assert s.sag_m == pytest.approx(sag, abs=5e-4)
    assert s.clearance_m == pytest.approx(H_ATTACH - sag, abs=5e-4)
    assert s.tension_kn == pytest.approx(max(h_case(t.ruling_span_m, -5, 0), h_case(t.ruling_span_m, 10, 700)) / 1000, abs=5e-4)
    assert all(x.passes for x in r.spans)
    assert r.lowest_clearance is not None and r.lowest_clearance.unit == "m"


def test_intermediate_poles_carry_only_the_wind(rs):
    r = run(rs, straight())
    p = support(r, "P1")
    assert (p.role, p.spans, p.stay, p.governing_case) == ("intermediate", 2, False, "wind")
    assert p.deviation_deg == pytest.approx(0, abs=0.05)  # the test grid is not quite the Lo projection
    assert p.load_kn == pytest.approx(700 * D * 40 / 1000, abs=0.01)  # the tensions cancel; wind on two half spans
    assert p.pole_class == "9m-140"


def test_terminal_poles_take_the_full_tension_and_are_stayed(rs):
    r = run(rs, straight())
    p = support(r, "P3")
    rs_m = r.sections[0].ruling_span_m
    cold = h_case(rs_m, -5, 0)
    windy = h_case(rs_m, 10, 700) + 700 * D * 20
    assert p.role == "terminal" and p.stay
    assert p.load_kn == pytest.approx(max(cold, windy) / 1000, abs=0.002)
    assert p.stay_tension_kn == pytest.approx(p.load_kn / math.cos(math.radians(45)), abs=0.002)
    assert p.pole_class == "9m-140"  # stayed: the lightest class
    tx = support(r, "TX1")
    assert tx.role == "terminal" and tx.pole_class is None  # the transformer structure is designed with it


def test_an_angle_pole_carries_the_resultant(rs):
    dev = 20
    bend = (40 + 40 * math.cos(math.radians(dev)), 40 * math.sin(math.radians(dev)))
    net = build(route("a", (0, 0), (40, 0), bend), site("t", "transformer", 0, 0), site("p1", "pole", 40, 0),
                site("p2", "pole", *bend), rules=RULES)
    r = run(rs, net)
    p = support(r, "P1")
    assert p.role == "angle" and p.deviation_deg == pytest.approx(dev, abs=0.05)
    assert len(r.sections) == 1
    rs_m = r.sections[0].ruling_span_m
    expected = max(2 * h_case(rs_m, -5, 0) * math.sin(math.radians(dev / 2)),
                   2 * h_case(rs_m, 10, 700) * math.sin(math.radians(dev / 2)) + 700 * D * 40)
    assert p.load_kn == pytest.approx(expected / 1000, abs=0.01)


def test_a_sharp_angle_is_a_strain_pole_and_splits_the_section(rs):
    net = build(route("a", (0, 0), (40, 0), (40, 40)), site("t", "transformer", 0, 0), site("p1", "pole", 40, 0),
                site("p2", "pole", 40, 40), rules=RULES)
    r = run(rs, net)
    p = support(r, "P1")
    assert p.role == "strain" and p.deviation_deg == pytest.approx(90, abs=0.05)
    assert len(r.sections) == 2


def test_long_spans_low_clearance_and_unmarked_ends(rs):
    net = build(route("a", (0, 0), (120, 0)), site("t", "transformer", 0, 0), rules=RULES)
    r = run(rs, net)
    codes = {i.code: i for i in r.issues}
    assert codes["span_too_long"].samples == ["TX1–N2"]
    assert "clearance" in codes  # 120 m sags far below 5,5 m
    assert codes["no_support"].count == 1
    assert r.summary.poles_needed == 1
    [s] = r.spans
    assert not s.passes


def test_a_bend_between_poles_is_flagged(rs):
    net = build(route("a", (0, 0), (20, 5), (40, 0)), site("t", "transformer", 0, 0), site("p1", "pole", 40, 0), rules=RULES)
    r = run(rs, net)
    [s] = r.spans
    assert s.bend_m == pytest.approx(5, abs=0.05)
    assert "bend_without_pole" in {i.code for i in r.issues}


def test_a_junction_pole(rs):
    net = build(route("a", (0, 0), (80, 0)), route("b", (40, 0), (40, 40)), site("t", "transformer", 0, 0),
                site("p1", "pole", 40, 0), site("p2", "pole", 80, 0), site("p3", "pole", 40, 40), rules=RULES)
    r = run(rs, net)
    p = support(r, "P1")
    assert (p.role, p.spans) == ("junction", 3)
    assert len(r.sections) == 3


def test_short_spans_are_strung_slacker_to_keep_within_the_maximum_tension(rs):
    r = run(rs, straight(1, 15.0))
    [t] = r.sections
    assert t.governing == "max_tension"
    assert max(t.tension_kn["cold"], t.tension_kn["wind"]) == pytest.approx(8.9, abs=1e-3)
    assert t.tension_kn["everyday"] < 4.005
    assert t.passes and "over_tension" not in {i.code for i in r.issues}
    assert run(rs, straight()).sections[0].governing == "everyday"


def test_a_harsh_case_is_met_by_stringing_slacker_at_the_cost_of_sag(rs):
    data = copy.deepcopy(rs.data)
    data["lv_overhead"]["cases"]["cold"]["wind_pa"] = 5000  # a gale no slack stringing can ride out within the limit
    stormy = replace(rs, data=data)
    r = run(stormy, straight())
    assert r.sections[0].governing == "max_tension"
    assert r.sections[0].passes  # strung slack enough
    assert r.spans[0].clearance_m < run(rs, straight()).spans[0].clearance_m  # but it sags more


def test_conductors_without_mechanical_data_are_not_checked(rs):
    net = straight(1)
    r = run(rs, net, **{net.branches[0].id: "CU-4C-16"})
    assert "no_mech_data" in {i.code for i in r.issues}
    assert r.spans[0].sag_m is None and r.sections == []


def test_needs_rules_with_lv_overhead():
    with pytest.raises(RulesError, match="lv_overhead"):
        check_overhead(OverheadRequest(rules="eskom/0.6.0", network=straight(1)), load_rules("eskom/0.6.0"))


def test_endpoint():
    client = TestClient(app)
    body = OverheadRequest(rules=RULES, network=straight()).model_dump(mode="json")
    res = client.post("/calc/lv/overhead", json=body)
    assert res.status_code == 200
    out = res.json()
    assert out["summary"]["spans"] == 3 and out["summary"]["stays"] == 2
    assert any(i["code"] == "overhead_placeholders" for i in out["issues"])
    assert client.post("/calc/lv/overhead", json={**body, "rules": "eskom/0.6.0"}).status_code == 422
