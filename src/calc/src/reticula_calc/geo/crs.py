"""Source coordinate systems for imported layouts, and conversion to WGS84 (EPSG:4326).

Supported specs:
  WGS84            lon/lat degrees (KML, GeoJSON, OSM).
  LO<nn>           South African Lo system, nn an odd meridian 15..33, in the usual CAD convention:
                   x = easting from the central meridian (= -Y), y = northing from the equator (= -X),
                   so southern-hemisphere y values are negative millions. Hartebeesthoek94 uses the
                   WGS84 ellipsoid, so this is a transverse Mercator with k=1 and no false origin.
  UTM34S..UTM36S   WGS84 / UTM south zones (EPSG:32734-32736).
"""

from __future__ import annotations

import re
from dataclasses import dataclass
from functools import lru_cache

from pyproj import Transformer

LO_MERIDIANS = tuple(range(15, 35, 2))
UTM_ZONES = (34, 35, 36)


class CrsError(ValueError):
    pass


def _proj_for(spec: str) -> str:
    s = spec.strip().upper()
    if s in ("WGS84", "EPSG:4326"):
        return "EPSG:4326"
    if m := re.fullmatch(r"LO(\d{2})", s):
        meridian = int(m.group(1))
        if meridian not in LO_MERIDIANS:
            raise CrsError(f"Lo meridian must be an odd number from 15 to 33, got {meridian}")
        return f"+proj=tmerc +lat_0=0 +lon_0={meridian} +k=1 +x_0=0 +y_0=0 +ellps=WGS84 +units=m +no_defs"
    if m := re.fullmatch(r"UTM(\d{2})S", s):
        zone = int(m.group(1))
        if zone not in UTM_ZONES:
            raise CrsError(f"UTM zone must be 34S, 35S or 36S, got {zone}S")
        return f"EPSG:{32700 + zone}"
    raise CrsError(f"Unknown coordinate system {spec!r}; use WGS84, LO15..LO33 or UTM34S..UTM36S")


def normalise(spec: str) -> str:
    """Canonical spelling, validating the spec."""
    _proj_for(spec)
    s = spec.strip().upper()
    return "WGS84" if s == "EPSG:4326" else s


@lru_cache(maxsize=32)
def to_wgs84(spec: str) -> Transformer:
    return Transformer.from_crs(_proj_for(spec), "EPSG:4326", always_xy=True)


def nearest_lo(lon: float) -> str:
    """Lo zone whose central meridian is closest to a longitude."""
    return f"LO{min(LO_MERIDIANS, key=lambda m: abs(m - lon))}"


@dataclass(frozen=True)
class Detection:
    spec: str | None
    reason: str


def detect(xs: list[float], ys: list[float], area_lon: float | None) -> Detection:
    """Guess the source system from coordinate ranges. Lo zones cannot be told apart from
    coordinates alone, so the zone nearest the project area is suggested."""
    if not xs:
        return Detection(None, "no coordinates to inspect")
    xmin, xmax, ymin, ymax = min(xs), max(xs), min(ys), max(ys)
    if -180 <= xmin and xmax <= 180 and -90 <= ymin and ymax <= 90:
        return Detection("WGS84", "coordinates look like longitude/latitude degrees")
    if -350_000 <= xmin and xmax <= 350_000 and -3_950_000 <= ymin and ymax <= -2_400_000:
        zone = nearest_lo(area_lon) if area_lon is not None else None
        if zone is None:
            return Detection(None, "coordinates look like Lo CAD coordinates; choose the Lo meridian")
        return Detection(zone, f"coordinates look like Lo CAD coordinates; {zone} is nearest the project area")
    if 100_000 <= xmin and xmax <= 900_000 and 6_000_000 <= ymin and ymax <= 7_700_000:
        return Detection(None, "coordinates look like UTM; choose the zone (34S, 35S or 36S)")
    return Detection(None, f"unrecognised coordinate range x {xmin:.0f}..{xmax:.0f}, y {ymin:.0f}..{ymax:.0f}")
