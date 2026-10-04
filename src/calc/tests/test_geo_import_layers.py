"""Roads, contours and the authority's existing network (plan item 1.1)."""

import csv
import io
import json
import zipfile

import ezdxf
import numpy as np
import pytest
import shapefile
import tifffile
from pyproj import Transformer

from reticula_calc.geo.importers import UnreadableFileError, import_file

LON, LAT = 28.10, -25.52
AREA = {"type": "Polygon", "coordinates": [[[28.09, -25.53], [28.12, -25.53], [28.12, -25.50], [28.09, -25.50], [28.09, -25.53]]]}
TO_LO29 = Transformer.from_crs("EPSG:4326", "+proj=tmerc +lat_0=0 +lon_0=29 +k=1 +x_0=0 +y_0=0 +ellps=WGS84 +units=m", always_xy=True)
TO_UTM35 = Transformer.from_crs("EPSG:4326", "EPSG:32735", always_xy=True)


def codes(r):
    return {i.code: i for i in r.issues}


def fc(*features):
    return json.dumps({"type": "FeatureCollection", "features": list(features)}).encode()


def feat(geom_type, coords, **props):
    return {"type": "Feature", "geometry": {"type": geom_type, "coordinates": coords}, "properties": props}


class TestRoads:
    def test_overpass_highways_become_lines_with_class_and_name(self):
        doc = {"elements": [
            {"type": "way", "id": 1, "tags": {"highway": "residential", "name": "Mabopane Rd"},
             "geometry": [{"lon": 28.10, "lat": -25.52}, {"lon": 28.101, "lat": -25.52}, {"lon": 28.102, "lat": -25.521}]},
            {"type": "way", "id": 2, "tags": {"highway": "footway"}, "geometry": [{"lon": 28.103, "lat": -25.52}, {"lon": 28.104, "lat": -25.52}]},
            {"type": "node", "id": 3, "lat": -25.52, "lon": 28.1},
        ]}
        r = import_file("roads.json", json.dumps(doc).encode(), "roads", area=AREA)
        assert r.format == "overpass"
        assert [(f.osm_id, f.subtype, f.name) for f in r.features] == [("way/1", "residential", "Mabopane Rd"), ("way/2", "footway", None)]
        assert r.features[0].geometry["type"] == "LineString"
        assert 200 < r.features[0].length_m < 260  # about 100 m east then 140 m diagonal
        assert not [i for i in r.issues if i.severity == "error"]

    def test_polygons_in_a_road_file_are_skipped_with_a_warning(self):
        data = fc(feat("LineString", [[LON, LAT], [LON + 0.001, LAT]], highway="tertiary"),
                  feat("Polygon", [[[LON, LAT], [LON + 0.001, LAT], [LON, LAT + 0.001], [LON, LAT]]]))
        r = import_file("roads.geojson", data, "roads", area=AREA)
        assert len(r.features) == 1
        assert codes(r)["geometry_type_skipped"].count == 1

    def test_dxf_open_polylines_and_lines_on_the_roads_layer(self):
        x, y = TO_LO29.transform(LON, LAT)
        doc = ezdxf.new()
        msp = doc.modelspace()
        msp.add_lwpolyline([(x, y), (x + 100, y), (x + 100, y + 50)], dxfattribs={"layer": "ROADS"})
        msp.add_line((x, y + 80), (x + 60, y + 80), dxfattribs={"layer": "ROADS"})
        msp.add_lwpolyline([(x, y), (x + 30, y), (x + 30, y + 30)], close=True, dxfattribs={"layer": "STANDS"})
        buf = io.StringIO()
        doc.write(buf)
        r = import_file("layout.dxf", buf.getvalue().encode(), "roads", layer="ROADS", area=AREA)
        assert r.source_crs == "LO29"
        assert sorted(round(f.length_m) for f in r.features) == [60, 150]


