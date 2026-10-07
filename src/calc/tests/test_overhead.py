"""Overhead line design (plan 2.5): poles along routes, sag and tension, clearance, pole class and stays."""

import math

import pytest
from test_lv_network import build, route, site

from reticula_calc.design.overhead import check, place_poles, state_change
from reticula_calc.rules import load_rules

RULES = "eskom/0.8.0"


@pytest.fixture
def rs():
    return load_rules(RULES)


def with_poles(rs, *cands):
    net0 = build(*cands, rules=RULES)
    poles = place_poles(net0, rs, "lv")
    return net0, poles, build(*cands, *poles, rules=RULES)


def test_state_change_keeps_the_tension_when_nothing_changes():
    t = state_change(3000.0, 9.2, 9.2, 40.0, 3.9e6, 2.3e-5, 0.0)
    assert t == pytest.approx(3000.0, rel=1e-9)


def test_state_change_satisfies_the_equation_by_hand():
    # Hot state: lower tension, larger sag. Substitute back into the equation worked by hand.
    t1, w, s, ea, a, dt = 3000.0, 9.22, 40.0, 3.92e6, 2.3e-5, 60.0
    t2 = state_change(t1, w, w, s, ea, a, dt)
    lhs = t2 * t2 * (t2 - t1 + w * w * s * s * ea / (24 * t1 * t1) + a * ea * dt)
    rhs = w * w * s * s * ea / 24
    assert t2 < t1
    assert lhs == pytest.approx(rhs, rel=1e-9)


def test_places_end_and_intermediate_poles_so_no_span_exceeds_the_maximum(rs):
    # 100 m from the transformer: the far end needs a pole, and two between, for spans of 33.3 m against 40 m.
    _, poles, net = with_poles(rs, route("a", (0, 0), (100, 0)), site("t", "transformer", 0, 0))
    assert len(poles) == 3
    spans = [b for b in net.branches if b.kind == "route"]
    assert len(spans) == 3
    assert max(b.length_m for b in spans) == pytest.approx(100 / 3, abs=0.3)


def test_puts_a_pole_at_a_turn_and_stays_it(rs):
    _, poles, net = with_poles(rs, route("a", (0, 0), (30, 0), (30, 30)), site("t", "transformer", 0, 0))
    res = check(net, rs, "lv", {}, "ABC-3C-70", {p.id for p in poles})
    corner = next(p for p in res.poles if abs(p.deviation_deg - 90) < 1)
    assert corner.kind == "angle" and corner.stays == 1
    end = next(p for p in res.poles if p.kind == "terminal")
    assert end.stays == 1
    assert res.issues == [] or all(i.severity == "warning" for i in res.issues)


def test_sag_tension_and_clearance_on_flat_ground(rs):
    _, _, net = with_poles(rs, route("a", (0, 0), (120, 0)), site("t", "transformer", 0, 0))
    res = check(net, rs, "lv", {}, "ABC-3C-70", set())
    [section] = res.sections
    # ABC-3C-70: 940 kg/km, breaking load 20 kN, everyday 15 % = 3 kN; spans of 40 m.
    assert section.everyday_tension_kn == pytest.approx(3.0)
    assert section.ruling_span_m == pytest.approx(40, abs=0.3)
    w = 0.94 * 9.81
    for s in res.spans:
        assert s.sag_m == pytest.approx(w * s.length_m**2 / (8 * section.hot_tension_kn * 1000), rel=1e-3)
    # A 7 m pole set 1.3 m deep, attached 0.3 m below the top: 5.4 m, less the sag, misses 5.5 m; 9 m poles clear it.
    assert {p.height_m for p in res.poles} == {9.0}
    assert all(s.passes for s in res.spans)
    assert res.worst_tension is not None and res.worst_tension.value == pytest.approx(max(section.cold_tension_kn, 3.0), abs=1e-3)


def test_suspension_poles_take_wind_only_and_get_the_lightest_class(rs):
    _, _, net = with_poles(rs, route("a", (0, 0), (120, 0)), site("t", "transformer", 0, 0))
    res = check(net, rs, "lv", {}, "ABC-3C-70", set())
    mids = [p for p in res.poles if p.kind == "suspension"]
    assert len(mids) == 2
    for p in mids:
        # 500 Pa on a 36 mm bundle over 40 m: 0.72 kN, within class 1 (2 kN).
        assert p.tip_load_kn == pytest.approx(0.5 * 0.036 * 40, rel=0.02)
        assert p.pole_class == "1" and p.stays == 0


def test_reports_spans_that_cannot_clear_the_ground(rs):
    from test_lv_network import ll

    from reticula_calc.design.geometry import ContourIn, Frame, Ground

    _, _, net = with_poles(rs, route("a", (0, 0), (80, 0)), site("t", "transformer", 0, 0))
    # Poles at 0, 40 and 80 m. A ridge 3.2 m above the poles' ground crosses mid-span at x = 20 m.
    contours = [ContourIn(elevation_m=100, coordinates=[tuple(ll(x, -50)), tuple(ll(x, 50))]) for x in (5, 35)]
    contours += [ContourIn(elevation_m=104, coordinates=[tuple(ll(20, -50)), tuple(ll(20, 50))])]
    ground = Ground(contours, Frame([n.coordinates for n in net.nodes]))
    res = check(net, rs, "lv", {}, "ABC-3C-70", set(), ground)
    assert "clearance" in {i.code for i in res.issues}
    assert res.worst_clearance is not None and res.worst_clearance.value < 5.5
    assert math.isfinite(res.worst_clearance.value)
