"""Roads, contours and the authority's existing network: lines and points, with kind-specific checks."""

import io
import json
import zipfile

import ezdxf
import numpy as np
import pytest
import shapefile
import tifffile

from reticula_calc.geo.importers import UnreadableFileError, asset_type, import_file

LON, LAT = 28.10, -25.52
AREA = {"type": "Polygon", "coordinates": [[[28.09, -25.53], [28.12, -25.53], [28.12, -25.50], [28.09, -25.50], [28.09, -25.53]]]}


def fc(*features):
    return json.dumps({"type": "FeatureCollection", "features": list(features)}).encode()


def line(coords, **props):
    return {"type": "Feature", "properties": props, "geometry": {"type": "LineString", "coordinates": coords}}


def point(lon, lat, **props):
    return {"type": "Feature", "properties": props, "geometry": {"type": "Point", "coordinates": [lon, lat]}}


STREET = [[LON, LAT], [LON + 0.002, LAT]]


class TestRoads:
    def test_geojson_roads_keep_name_class_and_length(self):
        r = import_file("roads.geojson", fc(line(STREET, name="Mmabatho St", highway="residential")), "roads", area=AREA)
        f = r.features[0]
        assert (f.name, f.category, f.geometry["type"]) == ("Mmabatho St", "residential", "LineString")
        assert f.length_m == pytest.approx(201, rel=0.02)  # 0.002° of longitude at 25.5° S
        assert not [i for i in r.issues if i.severity == "error"]

    def test_overpass_ways_are_lines_even_when_closed(self):
        ring = [{"lon": LON, "lat": LAT}, {"lon": LON + 0.001, "lat": LAT}, {"lon": LON + 0.001, "lat": LAT + 0.001}, {"lon": LON, "lat": LAT}]
        doc = {"elements": [
            {"type": "way", "id": 1, "tags": {"highway": "tertiary", "name": "Ruth First Ave"}, "geometry": ring[:2]},
            {"type": "way", "id": 2, "tags": {"highway": "residential", "junction": "roundabout"}, "geometry": ring},
        ]}
        r = import_file("osm.json", json.dumps(doc).encode(), "roads", area=AREA)
        assert [(f.osm_id, f.geometry["type"], f.category) for f in r.features] == [("way/1", "LineString", "tertiary"), ("way/2", "LineString", "residential")]
        assert r.features[0].tags["name"] == "Ruth First Ave"

    def test_polygons_are_skipped_for_roads_and_unnamed_roads_flagged(self):
        poly = {"type": "Feature", "properties": {}, "geometry": {"type": "Polygon", "coordinates": [[[LON, LAT], [LON + 0.001, LAT], [LON, LAT + 0.001], [LON, LAT]]]}}
        r = import_file("r.geojson", fc(line(STREET), poly), "roads", area=AREA)
        assert len(r.features) == 1
        assert {"wrong_geometry", "road_names_missing"} <= {i.code for i in r.issues}

    def test_dxf_open_polylines_and_lines_become_roads(self):
        doc = ezdxf.new()
        msp = doc.modelspace()
        msp.add_lwpolyline([(-10_000, -2_823_000), (-9_800, -2_823_000)], dxfattribs={"layer": "ROADS"})
        msp.add_line((-10_000, -2_823_100), (-9_800, -2_823_100), dxfattribs={"layer": "ROADS"})
        msp.add_lwpolyline([(-10_000, -2_823_000), (-9_900, -2_823_000), (-9_900, -2_822_900)], close=True, dxfattribs={"layer": "STANDS"})
        buf = io.StringIO()
        doc.write(buf)
        r = import_file("plan.dxf", buf.getvalue().encode(), "roads", layer="ROADS", area=AREA)
        assert r.source_crs == "LO29"
        assert len(r.features) == 2 and all(f.geometry["type"] == "LineString" for f in r.features)
        assert r.layers[0].name == "ROADS"