class TestContours:
    def test_geojson_contours_take_elevation_from_a_field_or_z(self):
        data = fc(feat("LineString", [[LON, LAT], [LON + 0.002, LAT]], ELEV="1302"),
                  feat("LineString", [[LON, LAT + 0.001, 1304.0], [LON + 0.002, LAT + 0.001, 1304.0]]),
                  feat("LineString", [[LON, LAT + 0.002], [LON + 0.002, LAT + 0.002]]))
        r = import_file("contours.geojson", data, "contours", area=AREA)
        assert [f.elevation_m for f in r.features] == [1302.0, 1304.0]
        assert codes(r)["contour_elevation_missing"].severity == "warning"

    def test_no_elevations_at_all_is_an_error(self):
        r = import_file("contours.geojson", fc(feat("LineString", [[LON, LAT], [LON + 0.002, LAT]])), "contours", area=AREA)
        assert codes(r)["contour_elevation_missing"].severity == "error"

    def test_dxf_contours_from_polyline_elevation_and_height_labels(self):
        x, y = TO_LO29.transform(LON, LAT)
        doc = ezdxf.new()
        msp = doc.modelspace()
        msp.add_lwpolyline([(x, y), (x + 200, y + 10)], dxfattribs={"layer": "CONTOURS", "elevation": 1310.0})
        flat = msp.add_lwpolyline([(x, y + 40), (x + 200, y + 50)], dxfattribs={"layer": "CONTOURS"})
        msp.add_text("1311", dxfattribs={"layer": "CONTOURS", "insert": (x + 100, y + 46)})
        assert flat.dxf.elevation == 0
        buf = io.StringIO()
        doc.write(buf)
        r = import_file("contours.dxf", buf.getvalue().encode(), "contours", layer="CONTOURS", area=AREA)
        assert sorted(f.elevation_m for f in r.features) == [1310.0, 1311.0]

    @staticmethod
    def dem(nodata=None) -> bytes:
        """A 60 x 60 UTM 35S elevation model, 5 m cells, rising 0.1 m per metre eastwards from 1300 m."""
        x0, y0 = TO_UTM35.transform(LON, LAT)
        z = 1300 + 0.5 * np.tile(np.arange(60, dtype=np.float32), (60, 1))
        if nodata is not None:
            z[:5, :5] = nodata
        geokeys = (1, 1, 0, 3, 1024, 0, 1, 1, 1025, 0, 1, 1, 3072, 0, 1, 32735)
        extratags = [(33550, "d", 3, (5.0, 5.0, 0.0)), (33922, "d", 6, (0, 0, 0, x0, y0, 0)), (34735, "H", len(geokeys), geokeys)]
        if nodata is not None:
            extratags.append((42113, "s", 0, str(nodata), False))
        buf = io.BytesIO()
        tifffile.imwrite(buf, z, extratags=extratags)
        return buf.getvalue()

    def test_geotiff_elevation_model_is_contoured_in_its_declared_system(self):
        r = import_file("dem.tif", self.dem(), "contours", area=AREA, contour_interval=5)
        assert r.format == "geotiff"
        assert r.source_crs == "UTM35S"
        assert "EPSG:32735" in r.crs_reason
        assert sorted({f.elevation_m for f in r.features}) == [1305.0, 1310.0, 1315.0, 1320.0, 1325.0]
        assert codes(r)["contours_generated"].severity == "warning"
        # Contours run north-south (along the UTM grid) because the ground rises eastwards.
        line = r.features[0].geometry["coordinates"]
        assert abs(line[0][0] - line[-1][0]) < 0.05 * abs(line[0][1] - line[-1][1])
        assert not [i for i in r.issues if i.severity == "error"]

    def test_geotiff_picks_an_interval_and_honours_nodata(self):
        r = import_file("dem.tif", self.dem(nodata=-9999), "contours", area=AREA)
        levels = sorted({f.elevation_m for f in r.features})
        assert levels and levels[0] > 1300 and levels[-1] < 1330
        assert "every 1 m" in codes(r)["contours_generated"].message

    def test_geotiff_only_as_contours(self):
        with pytest.raises(UnreadableFileError):
            import_file("dem.tif", self.dem(), "roads", area=AREA)


def shapefile_zip(records, geometry, prj_wkt=None) -> bytes:
    shp, shx, dbf = io.BytesIO(), io.BytesIO(), io.BytesIO()
    w = shapefile.Writer(shp=shp, shx=shx, dbf=dbf, shapeType=shapefile.POINT if geometry == "point" else shapefile.POLYLINE)
    keys = list(records[0][1].keys())
    for k in keys:
        w.field(k, "C", 40)
    for geom, props in records:
        if geometry == "point":
            w.point(*geom)
        else:
            w.line([geom])
        w.record(*[props[k] for k in keys])
    w.close()
    out = io.BytesIO()
    with zipfile.ZipFile(out, "w") as z:
        z.writestr("net.shp", shp.getvalue())
        z.writestr("net.shx", shx.getvalue())
        z.writestr("net.dbf", dbf.getvalue())
        if prj_wkt:
            z.writestr("net.prj", prj_wkt)
    return out.getvalue()


