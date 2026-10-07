from reticula_calc.geo.predict import BuildingInput, PredictRequest, predict
from reticula_calc.rules import load_rules

RULES = load_rules("eskom/0.1.0")


def run(**kw):
    return predict(PredictRequest(rules="eskom/0.1.0", buildings=[BuildingInput(id="b", **kw)]), RULES).predictions[0]


def test_osm_school_tag_is_confident():
    p = run(tags={"amenity": "school", "building": "yes"}, area_m2=900)
    assert (p.type, p.low_confidence) == ("school", False)
    assert p.source == "osm:amenity=school"


def test_agreeing_signals_boost_confidence():
    alone = run(area_m2=80)
    agreed = run(area_m2=80, zoning="Residential 1")
    assert alone.type == agreed.type == "house"
    assert agreed.confidence > alone.confidence


def test_footprint_only_is_low_confidence():
    p = run(area_m2=80)
    assert p.low_confidence and p.source.startswith("footprint:")


def test_tiny_footprint_is_other():
    assert run(area_m2=4).type == "other"


def test_shop_tag_beats_residential_zoning():
    p = run(tags={"shop": "convenience"}, zoning="Residential 1", area_m2=60)
    assert p.type == "shop"
    assert {s.source.split(":")[0] for s in p.signals} == {"osm", "zoning", "footprint"}


def test_no_signals():
    p = run()
    assert p.confidence == 0 and p.low_confidence


def test_endpoint(client):
    r = client.post("/predict/building-types", json={"rules": "eskom/0.1.0", "buildings": [{"id": "1", "area_m2": 50, "tags": {"building": "house"}}]})
    assert r.status_code == 200
    body = r.json()
    assert body["predictions"][0]["type"] == "house"
    assert body["rules_hash"] == RULES.hash


def test_load_class_by_zoning_takes_the_longest_match_and_only_for_houses():
    from reticula_calc.geo.predict import load_class_for
    from reticula_calc.rules import load_rules

    rs = load_rules("eskom/0.8.1")
    assert load_class_for("house", "Residential 1", rs)[0] == "urban_residential_1"
    assert load_class_for("house", "Informal settlement", rs)[0] == "rural_village"
    assert load_class_for("house", None, rs) == ("township_area", "default for a house (rules eskom/0.8.1 load_classes)")
    assert load_class_for("shop", "Residential 1", rs) == (None, None)
    assert load_class_for("house", "Residential 1", load_rules("eskom/0.8.0")) == (None, None)


def test_every_zoning_class_is_in_the_design_table():
    from reticula_calc.calcs.admd import _cfg, _load_class
    from reticula_calc.rules import load_rules

    rs = load_rules("eskom/0.8.1")
    sec = rs.data["load_classes"]
    for code in [sec["default"], *(m["load_class"] for m in sec["by_zone"])]:
        assert _load_class(_cfg(rs), rs, code, "score").admd_kva > 0
