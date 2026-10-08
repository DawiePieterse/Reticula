"""LV network model (plan 2.1): routes and sites marked in the field joined into a radial node-branch network."""

import networkx as nx
import pytest

from reticula_calc.geo import crs
from reticula_calc.lv.network import BuildRequest, CandidateIn, LvNetwork, build_network
from reticula_calc.rules import RulesError, load_rules

RULES = "eskom/0.3.0"
ORIGIN = crs.from_wgs84("LO29").transform(28.1, -25.5)


def ll(x: float, y: float) -> list[float]:
    """Metres east and north of a point near Soshanguve, as lon/lat."""
    return list(crs.to_wgs84("LO29").transform(ORIGIN[0] + x, ORIGIN[1] + y))


def route(id: str, *pts: tuple[float, float]) -> CandidateIn:
    return CandidateIn(id=id, kind="lv_route", geometry={"type": "LineString", "coordinates": [ll(*p) for p in pts]})


def site(id: str, kind: str, x: float, y: float) -> CandidateIn:
    return CandidateIn(id=id, kind=kind, geometry={"type": "Point", "coordinates": ll(x, y)})


def build(*candidates: CandidateIn, rules: str = RULES) -> LvNetwork:
    return build_network(BuildRequest(rules=rules, candidates=list(candidates)), load_rules(rules))


def codes(net: LvNetwork) -> set[str]:
    return {i.code for i in net.issues}


def node(net: LvNetwork, id: str):
    return next(n for n in net.nodes if n.id == id)


def test_tees_a_route_that_stops_short_and_feeds_it_from_a_linked_transformer():
    net = build(
        route("a", (0, 0), (200, 0)),
        route("b", (100, 1.5), (100, 100)),  # stops 1.5 m short of a: within the 2 m join distance
        site("t", "transformer", 0, -10),
    )
    assert net.issues == []
    assert [b.kind for b in net.branches] == ["route", "route", "route", "link"]
    assert sorted(n.kind for n in net.nodes) == ["end", "end", "joint", "junction", "source"]
    tx = next(n for n in net.nodes if n.kind == "source")
    assert (tx.label, tx.candidate_id, tx.distance_m) == ("TX1", "t", 0.0)

    [f] = net.feeders
    assert (f.id, f.source, f.branches, f.ends) == ("TX1-F1", tx.id, 3, 2)
    assert f.length_m == pytest.approx(300, abs=0.5)
    assert f.farthest_m == pytest.approx(210, abs=0.5)
    link = net.branches[-1]
    assert link.from_node == tx.id and link.length_m == pytest.approx(10, abs=0.05) and link.feeder is None
    # Every route branch runs away from the source.
    for b in net.branches[:3]:
        assert b.feeder == "TX1-F1"
        assert node(net, b.to_node).distance_m > node(net, b.from_node).distance_m
    assert net.summary.route_length_m == pytest.approx(300, abs=0.5)


def test_a_transformer_beside_the_middle_of_a_route_has_a_feeder_each_way():
    net = build(route("a", (0, 0), (200, 0)), site("t", "transformer", 100, 20))
    assert [(f.id, f.branches, round(f.length_m), round(f.farthest_m)) for f in net.feeders] == [
        ("TX1-F1", 1, 100, 120),  # east first: feeders are numbered clockwise from north
        ("TX1-F2", 1, 100, 120),
    ]
    east = next(b for b in net.branches if b.feeder == "TX1-F1")
    assert east.coordinates[-1][0] > east.coordinates[0][0]


def test_joins_ends_that_nearly_meet_and_routes_that_cross():
    net = build(
        route("a", (0, 0), (100, 0)),
        route("b", (101, 0.5), (200, 0)),  # end 1.1 m from a's end
        route("c", (150, -50), (150, 50)),  # crosses b
        site("m", "minisub", 0, -5),
    )
    assert net.issues == []
    assert sorted(n.kind for n in net.nodes) == ["end", "end", "end", "joint", "joint", "junction", "source"]
    assert net.summary.branches == 6 and len(net.feeders) == 1
    assert net.feeders[0].id == "MS1-F1" and net.feeders[0].ends == 3


def test_an_overshoot_is_folded_back_onto_the_route_it_crosses():
    net = build(route("a", (0, 0), (200, 0)), route("b", (100, -1), (100, 100)), site("t", "transformer", 0, 0))
    assert net.issues == []
    assert [b.candidate_id for b in net.branches].count("b") == 1
    assert next(b for b in net.branches if b.candidate_id == "b").length_m == pytest.approx(100, abs=0.05)


def test_flags_route_ends_that_stop_short_and_routes_nothing_feeds():
    net = build(route("a", (0, 0), (100, 0)), route("b", (50, 4), (50, 100)), site("t", "transformer", 0, 0))
    near = next(i for i in net.issues if i.code == "near_miss")
    assert near.severity == "warning" and len(near.at) == 1
    assert near.at[0] == pytest.approx(ll(50, 4), abs=1e-6)
    unfed = next(i for i in net.issues if i.code == "unfed")
    assert unfed.samples == ["B2"]
    assert net.summary.unfed_length_m == pytest.approx(96, abs=0.5)
    assert len(net.feeders) == 1


def test_a_gap_is_flagged_once_and_not_once_it_is_bridged():
    main, stray = route("a", (0, 0), (250, 0)), route("e", (252, 4.5), (252, 120))
    gap = build(main, stray, site("t", "transformer", 0, 0))
    [near] = [i for i in gap.issues if i.code == "near_miss"]
    assert near.count == 1  # both ends of the 4.9 m gap are near the other route, but it is one gap

    bridged = build(main, stray, route("j", (250, 0), (252, 4.5)), site("t", "transformer", 0, 0))
    assert bridged.issues == []
    assert [f.ends for f in bridged.feeders] == [1]


