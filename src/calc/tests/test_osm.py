import json

import pytest

from reticula_calc.geo import osm

AREA = {"type": "Polygon", "coordinates": [[[28.09, -25.53], [28.12, -25.53], [28.12, -25.50], [28.09, -25.50], [28.09, -25.53]]]}
WAYS = {"elements": [
    {"type": "way", "id": 7, "tags": {"highway": "residential", "name": "Mmabatho St"},
     "geometry": [{"lon": 28.10, "lat": -25.52}, {"lon": 28.102, "lat": -25.52}]},
]}


def test_query_covers_the_area_box_in_overpass_order():
    q = osm.overpass_query("roads", AREA)
    assert q.startswith("[out:json]") and q.endswith("out geom;")
    assert "(-25.530000,28.090000,-25.500000,28.120000)" in q
    assert 'way["building"]' in osm.overpass_query("buildings", AREA)


def test_large_areas_are_refused():
    big = {"type": "Polygon", "coordinates": [[[27, -26], [28, -26], [28, -25], [27, -26]]]}
    with pytest.raises(osm.OsmError, match="larger than"):
        osm.overpass_query("roads", big)


def test_fetch_uses_the_configured_server(monkeypatch):
    calls = []
    monkeypatch.setenv(osm.OVERPASS_ENV, "https://overpass.example/api/interpreter")
    osm.fetch_osm("roads", AREA, lambda url, q: calls.append((url, q)) or b"{}")
    assert calls[0][0] == "https://overpass.example/api/interpreter"


def test_endpoint_imports_fetched_roads(client, monkeypatch):
    monkeypatch.setattr(osm, "http_fetch", lambda url, q: json.dumps(WAYS).encode())
    monkeypatch.setattr("reticula_calc.app.fetch_osm", lambda kind, area: osm.fetch_osm(kind, area, osm.http_fetch))
    r = client.post("/geo/osm", json={"kind": "roads", "area": AREA})
    assert r.status_code == 200
    body = r.json()
    assert (body["format"], body["features"][0]["name"], body["features"][0]["osm_id"]) == ("overpass", "Mmabatho St", "way/7")


def test_endpoint_reports_an_unreachable_server(client, monkeypatch):
    def down(url, q):
        raise osm.OsmError("OpenStreetMap (Overpass) could not be reached: timed out")

    monkeypatch.setattr("reticula_calc.app.fetch_osm", lambda kind, area: osm.fetch_osm(kind, area, down))
    r = client.post("/geo/osm", json={"kind": "buildings", "area": AREA})
    assert r.status_code == 422 and "could not be reached" in r.json()["detail"]
    assert client.post("/geo/osm", json={"kind": "stands", "area": AREA}).status_code == 422
