"""The design run (plans 2.5 to 5.1): demand, kiosks, transformers, LV and MV sizing, bulk studies, cost, the whole run."""

import math

import pytest
from test_lv_network import build, ll, route, site

from reticula_calc import cost
from reticula_calc.design import transformers as tx_mod
from reticula_calc.design import underground as ug_mod
from reticula_calc.design.demand import Demand, DemandModel
from reticula_calc.design.run import DesignCandidate, DesignRequest, inputs_hash, run
from reticula_calc.design.sizing import library, size_lv
from reticula_calc.lv.analysis import LoadAt, SourceIn
from reticula_calc.lv.loads import AllocateRequest, LoadIn, allocate
from reticula_calc.mv import network as mv_mod
from reticula_calc.rules import load_rules

RULES = "eskom/0.8.0"


@pytest.fixture(scope="module")
def rs():
    return load_rules(RULES)


def load(id: str, x: float, y: float, kva: float = 2.37, kind: str = "residential") -> LoadIn:
    return LoadIn(id=id, building_id=f"b-{id}", label=id, coordinates=tuple(ll(x, y)), kva=kva, kind=kind)


def cand(id: str, kind: str, *pts: tuple[float, float], source: str = "field") -> DesignCandidate:
    g = {"type": "Point", "coordinates": ll(*pts[0])} if len(pts) == 1 else {"type": "LineString", "coordinates": [ll(*p) for p in pts]}
    return DesignCandidate(id=id, kind=kind, geometry=g, source=source)


def cp(**kw) -> mv_mod.ConnectionPointIn:
    args = {"coordinates": tuple(ll(400, -605)), "voltage_kv": 11, "capacity_kva": 1000, "fault_3ph_ka": 2.5, "fault_1ph_ka": 2.0,
            "x_over_r": 5}
    return mv_mod.ConnectionPointIn(**{**args, **kw})


def layout(proposed: bool = False) -> tuple[list[DesignCandidate], list[LoadIn], dict[str, str]]:
    """A transformer on a 300 m street with a 150 m side street, 22 houses, and an MV line from the connection point."""
    cands = [cand("tx", "transformer", (0, -10)), cand("r1", "lv_route", (-150, 0), (150, 0)),
             cand("r2", "lv_route", (0, 0), (0, 150), source="proposed" if proposed else "field"),
             cand("mv", "mv_route", (0, -12), (0, -600), (400, -600))]
    loads = [load(f"a{i}", i, 15) for i in range(-140, 141, 20)] + [load(f"b{j}", 12, j) for j in range(10, 141, 20)]
    return cands, loads, {x.id: "township_area" for x in loads}


# ------------------------------------------------------------------ demand


def test_fixed_loads_sum_their_current_and_the_worst_phase_sets_the_kva(rs):
    m = DemandModel(rs)
    d = Demand()
    for p, kva in (("R", 10.0), ("R", 5.0), ("W", 5.0), ("B", 5.0)):
        d.add(m.load(p, kva, "special", None))
    i = d.currents(m.conf)
    # 15 kVA on R: 15 000 / 230.94 = 64.95 A; the supply is loaded by its worst phase, 3 × 230.94 × 64.95 = 45 kVA.
    assert i["R"] == pytest.approx(15000 / (400 / math.sqrt(3)), rel=1e-9)
    assert d.kva(m.conf, m.v_ph) == pytest.approx(45.0, rel=1e-9)
    assert d.loads == 4


def test_a_three_phase_load_takes_a_third_on_each_phase(rs):
    m = DemandModel(rs)
    d = m.load("RWB", 30.0, "special", None)
    assert d.currents(m.conf)["W"] == pytest.approx(10000 / (400 / math.sqrt(3)), rel=1e-9)
    assert d.kva(m.conf, m.v_ph) == pytest.approx(30.0, rel=1e-9)


