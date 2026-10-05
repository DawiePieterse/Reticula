"""LV design checks (plan 2.4): Herman-Beta voltage drop, thermal loading and fault level, against hand-worked values."""

import math

import pytest
from scipy.stats import beta as beta_dist
from test_lv_network import build, route, site

from reticula_calc.lv.analysis import AnalyseRequest, LoadAt, analyse
from reticula_calc.rules import RulesError, load_rules

RULES = "eskom/0.6.0"
V_PH = 400 / math.sqrt(3)
# ABC-3C-70 (M-TEC): R 0,568 Ω/km at 90 °C, X 0,083 Ω/km; cos φ 0,98 from the rules file.
SIN = math.sqrt(1 - 0.98**2)
Z = 0.568 * 0.98 + 0.083 * SIN  # Ω/km, phase or neutral, for voltage drop


@pytest.fixture
def rs():
    return load_rules(RULES)


@pytest.fixture
def line():
    """A 200 m feeder fed at one end: branch B1 from the transformer's node to its far end."""
    net = build(route("a", (0, 0), (200, 0)), site("t", "transformer", 0, 0), rules=RULES)
    [b] = net.branches
    return net, b


def hb_current(n: int, a: float, b: float, c: float, conf: float = 0.9) -> float:
    """Herman-Beta design current of n identical consumers on one phase, worked independently of the code."""
    mu = c * a / (a + b)
    var = c * c * a * b / ((a + b) ** 2 * (a + b + 1))
    m, v = n * mu / (n * c), n * var / (n * c) ** 2
    k = m * (1 - m) / v - 1
    return n * c * beta_dist.ppf(conf, m * k, (1 - m) * k)


def run(rs, net, *loads, **conductors):
    return analyse(AnalyseRequest(rules=RULES, network=net, loads=list(loads), conductors=conductors), rs)


def at_end(b, lid, phase, kva=2.37, kind="residential", load_class="township_area"):
    return LoadAt(load_id=lid, branch=b.id, offset_m=b.length_m, phase=phase, kva=kva, kind=kind, load_class=load_class)


def point(result, pid):
    return next(p for p in result.points if p.id == pid)


def test_two_consumers_on_one_phase_at_the_end(rs, line):
    net, b = line
    r = run(rs, net, at_end(b, "h1", "R"), at_end(b, "h2", "R"))
    i = hb_current(2, 1.22, 5.86, 60)  # township area, 15-year
    km = b.length_m / 1000
    expected = 2 * Z * km * i / V_PH * 100  # phase and neutral carry the same current
    end = point(r, b.to_node)
    assert end.drop_pct["R"] == pytest.approx(expected, abs=5e-4)  # results are given to 0,001 %
    # The other phases rise a little: half the R current returns against them in the neutral.
    assert end.drop_pct["W"] < 0 and end.drop_pct["W"] == end.drop_pct["B"]
    assert point(r, "h1").drop_pct == end.drop_pct and point(r, "h1").kind == "connection"
    # Thermal: the section carries the same design current on R; nothing on W and B.
    [br] = r.branches
    assert br.current_a["R"] == pytest.approx(i, rel=1e-4) and br.current_a["W"] == 0
    assert br.utilisation_pct == pytest.approx(i / 228 * 100, abs=0.05)
    assert r.worst_drop.value == pytest.approx(expected, rel=1e-4) and r.worst_drop.formula_id == "lv.vdrop.herman-beta.v1"


def test_a_three_phase_load_drops_each_phase_by_the_phase_conductor_alone(rs, line):
    net, b = line
    r = run(rs, net, at_end(b, "school", "RWB", kva=30, kind="special", load_class=None))
    i = 30_000 / (3 * V_PH)
    expected = Z * b.length_m / 1000 * i / V_PH * 100  # balanced: no neutral current
    end = point(r, b.to_node)
    for p in "RWB":
        assert end.drop_pct[p] == pytest.approx(expected, abs=5e-4)
    assert r.branches[0].current_a == pytest.approx({"R": i, "W": i, "B": i}, abs=0.005)  # given to 0,01 A


def test_balanced_single_phase_loads_cancel_in_the_neutral(rs, line):
    net, b = line
    loads = [at_end(b, f"s{p}", p, kva=5, kind="special", load_class=None) for p in "RWB"]
    r = run(rs, net, *loads)
    i = 5_000 / V_PH
    # (z_ph + z_n)·I − ½·z_n·I − ½·z_n·I = z_ph·I on every phase
    expected = Z * b.length_m / 1000 * i / V_PH * 100
    assert point(r, b.to_node).drop_pct == pytest.approx({p: expected for p in "RWB"}, abs=5e-4)


