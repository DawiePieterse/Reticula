"""Pre-design placement (plan 2.0): transformers, LV coverage and MV route proposed from loads and roads."""

import pytest

from reticula_calc.geo import crs
from reticula_calc.lv.placement import LoadIn, PlacementRequest, RoadIn, place
from reticula_calc.rules import RulesError, load_rules

RULES = "eskom/0.7.0"
ORIGIN = crs.from_wgs84("LO29").transform(28.1, -25.5)
ADMD = 2.37  # township_area in nrs034_15y


def ll(x: float, y: float) -> tuple[float, float]:
    lon, lat = crs.to_wgs84("LO29").transform(ORIGIN[0] + x, ORIGIN[1] + y)
    return (lon, lat)


def grid(blocks: int = 4, block_m: float = 100.0) -> list[RoadIn]:
    """A street grid: (blocks+1) east-west and north-south roads."""
    roads = []
    for k in range(blocks + 1):
        roads.append(RoadIn(id=f"ew{k}", coordinates=[ll(0, k * block_m), ll(blocks * block_m, k * block_m)]))
        roads.append(RoadIn(id=f"ns{k}", coordinates=[ll(k * block_m, 0), ll(k * block_m, blocks * block_m)]))
    return roads


def houses(blocks: int = 4, block_m: float = 100.0, per_side: int = 5, load_class: str | None = "township_area") -> list[LoadIn]:
    """Houses 12 m back from both sides of every east-west road."""
    loads = []
    for k in range(blocks + 1):
        for j in range(blocks * per_side):
            x = (j + 0.5) * block_m / per_side
            for side, dy in (("n", 12), ("s", -12)):
                y = k * block_m + dy
                if 0 <= y <= blocks * block_m:
                    loads.append(LoadIn(id=f"h{k}-{j}{side}", coordinates=ll(x, y), kva=ADMD, load_class=load_class))
    return loads


CP = ll(-50, -50)


def run(roads, loads, cp=CP, rules=RULES):
    return place(PlacementRequest(rules=rules, roads=roads, loads=loads, connection_point=cp), load_rules(rules))


def test_every_house_is_fed_within_reach_and_capacity():
    r = run(grid(), houses())
    codes = {i.code for i in r.issues}
    assert "unassigned" not in codes and "far_from_road" not in codes
    assert len(r.assignments) == len(houses())
    assert len({a.load_id for a in r.assignments}) == len(r.assignments)
    for t in r.transformers:
        assert t.demand_kva <= t.rating_kva * 0.9 + 1e-9, t
        assert t.rating_kva in (16, 25, 50, 100, 200, 315, 500)
    for a in r.assignments:
        assert a.road_m <= 400


def test_a_township_needs_more_than_one_transformer_and_the_mv_reaches_them_all():
    r = run(grid(), houses(per_side=8))  # 256 houses: over one 500 kVA transformer at 10 % growth
    assert len(r.transformers) >= 2
    assert all(t.demand_kva <= 500 * 0.9 for t in r.transformers)
    # The MV tree touches every transformer site.
    mv_points = {c for route in r.mv_routes for c in route.coordinates}
    for t in r.transformers:
        assert t.coordinates in mv_points, t.id
    assert r.cost.value > 0 and r.cost.formula_id == "placement.cost.v1"
    assert {c.kind for c in r.candidates} == {"transformer", "lv_route", "mv_route"}


def test_herman_beta_demand_is_what_sizes_the_transformer():
    r = run(grid(), houses())
    assert r.worst_demand is not None and r.worst_demand.formula_id == "placement.demand.herman-beta.v1"
    tx = max(r.transformers, key=lambda t: t.demand_kva)
    # The 90 % demand per house sits above the class mean (the published ADMD) and falls towards it as the group grows.
    per_house = tx.demand_kva / tx.loads
    assert ADMD < per_house < 1.3 * ADMD
    small = run(grid(1), houses(blocks=1, per_side=1))
    assert small.transformers[0].demand_kva / small.transformers[0].loads > per_house


def test_fewer_houses_mean_fewer_transformers():
    few = run(grid(), houses(per_side=1))
    many = run(grid(), houses(per_side=5))
    assert len(few.transformers) <= len(many.transformers)


def test_a_house_far_from_any_road_is_reported():
    loads = houses(per_side=1) + [LoadIn(id="farm", coordinates=ll(2000, 2000), kva=ADMD, load_class="township_area")]
    r = run(grid(), loads)
    far = next(i for i in r.issues if i.code == "far_from_road")
    assert far.samples == ["farm"]


def test_loads_without_a_class_are_added_at_their_admd_and_warned():
    r = run(grid(), houses(per_side=1, load_class=None))
    assert any(i.code == "no_load_class" for i in r.issues)
    assert len(r.assignments) == len(houses(per_side=1))


def test_no_connection_point_means_no_mv_route():
    r = run(grid(), houses(per_side=1), cp=None)
    assert r.mv_routes == [] and any(i.code == "no_connection_point" for i in r.issues)


def test_older_rules_have_no_placement_settings():
    with pytest.raises(RulesError):
        run(grid(), houses(per_side=1), rules="eskom/0.6.0")


def test_endpoint(client):
    body = PlacementRequest(rules=RULES, roads=grid(2), loads=houses(blocks=2, per_side=2), connection_point=ll(-30, -30)).model_dump()
    r = client.post("/calc/lv/placement", json=body)
    assert r.status_code == 200 and r.json()["transformers"]


def test_reach_comes_from_the_drop_limit_when_it_is_shorter_than_the_rules_value():
    r = run(grid(), houses(), rules="eskom/0.8.1")
    assert r.reach is not None and r.reach.formula_id == "lv.reach.vdrop.v1"
    assert r.reach.value == pytest.approx(372.2, abs=0.1)  # test-cases/lv_reach/case-1
    assert all(a.road_m <= r.reach.value + 1e-6 for a in r.assignments)
    assert any("372.2 m from the drop limit" in p for p in r.placeholders)
    # Rules before 0.6.0 have no LV design settings: the rules reach stands.
    assert run(grid(), houses(), rules="eskom/0.7.0").reach is not None
    from reticula_calc.lv.placement import _derived_reach, _params

    rs = load_rules("eskom/0.7.0")
    assert _derived_reach(rs, _params(rs)).value == pytest.approx(372.2, abs=0.1)


def test_buildings_not_yet_inspected_take_their_class_admd():
    loads = [x.model_copy(update={"kva": None}) for x in houses(blocks=2, per_side=2)]
    loads.append(LoadIn(id="unknown", coordinates=ll(10, 10)))
    r = run(grid(2), loads, rules="eskom/0.8.1")
    codes = {i.code: i for i in r.issues}
    assert codes["class_admd"].count == len(loads) - 1
    assert codes["no_estimate"].samples == ["unknown"]
    assert len(r.assignments) == len(loads) - 1
