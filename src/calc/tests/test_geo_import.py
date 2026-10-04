import io
import json
import zipfile

import ezdxf
import pytest

from reticula_calc.geo import crs
from reticula_calc.geo.importers import UnreadableFileError, import_file

# A small block in Soshanguve (lon 28.10, lat -25.52), about 30 m stands.
LON, LAT, D = 28.10, -25.52, 0.0003
AREA = {"type": "Polygon", "coordinates": [[[28.09, -25.53], [28.12, -25.53], [28.12, -25.50], [28.09, -25.50], [28.09, -25.53]]]}


def square(lon, lat, d=D):
    return [[lon, lat], [lon + d, lat], [lon + d, lat + d], [lon, lat + d], [lon, lat]]


def kml(placemarks: str) -> bytes:
    return f'<?xml version="1.0"?><kml xmlns="http://www.opengis.net/kml/2.2"><Document>{placemarks}</Document></kml>'.encode()


def placemark(name, ring, extra=""):
    coords = " ".join(f"{x},{y},0" for x, y in ring)
    return (f"<Placemark><name>{name}</name>{extra}<Polygon><outerBoundaryIs><LinearRing>"
            f"<coordinates>{coords}</coordinates></LinearRing></outerBoundaryIs></Polygon></Placemark>")


class TestCrs:
    def test_lo29_central_meridian_point(self):
        lon, lat = crs.to_wgs84("LO29").transform(0, -2_900_000)
        assert lon == pytest.approx(29.0, abs=1e-9)
        assert lat == pytest.approx(-26.2, abs=0.05)

    def test_lo_easting_is_positive_east(self):
        lon, _ = crs.to_wgs84("LO29").transform(10_000, -2_900_000)
        assert lon > 29.0

    def test_detects_lon_lat_and_suggests_nearest_lo(self):
        assert crs.detect([28.1], [-25.5], None).spec == "WGS84"
        assert crs.detect([-20_000, 5_000], [-2_830_000, -2_820_000], 28.1).spec == "LO29"
        assert crs.detect([-20_000], [-2_830_000], None).spec is None

    def test_rejects_unknown(self):
        with pytest.raises(crs.CrsError):
            crs.normalise("LO30")
        with pytest.raises(crs.CrsError):
            crs.normalise("EPSG:999999")
        assert crs.normalise("epsg:2054") == "EPSG:2054"  # declared by a shapefile


class TestKml:
    def test_stands_with_erf_numbers(self):
        data = kml(placemark("1001", square(LON, LAT)) + placemark("1002", square(LON + D, LAT)))
        r = import_file("layout.kml", data, "stands", area=AREA)
        assert r.format == "kml" and r.source_crs == "WGS84"
        assert [f.erf for f in r.features] == ["1001", "1002"]
        assert r.features[0].area_m2 == pytest.approx(30 * 33.4, rel=0.05)  # ~30 m x ~33 m at this latitude
        assert not [i for i in r.issues if i.severity == "error"]

    def test_extended_data_and_kmz(self):
        extra = '<ExtendedData><Data name="ERF_NO"><value>2001</value></Data><Data name="Zoning"><value>Residential 1</value></Data></ExtendedData>'
        buf = io.BytesIO()
        with zipfile.ZipFile(buf, "w") as z:
            z.writestr("doc.kml", kml(placemark("Stand", square(LON, LAT), extra)))
        r = import_file("layout.kmz", buf.getvalue(), "stands", area=AREA)
        assert r.features[0].erf == "2001"  # an explicit erf field beats the placemark name
        assert r.features[0].zoning == "Residential 1"

    def test_duplicate_and_missing_erf_flagged(self):
        data = kml(placemark("5", square(LON, LAT)) + placemark("5", square(LON + D, LAT)) + placemark("", square(LON + 2 * D, LAT)))
        codes = {i.code for i in import_file("x.kml", data, "stands", area=AREA).issues}
        assert {"erf_duplicate", "erf_missing"} <= codes

    def test_outside_area_is_an_error_when_nothing_inside(self):
        r = import_file("x.kml", kml(placemark("1", square(2.35, 48.85))), "stands", area=AREA)
        assert any(i.code == "outside_area" and i.severity == "error" for i in r.issues)

    def test_self_intersecting_outline_is_repaired(self):
        bow = [[LON, LAT], [LON + D, LAT + D], [LON + D, LAT], [LON, LAT + D], [LON, LAT]]
        r = import_file("x.kml", kml(placemark("1", bow)), "stands", area=AREA)
        assert len(r.features) == 1
        assert any(i.code == "geometry_repaired" for i in r.issues)


