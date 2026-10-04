import pytest

from reticula_calc.calcs.admd import (
    AdmdInputError,
    EstimateRequest,
    GroupLoad,
    GroupRequest,
    estimate,
    form_definition,
    group,
)
from reticula_calc.rules import load_rules

RULES = load_rules("eskom/0.1.0")


def est(**kw):
    return estimate(EstimateRequest(rules="eskom/0.1.0", **kw), RULES)


def test_missing_indicators_are_listed_and_score_zero():
    r = est(observations={"dwelling": "brick_large"})
    assert r.points.value == 7
    assert set(r.missing) == {"roof", "walls", "vehicles", "appliances", "stand_size_m2", "occupants"}


def test_every_number_is_traced():
    r = est(observations={"dwelling": "rdp", "stand_size_m2": 250})
    assert r.admd_kva.formula_id and r.admd_kva.clause and r.admd_kva.rules_hash == RULES.hash
    assert {i.name for i in r.points.inputs} == {"dwelling", "stand_size_m2"}


def test_override_keeps_the_estimate_and_reason():
    r = est(observations={"dwelling": "rdp"}, override_kva=2.2, override_reason="Spaza shop at the back")
    assert r.admd_kva.value == 2.2 and r.estimated_kva == 0.8 and r.overridden
    assert any(i.name == "override_kva" and "Spaza" in i.source for i in r.admd_kva.inputs)


def test_band_boundaries_are_inclusive_upper():
    # 3 points is still "very low"; 4 points moves to "low"
    assert est(observations={"vehicles": "2+"}).category == "R2"  # 4 points
    assert est(observations={"roof": "tiles", "walls": "block_unplastered"}).category == "R1"  # 3 points


def test_special_load_default_and_override():
    r = est(kind="special", special_load="school")
    assert r.admd_kva.value == 25 and r.category == "school"
    r2 = est(kind="special", special_load="borehole_pump", override_kva=11, override_reason="15 kW pump nameplate")
    assert r2.admd_kva.value == 11 and r2.estimated_kva == 7.5


@pytest.mark.parametrize("obs", [{"dwelling": "castle"}, {"appliances": ["teleporter"]}, {"stand_size_m2": "big"}, {"occupants": -1}])
def test_bad_observations_are_rejected(obs):
    with pytest.raises(AdmdInputError):
        est(observations=obs)


def test_unknown_special_load():
    with pytest.raises(AdmdInputError):
        est(kind="special", special_load="stadium")


def test_group_with_no_residential_loads():
    r = group(GroupRequest(rules="eskom/0.1.0", loads=[GroupLoad(id="s", kind="special", kva=25)]), RULES)
    assert r.diversity_factor is None and r.total_kva.value == 25


def test_single_consumer_carries_the_largest_diversity_factor():
    one = group(GroupRequest(rules="eskom/0.1.0", loads=[GroupLoad(id="a", kind="residential", kva=1.5)]), RULES)
    many = group(GroupRequest(rules="eskom/0.1.0", loads=[GroupLoad(id=str(i), kind="residential", kva=1.5) for i in range(100)]), RULES)
    assert one.diversity_factor.value > many.diversity_factor.value > 1


def test_form_definition_lists_options_from_rules():
    f = form_definition(RULES)
    assert {i["key"] for i in f["indicators"]} == {"dwelling", "roof", "walls", "vehicles"}
    assert f["special_loads"]["school"] == 25


def test_endpoint_accepts_null_observations_for_special_loads(client):
    # The API sends observations: null for special loads; found in an end-to-end run.
    r = client.post("/calc/admd/estimate", json={"rules": "eskom/0.1.0", "kind": "special", "special_load": "shop", "observations": None,
                                                 "override_kva": 8, "override_reason": "Bakery"})
    assert r.status_code == 200 and r.json()["admd_kva"]["value"] == 8


def test_endpoints(client):
    r = client.post("/calc/admd/estimate", json={"rules": "eskom/0.1.0", "observations": {"dwelling": "flat"}})
    assert r.status_code == 200 and r.json()["category"] == "R2"
    assert client.post("/calc/admd/estimate", json={"rules": "eskom/0.1.0", "observations": {"dwelling": "castle"}}).status_code == 422
    g = client.post("/calc/admd/group", json={"rules": "eskom/0.1.0", "loads": [{"id": "a", "kind": "residential", "kva": 1.5}]})
    assert g.status_code == 200 and g.json()["total_kva"]["value"] == pytest.approx(3.75)
    assert client.get("/calc/admd/form/eskom/0.1.0").status_code == 200
