"""LV load allocation and phasing (plan 2.2)."""

import dataclasses
import math

import pytest
from test_lv_network import build, ll, route, site

from reticula_calc.lv.loads import AllocateRequest, LoadIn, allocate
from reticula_calc.rules import RulesError, load_rules

RULES = "eskom/0.4.0"


def house(id: str, x: float, y: float, kva: float | None = 2.0, kind: str = "residential") -> LoadIn:
    return LoadIn(id=f"lp-{id}", building_id=f"b-{id}", label=id, coordinates=tuple(ll(x, y)), kva=kva, kind=kind)


def around(prefix: str, x: float, y: float, bearings, r: float = 20, kva: float = 2.0) -> list[LoadIn]:
    """Houses r metres from (x, y) at the given bearings, clockwise from north."""
    return [house(f"{prefix}{b}", x + r * math.sin(math.radians(b)), y + r * math.cos(math.radians(b)), kva) for b in bearings]


def run(net, *loads: LoadIn, rules=None):
    rs = rules or load_rules(RULES)
    return allocate(AllocateRequest(rules=rs.ref, network=net, loads=list(loads)), rs)


def with_rules(**lv_loads):
    rs = load_rules(RULES)
    return dataclasses.replace(rs, data={**rs.data, "lv_loads": {**rs.data["lv_loads"], **lv_loads}})


POINT = with_rules(attach="nearest_point")


def by_label(result):
    return {a.label: a for a in result.allocations}


def codes(result):
    return {i.code: i for i in result.issues}


# ---------- service distribution boxes on poles (the Eskom default) ----------


def test_a_poles_loads_fill_boxes_of_four_with_neighbours_together_on_different_phases():
    net = build(route("a", (0, 0), (200, 0)), site("t", "transformer", 0, 0), site("p", "pole", 100, 0))
    north = around("n", 100, 0, [300, 330, 30, 60])
    south = around("s", 100, 0, [150, 175])
    r = run(net, *north, *south)
    assert r.issues == []
    [b1, b2] = r.boxes
    assert (b1.id, b1.loads, b2.id, b2.loads) == ("P1-1", 4, "P1-2", 2)
    a = by_label(r)
    assert {a[x.label].box for x in north} == {"P1-1"} and {a[x.label].box for x in south} == {"P1-2"}
    assert b1.phase != b2.phase
    assert all(a[x.label].phase == b1.phase for x in north)
    assert all(x.node == r.allocations[0].node for x in r.allocations)
    assert a["n300"].service_m == pytest.approx(20, abs=0.1) and a["n300"].distance_m == pytest.approx(100, abs=0.2)
    [f] = r.feeders
    assert sorted((x.boxes, x.customers) for x in f.phases.values()) == [(0, 0), (1, 2), (1, 4)]
    assert r.summary.boxes == 2


def test_a_full_pole_passes_loads_to_the_next_nearest_pole():
    net = build(route("a", (0, 0), (200, 0)), site("t", "transformer", 0, 0), site("p1", "pole", 100, 0), site("p2", "pole", 130, 0))
    houses = around("h", 100, 0, range(0, 360, 36), r=8)  # ten houses round P1
    r = run(net, *houses)
    assert r.issues == []
    per_box = {b.id: b.loads for b in r.boxes}
    assert per_box == {"P1-1": 4, "P1-2": 4, "P2-1": 2}
    # The two that moved are the ones nearest P2.
    moved = sorted(a.label for a in r.allocations if a.box == "P2-1")
    assert moved == ["h108", "h72"]


def test_buildings_with_no_pole_or_only_full_ones_are_reported():
    net = build(route("a", (0, 0), (300, 0)), site("t", "transformer", 0, 0), site("p", "pole", 50, 0))
    r = run(net, *around("h", 50, 0, range(0, 360, 40), r=10), house("lonely", 250, 10))
    c = codes(r)
    assert c["poles_full"].severity == "error" and c["poles_full"].count == 1
    assert c["no_pole"].samples == ["lonely"]
    assert (r.summary.allocated, r.summary.unallocated) == (8, 2)


def test_a_second_box_on_a_pole_takes_another_phase_even_when_balance_would_not():
    net = build(route("a", (0, 0), (300, 0)), site("t", "transformer", 0, 0),
                site("p1", "pole", 100, 0), site("p2", "pole", 200, 0), site("p3", "pole", 300, 0))
    loads = [*around("far", 300, 0, [0, 90, 180, 270], r=10, kva=12.5), *around("mid", 200, 0, [0, 90, 180, 270], r=10, kva=12.5),
             *around("near", 100, 0, [0, 30, 60, 90, 180], r=10, kva=1)]
    boxes = {b.id: b.phase for b in run(net, *loads).boxes}
    # P3 and P2 take R and W with 50 kVA each. P1's first box goes to B, the empty phase; its small second box would
    # also go to B on balance alone, but takes R instead.
    assert boxes == {"P3-1": "R", "P2-1": "W", "P1-1": "B", "P1-2": "R"}
    loose = {b.id: b.phase for b in run(net, *loads, rules=with_rules(second_box_other_phase=False)).boxes}
    assert loose["P1-1"] == loose["P1-2"] == "B"


