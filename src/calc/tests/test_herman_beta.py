import pytest

from reticula_calc.calcs.admd import (
    AdmdInputError,
    EstimateRequest,
    GroupLoad,
    GroupRequest,
    _moments,
    estimate,
    form_definition,
    group,
)
from reticula_calc.rules import load_rules

RULES = load_rules("eskom/0.2.0")


def res(n, cls, kva=1.0):
    return [GroupLoad(id=f"{cls}-{i}", kind="residential", kva=kva, load_class=cls) for i in range(n)]


def grp(loads):
    return group(GroupRequest(rules="eskom/0.2.0", loads=loads), RULES)


class TestEstimate:
    def test_score_maps_to_an_nrs034_class(self):
        r = estimate(EstimateRequest(rules="eskom/0.2.0", observations={"dwelling": "rdp", "roof": "tiles", "vehicles": "0"}), RULES)
        assert r.points.value == 4
        assert (r.category, r.load_class.description, r.load_class.chosen_by) == ("township_area", "Township area", "score")
        assert r.admd_kva.value == 2.37
        assert {i.name for i in r.admd_kva.inputs} >= {"alpha", "beta", "c", "class_admd"}
        assert "NRS 034" in r.admd_kva.clause

    def test_engineer_can_choose_the_class(self):
        r = estimate(EstimateRequest(rules="eskom/0.2.0", observations={"dwelling": "informal"}, load_class="rural_village"), RULES)
        assert (r.category, r.admd_kva.value, r.load_class.chosen_by) == ("rural_village", 0.84, "engineer")

    def test_unknown_or_unverified_class_is_refused(self):
        with pytest.raises(AdmdInputError, match="unknown load class"):
            estimate(EstimateRequest(rules="eskom/0.2.0", load_class="castle"), RULES)

    def test_old_rules_keep_working(self):
        r = estimate(EstimateRequest(rules="eskom/0.1.0", observations={"dwelling": "rdp"}), load_rules("eskom/0.1.0"))
        assert r.load_class is None and r.category == "R1" and r.admd_kva.value == 0.8

    def test_form_lists_classes_and_flags_unusable(self):
        f = form_definition(RULES)
        assert [c["code"] for c in f["load_classes"]][:2] == ["rural_settlement", "rural_village"]
        assert all(c["usable"] for c in f["load_classes"])


class TestGroup:
    def test_per_consumer_demand_falls_towards_the_mean_as_groups_grow(self):
        # The confidence margin shrinks as 1/√N, so per-consumer demand approaches μ·V (the ADMD).
        mean_kva = 60 * 1.22 / 7.08 * 230 / 1000
        per = [grp(res(n, "township_area")).residential_kva.value / n for n in (3, 30, 300, 3000, 300_000)]
        assert per == sorted(per, reverse=True)
        assert per[-1] == pytest.approx(mean_kva, rel=0.005)
        assert per[-1] == pytest.approx(2.37, rel=0.008)

    def test_hand_worked_township_group(self):
        r = grp(res(30, "township_area"))
        assert (r.method, r.phases, r.confidence_pct) == ("herman_beta", 3, 90)
        assert r.design_current_a.value == pytest.approx(136.72, rel=0.001)
        assert r.residential_kva.value == pytest.approx(94.34, rel=0.001)
        assert r.diversity_factor.value == pytest.approx(94.34 / (30 * 2.37), rel=0.001)

    def test_small_group_stays_on_one_phase_and_never_exceeds_breakers(self):
        r = grp(res(2, "urban_residential_1"))
        assert r.phases == 1
        assert r.design_current_a.value < 2 * 60

    def test_special_loads_add_at_their_kva(self):
        base = grp(res(12, "township_area"))
        both = grp(res(12, "township_area") + [GroupLoad(id="school", kind="special", kva=25)])
        assert both.total_kva.value == pytest.approx(base.total_kva.value + 25)

    def test_mixed_classes_lie_between_the_pure_cases(self):
        low = grp(res(30, "informal_settlement")).residential_kva.value
        high = grp(res(30, "urban_residential_2")).residential_kva.value
        mixed = grp(res(15, "informal_settlement") + res(15, "urban_residential_2")).residential_kva.value
        assert low < mixed < high

    def test_class_is_required_for_herman_beta(self):
        with pytest.raises(AdmdInputError, match="needs a load class"):
            grp([GroupLoad(id="x", kind="residential", kva=1.5)])

    def test_unverified_sans507_class_is_refused(self):
        rules = load_rules("eskom/0.2.0")
        cfg = dict(rules.data["income_admd"], design_table="sans507_15y")
        from reticula_calc.calcs.admd import _load_class
        with pytest.raises(AdmdInputError, match="unverified"):
            _load_class(cfg, rules, "c8", "engineer")
        assert _load_class(cfg, rules, "c9", "engineer").admd_kva == 9.64


def test_reticmaster_example():
    # ReticMaster help, Herman Beta Method: alpha 1.65, beta 7.35, Icb 60 A. The page prints 10.98 A after
    # rounding the mean to 0.183; unrounded it is 11.00 A.
    mean, _ = _moments(1.65, 7.35, 60)
    assert mean == pytest.approx(11.0, abs=1e-9)


def test_three_phase_connections_are_diversified_separately_and_added():
    # Engineer decision 2026-10-04: 1-phase and 3-phase domestic connections are separate groups whose currents add.
    single = res(30, "township_area")
    three = [GroupLoad(id=f"t{i}", kind="residential", kva=2.37, load_class="township_area", phases=3) for i in range(3)]
    alone = grp(single)
    both = grp(single + three)
    only3 = grp(three)
    assert alone.design_current_a.value == pytest.approx(136.72, abs=0.01)
    # Three 3-phase consumers put 3 consumers on each phase.
    assert only3.design_current_a.value == pytest.approx(grp(res(9, "township_area")).design_current_a.value, rel=1e-9)
    assert both.design_current_a.value == pytest.approx(alone.design_current_a.value + only3.design_current_a.value, rel=1e-9)
    assert both.residential_kva.value == pytest.approx(3 * 230 * both.design_current_a.value / 1000, rel=1e-9)
