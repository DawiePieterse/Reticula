"""Bulk supply study: load flow, faults, capacity and NMD on an MV design (plan Phase 4)."""

import pytest
from test_mv import LAT, LON, LV, MV, NORTH, SITES, street

from reticula_calc.bulk.study import BulkStudyRequest, ConnectionPoint, SiteLoad, bulk_study
from reticula_calc.mv.design import MvDesignRequest, design_mv
from reticula_calc.rules import load_rules
from reticula_calc.rules.loader import RulesError

RULES = load_rules("eskom/0.4.0")


@pytest.fixture(scope="module")
def mv():
    return design_mv(MvDesignRequest(rules="eskom/0.4.0", supply=(LON - 0.01, LAT + 0.006), sites=SITES, lv_routes=LV, mv_routes=MV,
                                     customers=street("a", LAT) + street("b", NORTH)), RULES)


def request(mv, **cp) -> BulkStudyRequest:
    point = {"lon": LON - 0.01, "lat": LAT + 0.006, "voltage_kv": 11, "available_capacity_kva": 1000, "fault_mva_max": 150, "fault_mva_min": 100, **cp}
    sites = [SiteLoad(site_id=s.placement.site_id, rating_kva=s.placement.rating_kva, z_pct=s.placement.z_pct, x_r=s.placement.x_r or 2,
                      tap_pct=s.tap_pct, design_kva=s.placement.design_kva) for s in mv.sites]
    return BulkStudyRequest(rules="eskom/0.4.0", connection_point=ConnectionPoint(**point), mv_network=mv.mv_network, sites=sites)


def test_two_sites_pass_and_the_nmd_rounds_up(mv):
    r = bulk_study(request(mv), RULES)
    assert r.passed, [(c.code, c.subject, c.value, c.limit) for c in r.checks if not c.passed]
    assert {t.site_id for t in r.transformers} == {"T1", "T2"}
    assert r.supply_kva > sum(s.placement.design_kva for s in mv.sites) * 0.99  # losses on top of the site demands
    assert r.notified_max_demand_kva % 50 == 0 and r.notified_max_demand_kva >= r.supply_kva
    assert r.bulk_feeder is not None and r.bulk_feeder.loading_pct > 0
    lv = [b for b in r.buses if b.kind == "lv"]
    assert all(b.ikss3_max_ka > b.ikss1_min_ka > 0 for b in lv)
    assert {"bulk", "mv_design"} <= set(r.unverified)


def test_the_fault_level_falls_away_from_the_connection_point(mv):
    r = bulk_study(request(mv), RULES)
    supply = next(b for b in r.buses if b.kind == "supply")
    assert supply.ikss3_max_ka == pytest.approx(150 / (3**0.5 * 11), rel=1e-3)  # S"k = √3·Un·I"k by definition
    assert all(b.ikss3_max_ka <= supply.ikss3_max_ka for b in r.buses if b.kind == "mv")


def test_capacity_short_fails_the_supply_check(mv):
    r = bulk_study(request(mv, available_capacity_kva=100), RULES)
    cap = next(c for c in r.checks if c.code == "supply_capacity")
    assert not cap.passed and not r.passed


def test_switchgear_rating_exceeded_at_a_strong_source(mv):
    r = bulk_study(request(mv, fault_mva_max=500), RULES)
    assert any(c.code == "sc_max" and not c.passed for c in r.checks)


def test_missing_values_are_listed_as_assumptions(mv):
    r = bulk_study(request(mv, fault_mva_min=None), RULES)
    assert any("Minimum fault level" in a for a in r.assumptions)
    assert any("R/X" in a for a in r.assumptions)


def test_rules_without_a_bulk_section_are_refused(mv):
    with pytest.raises(RulesError):
        bulk_study(request(mv), load_rules("eskom/0.3.0"))


def test_endpoint(client, mv):
    res = client.post("/calc/bulk/study", json=request(mv).model_dump(mode="json"))
    assert res.status_code == 200, res.text
    assert res.json()["notified_max_demand_kva"] > 0
    bad = request(mv).model_dump(mode="json") | {"rules": "eskom/0.3.0"}
    assert client.post("/calc/bulk/study", json=bad).status_code == 422