class TestGeoJsonAndOverpass:
    def test_geojson_buildings_keep_tags(self):
        fc = {"type": "FeatureCollection", "features": [
            {"type": "Feature", "id": "way/1", "properties": {"building": "house"}, "geometry": {"type": "Polygon", "coordinates": [square(LON, LAT, 0.0001)]}},
        ]}
        r = import_file("b.geojson", json.dumps(fc).encode(), "buildings", area=AREA)
        assert r.features[0].tags == {"building": "house"}
        assert r.features[0].ref == "way/1"

    def test_overpass_ways_become_buildings_and_relations_are_reported(self):
        ring = square(LON, LAT, 0.0001)
        doc = {"elements": [
            {"type": "way", "id": 42, "tags": {"building": "school", "amenity": "school"}, "geometry": [{"lon": x, "lat": y} for x, y in ring]},
            {"type": "relation", "id": 7, "tags": {"building": "yes"}},
        ]}
        r = import_file("export.json", json.dumps(doc).encode(), "buildings", area=AREA)
        assert r.format == "overpass"
        assert r.features[0].osm_id == "way/42"
        assert r.features[0].tags["amenity"] == "school"
        assert any(i.code == "osm_relations_skipped" for i in r.issues)


class TestDxf:
    @staticmethod
    def dxf_bytes(lo_x: float, lo_y: float) -> bytes:
        doc = ezdxf.new()
        msp = doc.modelspace()
        for i in range(3):
            x = lo_x + i * 30
            msp.add_lwpolyline([(x, lo_y), (x + 30, lo_y), (x + 30, lo_y + 30), (x, lo_y + 30)], close=True, dxfattribs={"layer": "STANDS"})
            msp.add_text(str(3001 + i), dxfattribs={"layer": "STANDS", "insert": (x + 10, lo_y + 10)})
        msp.add_lwpolyline([(lo_x, lo_y - 20), (lo_x + 90, lo_y - 20)], dxfattribs={"layer": "ROADS"})
        buf = io.StringIO()
        doc.write(buf)
        return buf.getvalue().encode()

    def test_lo29_cad_stands_with_labels(self):
        # Lo29 CAD coordinates near lon 28.10, lat -25.52
        x, y = crs.to_wgs84("LO29").transform(LON, LAT, direction="INVERSE")
        r = import_file("layout.dxf", self.dxf_bytes(x, y), "stands", layer="STANDS", area=AREA)
        assert r.source_crs == "LO29"  # detected from the project area
        assert [f.erf for f in r.features] == ["3001", "3002", "3003"]
        assert r.features[0].area_m2 == pytest.approx(900, rel=0.01)
        assert {li.name for li in r.layers} >= {"STANDS", "ROADS"}
        assert not [i for i in r.issues if i.severity == "error"]

    def test_wrong_layer_gives_no_features_error(self):
        t = crs.to_wgs84("LO29")
        x, y = t.transform(LON, LAT, direction="INVERSE")
        r = import_file("layout.dxf", self.dxf_bytes(x, y), "stands", layer="ROADS", area=AREA)
        assert any(i.code == "no_features" for i in r.issues)
        assert any(i.code == "open_polylines" for i in r.issues)


def test_unsupported_file_type():
    with pytest.raises(UnreadableFileError):
        import_file("layout.dwg", b"AC1032", "stands")


def test_import_endpoint(client):
    data = kml(placemark("1001", square(LON, LAT)))
    r = client.post("/geo/import", files={"file": ("layout.kml", data)}, data={"kind": "stands", "area": json.dumps(AREA)})
    assert r.status_code == 200
    assert r.json()["features"][0]["erf"] == "1001"
    bad = client.post("/geo/import", files={"file": ("layout.kml", data)}, data={"kind": "rivers"})
    assert bad.status_code == 422