def test_classed_houses_diversify_and_unclassed_ones_are_reported(rs):
    m = DemandModel(rs)
    one = m.load("R", 2.37, "residential", "township_area")
    many = Demand()
    for _ in range(20):
        many.add(m.load("R", 2.37, "residential", "township_area"))
    assert many.currents(m.conf)["R"] < 20 * one.currents(m.conf)["R"]
    m.load("R", 2.37, "residential", None, "h1")
    assert m.unclassed == ["h1"]


# ------------------------------------------------------------------ kiosks and de-rating


def test_kiosks_go_where_the_customers_are_within_reach_and_capacity(rs):
    net = build(route("a", (0, 0), (200, 0)), site("t", "transformer", 0, -5), rules=RULES)
    houses = [load(f"h{c}", c, 10) for c in range(5, 200, 10)]  # 20 houses 10 m off the route
    kiosks = ug_mod.place_kiosks(net, rs, houses)
    # Within 35 m of a kiosk 10 m away means within 33.5 m along the route: groups of 7, 7 and 6.
    assert [k.label for k in kiosks] == ["K1", "K2", "K3"]
    net2 = build(route("a", (0, 0), (200, 0)), site("t", "transformer", 0, -5), *kiosks, rules=RULES)
    alloc = allocate(AllocateRequest(rules=RULES, network=net2, loads=houses), rs, ug_mod.kiosk_params(rs))
    assert alloc.summary.allocated == 20 and alloc.summary.longest_service_m <= 35
    assert not [i for i in alloc.issues if i.severity == "error"]


def test_no_kiosk_on_a_route_without_customers(rs):
    net = build(route("a", (0, 0), (200, 0)), site("t", "transformer", 0, -5), rules=RULES)
    assert ug_mod.place_kiosks(net, rs, [load("far", 100, 80)]) == []


def test_derating_multiplies_the_factors_and_groups_the_cables_leaving_one_board(rs):
    net = build(route("a", (0, 0), (100, 0)), route("b", (0, 0), (-100, 0)), site("t", "transformer", 0, -5), rules=RULES)
    std = ug_mod.derate(net, rs, {}, "AL-4C-70", rs.data["underground"]["standard"])
    # At the standard conditions only grouping de-rates.
    first = [r for r in std.ratings if r.group > 1]
    assert first and all(r.soil == r.depth == r.temperature == 1.0 for r in std.ratings)
    hot = ug_mod.derate(net, rs, {}, "AL-4C-70", {"ground_temp_c": 35, "soil_resistivity_kmw": 2.0})
    for a, b in zip(std.ratings, hot.ratings, strict=True):
        assert b.derated_a < a.derated_a
        assert b.derated_a == pytest.approx(b.base_a * b.soil * b.depth * b.temperature * b.grouping, abs=0.01)


# ------------------------------------------------------------------ transformers


def specials(net, rs, kva: float):
    loads = [load(f"s{k}", x, 5, kva=kva, kind="special") for k, x in enumerate((20, 40, 60))]
    return allocate(AllocateRequest(rules=RULES, network=net, loads=loads), rs)


def test_transformer_is_the_smallest_rating_with_the_growth_allowance(rs):
    cands = [route("a", (0, 0), (80, 0)), site("t", "transformer", 0, -5)]
    net0 = build(*cands, rules=RULES)
    from reticula_calc.design.overhead import place_poles

    net = build(*cands, *place_poles(net0, rs, "lv"), rules=RULES)
    alloc = specials(net, rs, 20.0)  # three 20 kVA three-phase loads: 20 kVA on each phase, 60 kVA
    res = tx_mod.size(net, alloc, {}, rs, "overhead")
    [t] = res.transformers
    # 60 kVA needs 60 / 0.9 = 66.7 kVA of rating: 100 kVA, pole-mounted.
    assert t.demand_kva == pytest.approx(60.0, rel=1e-6)
    assert (t.rating_kva, t.mounting, t.passes) == (100.0, "pole", True)
    assert t.spare_kva == pytest.approx(100 * 0.9 - 60.0, abs=0.01)
    assert (t.impedance_pct, t.x_over_r) == (4.0, 2.0)
    assert t.trace.formula_id == tx_mod.SIZE_ID

    under = tx_mod.size(net, alloc, {}, rs, "underground")
    assert (under.transformers[0].rating_kva, under.transformers[0].mounting) == (200.0, "minisub")
    small = tx_mod.size(net, alloc, {}, rs, "overhead", {"TX1": 50})
    assert small.transformers[0].passes is False and "transformer_overloaded" in {i.code for i in small.issues}


