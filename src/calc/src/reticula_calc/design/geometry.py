"""Shared geometry for the design stages: a metric frame for a network, and ground level from contours."""

from __future__ import annotations

import math
from collections.abc import Iterable

from pydantic import BaseModel
from shapely import STRtree
from shapely.geometry import LineString, Point
from shapely.ops import transform

from ..geo import crs as crs_mod


class ContourIn(BaseModel):
    elevation_m: float
    coordinates: list[tuple[float, float]]
    """Lon/lat along the contour."""


class Frame:
    """The Lo zone nearest some points, for work in metres."""

    def __init__(self, lonlats: Iterable[tuple[float, float]]):
        pts = list(lonlats)
        lon = sum(p[0] for p in pts) / len(pts) if pts else 27.0
        self.zone = crs_mod.nearest_lo(lon)
        self._fwd = crs_mod.from_wgs84(self.zone)
        self._inv = crs_mod.to_wgs84(self.zone)

    def xy(self, lonlat: tuple[float, float]) -> tuple[float, float]:
        return self._fwd.transform(*lonlat)

    def lonlat(self, xy: tuple[float, float]) -> tuple[float, float]:
        lon, lat = self._inv.transform(*xy)
        return (round(lon, 7), round(lat, 7))

    def project(self, g):
        return transform(self._fwd.transform, g)


def deviation_deg(a: tuple[float, float], b: tuple[float, float], c: tuple[float, float]) -> float:
    """How far the line a→b→c turns at b, in degrees (0 = straight on)."""
    h1 = math.atan2(b[1] - a[1], b[0] - a[0])
    h2 = math.atan2(c[1] - b[1], c[0] - b[0])
    d = abs(math.degrees(h2 - h1)) % 360
    return 360 - d if d > 180 else d


class Ground:
    """Ground level from contour lines: between the two nearest contours of different elevation, weighted by distance."""

    def __init__(self, contours: list[ContourIn], frame: Frame):
        self.frame = frame
        self.lines = [frame.project(LineString(c.coordinates)) for c in contours if len(c.coordinates) >= 2]
        self.levels = [c.elevation_m for c in contours if len(c.coordinates) >= 2]
        self.tree = STRtree(self.lines) if self.lines else None

    def __bool__(self) -> bool:
        return self.tree is not None

    def at(self, xy: tuple[float, float], search_m: float = 250.0) -> float | None:
        if self.tree is None:
            return None
        pt = Point(xy)
        idx = self.tree.query(pt, predicate="dwithin", distance=search_m)
        if not len(idx):
            idx = [int(self.tree.nearest(pt))]
        best: dict[float, float] = {}
        for i in idx:
            d = self.lines[int(i)].distance(pt)
            z = self.levels[int(i)]
            if z not in best or d < best[z]:
                best[z] = d
        near = sorted(best.items(), key=lambda kv: kv[1])[:2]
        if len(near) == 1 or near[0][1] < 1e-6:
            return near[0][0]
        (z1, d1), (z2, d2) = near
        return (z1 * d2 + z2 * d1) / (d1 + d2)
