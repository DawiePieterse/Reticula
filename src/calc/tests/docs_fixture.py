"""A small but complete design package (two transformer sites, MV, bulk study) for the document tests."""

from functools import lru_cache

from reticula_calc.bulk.study import BulkStudyRequest, ConnectionPoint, SiteLoad, bulk_study
from reticula_calc.docs.package import DocumentPackage, LoadRow, LvSite, ProjectInfo, Stamp, Stand, StoredRun
from reticula_calc.lv.build import CustomerIn, RouteIn
from reticula_calc.lv.design import LvDesignRequest, design_lv
from reticula_calc.mv.design import MvDesignRequest, design_mv
from reticula_calc.mv.placement import SiteIn
from reticula_calc.rules import load_rules

LON, LAT, D = 28.10, -25.52, 0.00045
NORTH = LAT + 0.0036


def street(prefix, lat, n=24):
    return [CustomerIn(building_id=f"{prefix}{i}", lon=LON + (i // 2 + 0.5) * D / 2, lat=lat + (0.00015 if i % 2 else -0.00015),
                       load_class="township_area", erf=f"{prefix}{i}", phases=3 if i == 0 else 1) for i in range(n)]


@lru_cache(maxsize=1)
def package() -> DocumentPackage:
    rules = load_rules("eskom/0.5.0")
    lv_routes = [RouteIn(id="s1", coordinates=[(LON, LAT), (LON + 6 * D, LAT)]), RouteIn(id="s2", coordinates=[(LON, NORTH), (LON + 6 * D, NORTH)])]
    mv_routes = [RouteIn(id="mv", coordinates=[(LON - 0.01, LAT + 0.006), (LON + 3 * D, LAT + 0.006), (LON + 3 * D, LAT - 0.0005)])]
    a, b = street("a", LAT), street("b", NORTH)
    sites = [SiteIn(id="T1", kind="transformer", lon=LON + 3 * D, lat=LAT), SiteIn(id="T2", kind="transformer", lon=LON + 3 * D, lat=NORTH)]
    lv = []
    for s, cust, route in ((sites[0], a, lv_routes[0]), (sites[1], b, lv_routes[1])):
        res = design_lv(LvDesignRequest(rules="eskom/0.5.0", source=(s.lon, s.lat), routes=[route], customers=cust,
                                        constructions=["overhead", "underground"]), rules)
        lv.append(LvSite(site_id=s.id, label=f"Site {s.id}", run_id=f"run-{s.id}", result=res.model_dump(mode="json")))
    mv = design_mv(MvDesignRequest(rules="eskom/0.5.0", supply=(LON - 0.01, LAT + 0.006), sites=sites, lv_routes=lv_routes, mv_routes=mv_routes,
                                   customers=a + b), rules)
    cp = ConnectionPoint(lon=LON - 0.01, lat=LAT + 0.006, voltage_kv=11, available_capacity_kva=500, fault_mva_max=150, fault_mva_min=100,
                         reference="BQ-0042")
    loads = [SiteLoad(site_id=s.placement.site_id, rating_kva=s.placement.rating_kva, z_pct=s.placement.z_pct, x_r=s.placement.x_r or 2,
                      tap_pct=s.tap_pct, design_kva=s.placement.design_kva) for s in mv.sites]
    bulk = bulk_study(BulkStudyRequest(rules="eskom/0.5.0", connection_point=cp, mv_network=mv.mv_network, sites=loads), rules)
    stands = [Stand(erf=c.erf, coordinates=[[(c.lon - 0.00005, c.lat - 0.00005), (c.lon + 0.00005, c.lat - 0.00005), (c.lon + 0.00005, c.lat + 0.00005),
                                             (c.lon - 0.00005, c.lat + 0.00005), (c.lon - 0.00005, c.lat - 0.00005)]]) for c in a[:6]]
    return DocumentPackage(
        project=ProjectInfo(id="0192f7a0-1111-7000-8000-000000000001", name="Test Township Ext 1", reference="TT1"),
        stamp=Stamp(rules="eskom/0.5.0", rules_hash=rules.hash, rate_list="indicative/2026-10", rate_date="2026-10-01", design_date="2026-10-04",
                    revision="D1", generated_at="2026-10-04T12:00:00Z", engineer="A. Engineer Pr Eng"),
        lv_designs=lv, mv_design=StoredRun(run_id="run-mv", result=mv.model_dump(mode="json")),
        bulk_study=StoredRun(run_id="run-bulk", result=bulk.model_dump(mode="json")),
        loads=[LoadRow(building_id=c.building_id, erf=c.erf, kind="residential", load_class=c.load_class, kva=2.37, phases=c.phases, status="confirmed",
                       lon=c.lon, lat=c.lat) for c in a + b],
        connection_point=cp.model_dump(), stands=stands,
        assumptions=[{"text": "Soil thermal resistivity 1.2 K·m/W assumed (no survey).", "source": "LV design", "status": "open"}],
    )
