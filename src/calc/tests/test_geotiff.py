"""Contours from a GeoTIFF elevation model (plan 1.1): georeferencing, pixel convention, NoData and the interval."""

import io

import numpy as np
import pytest
import tifffile
from pyproj import Transformer

from reticula_calc.geo.importers import import_file

DOUBLE, SHORT, ASCII = 12, 3, 2


def geotiff(z: np.ndarray, geokeys: list[int], scale=None, tie=None, matrix=None, nodata: str | None = None) -> bytes:
    tags = [(34735, SHORT, len(geokeys), tuple(geokeys), True)]
    if scale is not None:
        tags += [(33550, DOUBLE, 3, tuple(scale), True), (33922, DOUBLE, 6, tuple(tie), True)]
    if matrix is not None:
        tags.append((34264, DOUBLE, 16, tuple(matrix), True))
    if nodata is not None:
        tags.append((42113, ASCII, len(nodata) + 1, nodata, True))
    buf = io.BytesIO()
    tifffile.imwrite(buf, z.astype("float32"), extratags=tags)
    return buf.getvalue()


def keys(*entries: tuple[int, int]) -> list[int]:
    out = [1, 1, 0, len(entries)]
    for k, v in entries:
        out += [k, 0, 1, v]
    return out


X0, Y0 = 600_000.0, 7_180_000.0  # UTM 35S, near Pretoria
TO_UTM = Transformer.from_crs("EPSG:4326", "EPSG:32735", always_xy=True)


def test_projected_plane_gives_straight_contours_where_the_plane_crosses_each_level():
    # 200 × 100 cells of 1 m; elevation rises 0.1 m per metre east, sampled at cell centres.
    cols = np.arange(200)
    z = np.tile(100 + 0.1 * (cols + 0.5), (100, 1))
    data = geotiff(z, keys((1024, 1), (1025, 1), (3072, 32735)), scale=(1, 1, 0), tie=(0, 0, 0, X0, Y0, 0))
    r = import_file("dem.tif", data, "contours")
    assert r.format == "geotiff" and r.source_crs == "WGS84"
    levels = sorted(f.elevation_m for f in r.features)
    assert levels == [float(v) for v in range(101, 120)]
    for f in r.features:
        x, y = TO_UTM.transform(*np.array(f.geometry["coordinates"]).T)
        # Level L lies 10·(L − 100) m east of the raster's west edge, along the whole north–south extent.
        assert np.allclose(x, X0 + 10 * (f.elevation_m - 100), atol=0.05)
        assert y.max() - y.min() == pytest.approx(99, abs=0.6)


def test_geographic_matrix_and_pixel_is_point_with_an_interval():
    # Values at the grid points themselves; 2 m per row southwards; 0.0001° cells.
    lon0, lat0, cell = 28.1, -25.5, 0.0001
    rows = np.arange(60)
    z = np.tile((50 + 2 * rows)[:, None], (1, 40))
    matrix = [cell, 0, 0, lon0, 0, -cell, 0, lat0, 0, 0, 0, 0, 0, 0, 0, 1]
    data = geotiff(z, keys((1024, 2), (1025, 2), (2048, 4326)), matrix=matrix)
    r = import_file("dem.tiff", data, "contours", interval_m=5)
    levels = sorted(f.elevation_m for f in r.features)
    assert levels == [float(v) for v in range(50, 169, 5)]
    for f in r.features:
        lat = np.array(f.geometry["coordinates"])[:, 1]
        assert np.allclose(lat, lat0 - cell * (f.elevation_m - 50) / 2, atol=1e-9)


def test_nodata_cells_are_left_out():
    cols = np.arange(100)
    z = np.tile(100 + 0.1 * (cols + 0.5), (20, 1))
    z[:, :50] = -9999
    data = geotiff(z, keys((1024, 1), (3072, 32735)), scale=(1, 1, 0), tie=(0, 0, 0, X0, Y0, 0), nodata="-9999")
    r = import_file("dem.tif", data, "contours")
    assert sorted(f.elevation_m for f in r.features) == [float(v) for v in range(106, 110)]


def test_without_an_epsg_code_the_file_is_refused():
    from reticula_calc.geo.importers import UnreadableFileError

    data = geotiff(np.zeros((5, 5)), keys((1024, 1), (3072, 32767)), scale=(1, 1, 0), tie=(0, 0, 0, X0, Y0, 0))
    with pytest.raises(UnreadableFileError, match="EPSG"):
        import_file("dem.tif", data, "contours")