# ------------------------------------------------------------------ LV sizing


def test_lv_feeders_step_up_until_the_drop_passes_and_a_fixed_conductor_is_kept(rs):
    net = build(route("a", (0, 0), (600, 0)), site("t", "transformer", 0, -5), rules=RULES)
    b = next(x for x in net.branches if x.kind == "route")
    loads = [LoadAt(load_id=f"s{p}", branch=b.id, offset_m=b.length_m, phase=p, kva=12, kind="special") for p in "RWB"]
    src = {"TX1": SourceIn(rating_kva=100, impedance_pct=4, x_over_r=2)}
    sizing, result = size_lv(net, loads, rs, "overhead", src)
    lib = [c.code for c in library(rs, "overhead")]
    f = net.feeders[0].id
    assert sizing.feeders[f] != lib[0] and any(s.reason.startswith("voltage drop") for s in sizing.steps)
    assert result.feeders[0].max_drop_pct <= result.limit_pct or sizing.feeders[f] == lib[-1]
    fixed, r2 = size_lv(net, loads, rs, "overhead", src, {f: lib[0]})
    assert fixed.feeders[f] == lib[0] and r2.feeders[0].max_drop_pct > r2.limit_pct


# ------------------------------------------------------------------ MV


def test_mv_drop_to_a_tap_by_hand(rs):
    cands, loads, classes = layout()
    d = run(DesignRequest(rules=RULES, candidates=cands, loads=loads, classes=classes, connection_point=cp(),
                          options={"construction": "overhead"}), rs)
    [tap] = d.mv.taps
    pf = 0.95
    fox = rs.conductor("FOX")
    t = d.transformers.transformers[0]
    # One transformer: every MV section carries its consumers' demand, I = S / (√3·11 kV), over the route length.
    i = d.mv.branches[0].demand_kva / (math.sqrt(3) * 11)
    length = sum(b.length_m for b in d.mv_network.branches if b.kind in ("route", "link"))
    hand = math.sqrt(3) * i * (fox.r_ohm_per_km * pf + fox.x_ohm_per_km * math.sqrt(1 - pf * pf)) * length / 1000 / 11000 * 100
    assert tap.drop_pct == pytest.approx(hand, rel=0.01)
    er = t.impedance_pct / math.sqrt(1 + t.x_over_r**2)
    reg = t.demand_kva / t.rating_kva * (er * pf + er * t.x_over_r * math.sqrt(1 - pf * pf))
    assert tap.regulation_pct == pytest.approx(reg, rel=1e-3)
    assert tap.lv_full_load_pct == pytest.approx(100 - tap.drop_pct - reg + tap.tap_pct, abs=1e-3)


# ------------------------------------------------------------------ bulk


def test_bulk_stops_without_the_connection_point_data(rs):
    cands, loads, classes = layout()
    d = run(DesignRequest(rules=RULES, candidates=cands, loads=loads, classes=classes, connection_point=cp(fault_3ph_ka=None),
                          options={"construction": "overhead"}), rs)
    assert d.bulk.stopped and "three-phase fault level" in d.bulk.stopped
    assert "connection_point_incomplete" in {i.code for i in d.issues}
    assert not d.fit_to_submit
    none = run(DesignRequest(rules=RULES, candidates=cands, loads=loads, classes=classes, options={"construction": "overhead"}), rs)
    assert {"no_connection_point", "connection_point_incomplete"} <= {i.code for i in none.issues}


