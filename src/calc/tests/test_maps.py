import http.server
import io
import threading
from pathlib import Path

import pytest
from pmtiles.reader import MemorySource, Reader, all_tiles
from pmtiles.tile import Compression, TileType, tileid_to_zxy, zxy_to_tileid
from pmtiles.writer import Writer

from reticula_calc.maps.extract import ExtractRequest, MapSourceError, extract, file_source, http_source, tile_range

# Soshanguve-ish: about 2 km across.
BBOX = (28.09, -25.53, 28.11, -25.51)
WORLD_MAX_ZOOM = 10


def tile_bytes(z: int, x: int, y: int) -> bytes:
    return f"{z}/{x}/{y}".encode()


@pytest.fixture
def source(tmp_path: Path) -> Path:
    """A small 'planet': every tile over a wider box plus a tile far away, zooms 0-10."""
    wide = (27.5, -26.0, 28.7, -25.0)
    ids = {zxy_to_tileid(z, x, y) for z in range(WORLD_MAX_ZOOM + 1) for xs, ys in [tile_range(wide, z)] for x in xs for y in ys}
    ids.add(zxy_to_tileid(10, 0, 0))  # far away, must not be copied
    out = io.BytesIO()
    w = Writer(out)
    for tid in sorted(ids):
        w.write_tile(tid, tile_bytes(*tileid_to_zxy(tid)))
    w.finalize(
        {
            "version": 3,
            "tile_type": TileType.MVT,
            "tile_compression": Compression.GZIP,
            "center_zoom": 0,
            "center_lon_e7": 0,
            "center_lat_e7": 0,
        },
        {"attribution": "© OpenStreetMap", "vector_layers": [{"id": "roads"}]},
    )
    path = tmp_path / "planet.pmtiles"
    path.write_bytes(out.getvalue())
    return path


def test_tile_range_covers_the_box():
    xs, ys = tile_range(BBOX, 15)
    # 0.02° of longitude at zoom 15 is about 2 tiles wide (360/2^15 = 0.011°).
    assert 2 <= len(xs) <= 3 and 2 <= len(ys) <= 4
    assert tile_range(BBOX, 0) == (range(1), range(1))
    # Rows count from the north: a box further south has larger rows.
    assert tile_range((28.0, -26.1, 28.1, -26.0), 10)[1].start > tile_range((28.0, -25.1, 28.1, -25.0), 10)[1].start


def test_extract_copies_only_the_area_and_keeps_the_metadata(source: Path):
    pack = extract(file_source(source), ExtractRequest(bbox=BBOX), source.name)
    reader = Reader(MemorySource(pack.data))
    h = reader.header()
    assert (h["min_zoom"], h["max_zoom"], pack.max_zoom) == (0, WORLD_MAX_ZOOM, WORLD_MAX_ZOOM)
    assert h["tile_type"] == TileType.MVT and h["tile_compression"] == Compression.GZIP
    assert (h["min_lon_e7"], h["max_lat_e7"]) == (280_900_000, -255_100_000)

    tiles = dict(all_tiles(MemorySource(pack.data)))
    expected = {(z, x, y) for z in range(WORLD_MAX_ZOOM + 1) for xs, ys in [tile_range(BBOX, z)] for x in xs for y in ys}
    assert set(tiles) == expected
    assert pack.tiles == len(expected)
    assert all(data == tile_bytes(*zxy) for zxy, data in tiles.items())
    assert (10, 0, 0) not in tiles

    meta = reader.metadata()
    assert meta["attribution"] == "© OpenStreetMap"
    assert meta["reticula"] == {"bbox": list(BBOX), "source": "planet.pmtiles"}


def test_extract_honours_the_zoom_limits(source: Path):
    pack = extract(file_source(source), ExtractRequest(bbox=BBOX, min_zoom=6, max_zoom=8), "x")
    assert {z for (z, _, _), _ in all_tiles(MemorySource(pack.data))} == {6, 7, 8}


def test_an_area_without_tiles_is_refused(source: Path):
    with pytest.raises(MapSourceError, match="no tiles"):
        extract(file_source(source), ExtractRequest(bbox=(10.0, 10.0, 10.1, 10.1), min_zoom=5), "x")


def test_a_file_that_is_not_pmtiles_is_refused(tmp_path: Path):
    bad = tmp_path / "bad.pmtiles"
    bad.write_bytes(b"not a map" * 50)
    with pytest.raises(MapSourceError, match="cannot be read as PMTiles"):
        extract(file_source(bad), ExtractRequest(bbox=BBOX), "bad")


def test_large_or_inverted_areas_are_refused():
    with pytest.raises(ValueError, match="larger than"):
        ExtractRequest(bbox=(27.0, -26.0, 28.0, -25.0))
    with pytest.raises(ValueError, match="bbox"):
        ExtractRequest(bbox=(28.2, -25.5, 28.1, -25.4))


class _RangeHandler(http.server.BaseHTTPRequestHandler):
    data = b""

    def do_GET(self):
        start, end = (int(v) for v in self.headers["Range"].removeprefix("bytes=").split("-"))
        body = self.data[start : end + 1]
        self.send_response(206)
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def log_message(self, *args):
        pass


def test_http_source_reads_ranges(source: Path):
    _RangeHandler.data = source.read_bytes()
    server = http.server.HTTPServer(("127.0.0.1", 0), _RangeHandler)
    threading.Thread(target=server.serve_forever, daemon=True).start()
    try:
        url = f"http://127.0.0.1:{server.server_port}/planet.pmtiles"
        over_http = extract(http_source(url), ExtractRequest(bbox=BBOX), "planet.pmtiles")
        from_file = extract(file_source(source), ExtractRequest(bbox=BBOX), "planet.pmtiles")
        assert over_http.data == from_file.data
    finally:
        server.shutdown()


def test_unreachable_source_is_reported():
    with pytest.raises(MapSourceError, match="cannot be reached"):
        extract(http_source("http://127.0.0.1:9/none.pmtiles", timeout=2), ExtractRequest(bbox=BBOX), "x")


def test_endpoint_returns_the_pack(client, source: Path, monkeypatch):
    monkeypatch.setenv("RETICULA_MAP_SOURCE", str(source))
    r = client.post("/maps/extract", json={"bbox": list(BBOX)})
    assert r.status_code == 200
    assert r.headers["content-type"] == "application/vnd.pmtiles"
    assert int(r.headers["x-tile-count"]) > 10
    assert r.headers["x-map-source"] == "planet.pmtiles"
    assert Reader(MemorySource(r.content)).header()["max_zoom"] == WORLD_MAX_ZOOM


def test_endpoint_explains_a_missing_source(client, monkeypatch):
    monkeypatch.delenv("RETICULA_MAP_SOURCE", raising=False)
    r = client.post("/maps/extract", json={"bbox": list(BBOX)})
    assert r.status_code == 422
    assert "RETICULA_MAP_SOURCE" in r.json()["detail"]
    assert client.post("/maps/extract", json={"bbox": [27.0, -26.0, 28.0, -25.0]}).status_code == 422