class TestContours:
    def test_elevation_from_attribute_or_z(self):
        data = fc(line(STREET, ELEV="1250"), {**line([[LON, LAT + 0.001, 1255.0], [LON + 0.002, LAT + 0.001, 1255.0]])})
        r = import_file("c.geojson", data, "contours", area=AREA)
        assert [f.elevation_m for f in r.features] == [1250.0, 1255.0]
        assert all(len(f.geometry["coordinates"][0]) == 2 for f in r.features)  # stored flat

    def test_dxf_polyline_elevation_and_closed_rings(self):
        doc = ezdxf.new()
        msp = doc.modelspace()
        msp.add_lwpolyline([(-10_000, -2_823_000), (-9_800, -2_823_000)], dxfattribs={"layer": "CONTOURS", "elevation": 1260})
        msp.add_lwpolyline([(-10_000, -2_823_000), (-9_900, -2_823_000), (-9_900, -2_822_900)], close=True, dxfattribs={"layer": "CONTOURS", "elevation": 1265})
        msp.add_lwpolyline([(-10_000, -2_823_050), (-9_800, -2_823_050)], dxfattribs={"layer": "CONTOURS"})  # no elevation
        buf = io.StringIO()
        doc.write(buf)
        r = import_file("survey.dxf", buf.getvalue().encode(), "contours", area=AREA)
        assert sorted(f.elevation_m for f in r.features) == [1260, 1265]
        issue = next(i for i in r.issues if i.code == "elevation_missing")
        assert (issue.severity, issue.count) == ("warning", 1)

    def test_no_elevations_is_an_error_and_odd_units_are_flagged(self):
        assert any(i.code == "elevation_missing" and i.severity == "error" for i in import_file("c.geojson", fc(line(STREET)), "contours", area=AREA).issues)
        odd = import_file("c.geojson", fc(line(STREET, elevation=125000)), "contours", area=AREA)
        assert "elevation_implausible" in {i.code for i in odd.issues}

    def test_geotiff_without_georeferencing_raises_error(self):
        """GeoTIFF without georeference tags should raise UnreadableFileError."""
        # Create a simple TIFF without georeferencing
        raster = np.random.rand(100, 100).astype(np.float32)
        buf = io.BytesIO()
        with tifffile.TiffWriter(buf) as tif:
            tif.write(raster)
        data = buf.getvalue()

        with pytest.raises(UnreadableFileError, match="georeferencing"):
            import_file("no_geo.tif", data, "contours")

    def test_geotiff_only_for_contours(self):
        """GeoTIFF should only be imported as contours, not other kinds."""
        raster = np.full((50, 50), 1200.0, dtype=np.float32)
        buf = io.BytesIO()
        with tifffile.TiffWriter(buf) as tif:
            tif.write(raster)
        data = buf.getvalue()

        with pytest.raises(UnreadableFileError, match="only supported for contours"):
            import_file("dem.tif", data, "stands")