class TestNetwork:
    def test_geojson_assets_are_normalised_from_aliases(self):
        data = fc(feat("LineString", [[LON, LAT], [LON + 0.003, LAT]], TYPE="MV Line", Voltage="11 kV", Conductor="Hare", AssetID="MV-7"),
                  feat("Point", [LON + 0.003, LAT], asset="tx", KVA="200", kv="11", status="existing"),
                  feat("Point", [LON + 0.001, LAT], asset_type="pole"))
        r = import_file("network.geojson", data, "network", area=AREA)
        assert not [i for i in r.issues if i.severity == "error"], r.issues
        line, tx, pole = r.features
        assert (line.subtype, line.attributes["voltage_kv"], line.attributes["conductor"], line.attributes["asset_id"]) == ("mv_line", 11.0, "Hare", "MV-7")
        assert (tx.subtype, tx.attributes["rating_kva"], tx.attributes["status"]) == ("transformer", 200.0, "existing")
        assert pole.subtype == "pole"
        assert line.attributes["source_fields"]["TYPE"] == "MV Line"

    def test_missing_required_fields_and_unknown_types_are_errors(self):
        data = fc(feat("Point", [LON, LAT], asset_type="transformer", kv="11"),
                  feat("Point", [LON + 0.001, LAT], asset_type="streetlight"),
                  feat("Point", [LON + 0.002, LAT], asset_type="mv_line", kv="11", conductor="Fox"),
                  feat("Point", [LON + 0.003, LAT], asset_type="minisub", kva="lots", kv="11"))
        c = codes(import_file("network.geojson", data, "network", area=AREA))
        assert c["network_field_missing"].severity == "error"
        assert "rating_kva" in c["network_field_missing"].message
        assert c["network_type_unknown"].samples == ["feature-2: streetlight"]
        assert c["network_geometry_mismatch"].samples == ["feature-3: mv_line drawn as a point"]
        assert c["network_value_invalid"].samples == ["feature-4: rating_kva = lots"]

    def test_csv_points_with_lon_lat_columns(self):
        out = io.StringIO()
        w = csv.writer(out, delimiter=";")
        w.writerow(["asset_id", "asset_type", "lon", "lat", "rating_kva", "voltage_kv"])
        w.writerow(["T1", "Transformer", LON, LAT, "100", "22"])
        w.writerow(["P1", "pole", LON + 0.001, LAT, "", ""])
        r = import_file("assets.csv", out.getvalue().encode(), "network", area=AREA)
        assert r.format == "csv"
        assert [(f.ref, f.subtype) for f in r.features] == [("T1", "transformer"), ("P1", "pole")]
        assert r.features[0].attributes["voltage_kv"] == 22.0

    def test_csv_with_wkt_lines(self):
        data = (f'asset_type,conductor,wkt\nlv_line,ABC 70,"LINESTRING ({LON} {LAT}, {LON + 0.001} {LAT})"\n').encode()
        r = import_file("lv.csv", data, "network", area=AREA)
        assert r.features[0].geometry["type"] == "LineString"
        assert r.features[0].attributes["conductor"] == "ABC 70"

    def test_csv_without_coordinates_is_unreadable(self):
        with pytest.raises(UnreadableFileError):
            import_file("assets.csv", b"asset_type,name\npole,P1\n", "network", area=AREA)

    def test_zipped_shapefile_uses_its_prj(self):
        x0, y0 = TO_UTM35.transform(LON, LAT)
        x1, y1 = TO_UTM35.transform(LON + 0.003, LAT)
        prj = 'PROJCS["WGS 84 / UTM zone 35S",GEOGCS["WGS 84",DATUM["WGS_1984",SPHEROID["WGS 84",6378137,298.257223563]],PRIMEM["Greenwich",0],UNIT["degree",0.0174532925199433]],PROJECTION["Transverse_Mercator"],PARAMETER["latitude_of_origin",0],PARAMETER["central_meridian",27],PARAMETER["scale_factor",0.9996],PARAMETER["false_easting",500000],PARAMETER["false_northing",10000000],UNIT["metre",1],AUTHORITY["EPSG","32735"]]'
        data = shapefile_zip([([(x0, y0), (x1, y1)], {"asset_type": "lv_line", "conductor": "ABC 95"})], "line", prj)
        r = import_file("lv.zip", data, "network", area=AREA)
        assert (r.format, r.source_crs) == ("shapefile", "UTM35S")
        assert r.features[0].geometry["coordinates"][0] == pytest.approx([LON, LAT], abs=1e-7)
        assert 290 < r.features[0].length_m < 315

    def test_shapefile_without_prj_falls_back_to_detection(self):
        data = shapefile_zip([((LON, LAT), {"asset_type": "pole"})], "point")
        r = import_file("poles.zip", data, "network", area=AREA)
        assert r.source_crs == "WGS84"


def test_endpoint_accepts_new_kinds_and_contour_interval(client):
    r = client.post("/geo/import", files={"file": ("dem.tif", TestContours.dem())},
                    data={"kind": "contours", "area": json.dumps(AREA), "contour_interval": "10"})
    assert r.status_code == 200, r.text
    assert sorted({f["elevation_m"] for f in r.json()["features"]}) == [1310.0, 1320.0]
