"""Optimisation (plans 5.2 to 5.6): the siting MILP against brute force, local search, objectives and the compare rows."""

import math
import random

import pytest
from test_design import RULES, cand, cp, layout, load

from reticula_calc.design.optimise import (
    OptimiseOptions,
    OptimiseRequest,
    SitingProblem,
    brute_force_siting,
    optimise,
    solve_siting,
)
from reticula_calc.design.run import DesignRequest, unsound
from reticula_calc.rules import load_rules


def problem(seed: int, sites: int = 3, loads: int = 6) -> SitingProblem:
    rnd = random.Random(seed)
    s_xy = [(rnd.uniform(0, 300), rnd.uniform(0, 300)) for _ in range(sites)]
    l_xy = [(rnd.uniform(0, 300), rnd.uniform(0, 300)) for _ in range(loads)]
    dist = [[math.dist(a, b) if math.dist(a, b) <= 250 else math.inf for b in s_xy] for a in l_xy]
    return SitingProblem(sites=[f"s{i}" for i in range(sites)], loads=[f"l{i}" for i in range(loads)],
                         kva=[rnd.choice([2.0, 3.5, 5.0, 8.0]) for _ in range(loads)], distance=dist, ratings=[16, 25, 50],
                         rating_cost=[60000, 70000, 90000], site_cost=[rnd.uniform(5000, 40000) for _ in range(sites)], growth=0.1,
                         assign_rate=4.0)


@pytest.mark.parametrize("seed", range(8))
def test_siting_milp_finds_the_brute_force_optimum(seed):
    p = problem(seed)
    best = brute_force_siting(p)
    sol = solve_siting(p)
    if math.isinf(best):
        assert not sol.optimal or not sol.open
        return
    assert sol.optimal
    assert sol.cost == pytest.approx(best, rel=1e-6, abs=0.01)
    # Every load served once, within capacity.
    assert sorted(sol.assign) == sorted(p.loads)
    for site, rating in sol.open.items():
        used = sum(p.kva[p.loads.index(lid)] for lid, s in sol.assign.items() if s == site)
        assert used <= rating * 0.9 + 1e-9


def test_siting_opens_a_second_site_when_one_cannot_carry_the_load():
    # Two clusters 1 km apart, 40 kVA each: one 50 kVA transformer carries 45 kVA, so each cluster gets its own.
    p = SitingProblem(sites=["a", "b"], loads=[f"l{i}" for i in range(8)], kva=[10.0] * 8,
                      distance=[[10, 1000] if i < 4 else [1000, 10] for i in range(8)], ratings=[16, 25, 50],
                      rating_cost=[60000, 70000, 90000], site_cost=[0, 0], growth=0.1, assign_rate=4.0)
    sol = solve_siting(p)
    assert sol.optimal and sol.open == {"a": 50, "b": 50}


def test_siting_opens_one_site_per_connected_network():
    # Both sites are on one network with no open point between them, so only one may open, and it must carry all the load.
    p = SitingProblem(sites=["a", "b"], loads=[f"l{i}" for i in range(4)], kva=[10.0] * 4,
                      distance=[[10, 400] if i < 2 else [400, 10] for i in range(4)], ratings=[16, 25, 50],
                      rating_cost=[60000, 70000, 90000], site_cost=[0, 0], growth=0.1, assign_rate=4.0, groups=[0, 0])
    sol = solve_siting(p)
    assert sol.optimal and len(sol.open) == 1 and list(sol.open.values()) == [50]
    assert sol.cost == pytest.approx(brute_force_siting(p), rel=1e-6)


def street():
    """One 400 m street of 20 houses, the transformer at its start and the MV route ending at the transformer; the connection
    point's fault level is high enough that the LV conductors fail their withstand checks."""
    cands = [cand("tx", "transformer", (0, 0)), cand("r", "lv_route", (0, 0), (400, 0)), cand("mv", "mv_route", (-800, 20), (0, 0))]
    loads = [load(f"h{i}{side}", 40 * i + 20, side * 15, kva=2.0) for i in range(10) for side in (-1, 1)]
    return DesignRequest(rules=RULES, candidates=cands, loads=loads, classes={x.id: "township_area" for x in loads},
                         connection_point=cp(coordinates=tuple(cands[2].geometry["coordinates"][0]), fault_3ph_ka=8.5, fault_1ph_ka=4))


def test_no_option_is_less_sound_than_the_marked_design():
    # Regression: siting opened a second transformer on the same street, so the sources were tied and nothing fed the loads;
    # and moving the transformer beyond the MV tap reach stopped the bulk study, which removed its failed withstand checks.
    # Either way fewer checks ran, so fewer failed, and the broken design won.
    req = street()
    res = optimise(OptimiseRequest(design=req, options=OptimiseOptions(max_evaluations=10)), load_rules(RULES))
    assert res.siting is not None and len(res.siting.sites) == 1
    from reticula_calc.design.run import run

    marked = run(req, load_rules(RULES))
    for o in res.options:
        assert unsound(o.design) <= unsound(marked) == 0, (o.objective, [i.code for i in o.design.issues if i.severity == "error"])
        assert o.design.summary.connected == len(req.loads) and o.design.bulk.stopped is None
        assert all(a.feeder for a in o.design.lv.allocation.allocations)


@pytest.fixture(scope="module")
def result():
    cands, loads, classes = layout()
    req = OptimiseRequest(design=DesignRequest(rules=RULES, candidates=cands, loads=loads, classes=classes, connection_point=cp()),
                          options=OptimiseOptions(max_evaluations=25))
    return optimise(req, load_rules(RULES))


def test_three_options_each_best_for_its_objective(result):
    by = {o.objective: o for o in result.options}
    assert set(by) == {"capex", "lifetime", "spare"}
    capex = min(o.design.cost.capex for o in result.options)
    assert by["capex"].design.cost.capex == pytest.approx(capex)
    assert by["lifetime"].design.cost.lifetime <= by["capex"].design.cost.lifetime + 1e-6
    ceiling = by["capex"].design.cost.capex * 1.15
    assert by["spare"].design.cost.capex <= ceiling + 1e-6
    assert by["spare"].design.summary.spare_pct >= max(o.design.summary.spare_pct for o in result.options if o.design.cost.capex <= ceiling) - 1e-9
    # Every option is a full, checked design.
    assert all(o.design.checks and o.design.rules_hash == result.rules_hash for o in result.options)


def test_options_are_no_worse_than_their_start_and_record_their_moves(result):
    for o in result.options:
        assert o.start
        for m in o.moves:
            assert m.kind and m.detail
    assert result.evaluations > 3
    assert result.siting is not None and result.siting.optimal and result.siting.trace.formula_id == "opt.siting.milp.v2"


def test_compare_flags_options_too_close_to_call(result):
    rows = {r.objective: r for r in result.comparison}
    for r in rows.values():
        for other in r.too_close:
            o = rows[other]
            assert not (o.capex_low > r.capex_high or o.capex_high < r.capex_low)
    assert all(r.failures == 0 for r in rows.values())


def test_optimise_endpoint(client):
    cands, loads, classes = layout()
    body = OptimiseRequest(design=DesignRequest(rules=RULES, candidates=cands, loads=loads, classes=classes, connection_point=cp()),
                           options=OptimiseOptions(objectives=["capex"], max_evaluations=4, siting=False)).model_dump(mode="json")
    r = client.post("/calc/design/optimise", json=body)
    assert r.status_code == 200, r.text
    assert [o["objective"] for o in r.json()["options"]] == ["capex"]
