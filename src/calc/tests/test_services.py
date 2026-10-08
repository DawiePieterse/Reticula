"""Services and LV protection (eskom/0.9.0): Airdac service conductors, the drop at the customer, service spans and
service poles, and the feeder fuse against the end-of-feeder fault."""

import math

import pytest
from scipy.stats import beta as beta_dist
from test_design import cp, layout
from test_lv_network import build, route, site

from reticula_calc.design.run import DesignRequest, run
from reticula_calc.design.services import ServiceSizer, _low_point, span_service
from reticula_calc.design.sizing import size_lv
from reticula_calc.lv.analysis import AnalyseRequest, LoadAt, analyse
from reticula_calc.rules import load_rules

RULES = "eskom/0.9.0"


@pytest.fixture(scope="module")
def rs():
    return load_rules(RULES)


# ------------------------------------------------------------------ rules


def test_the_rules_carry_the_engineers_answers(rs):
    assert rs.data["voltage"]["lv_max_drop_pct"] == 8 and rs.data["voltage"]["lv_service_max_drop_pct"] == 2
    assert "lv_max_drop_pct" not in rs.data["voltage"]["placeholder"]
    assert rs.data["lv_loads"]["max_service_m"] == 50
    o = rs.data["services"]["overhead"]
    assert o["single_phase"] == ["AIRDAC-SNE-10", "AIRDAC-SNE-16"] and o["max_span_m"] == 50 and o["pole_height_m"] == 7
    a10, a16 = rs.conductor("AIRDAC-SNE-10"), rs.conductor("AIRDAC-SNE-16")
    assert (a10.rating_a, a16.rating_a) == (50, 70) and a10.uses == ("service",) and a10.material == "cu"
    # Three-phase ABC ratings: the mean of M-TEC, Voltex/Aberdare and CBi.
    assert rs.conductor("ABC-3C-70").rating_a == round((228 + 228 + 213) / 3)
    # Supplier impedances replace the placeholders on the underground cables.
    cu = rs.conductor("CU-4C-25")
    assert cu.r_ac_ohm_per_km == pytest.approx((0.87 + math.sqrt(0.8749**2 - 0.08**2)) / 2, abs=1e-4) and not cu.placeholder
    assert rs.conductor("AL-2C-25").placeholder == ("r_ac_ohm_per_km", "x_ohm_per_km")


def test_the_sag_formula_reproduces_the_suppliers_table(rs):
    """Aberdare's Airdac SNE installation sags, mm, at its maximum working tension; it takes g as 10 m/s²."""
    table = {"AIRDAC-SNE-10": {10: 45, 20: 180, 30: 400, 40: 710, 50: 1110}, "AIRDAC-SNE-16": {10: 40, 20: 170, 30: 380, 40: 670, 50: 1050}}
    for code, sags in table.items():
        m = rs.data["overhead"]["mechanical"][code]
        for length, mm in sags.items():
            at_g10 = m["mass_kg_per_km"] / 1000 * 10 * length**2 / (8 * 0.25 * m["breaking_load_kn"] * 1000)
            assert at_g10 * 1000 == pytest.approx(mm, abs=5.5), (code, length)  # the table rounds up to 5 or 10 mm
            [span] = span_service(rs, code, length, 9).spans  # no service pole from a 9 m pole
            assert span.sag_m == pytest.approx(at_g10 * 9.81 / 10, abs=1e-3)


def test_the_low_point_of_a_span():
    assert _low_point(6.0, 6.0, 1.0) == pytest.approx(5.0)  # level supports: mid-span, less the sag
    assert _low_point(6.6, 3.5, 0.2) == pytest.approx(3.5)  # steep: the lower support is the lowest point
    u = 0.5 + 3.1 / (8 * 1.004544)
    assert _low_point(6.6, 3.5, 1.004544) == pytest.approx(6.6 - 3.1 * u - 4 * 1.004544 * u * (1 - u))


def test_a_service_pole_goes_in_where_the_sag_is_too_low(rs):
    short = span_service(rs, "AIRDAC-SNE-10", 30, 7)
    assert short.poles == 0 and short.clears
    long = span_service(rs, "AIRDAC-SNE-10", 50, 7)
    assert long.poles == 1 and long.clears and [s.length_m for s in long.spans] == [25.0, 25.0]
    # Longer than the 50 m span limit: a pole whatever the sag.
    assert span_service(rs, "AIRDAC-SNE-16", 70, 9).poles >= 1


def test_a_customers_own_current_has_no_diversity(rs):
    z = ServiceSizer(rs, "overhead")
    lc = next(c for c in rs.data["load_tables"][rs.data["income_admd"]["design_table"]]["classes"] if c["code"] == "township_area")
    conf = rs.data["income_admd"]["diversity"]["confidence_pct"] / 100
    assert z.current("residential", 2.37, "township_area", False) == pytest.approx(lc["c_amps"] * beta_dist.ppf(conf, lc["alpha"], lc["beta"]))
    assert z.current("residential", 2.37, None, False) == pytest.approx(2370 / (400 / math.sqrt(3)))
    assert z.current("special", 30, None, True) == pytest.approx(10000 / (400 / math.sqrt(3)))
    # A large customer far from its pole: no Airdac keeps the drop within 2 %, so the largest is kept and fails.
    c, pct, ok = z.choose(60.0, 50, False)
    assert c.code == "AIRDAC-SNE-16" and pct > 2 and not ok