class TestNetwork:
    def test_types_and_required_fields(self):
        data = fc(
            point(LON, LAT, type="Pole-mounted transformer", kva="100", label="TRF 12"),
            point(LON + 0.001, LAT, ASSET_TYPE="Mini-sub", RATING_KVA=500),
            point(LON + 0.002, LAT, type="Pole"),
            line(STREET, type="11kV overhead line"),
            line([[LON, LAT + 0.001], [LON + 0.001, LAT + 0.001]], type="LV ABC"),
            point(LON, LAT + 0.002, type="Point of supply", voltage="11000"),
            point(LON + 0.002, LAT + 0.002, type="borehole"),
        )
        r = import_file("eskom.geojson", data, "network", area=AREA)
        got = [(f.category, f.name, f.rating_kva, f.voltage_kv, f.missing) for f in r.features]
        assert got == [
            ("transformer", "TRF 12", 100, None, []),
            ("minisub", None, 500, None, []),
            ("pole", None, None, None, []),
            ("mv_line", None, None, 11, []),
            ("lv_line", None, None, 0.4, []),
            ("connection_point", None, None, 11, ["capacity_kva", "fault_level_ka"]),
            ("other", None, None, None, []),
        ]
        codes = {i.code: i for i in r.issues}
        assert codes["network_type_unknown"].severity == "warning"
        assert codes["connection_point_incomplete"].count == 1
        assert "capacity_kva, fault_level_ka" in codes["network_field_missing"].samples[0]

    def test_csv_points_with_semicolons(self):
        data = (
            "asset_id;type;lat;lon;kva\n"
            f"T1;Transformer;{LAT};{LON};200\n"
            f"T2;Transformer;{LAT + 0.001};{LON};\n"
            "bad;Transformer;x;y;100\n"
        ).encode()
        r = import_file("assets.csv", data, "network", area=AREA)
        assert [(f.name, f.category, f.rating_kva, f.missing) for f in r.features] == [
            ("T1", "transformer", 200, []), ("T2", "transformer", None, ["rating_kva"])]
        assert r.source_crs == "WGS84"

    def test_csv_without_coordinates_is_refused(self):
        with pytest.raises(UnreadableFileError, match="coordinate columns"):
            import_file("assets.csv", b"name,type\nT1,transformer\n", "network")

    def test_zipped_shapefile_with_prj(self):
        shp, shx, dbf = io.BytesIO(), io.BytesIO(), io.BytesIO()
        with shapefile.Writer(shp=shp, shx=shx, dbf=dbf, shapeType=shapefile.POINT) as w:
            w.field("TYPE", "C")
            w.field("KVA", "N", decimal=0)
            w.point(LON, LAT)
            w.record("Minisub", 315)
        wgs84 = 'GEOGCS["GCS_WGS_1984",DATUM["D_WGS_1984",SPHEROID["WGS_1984",6378137,298.257223563]],PRIMEM["Greenwich",0],UNIT["Degree",0.017453292519943295]]'
        buf = io.BytesIO()
        with zipfile.ZipFile(buf, "w") as z:
            for name, part in (("net.shp", shp), ("net.shx", shx), ("net.dbf", dbf)):
                z.writestr(name, part.getvalue())
            z.writestr("net.prj", wgs84)
        r = import_file("network.zip", buf.getvalue(), "network", area=AREA)
        assert "prj" in r.crs_reason and r.source_crs == "WGS84"
        assert [(f.category, f.rating_kva) for f in r.features] == [("minisub", 315)]
        assert r.features[0].geometry["coordinates"] == pytest.approx((LON, LAT))

    def test_shapefile_needs_its_dbf(self):
        buf = io.BytesIO()
        with zipfile.ZipFile(buf, "w") as z:
            z.writestr("net.shp", b"x")
        with pytest.raises(UnreadableFileError, match=".dbf"):
            import_file("n.zip", buf.getvalue(), "network")

    def test_dxf_blocks_and_layers_name_the_assets(self):
        doc = ezdxf.new()
        doc.blocks.new(name="TRANSFORMER")
        msp = doc.modelspace()
        msp.add_blockref("TRANSFORMER", (-10_000, -2_823_000), dxfattribs={"layer": "EXISTING"})
        msp.add_point((-9_900, -2_823_000), dxfattribs={"layer": "POLES"})
        msp.add_lwpolyline([(-10_000, -2_823_000), (-9_900, -2_823_000)], dxfattribs={"layer": "MV 11kV"})
        msp.add_text("TRF 7", dxfattribs={"layer": "EXISTING", "insert": (-10_001, -2_823_001)})
        buf = io.StringIO()
        doc.write(buf)
        r = import_file("network.dxf", buf.getvalue().encode(), "network", area=AREA)
        got = sorted((f.category, f.name, f.voltage_kv) for f in r.features)
        assert got == [("mv_line", None, 11), ("pole", None, None), ("transformer", "TRF 7", None)]

    def test_asset_type_words(self):
        assert asset_type({"type": "RMU"}, False) == "switchgear"
        assert asset_type({"type": "11kV cable"}, True) == "mv_cable"
        assert asset_type({"type": "Substation 88/11"}, False) == "substation"
        assert asset_type({"type": "transformer"}, True) is None  # a transformer is not a line


def test_unzipped_shapefile_is_explained():
    with pytest.raises(UnreadableFileError, match="Zip the shapefile"):
        import_file("network.shp", b"x", "network")
