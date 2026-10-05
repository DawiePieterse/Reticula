"""Cuts a project's basemap out of a large PMTiles archive into a small one a tablet can keep offline.

The source is a PMTiles v3 vector basemap (for example a Protomaps build, or a South Africa extract of one) named by
RETICULA_MAP_SOURCE: an http(s) URL read with range requests, or a local path. It comes from configuration only, so a
caller cannot make the service fetch arbitrary URLs.
"""

from __future__ import annotations

import io
import math
import os
import urllib.error
import urllib.request
from collections.abc import Callable
from dataclasses import dataclass
from functools import lru_cache
from pathlib import Path

from pmtiles.reader import Reader
from pmtiles.tile import Compression, tileid_to_zxy, zxy_to_tileid
from pmtiles.writer import Writer
from pydantic import BaseModel, Field, model_validator

SOURCE_ENV = "RETICULA_MAP_SOURCE"

# Vector tiles overzoom cleanly, so zoom 15 data draws street level. A township of a few kilometres is a few MB.
DEFAULT_MAX_ZOOM = 15
# About 28 km at South African latitudes: larger areas make packs too big for a tablet.
MAX_SPAN_DEG = 0.25

GetBytes = Callable[[int, int], bytes]


class MapSourceError(Exception):
    """The source is not configured, cannot be read, or has nothing for the area."""


class ExtractRequest(BaseModel):
    bbox: tuple[float, float, float, float] = Field(description="min_lon, min_lat, max_lon, max_lat in degrees")
    min_zoom: int = Field(0, ge=0, le=16)
    max_zoom: int = Field(DEFAULT_MAX_ZOOM, ge=0, le=16)

    @model_validator(mode="after")
    def _check(self) -> ExtractRequest:
        w, s, e, n = self.bbox
        if not (-180 <= w < e <= 180 and -85 <= s < n <= 85):
            raise ValueError("bbox must be min_lon, min_lat, max_lon, max_lat within the map")
        if e - w > MAX_SPAN_DEG or n - s > MAX_SPAN_DEG:
            raise ValueError(f"The area is larger than {MAX_SPAN_DEG}° across; split the project or shrink its area")
        if self.min_zoom > self.max_zoom:
            raise ValueError("min_zoom must not exceed max_zoom")
        return self


@dataclass(frozen=True)
class Extract:
    data: bytes
    tiles: int
    max_zoom: int
    source: str


def tile_range(bbox: tuple[float, float, float, float], z: int) -> tuple[range, range]:
    """Columns and rows of the web-mercator tiles at zoom z that cover the box."""
    w, s, e, n = bbox
    size = 2**z

    def col(lon: float) -> int:
        return min(size - 1, max(0, math.floor((lon + 180) / 360 * size)))

    def row(lat: float) -> int:
        r = math.radians(lat)
        return min(size - 1, max(0, math.floor((1 - math.asinh(math.tan(r)) / math.pi) / 2 * size)))

    return range(col(w), col(e) + 1), range(row(n), row(s) + 1)


def extract(get_bytes: GetBytes, req: ExtractRequest, source_name: str) -> Extract:
    reader = Reader(get_bytes)
    try:
        src = reader.header()
        metadata = reader.metadata()
    except MapSourceError:
        raise
    except Exception as e:  # any parse failure means the source is not a PMTiles archive
        raise MapSourceError(f"The map source cannot be read as PMTiles: {e}") from e

    max_zoom = min(req.max_zoom, src["max_zoom"])
    min_zoom = max(req.min_zoom, src["min_zoom"])
    ids = sorted(
        zxy_to_tileid(z, x, y)
        for z in range(min_zoom, max_zoom + 1)
        for xs, ys in [tile_range(req.bbox, z)]
        for x in xs
        for y in ys
    )

    out = io.BytesIO()
    writer = Writer(out)
    count = 0
    for tid in ids:  # tile-id order keeps the archive clustered
        data = reader.get(*tileid_to_zxy(tid))
        if data:
            writer.write_tile(tid, bytes(data))
            count += 1
    if not count:
        raise MapSourceError("The map source has no tiles for this area.")

    w, s, e, n = req.bbox
    header = {
        "version": 3,
        "tile_type": src["tile_type"],
        "tile_compression": src["tile_compression"],
        "internal_compression": Compression.GZIP,
        "min_lon_e7": round(w * 1e7),
        "min_lat_e7": round(s * 1e7),
        "max_lon_e7": round(e * 1e7),
        "max_lat_e7": round(n * 1e7),
        "center_zoom": min(max_zoom, 14),
        "center_lon_e7": round((w + e) / 2 * 1e7),
        "center_lat_e7": round((s + n) / 2 * 1e7),
    }
    metadata = {**metadata, "reticula": {"bbox": list(req.bbox), "source": source_name}}
    writer.finalize(header, metadata)
    return Extract(out.getvalue(), count, max_zoom, source_name)


def http_source(url: str, timeout: float = 30) -> GetBytes:
    """Range reads over HTTP. Directories are read again and again while extracting, so reads are cached."""

    @lru_cache(maxsize=512)
    def get_bytes(offset: int, length: int) -> bytes:
        req = urllib.request.Request(url, headers={"Range": f"bytes={offset}-{offset + length - 1}"})
        try:
            with urllib.request.urlopen(req, timeout=timeout) as r:  # the URL comes from configuration
                if r.status != 206:
                    raise MapSourceError(f"The map source does not support range requests (HTTP {r.status}).")
                return r.read()
        except urllib.error.URLError as e:
            raise MapSourceError(f"The map source cannot be reached: {e.reason}") from e

    return get_bytes


def file_source(path: Path) -> GetBytes:
    """Range reads from a local archive, which may be far larger than memory."""

    @lru_cache(maxsize=512)
    def get_bytes(offset: int, length: int) -> bytes:
        with path.open("rb") as f:
            f.seek(offset)
            return f.read(length)

    return get_bytes


def configured_source() -> tuple[GetBytes, str]:
    spec = os.environ.get(SOURCE_ENV, "").strip()
    if not spec:
        raise MapSourceError(f"No offline map source is configured. Set {SOURCE_ENV} to a PMTiles basemap URL or file.")
    if spec.startswith(("http://", "https://")):
        return http_source(spec), spec.rsplit("/", 1)[-1]
    path = Path(spec)
    if not path.is_file():
        raise MapSourceError(f"The map source file {spec} does not exist.")
    return file_source(path), path.name


def extract_configured(req: ExtractRequest) -> Extract:
    get_bytes, name = configured_source()
    return extract(get_bytes, req, name)
