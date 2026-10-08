"""Fetch buildings or roads for a project area straight from OpenStreetMap (Overpass API).

The server is RETICULA_OVERPASS_URL (default the main public instance), from configuration only. Requests name the
app in the User-Agent, as the Overpass usage policy asks; projects are small, so one query per import is light.
"""

from __future__ import annotations

import os
import urllib.error
import urllib.parse
import urllib.request
from collections.abc import Callable
from typing import Any, Literal

from shapely.geometry import shape

from .. import __version__

OVERPASS_ENV = "RETICULA_OVERPASS_URL"
DEFAULT_OVERPASS = "https://overpass-api.de/api/interpreter"
MAX_SPAN_DEG = 0.25
TIMEOUT_S = 90

OsmKind = Literal["buildings", "roads"]

# Ways only: buildings as outlines, roads as lines. Roads leave out ones that are not there to route along.
SELECTORS: dict[str, str] = {
    "buildings": 'way["building"]({bbox});',
    "roads": 'way["highway"]["highway"!~"^(proposed|construction|abandoned|platform|raceway|corridor|elevator)$"]({bbox});',
}

Fetch = Callable[[str, str], bytes]


class OsmError(Exception):
    """The area is unsuitable, or OpenStreetMap could not be reached."""


def overpass_query(kind: OsmKind, area: dict[str, Any]) -> str:
    w, s, e, n = shape(area).bounds
    if e - w > MAX_SPAN_DEG or n - s > MAX_SPAN_DEG:
        raise OsmError(f"The project area is larger than {MAX_SPAN_DEG}° across; import a file instead")
    bbox = f"{s:.6f},{w:.6f},{n:.6f},{e:.6f}"  # Overpass order: south, west, north, east
    return f"[out:json][timeout:{TIMEOUT_S - 30}];({SELECTORS[kind].format(bbox=bbox)});out geom;"


def http_fetch(url: str, query: str) -> bytes:
    body = urllib.parse.urlencode({"data": query}).encode()
    req = urllib.request.Request(url, data=body, headers={"User-Agent": f"Reticula/{__version__} (electrical network planning)"})
    try:
        with urllib.request.urlopen(req, timeout=TIMEOUT_S) as r:  # the URL comes from configuration
            return r.read()
    except urllib.error.HTTPError as e:
        busy = " The server is busy; try again in a minute." if e.code in (429, 504) else ""
        raise OsmError(f"OpenStreetMap (Overpass) answered {e.code}.{busy}") from e
    except (urllib.error.URLError, TimeoutError) as e:
        raise OsmError(f"OpenStreetMap (Overpass) could not be reached: {getattr(e, 'reason', e)}") from e


def fetch_osm(kind: OsmKind, area: dict[str, Any], fetch: Fetch = http_fetch) -> bytes:
    url = os.environ.get(OVERPASS_ENV, "").strip() or DEFAULT_OVERPASS
    return fetch(url, overpass_query(kind, area))