def test_loops_and_tied_sources_are_errors_and_get_no_feeders():
    square = build(
        route("n", (0, 0), (100, 0)), route("e", (100, 0), (100, 100)),
        route("s", (100, 100), (0, 100)), route("w", (0, 100), (0, 0)),
        site("t", "transformer", -10, -10),
    )
    loop = next(i for i in square.issues if i.code == "loop")
    assert loop.severity == "error" and loop.count == 1 and len(loop.at) == 1
    assert square.feeders == [] and all(n.distance_m is None for n in square.nodes if n.kind != "source")

    tied = build(route("a", (0, 0), (300, 0)), site("t1", "transformer", 0, 5), site("t2", "transformer", 300, 5))
    issue = next(i for i in tied.issues if i.code == "sources_tied")
    assert issue.samples == ["TX1", "TX2"] and tied.feeders == []


def test_routes_drawn_twice_make_a_loop():
    net = build(route("a", (0, 0), (100, 0)), route("b", (0, 0), (100, 0)), site("t", "transformer", 0, 0))
    assert {"loop", "route_overlap"} <= codes(net)


def test_poles_split_the_route_into_spans():
    net = build(
        route("a", (0, 0), (120, 0)),
        site("t", "transformer", 0, 0),  # on the route's end: no link
        site("p1", "pole", 40, 1), site("p2", "pole", 80, -1), site("p3", "pole", 121, 0),
        site("p4", "pole", 60, 30),  # 30 m away: left out
    )
    assert [b.kind for b in net.branches] == ["route"] * 3
    assert [round(b.length_m) for b in net.branches] == [40, 40, 40]
    poles = sorted((n for n in net.nodes if n.kind == "pole"), key=lambda n: n.distance_m)
    assert [p.label for p in poles] == ["P1", "P2", "P3"]
    assert [round(p.distance_m) for p in poles] == [40, 80, 120]
    off = next(i for i in net.issues if i.code == "pole_off_route")
    assert off.samples == ["P4"]
    assert (net.summary.poles, net.summary.poles_placed) == (4, 3)


def test_a_pole_at_a_pole_mounted_transformer_is_its_pole():
    for order in (0, 1):  # whichever was marked first
        sites = [site("p", "pole", 0.3, 0.2), site("t", "transformer", 0, 0)]
        net = build(route("a", (0, 0), (100, 0)), *sites[order:], *sites[:order])
        assert net.issues == [] and [b.kind for b in net.branches] == ["route"]
        assert [n.kind for n in net.nodes if n.kind in ("source", "pole")] == ["source"]
        assert (net.summary.poles, net.summary.poles_placed) == (1, 1)

    twice = build(route("a", (0, 0), (100, 0)), site("t", "transformer", 0, 0), site("p1", "pole", 50, 0), site("p2", "pole", 50.5, 1))
    assert next(i for i in twice.issues if i.code == "pole_doubled").samples == ["P2"]


def test_sources_out_of_reach_and_missing_sources():
    far = build(route("a", (0, 0), (100, 0)), site("t", "transformer", 50, 60))
    assert {"source_unconnected", "unfed"} <= codes(far)
    assert far.summary.sources == 1 and far.summary.sources_connected == 0

    none = build(route("a", (0, 0), (100, 0)))
    assert "no_sources" in codes(none)
    assert next(i for i in build(site("t", "transformer", 0, 0)).issues).code == "no_routes"


def test_ignores_mv_routes_and_needs_a_rules_file_with_lv_network_tolerances():
    mv = CandidateIn(id="mv", kind="mv_route", geometry={"type": "LineString", "coordinates": [ll(0, 0), ll(0, 500)]})
    net = build(route("a", (0, 0), (100, 0)), site("t", "transformer", 0, 0), mv)
    assert net.summary.routes == 1 and "mv" not in {b.candidate_id for b in net.branches}
    with pytest.raises(RulesError, match="lv_network"):
        build(route("a", (0, 0), (100, 0)), rules="eskom/0.2.0")


def test_round_trips_through_json_to_the_same_graph():
    net = build(route("a", (0, 0), (200, 0)), route("b", (100, 0), (100, 80)), site("t", "transformer", 100, 20),
                site("p", "pole", 150, 0))
    again = LvNetwork.model_validate_json(net.model_dump_json())
    assert again == net
    g, h = net.graph(), again.graph()
    assert sorted(g.edges(keys=True)) == sorted(h.edges(keys=True))
    assert dict(g.nodes(data="kind")) == dict(h.nodes(data="kind"))
    assert nx.is_tree(nx.Graph(g)) and g.number_of_edges() == len(net.branches)
    b = next(iter(g.edges(keys=True, data=True)))
    assert b[3]["geometry"].geom_type == "LineString" and b[3]["length_m"] > 0


def test_endpoint(client):
    body = {"rules": RULES, "candidates": [c.model_dump() for c in (route("a", (0, 0), (100, 0)), site("t", "transformer", 0, 0))]}
    r = client.post("/calc/lv/network", json=body)
    assert r.status_code == 200
    out = r.json()
    assert out["rules_ref"] == RULES and out["summary"]["feeders"] == 1 and out["clause"]
    assert client.post("/calc/lv/network", json={**body, "rules": "eskom/0.2.0"}).status_code == 422