# ------------------------------------------------------------------ the drop at the customer


def test_the_drop_at_the_customer_adds_the_service_on_its_own_phase(rs):
    net = build(route("a", (0, 0), (200, 0)), site("t", "transformer", 0, 0), rules=RULES)
    [b] = net.branches
    loads = [LoadAt(load_id=x, branch=b.id, offset_m=b.length_m, phase=p, kva=5, kind="special", service_pct=s)
             for x, p, s in (("r", "R", 1.0), ("w", "W", 0.0))]
    r = analyse(AnalyseRequest(rules=RULES, network=net, loads=loads), rs)
    end = next(p for p in r.points if p.id == b.to_node)
    cr, cw = (next(p for p in r.points if p.id == x) for x in ("r", "w"))
    assert cr.worst_pct == pytest.approx(end.drop_pct["R"] + 1.0, abs=2e-3) and cr.service_pct == 1.0
    assert cw.worst_pct == pytest.approx(end.drop_pct["W"], abs=2e-3)
    assert r.worst_customer.formula_id == "lv.vdrop.customer.v1" and r.worst_customer.value == pytest.approx(cr.worst_pct, abs=2e-3)
    # The drop limit is the engineer's from 0.9.0, no longer a placeholder; older rules still say it is.
    assert not any("voltage drop limit" in p for p in r.placeholders)
    old = analyse(AnalyseRequest(rules="eskom/0.8.1", network=net, loads=loads), load_rules("eskom/0.8.1"))
    assert any("voltage drop limit" in p for p in old.placeholders)


# ------------------------------------------------------------------ feeder fuses


def test_the_fuse_is_the_smallest_rating_above_the_design_current(rs):
    net = build(route("a", (0, 0), (200, 0)), site("t", "transformer", 0, 0), rules=RULES)
    [b] = net.branches
    load = LoadAt(load_id="L", branch=b.id, offset_m=b.length_m, phase="RWB", kva=60, kind="special")
    r = analyse(AnalyseRequest(rules=RULES, network=net, loads=[load], conductors={b.id: "ABC-3C-70"}), rs)
    [f] = r.feeders
    i_b = 60000 / (3 * 400 / math.sqrt(3))
    assert f.design_current_a == pytest.approx(i_b, abs=0.01) and f.fuse_a == 100 and f.conductor_rating_a == 223
    assert f.min_fault_required_a == 300 and f.protected and f.min_fault_a >= 300
    assert r.protection.formula_id == "lv.protection.fuse.v1"
    assert any("LV feeder fuses" in p for p in r.placeholders)
    # No fuse between the current and the conductor: 150 A on 25 mm² ABC (107 A) needs 160 A.
    big = LoadAt(load_id="L", branch=b.id, offset_m=b.length_m, phase="RWB", kva=104, kind="special")
    [g] = analyse(AnalyseRequest(rules=RULES, network=net, loads=[big], conductors={b.id: "ABC-3C-25"}), rs).feeders
    assert g.fuse_a == 160 and not g.protected and not g.passes


def test_a_long_feeder_steps_up_until_its_end_fault_clears_the_fuse(rs):
    """A small load 900 m out: the drop is fine, but the end fault on 25 mm² is far below 3 × 63 A."""
    net = build(route("a", (0, 0), (900, 0)), site("t", "transformer", 0, 0), rules=RULES)
    b = next(x for x in net.branches if x.kind == "route")
    load = LoadAt(load_id="L", branch=b.id, offset_m=b.length_m, phase="R", kva=1, kind="special")
    sizing, r = size_lv(net, [load], rs, "overhead", {})
    f = next(x for x in r.feeders if x.feeder == b.feeder)
    assert f.fuse_a == 63 and f.protected and f.min_fault_a >= 189
    assert sizing.feeders[b.feeder] == "ABC-3C-70"
    assert any(s.reason == "fault current at the far end too low for the fuse" for s in sizing.steps)


# ------------------------------------------------------------------ the design run


@pytest.mark.parametrize("construction", ["overhead", "underground"])
def test_the_design_sizes_every_service_and_checks_fuses(rs, construction):
    cands, loads, classes = layout()
    d = run(DesignRequest(rules=RULES, candidates=cands, loads=loads, classes=classes, connection_point=cp(),
                          options={"construction": construction}), rs)
    assert d.services is not None and len(d.services.services) == len(d.lv.allocation.allocations)
    expected = {"AIRDAC-SNE-10"} if construction == "overhead" else {"CU-2C-16"}
    assert {s.conductor for s in d.services.services} == expected
    cats = {c.category for c in d.checks}
    assert {"lv_fuse", "lv_fault", "lv_service_drop"} <= cats
    assert ("oh_service" in cats) == (construction == "overhead")
    by_load = {s.load_id: s for s in d.services.services}
    for p in d.lv.analysis.points:
        if p.kind == "connection":
            assert p.service_pct == pytest.approx(by_load[p.id].drop_pct, abs=1e-3)
    ph = "; ".join(d.placeholders)
    assert f"services ({construction})" in ph and "voltage drop limit" not in ph
    assert ("overhead.house_attachment_m" in ph) == (construction == "overhead")
