"""LV design engine: layout, phasing, sizing, overhead checks and the design endpoint (plan Phase 2)."""

import networkx as nx
import pytest

from reticula_calc.lv.build import BuildRequest, CustomerIn, RouteIn, build_network
from reticula_calc.lv.design import LvDesignRequest, design_lv
from reticula_calc.lv.library import feeder_options
from reticula_calc.lv.sizing import size_network
from reticula_calc.rules import load_rules
from reticula_calc.rules.loader import RulesError

RULES = load_rules("eskom/0.3.0")
LON, LAT, D = 28.10, -25.52, 0.00045  # D is about 45 m east-west


def street(n_houses=12, length=8, **kw):
    """One east-west route with houses on both sides."""
    route = RouteIn(id="r1", coordinates=[(LON, LAT), (LON + length * D, LAT)])
    houses = [CustomerIn(building_id=f"h{i}", lon=LON + (i // 2 + 0.5) * D * length / (n_houses / 2), lat=LAT + (0.00015 if i % 2 else -0.00015),
                         load_class="township_area", erf=str(100 + i), **kw) for i in range(n_houses)]
    return route, houses


def build(routes, customers, construction="overhead", source=(LON, LAT), **kw):
    return build_network(BuildRequest(source=source, routes=routes, customers=customers, construction=construction, **kw), RULES)


class TestBuild:
    def test_radial_tree_with_poles_no_further_apart_than_the_span(self):
        route, houses = street()
        r = build([route], houses)
        net = r.network
        assert net.validate_radial() == []
        feeders = [b for b in net.branches if b.kind == "feeder"]
        assert max(b.length_m for b in feeders) <= RULES.data["overhead"]["max_span_m"] + 0.01
        route_m = 8 * D * 100_500  # about 360 m of route
        assert 0.8 * route_m < sum(b.length_m for b in feeders) <= route_m * 1.01  # the dead end past the last house is removed
        assert {n.kind for n in net.nodes} == {"source", "pole", "connection"}
        assert len(net.customers) == 12 and all(b.length_m < 35 for b in net.branches if b.kind == "service")

    def test_phases_follow_the_paired_rotation_in_feeder_order(self):
        route, houses = street(n_houses=12)
        net = build([route], houses).network
        g = net.graph()
        dist = nx.single_source_dijkstra_path_length(g.to_undirected(), "S", weight=lambda u, v, e: e["branch"].length_m)
        order = sorted(net.customers, key=lambda c: dist[c.node_id])
        assert [c.phases[0] for c in order] == ["W", "W", "R", "R", "B", "B"] * 2

    def test_three_phase_connection_takes_all_phases(self):
        route, houses = street(n_houses=4)
        houses[0] = houses[0].model_copy(update={"phases": 3})
        net = build([route], houses).network
        assert next(c for c in net.customers if c.id == "h0").phases == ["R", "W", "B"]

    def test_loops_are_opened_and_far_buildings_reported(self):
        square = [RouteIn(id="a", coordinates=[(LON, LAT), (LON + 4 * D, LAT), (LON + 4 * D, LAT + 4 * D), (LON, LAT + 4 * D), (LON, LAT)])]
        houses = [CustomerIn(building_id=f"h{i}", lon=LON + 2 * D, lat=LAT + 0.0001 * (1 if i else -1), load_class="township_area") for i in range(2)]
        houses.append(CustomerIn(building_id="far", lon=LON + 2 * D, lat=LAT + 2 * D, load_class="township_area", erf="999"))
        r = build(square, houses)
        codes = {i.code: i for i in r.issues}
        assert "route_loop_opened" in codes
        assert codes["customer_unconnected"].samples == ["999"]
        assert r.network.validate_radial() == []

    def test_transformer_off_the_route_is_moved_onto_it(self):
        route, houses = street(n_houses=4)
        r = build([route], houses, source=(LON + D, LAT + 0.0002))
        assert any(i.code == "source_moved_to_route" for i in r.issues)
        s = r.network.node("S")
        assert s.lat == pytest.approx(LAT, abs=1e-7)

    def test_underground_drops_plain_joints_and_keeps_kiosks(self):
        route, houses = street(n_houses=2, length=8)
        net = build([route], houses[:1], construction="underground").network
        kinds = sorted(n.kind for n in net.nodes)
        assert kinds.count("junction") == 0
        assert "kiosk" in kinds or net.customers[0].node_id in {b.to_id for b in net.branches if b.from_id == "S"}

    def test_crossing_a_road_is_flagged_on_the_span(self):
        route, houses = street(n_houses=4)
        road = [(LON + 2.05 * D, LAT - 0.001), (LON + 2.05 * D, LAT + 0.001)]
        net = build([route], houses, roads=[road]).network
        assert sum(b.crosses_road for b in net.branches if b.kind == "feeder") == 1


class TestSizing:
    def test_converges_and_tapers_outwards(self):
        route, houses = street(n_houses=40, length=10)
        net = build([route], houses, source=(LON + 5 * D, LAT)).network  # fed from mid-street
        r = size_network(net, RULES, "overhead")
        assert r.converged
        assert all(c.passed for c in r.analysis.checks if c.code in ("thermal", "feeder_vdrop", "supply_vdrop", "min_fault"))
        rank = {c.code: i for i, c in enumerate(feeder_options(RULES, "overhead"))}
        g = r.network.graph()
        for b in r.network.branches:
            if b.kind != "feeder":
                continue
            below = [g.edges[b.to_id, ch]["branch"] for ch in g.successors(b.to_id) if g.edges[b.to_id, ch]["branch"].kind == "feeder"]
            assert all(rank[b.conductor] >= rank[x.conductor] for x in below)

    def test_light_street_stays_on_the_smallest_conductor(self):
        route, houses = street(n_houses=4, length=2)
        r = size_network(build([route], houses).network, RULES, "overhead")
        assert {b.conductor for b in r.network.branches if b.kind == "feeder"} == {"ABC-35"}

    def test_impossible_load_is_reported_not_hidden(self):
        route, houses = street(n_houses=60, length=24)  # a kilometre of street on one side of the transformer
        r = size_network(build([route], houses).network, RULES, "overhead")
        assert not r.converged
        assert any(not c.passed for c in r.analysis.checks)


class TestDesign:
    def test_overhead_and_underground_compared(self):
        route, houses = street(n_houses=24, length=8)
        res = design_lv(LvDesignRequest(rules="eskom/0.3.0", source=(LON + 4 * D, LAT), routes=[route], customers=houses,
                                        constructions=["overhead", "underground"]), RULES)
        oh, ug = res.comparison
        assert (oh.construction, ug.construction) == ("overhead", "underground")
        assert oh.poles > 0 and oh.stays >= 2 and ug.kiosks > 0 and ug.poles == 0
        assert oh.cost_total < ug.cost_total
        assert res.options[0].overhead is not None and res.options[1].overhead is None
        assert "lv_design" in res.unverified
        terminals = [p for p in res.options[0].overhead.poles if p.terminal]
        assert len(terminals) == 2 and all(p.stays >= 1 for p in terminals)

    def test_rules_without_lv_design_are_refused(self):
        route, houses = street(n_houses=4)
        with pytest.raises(RulesError):
            design_lv(LvDesignRequest(rules="eskom/0.2.0", source=(LON, LAT), routes=[route], customers=houses), load_rules("eskom/0.2.0"))

    def test_not_inspected_buildings_and_source_are_flagged(self):
        route, houses = street(n_houses=4, inspected=False)
        res = design_lv(LvDesignRequest(rules="eskom/0.3.0", source=(LON, LAT), source_inspected=False, routes=[route], customers=houses), RULES)
        codes = {i.code: i for i in res.issues}
        assert codes["not_inspected"].count == 4
        assert "source_not_inspected" in codes


def test_design_endpoint(client):
    route, houses = street(n_houses=6, length=3)
    body = LvDesignRequest(rules="eskom/0.3.0", source=(LON, LAT), routes=[route], customers=houses).model_dump(mode="json")
    r = client.post("/calc/lv/design", json=body)
    assert r.status_code == 200, r.text
    assert r.json()["comparison"][0]["passed"] is True
    body["rules"] = "eskom/0.1.0"
    assert client.post("/calc/lv/design", json=body).status_code == 422


def test_a_route_drawn_to_end_on_another_joins_it():
    # As drawn on the map: the spur's end lies on the street in lon/lat, but misses it slightly once projected.
    street_route = RouteIn(id="street", coordinates=[(28.0998, -25.51968), (28.1012, -25.51968)])
    spur = RouteIn(id="spur", coordinates=[(28.1003, -25.5195), (28.1003, -25.51968)])
    houses = [CustomerIn(building_id=f"h{i}", lon=28.0999 + i * 0.0002, lat=-25.51968 - 0.00012, load_class="township_area") for i in range(6)]
    r = build([street_route, spur], houses, source=(28.1003, -25.5195))
    assert not [i for i in r.issues if i.code in ("routes_not_connected", "customer_unconnected")]
    assert len(r.network.customers) == 6


def test_an_unconnected_building_fails_the_design():
    route, houses = street(n_houses=4)
    far = CustomerIn(building_id="far", lon=LON + 2 * D, lat=LAT + 0.002, load_class="township_area", erf="999")
    res = design_lv(LvDesignRequest(rules="eskom/0.3.0", source=(LON, LAT), routes=[route], customers=[*houses, far]), RULES)
    assert res.comparison[0].passed is False
    failed = [c for c in res.options[0].analysis.checks if not c.passed]
    assert [(c.code, c.subject) for c in failed] == [("customer_unconnected", "999")]