def test_bulk_fault_at_the_connection_point_is_the_authoritys_and_the_supply_is_a_step(rs):
    cands, loads, classes = layout()
    d = run(DesignRequest(rules=RULES, candidates=cands, loads=loads, classes=classes, connection_point=cp(),
                          options={"construction": "overhead"}), rs)
    b = d.bulk
    assert b.converged and b.stopped is None
    at_cp = next(x for x in b.buses if x.id == "MV:" + next(n.id for n in d.mv_network.nodes if n.kind == "source"))
    assert at_cp.ik3_max_ka == pytest.approx(2.5, rel=0.01)
    lv = [x for x in b.buses if x.level == "lv" and x.ik1_min_ka is not None]
    assert lv and all(x.ik1_min_ka > 0 for x in lv)
    s = b.supply
    assert s.required_kva == pytest.approx(s.demand_kva / 0.9, rel=1e-3)
    assert s.nmd_kva == min(x for x in (100, 200, 315, 500, 750, 1000) if x >= s.required_kva)
    small = run(DesignRequest(rules=RULES, candidates=cands, loads=loads, classes=classes, connection_point=cp(capacity_kva=50),
                              options={"construction": "overhead"}), rs)
    assert "supply_capacity" in {i.code for i in small.issues}


# ------------------------------------------------------------------ cost


def lib():
    return cost.RateLibrary(name="test", rate_date="2026-01-01", source="hand", items=[
        cost.RateItem(code="POLE", description="Pole", unit="each", rate=1000, uncertainty_pct=10),
        cost.RateItem(code="LAB", description="Labour", unit="h", rate=200)],
        assemblies=[cost.Assembly(code="A-POLE", description="Pole erected", unit="each",
                                  components=[cost.AssemblyComponent(item="POLE", qty=1), cost.AssemblyComponent(item="LAB", qty=2.5)])])


def test_price_sums_assemblies_with_their_uncertainty(rs):
    econ = cost.economics(rs, {"rate_uncertainty_pct": 20})
    q = cost.Quantities()
    q.add("LV", "A-POLE", 3)
    q.add("LV", "A-MISSING", 2)
    nil = cost.losses(0, 0, 0, 0, econ)
    r = cost.price(q, lib(), econ, nil, rs)
    # 3 × (1000 + 2.5 × 200) = 4500; low 3 × (900 + 400) = 3900; high 3 × (1100 + 600) = 5100.
    assert (r.capex, r.capex_low, r.capex_high) == (4500, 3900, 5100)
    assert [i.code for i in r.issues] == ["no_rate"]
    assert r.lines[1].amount is None


def test_losses_present_value_by_hand(rs):
    econ = cost.Economics(period_years=2, discount_rate_pct=10, energy_cost_zar_per_kwh=2, load_growth_pct=0,
                          loss_load_factor=0.5, rate_uncertainty_pct=0)
    loss = cost.losses(1.0, 0.0, 0.0, 0.1, econ)
    # E = 1 kW × 0.5 × 8760 + 0.1 kW × 8760 = 5256 kWh a year, R 10 512, over 2 years at 10 %.
    assert loss.energy_mwh_per_year == pytest.approx(5.256)
    assert loss.npv == pytest.approx(10512 / 1.1 + 10512 / 1.21, abs=0.01)


def test_the_indicative_library_prices_every_assembly(rs):
    lib = cost.default_library()
    items = {i.code for i in lib.items}
    assert lib.indicative and all(c.item in items for a in lib.assemblies for c in a.components)


# ------------------------------------------------------------------ the run