def test_three_phase_loads_take_their_own_service_and_no_box_space():
    net = build(route("a", (0, 0), (200, 0)), site("t", "transformer", 0, 0), site("p", "pole", 100, 0))
    r = run(net, house("school", 100, 30, kva=30, kind="special"), *around("h", 100, 0, range(0, 360, 45), r=10))
    school = by_label(r)["school"]
    assert (school.phase, school.box) == ("RWB", None)
    assert r.summary.allocated == 9 and r.summary.boxes == 2 and r.summary.three_phase == 1
    [f] = r.feeders
    # 10 kVA of the school on each phase; two boxes of four 2 kVA houses on two phases.
    assert sorted(x.kva for x in f.phases.values()) == [10, 18, 18]
    assert f.unbalance_pct == pytest.approx((46 / 3 - 10) / (46 / 3) * 100, abs=0.1)


# ---------- nearest point on a route, phased per load ----------


def test_connects_each_house_to_the_nearest_point_of_its_feeder():
    net = build(route("a", (0, 0), (200, 0)), site("t", "transformer", 100, 20))
    r = run(net, house("e1", 150, -15), house("w1", 30, 12), rules=POINT)
    e1, w1 = by_label(r)["e1"], by_label(r)["w1"]
    assert (e1.feeder, w1.feeder) == (net.feeders[0].id, net.feeders[1].id)
    assert e1.service_m == pytest.approx(15, abs=0.1) and w1.service_m == pytest.approx(12, abs=0.1)
    # The board is 20 m down the link; e1 connects 50 m east of it.
    assert e1.distance_m == pytest.approx(70, abs=0.2) and w1.distance_m == pytest.approx(90, abs=0.2)
    assert e1.offset_m == pytest.approx(50, abs=0.2) and e1.node is None and e1.box is None
    assert e1.at == pytest.approx(ll(150, 0), abs=1e-6)
    assert r.issues == [] and r.boxes == [] and r.summary.longest_service_m == pytest.approx(15, abs=0.1)


def test_spreads_single_loads_evenly_from_the_far_end():
    net = build(route("a", (0, 0), (400, 0)), site("t", "transformer", 0, 0))
    r = run(net, *[house(f"h{i}", 40 * i, 10) for i in range(1, 10)], rules=POINT)
    [f] = r.feeders
    assert {ph: x.customers for ph, x in f.phases.items()} == {"R": 3, "W": 3, "B": 3} and f.unbalance_pct == 0
    # Beyond any point of the feeder the loads stay as even as they can: within one customer per phase.
    ordered = sorted(r.allocations, key=lambda a: -a.distance_m)
    for k in range(1, len(ordered) + 1):
        counts = [sum(1 for a in ordered[:k] if a.phase == ph) for ph in "RWB"]
        assert max(counts) - min(counts) <= 1
    assert [a.phase for a in ordered[:3]] == ["R", "W", "B"]


def test_reports_buildings_out_of_reach_without_estimates_or_on_no_feeder():
    net = build(route("a", (0, 0), (100, 0)), route("b", (500, 0), (600, 0)), site("t", "transformer", 0, 0))
    r = run(net, house("ok", 50, 10), house("far", 50, 100), house("new", 60, 10, kva=None), house("lost", 550, 10), rules=POINT)
    c = codes(r)
    assert c["unallocated"].severity == "error" and c["unallocated"].samples == ["far"]
    assert c["no_load"].samples == ["new"] and c["no_load"].at[0] == pytest.approx(ll(60, 10), abs=1e-9)
    assert c["loads_unfed"].samples == ["lost"]
    assert by_label(r)["lost"].phase is None and by_label(r)["lost"].distance_m is None
    s = r.summary
    assert (s.loads, s.allocated, s.unallocated, s.unestimated, s.on_feeders) == (4, 2, 1, 1, 1)


# ---------- inputs ----------


def test_needs_a_network_and_rules_with_load_settings():
    r = run(build(site("t", "transformer", 0, 0)), house("h", 0, 10))
    assert [i.code for i in r.issues] == ["no_network"] and r.summary.unallocated == 1
    net = build(route("a", (0, 0), (100, 0)), site("t", "transformer", 0, 0))
    with pytest.raises(RulesError, match="lv_loads"):
        run(net, house("h", 0, 10), rules=load_rules("eskom/0.3.0"))


def test_the_schema_requires_box_settings_with_pole_boxes():
    import jsonschema

    from reticula_calc.rules.loader import _schema

    rs = load_rules(RULES)
    doc = {**rs.data, "lv_loads": {k: v for k, v in rs.data["lv_loads"].items() if k != "box_loads"}}
    with pytest.raises(jsonschema.ValidationError, match="box_loads"):
        jsonschema.validate(doc, _schema())
    doc["lv_loads"]["attach"] = "nearest_point"
    jsonschema.validate(doc, _schema())


def test_endpoint(client):
    net = build(route("a", (0, 0), (100, 0)), site("t", "transformer", 0, 0), site("p", "pole", 50, 0))
    body = {"rules": RULES, "network": net.model_dump(), "loads": [house("h", 50, 10).model_dump()]}
    r = client.post("/calc/lv/loads", json=body)
    assert r.status_code == 200
    out = r.json()
    assert out["allocations"][0]["box"] == "P1-1" and out["boxes"][0]["phase"] == "R"
    assert out["feeders"][0]["phases"]["R"] == {"customers": 1, "kva": 2.0, "boxes": 1}
    assert client.post("/calc/lv/loads", json={**body, "rules": "eskom/0.3.0"}).status_code == 422
