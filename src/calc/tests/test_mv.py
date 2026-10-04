"""MV design: placement, unit selection, MV routing and sizing, taps and the endpoint (plan Phase 3)."""

import pytest

from reticula_calc.lv.build import CustomerIn, RouteIn
from reticula_calc.mv.design import MvDesignRequest, design_mv
from reticula_calc.mv.network import choose_tap
from reticula_calc.mv.placement import SiteIn, allocate
from reticula_calc.rules import load_rules
from reticula_calc.rules.loader import RulesError

RULES = load_rules("eskom/0.4.0")
LON, LAT, D = 28.10, -25.52, 0.00045
NORTH = LAT + 0.0036  # a second street about 400 m north


def street(prefix, lat, n=40, cls="urban_residential_1"):
    return [CustomerIn(building_id=f"{prefix}{i}", lon=LON + (i // 2 + 0.5) * D / 2, lat=lat + (0.00015 if i % 2 else -0.00015),
                       load_class=cls, erf=f"{prefix}{i}") for i in range(n)]


LV = [RouteIn(id="s1", coordinates=[(LON, LAT), (LON + 10 * D, LAT)]), RouteIn(id="s2", coordinates=[(LON, NORTH), (LON + 10 * D, NORTH)])]
MV = [RouteIn(id="mv", coordinates=[(LON - 0.01, LAT + 0.006), (LON + 5 * D, LAT + 0.006), (LON + 5 * D, LAT - 0.0005)])]
SITES = [SiteIn(id="T1", kind="transformer", lon=LON + 5 * D, lat=LAT), SiteIn(id="T2", kind="transformer", lon=LON + 5 * D, lat=NORTH)]


class TestPlacement:
    def test_each_building_goes_to_the_nearest_site_along_the_routes(self):
        r = allocate(SITES, street("a", LAT) + street("b", NORTH), LV, RULES, "overhead")
        by_site = {p.site_id: set(p.customers) for p in r.placements}
        assert by_site["T1"] == {f"a{i}" for i in range(40)}
        assert by_site["T2"] == {f"b{i}" for i in range(40)}
        assert r.unallocated == [] and r.unused_sites == []

    def test_unused_sites_and_unreachable_buildings_are_reported(self):
        far = CustomerIn(building_id="x", lon=LON + 0.02, lat=LAT, load_class="township_area", erf="999")
        spare = SiteIn(id="T9", kind="minisub", lon=LON + 0.05, lat=LAT + 0.05)
        r = allocate([*SITES, spare], street("a", LAT, 6) + [far], LV, RULES, "overhead")
        assert r.unallocated == ["999"]
        assert set(r.unused_sites) == {"T2", "T9"}

    def test_underground_lv_or_a_large_load_needs_a_minisub(self):
        heavy = street("a", LAT, 40, "urban_multistorey_estate")
        r = allocate(SITES[:1], heavy, LV[:1], RULES, "overhead")
        p = r.placements[0]
        assert p.unit == "minisub" and p.rating_kva > 200 and "pole-mount" in p.note
        ug = allocate(SITES[:1], street("a", LAT, 6), LV[:1], RULES, "underground").placements[0]
        assert ug.unit == "minisub" and "underground" in ug.note

    def test_pole_mount_kept_when_it_fits(self):
        p = allocate(SITES[:1], street("a", LAT, 12, "township_area"), LV[:1], RULES, "overhead").placements[0]
        assert (p.unit, p.rating_kva) == ("pole_mount", 50.0)


class TestTaps:
    def test_no_tap_fits_when_the_drops_are_too_large(self):
        tap, lo, _, _ = choose_tap(RULES, mv_drop_pct=6, load_kva=200, rating_kva=200, z_pct=4.5, x_r=3, lv_drop_pct=9)
        assert tap is None and lo < 90

    def test_the_tap_centres_customers_in_the_band(self):
        # Light drops: tap 0 leaves 6 points below and 10 above; +2.5 % balances them better.
        tap, lo, hi, _ = choose_tap(RULES, mv_drop_pct=0.5, load_kva=40, rating_kva=100, z_pct=4, x_r=2, lv_drop_pct=2)
        assert tap == 2.5 and hi == 102.5
        assert min(lo - 90, 110 - hi) > min(lo - 2.5 - 90, 110 - 100)


class TestDesign:
    def test_two_transformers_on_one_mv_line(self):
        res = design_mv(MvDesignRequest(rules="eskom/0.4.0", supply=(LON - 0.01, LAT + 0.006), sites=SITES, lv_routes=LV, mv_routes=MV,
                                        customers=street("a", LAT) + street("b", NORTH)), RULES)
        assert res.passed, [(c.code, c.subject, c.value, c.limit) for c in res.checks if not c.passed]
        assert [s.placement.rating_kva for s in res.sites] == [200.0, 200.0]
        assert all(s.tap_pct is not None and 90 <= s.v_min_pct and s.v_max_pct <= 110 for s in res.sites)
        trunk = res.mv_analysis.branches[1]
        assert trunk.sites == 2 and trunk.demand_kva < sum(s.placement.demand_kva for s in res.sites)  # diversity across sites
        assert res.cost_total > 0 and any(line.item.startswith("MV ") for line in res.cost_lines)
        assert "mv_design" in res.unverified

    def test_a_site_far_from_the_mv_route_fails_its_tee(self):
        far = SiteIn(id="T3", kind="transformer", lon=LON + 5 * D, lat=LAT - 0.004)
        lv = [*LV, RouteIn(id="s3", coordinates=[(LON, LAT - 0.004), (LON + 10 * D, LAT - 0.004)])]
        res = design_mv(MvDesignRequest(rules="eskom/0.4.0", supply=(LON - 0.01, LAT + 0.006), sites=[*SITES, far], lv_routes=lv, mv_routes=MV,
                                        customers=street("a", LAT) + street("b", NORTH) + street("c", LAT - 0.004, 10)), RULES)
        tee = next(c for c in res.checks if c.code == "mv_tee" and c.subject == "T3")
        assert not tee.passed and tee.value > 150
        assert not res.passed

    def test_rules_without_mv_design_are_refused(self):
        with pytest.raises(RulesError):
            design_mv(MvDesignRequest(rules="eskom/0.3.0", supply=(LON, LAT), sites=SITES, lv_routes=LV, mv_routes=MV, customers=street("a", LAT, 4)),
                      load_rules("eskom/0.3.0"))


def test_mv_endpoint(client):
    body = MvDesignRequest(rules="eskom/0.4.0", supply=(LON - 0.01, LAT + 0.006), sites=SITES[:1], lv_routes=LV[:1], mv_routes=MV,
                           customers=street("a", LAT, 10)).model_dump(mode="json")
    r = client.post("/calc/mv/design", json=body)
    assert r.status_code == 200, r.text
    site = r.json()["sites"][0]
    assert site["placement"]["unit"] == "pole_mount" and site["tap_pct"] is not None