def test_fault_level_at_the_end_from_the_source_and_the_hot_loop(rs, line):
    net, b = line
    r = run(rs, net, at_end(b, "h1", "R"))
    z_base = 400**2 / 100_000
    zs = 0.04 * z_base
    z_src = complex(zs / math.sqrt(1 + 1.5**2), zs * 1.5 / math.sqrt(1 + 1.5**2))
    loop = b.length_m / 1000 * 2 * complex(0.568, 0.083)
    expected = V_PH / abs(z_src + loop)
    assert point(r, b.to_node).fault_a == pytest.approx(expected, abs=0.1)
    assert r.lowest_fault.value == pytest.approx(expected, rel=1e-6)
    assert any(i.code == "fault_not_checked" for i in r.issues)


def test_drop_grows_along_the_feeder_and_connections_mid_branch_get_their_own_point(rs, line):
    net, b = line
    mid = LoadAt(load_id="mid", branch=b.id, offset_m=b.length_m / 2, phase="W", kva=2.37, load_class="township_area")
    r = run(rs, net, mid, at_end(b, "end", "W"))
    assert 0 < point(r, "mid").drop_pct["W"] < point(r, b.to_node).drop_pct["W"]
    assert point(r, "mid").distance_m == pytest.approx(b.length_m / 2, abs=0.01)
    [f] = r.feeders
    assert f.max_drop_at in (b.to_node, "end") and f.passes and f.min_fault_at in (b.to_node, "end")


def test_a_long_heavily_loaded_feeder_fails_and_says_where(rs):
    net = build(route("a", (0, 0), (1500, 0)), site("t", "transformer", 0, 0), rules=RULES)
    [b] = net.branches
    loads = [LoadAt(load_id=f"h{k}", branch=b.id, offset_m=b.length_m, phase="RWB"[k % 3], kva=3.59,
                    load_class="urban_residential_1") for k in range(60)]
    r = run(rs, net, *loads)
    codes = {i.code: i for i in r.issues}
    assert codes["drop_over_limit"].severity == "error" and b.to_node in codes["drop_over_limit"].samples
    assert codes["overload"].samples == [b.id]
    assert not r.feeders[0].passes
    placeholders = codes["placeholders"].message
    assert "source transformer" in placeholders and "ABC-3C-70 rating_a, ratings_a" in placeholders


def test_branches_can_take_another_conductor(rs, line):
    net, b = line
    base = run(rs, net, at_end(b, "h1", "R"))
    bigger = run(rs, net, at_end(b, "h1", "R"), **{b.id: "ABC-3C-150"})
    assert bigger.branches[0].conductor == "ABC-3C-150"
    assert point(bigger, b.to_node).drop_pct["R"] < point(base, b.to_node).drop_pct["R"]


def test_loads_without_a_class_are_taken_at_their_admd(rs, line):
    net, b = line
    r = run(rs, net, at_end(b, "h1", "R", kva=2.0, load_class=None))
    expected = 2 * Z * b.length_m / 1000 * (2000 / V_PH) / V_PH * 100
    assert point(r, b.to_node).drop_pct["R"] == pytest.approx(expected, abs=5e-4)
    assert next(i for i in r.issues if i.code == "no_load_class").samples == ["h1"]
    # A band label from older rules, or an unknown class, is treated the same way.
    odd = run(rs, net, at_end(b, "h1", "R", kva=2.0, load_class="R2"))
    assert point(odd, b.to_node).drop_pct["R"] == pytest.approx(expected, abs=5e-4)


def test_needs_rules_with_lv_design(line):
    net, _ = line
    with pytest.raises(RulesError, match="lv_design"):
        analyse(AnalyseRequest(rules="eskom/0.5.0", network=net, loads=[]), load_rules("eskom/0.5.0"))


def test_endpoint(client, line):
    net, b = line
    body = {"rules": RULES, "network": net.model_dump(), "loads": [at_end(b, "h1", "R").model_dump()]}
    r = client.post("/calc/lv/analyse", json=body)
    assert r.status_code == 200
    out = r.json()
    assert out["feeders"][0]["passes"] and out["worst_drop"]["formula_id"] == "lv.vdrop.herman-beta.v1"
    assert client.post("/calc/lv/analyse", json={**body, "rules": "eskom/0.5.0"}).status_code == 422