def test_overhead_run_connects_every_house_and_lists_its_checks(rs):
    cands, loads, classes = layout()
    d = run(DesignRequest(rules=RULES, candidates=cands, loads=loads, classes=classes, connection_point=cp(),
                          options={"construction": "overhead"}), rs)
    s = d.summary
    assert (s.construction, s.loads, s.connected, s.transformers) == ("overhead", 22, 22, 1)
    assert s.poles > 0 and s.kiosks == 0 and s.failures == 0
    assert {c.category for c in d.checks} >= {"lv_drop", "lv_loading", "oh_clearance", "oh_tension", "oh_pole", "tx_loading", "mv_drop",
                                               "bulk_supply", "bulk_fault"}
    assert all(c.clause for c in d.checks)
    # Placeholders in the rules make every design unfit to submit, even one that passes.
    assert d.placeholders and not d.fit_to_submit
    groups = {line.group for line in d.cost.lines}
    assert groups == {"LV", "Services", "Transformers", "MV"} and d.cost.capex > 0 and d.cost.lifetime > d.cost.capex


def test_underground_run_uses_kiosks_cable_and_a_minisub(rs):
    cands, loads, classes = layout()
    d = run(DesignRequest(rules=RULES, candidates=cands, loads=loads, classes=classes, connection_point=cp(),
                          options={"construction": "underground"}), rs)
    assert d.summary.kiosks > 0 and d.summary.poles == 0 and d.summary.connected == 22
    assert d.transformers.transformers[0].mounting == "minisub"
    codes = {line.code for line in d.cost.lines}
    assert {"A-TRENCH-LV", "A-KIOSK", "A-SERVICE-UG"} <= codes
    assert d.underground is not None and d.overhead is None


def test_compare_returns_the_preferred_option_with_both_summarised(rs):
    cands, loads, classes = layout()
    d = run(DesignRequest(rules=RULES, candidates=cands, loads=loads, classes=classes, connection_point=cp(),
                          options={"construction": "compare", "objective": "capex"}), rs)
    assert {o.construction for o in d.comparison} == {"overhead", "underground"}
    [chosen] = [o for o in d.comparison if o.chosen]
    assert chosen.construction == d.construction
    other = next(o for o in d.comparison if not o.chosen)
    assert chosen.failures <= other.failures or chosen.capex <= other.capex


def test_proposed_routes_are_not_inspected_and_block_submission(rs):
    cands, loads, classes = layout(proposed=True)
    d = run(DesignRequest(rules=RULES, candidates=cands, loads=loads, classes=classes, connection_point=cp(),
                          options={"construction": "overhead"}), rs)
    [ni] = d.not_inspected
    assert ni.candidate_id == "r2" and ni.elements
    assert any(c.category == "not_inspected" and not c.passes for c in d.checks)


def test_inputs_hash_is_stable_and_changes_with_the_inputs(rs):
    cands, loads, classes = layout()
    a = DesignRequest(rules=RULES, candidates=cands, loads=loads, classes=classes)
    b = DesignRequest(rules=RULES, candidates=cands, loads=loads, classes=classes)
    c = DesignRequest(rules=RULES, candidates=cands, loads=loads[:-1], classes=classes)
    assert inputs_hash(a) == inputs_hash(b) != inputs_hash(c)


def test_design_endpoint(client):
    cands, loads, classes = layout()
    body = DesignRequest(rules=RULES, candidates=cands, loads=loads, classes=classes, connection_point=cp()).model_dump(mode="json")
    r = client.post("/calc/design/run", json=body)
    assert r.status_code == 200, r.text
    out = r.json()
    assert out["rules_ref"] == RULES and out["summary"]["connected"] == 22 and out["fit_to_submit"] is False
    assert client.get("/rates/default").json()["indicative"] is True


def test_a_design_request_reproduces_bit_for_bit(client):
    # Plan 7.2 and the release gate: the stored request of a revision gives the same bytes again, in any order of its keys.
    cands, loads, classes = layout(proposed=True)
    body = DesignRequest(rules=RULES, candidates=cands, loads=loads, classes=classes, connection_point=cp(),
                         options={"construction": "compare"}).model_dump(mode="json")
    first = client.post("/calc/design/run", json=body)
    load_rules.cache_clear()  # read and hash the rules file afresh, as a later reproduction would
    again = client.post("/calc/design/run", json=dict(reversed(list(body.items()))))
    assert first.status_code == again.status_code == 200
    assert first.content == again.content
