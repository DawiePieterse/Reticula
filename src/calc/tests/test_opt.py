"""Optimisation: lifetime cost, assemblies, the option search and the endpoint (plan Phase 5)."""

import pytest

from reticula_calc.lv.analysis import customer_loads
from reticula_calc.lv.build import CustomerIn, RouteIn
from reticula_calc.lv.costs import estimate, load_rates, resolve_rate
from reticula_calc.lv.model import Branch, Customer, LvNetwork, Node
from reticula_calc.opt.lifetime import LifetimeParams, lifetime_cost, pv_factor, resolved_params
from reticula_calc.opt.search import OptimiseRequest, optimise, upsize
from reticula_calc.rules import load_rules
from reticula_calc.rules.loader import RulesError

RULES = load_rules("eskom/0.5.0")
LON, LAT, D = 28.10, -25.52, 0.00045


def street(n: int) -> list[CustomerIn]:
    return [CustomerIn(building_id=f"a{i}", lon=LON + (i // 2 + 0.5) * D / 2, lat=LAT + (0.00015 if i % 2 else -0.00015),
                       load_class="township_area", erf=f"a{i}") for i in range(n)]


def request(n: int = 24, **over) -> OptimiseRequest:
    base = {"rules": "eskom/0.5.0", "source": (LON + 0.25 * D, LAT), "routes": [RouteIn(id="s1", coordinates=[(LON, LAT), (LON + n / 4 * D, LAT)])],
            "customers": street(n), "max_evaluations": 80}
    return OptimiseRequest(**(base | over))


def chain() -> LvNetwork:
    nodes = [Node(id=i, kind="source" if i == "S" else "pole", lon=LON + k * 0.0004, lat=LAT) for k, i in enumerate(["S", "N1", "N2"])]
    branches = [Branch(id=f"B{k}", from_id=a, to_id=b, kind="feeder", construction="overhead", conductor="ABC-35", length_m=40,
                       geometry=[(0, 0), (1, 1)]) for k, (a, b) in enumerate([("S", "N1"), ("N1", "N2")], 1)]
    return LvNetwork(source_id="S", nodes=nodes, branches=branches,
                     customers=[Customer(id="c", building_id="c", node_id="N2", phases=["R"], load_class="township_area")])


class TestLifetime:
    def test_defaults_come_from_the_rate_list_and_runs_can_override_them(self):
        p = resolved_params(None, "indicative/2026-10")
        assert p == {"period_years": 25, "discount_rate_pct": 8, "energy_cost_per_kwh": 1.85, "load_growth_pct": 0}
        assert resolved_params(LifetimeParams(discount_rate_pct=10), "indicative/2026-10")["discount_rate_pct"] == 10

    def test_pv_factor_is_the_annuity_without_growth(self):
        assert pv_factor(25, 8, 0) == pytest.approx((1 - 1.08**-25) / 0.08)
        assert pv_factor(10, 0, 0) == 10

    def test_neutral_carries_the_unbalance(self):
        net = chain()
        params = resolved_params(None, "indicative/2026-10")
        (_, m, v, _, _), = customer_loads(net, RULES)["c"]
        single = lifetime_cost(net, RULES, 50, 0, params)
        net.customers[0].phases = ["R", "W", "B"]
        three = lifetime_cost(net, RULES, 50, 0, params)
        # Single phase: phase and neutral each carry E[I²] = m² + v. Three phases: 3(m² + v) in the phases, and the
        # neutral only the variance 3v (the means cancel).
        assert three.line_losses_kw / single.line_losses_kw == pytest.approx((3 * m * m + 6 * v) / (2 * m * m + 2 * v), rel=1e-2)  # losses are reported to 0.1 W
        assert three.total.formula_id == "opt.lifetime-cost.v1"

    def test_rules_without_an_optimisation_section_are_refused(self):
        with pytest.raises(RulesError):
            lifetime_cost(chain(), load_rules("eskom/0.4.0"), 50, 0, resolved_params(None, "indicative/2026-10"))


class TestAssemblies:
    def test_an_assembly_rate_is_the_sum_of_its_materials(self):
        r = load_rates()
        rate, code, parts = resolve_rate(r, r["pole_each"]["WP-9-160"])
        assert (rate, code) == (5200.0, "OH-POLE-9-160")
        assert [p.material for p in parts] == ["POLE-9-160", "LV-POLE-HW", "LAB-POLE-9"]

    def test_cost_lines_list_the_assembly_components(self):
        net = chain()
        for n in net.nodes[1:]:
            n.pole = "WP-9-160"
        line = next(x for x in estimate(net, 50).lines if x.item == "Pole WP-9-160")
        assert line.quantity == 2 and line.amount == 10400 and len(line.components) == 3


def test_upsizing_a_branch_keeps_the_feeder_tapered():
    net = upsize(chain(), "B2", ["ABC-35", "ABC-50", "ABC-70"])
    assert [b.conductor for b in net.branches] == ["ABC-50", "ABC-50"]


class TestSearch:
    def test_moves_the_transformer_off_a_failing_end_site_and_returns_three_options(self):
        r = optimise(request(40, constructions=["overhead"]), RULES)
        assert r.baseline is not None and not r.baseline.passed  # the marked site at the street end fails voltage drop
        assert [o.objective for o in r.options] == ["capex", "lifetime", "spare"]
        assert all(o.option.passed for o in r.options)
        capex, lifetime, spare = r.options
        assert capex.design.position_label != "marked site" and any("confirm" in n for n in capex.notes)
        assert lifetime.lifetime.total.value <= capex.lifetime.total.value + 1e-6
        assert capex.option.cost.total <= lifetime.option.cost.total + 1e-6
        assert spare.spare.spare_pct >= capex.spare.spare_pct and spare.option.cost.total <= r.capex_ceiling
        assert r.evaluations <= 80 and r.feasible > 0 and "optimisation" in r.unverified

    def test_a_fixed_site_keeps_every_option_there(self):
        r = optimise(request(12, allow_move=False, objectives=["capex", "lifetime"]), RULES)
        assert r.positions == 1
        assert {o.design.position_label for o in r.options} == {"marked site"}
        assert [o.objective for o in r.options] == ["capex", "lifetime"]

    def test_a_given_ceiling_bounds_the_spare_capacity_option(self):
        cheap = optimise(request(12, allow_move=False, objectives=["capex"]), RULES).options[0].option.cost.total
        r = optimise(request(12, allow_move=False, objectives=["spare"], capex_ceiling=cheap * 1.05), RULES)
        assert r.options[0].option.cost.total <= cheap * 1.05 and r.capex_ceiling == pytest.approx(cheap * 1.05)

    def test_options_inside_the_uncertainty_band_are_too_close_to_call(self):
        r = optimise(request(24, constructions=["overhead"]), RULES)
        assert r.uncertainty_pct == 15
        assert all(c.difference_pct <= 15 for c in r.close_calls)
        assert any(c.measure == "capex" for c in r.close_calls)

    def test_endpoint(self, client):
        res = client.post("/calc/lv/optimise", json=request(12, allow_move=False, objectives=["capex"]).model_dump(mode="json"))
        assert res.status_code == 200, res.text
        assert res.json()["options"][0]["objective"] == "capex"
        bad = request(12).model_dump(mode="json") | {"rules": "eskom/0.4.0"}
        assert client.post("/calc/lv/optimise", json=bad).status_code == 422
