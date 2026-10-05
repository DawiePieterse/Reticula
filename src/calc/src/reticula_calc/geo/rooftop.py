"""Rooftop-imagery signal for the building-type predictor (plan 1.3).

Imagery is an orthophoto GeoTIFF (NGI, municipal or drone survey) or a zip of XYZ map tiles (`z/x/y.png|jpg`, Web
Mercator; e.g. Google satellite where the licence allows derived use). Each building's footprint is cut out of the
image and described by colour, texture and shape measures. A random forest is trained on the buildings of the same
project that were confirmed in the field, and its accuracy is measured by cross-validation on buildings it was not
trained on. Below the rules' accuracy the model is not used. Otherwise each unconfirmed building gets a signal whose
confidence is the model's probability times the cross-validated precision of the predicted type, capped by the rules.

This is a project-specific heuristic, not a standard: the signal joins the OSM-tag, zoning and footprint signals and
the inspector still confirms every building on site.
"""

from __future__ import annotations

import io
import math
import re
import zipfile
from collections.abc import Callable
from dataclasses import dataclass
from typing import Literal

import numpy as np
import shapely
from pydantic import BaseModel, Field
from pyproj import Transformer
from shapely.geometry import Polygon

from ..rules import RulesError, RuleSet
from .crs import _proj_for
from .predict import Signal

TILE_NAME = re.compile(r"(?:^|/)(\d+)/(\d+)/(\d+)\.(png|jpg|jpeg)$", re.IGNORECASE)
FEATURES = ("r", "g", "b", "r_sd", "g_sd", "b_sd", "hue_sin", "hue_cos", "sat", "val", "val_sd", "texture", "bright", "grey", "reddish",
            "log_area", "compactness", "elongation")


class ImageryError(ValueError):
    pass


class RoofBuilding(BaseModel):
    id: str
    #: Footprint rings, WGS84 lon/lat; the first is the outer ring.
    footprint: list[list[tuple[float, float]]] = Field(min_length=1)
    confirmed_type: Literal["house", "shop", "school", "other"] | None = None


class RooftopRequest(BaseModel):
    rules: str
    imagery_kind: Literal["geotiff", "tiles"]
    #: Shown with every signal, e.g. "NGI 2023 0.25 m" or "Google satellite".
    imagery_label: str = Field(min_length=1, max_length=80)
    buildings: list[RoofBuilding]


class TypeScore(BaseModel):
    precision: float
    recall: float
    examples: int


class ModelReport(BaseModel):
    used: bool
    reason: str
    trained_on: int
    types: dict[str, int]
    left_out_types: list[str]
    accuracy: float | None
    per_type: dict[str, TypeScore]
    min_accuracy: float


class RoofSignal(BaseModel):
    id: str
    signal: Signal


class RooftopResponse(BaseModel):
    rules_hash: str
    imagery_label: str
    width: int
    height: int
    gsd_m: float
    model: ModelReport
    signals: list[RoofSignal]
    outside_imagery: int
    method: str


@dataclass
class Raster:
    rgb: np.ndarray  # rows × cols × 3, 0..1
    to_pixel: Callable[[np.ndarray, np.ndarray], tuple[np.ndarray, np.ndarray]]  # lon, lat → col, row (pixel centres at .5)
    gsd_m: float


def _config(rules: RuleSet) -> dict:
    cfg = rules.data.get("building_prediction", {}).get("rooftop")
    if not cfg:
        raise RulesError(f"rules {rules.ref} have no building_prediction.rooftop section; use eskom/0.6.0 or later")
    return cfg


# ---------- imagery ----------

def _rgb(arr: np.ndarray) -> np.ndarray:
    if arr.ndim == 3 and arr.shape[0] in (3, 4) and arr.shape[-1] not in (3, 4):
        arr = np.moveaxis(arr, 0, -1)
    if arr.ndim != 3 or arr.shape[-1] < 3:
        raise ImageryError("The image is not a colour (RGB) image")
    rgb = arr[..., :3].astype(np.float32)
    top = 255.0 if arr.dtype == np.uint8 else 65535.0 if arr.dtype == np.uint16 else max(float(rgb.max()), 1.0)
    return np.clip(rgb / top, 0, 1)


def read_geotiff(data: bytes, max_pixels: int) -> Raster:
    import tifffile

    from .importers import _epsg_spec, _geokeys

    try:
        with tifffile.TiffFile(io.BytesIO(data)) as tif:
            page = tif.pages[0]
            if page.shape[0] * page.shape[1] > max_pixels:
                raise ImageryError(f"The orthophoto has {page.shape[0] * page.shape[1]:,} pixels; crop it to the project (at most {max_pixels:,})")
            tags = {t.code: t.value for t in page.tags.values()}
            arr = page.asarray()
    except (tifffile.TiffFileError, ValueError) as e:
        if isinstance(e, ImageryError):
            raise
        raise ImageryError(f"The orthophoto cannot be read: {e}") from e
    scale, tie = tags.get(33550), tags.get(33922)
    if not scale or not tie:
        raise ImageryError("The orthophoto has no georeferencing (ModelPixelScale and ModelTiepoint tags)")
    rgb = _rgb(arr)
    keys = _geokeys(tags.get(34735))
    epsg = keys.get(3072) or keys.get(2048)
    sx, sy = float(scale[0]), float(scale[1])
    i0, j0, x0, y0 = float(tie[0]), float(tie[1]), float(tie[3]), float(tie[4])
    if epsg and epsg != 32767:
        spec = _epsg_spec(int(epsg))
    elif abs(x0) <= 180 and abs(y0) <= 90:
        spec = "WGS84"
    else:
        raise ImageryError("The orthophoto's coordinate system is not stated in its GeoTIFF keys")
    to_crs = Transformer.from_crs("EPSG:4326", _proj_for(spec), always_xy=True)
    geographic = spec == "WGS84" or (epsg is not None and keys.get(1024) == 2)

    def to_pixel(lon: np.ndarray, lat: np.ndarray) -> tuple[np.ndarray, np.ndarray]:
        x, y = to_crs.transform(lon, lat)
        return (np.asarray(x) - x0) / sx + i0, (y0 - np.asarray(y)) / sy + j0

    gsd = sx * 111_320 * math.cos(math.radians(y0)) if geographic else sx
    return Raster(rgb=rgb, to_pixel=to_pixel, gsd_m=float(gsd))


def read_tiles(data: bytes, max_pixels: int) -> Raster:
    from PIL import Image

    try:
        z = zipfile.ZipFile(io.BytesIO(data))
    except zipfile.BadZipFile as e:
        raise ImageryError("The tile archive is not a zip file") from e
    tiles: dict[tuple[int, int], bytes] = {}
    zoom: int | None = None
    for name in z.namelist():
        m = TILE_NAME.search(name)
        if not m:
            continue
        tz, tx, ty = int(m.group(1)), int(m.group(2)), int(m.group(3))
        if zoom is not None and tz != zoom:
            raise ImageryError("The tile archive mixes zoom levels")
        zoom = tz
        tiles[(tx, ty)] = z.read(name)
    if not tiles or zoom is None:
        raise ImageryError("The tile archive holds no z/x/y.png or .jpg tiles")
    xs, ys = [k[0] for k in tiles], [k[1] for k in tiles]
    tx0, ty0 = min(xs), min(ys)
    first = Image.open(io.BytesIO(next(iter(tiles.values()))))
    size = first.size[0]
    width, height = (max(xs) - tx0 + 1) * size, (max(ys) - ty0 + 1) * size
    if width * height > max_pixels:
        raise ImageryError(f"The tiles cover {width * height:,} pixels; fetch a smaller area or zoom (at most {max_pixels:,})")
    rgb = np.full((height, width, 3), np.nan, dtype=np.float32)
    for (tx, ty), raw in tiles.items():
        img = np.asarray(Image.open(io.BytesIO(raw)).convert("RGB"), dtype=np.float32) / 255.0
        r, c = (ty - ty0) * size, (tx - tx0) * size
        rgb[r:r + size, c:c + size] = img
    n = 2.0 ** zoom

    def to_pixel(lon: np.ndarray, lat: np.ndarray) -> tuple[np.ndarray, np.ndarray]:
        lat_r = np.radians(np.clip(lat, -85.05, 85.05))
        gx = (np.asarray(lon) + 180.0) / 360.0 * n
        gy = (1.0 - np.log(np.tan(lat_r) + 1.0 / np.cos(lat_r)) / math.pi) / 2.0 * n
        return (gx - tx0) * size, (gy - ty0) * size

    lat_c = math.degrees(math.atan(math.sinh(math.pi * (1 - 2 * (ty0 + (max(ys) - ty0 + 1) / 2) / n))))
    gsd = 156_543.03392 * math.cos(math.radians(lat_c)) / n * 256 / size
    return Raster(rgb=rgb, to_pixel=to_pixel, gsd_m=float(gsd))


# ---------- features ----------

def _hsv(rgb: np.ndarray) -> tuple[np.ndarray, np.ndarray, np.ndarray]:
    mx, mn = rgb.max(axis=1), rgb.min(axis=1)
    d = mx - mn
    r, g, b = rgb[:, 0], rgb[:, 1], rgb[:, 2]
    h = np.zeros_like(mx)
    nz = d > 1e-6
    rm = nz & (mx == r)
    gm = nz & (mx == g) & ~rm
    bm = nz & ~rm & ~gm
    h[rm] = ((g[rm] - b[rm]) / d[rm]) % 6
    h[gm] = (b[gm] - r[gm]) / d[gm] + 2
    h[bm] = (r[bm] - g[bm]) / d[bm] + 4
    s = np.where(mx > 1e-6, d / np.maximum(mx, 1e-6), 0)
    return h * (math.pi / 3), s, mx


def features(raster: Raster, b: RoofBuilding) -> tuple[np.ndarray | None, float]:
    """Feature vector for one footprint and the share of it inside the imagery."""
    ring = np.asarray(b.footprint[0], dtype=float)
    cols, rows = raster.to_pixel(ring[:, 0], ring[:, 1])
    poly = Polygon(np.column_stack([cols, rows]))
    if not poly.is_valid or poly.area <= 0:
        poly = poly.buffer(0)
    if poly.is_empty:
        return None, 0.0
    h, w = raster.rgb.shape[:2]
    c0, r0, c1, r1 = poly.bounds
    ci = np.arange(max(math.floor(c0), 0), min(math.ceil(c1), w))
    ri = np.arange(max(math.floor(r0), 0), min(math.ceil(r1), h))
    if ci.size == 0 or ri.size == 0:
        return None, 0.0
    cc, rr = np.meshgrid(ci, ri)
    inside = shapely.contains_xy(poly, cc + 0.5, rr + 0.5)
    px = raster.rgb[rr[inside], cc[inside]]
    px = px[~np.isnan(px).any(axis=1)]
    coverage = px.shape[0] / max(poly.area, 1.0)
    if px.shape[0] < 4:
        return None, min(coverage, 1.0)
    hue, sat, val = _hsv(px)
    window = raster.rgb[ri[0]:ri[-1] + 1, ci[0]:ci[-1] + 1].mean(axis=2)
    gy, gx = np.gradient(np.nan_to_num(window))
    grad = np.hypot(gx, gy)[inside]
    area_px = poly.area
    perim = poly.length
    mrr = poly.minimum_rotated_rectangle
    edges = np.diff(np.asarray(mrr.exterior.coords), axis=0)
    lens = sorted(np.hypot(edges[:, 0], edges[:, 1])[:2])
    vec = np.array([
        px[:, 0].mean(), px[:, 1].mean(), px[:, 2].mean(), px[:, 0].std(), px[:, 1].std(), px[:, 2].std(),
        float(np.sin(hue).mean()), float(np.cos(hue).mean()), sat.mean(), val.mean(), val.std(), float(grad.mean()),
        float((val > 0.8).mean()), float((sat < 0.15).mean()),
        float(((px[:, 0] > px[:, 1] + 0.08) & (px[:, 0] > px[:, 2] + 0.08)).mean()),
        math.log(max(area_px * raster.gsd_m ** 2, 0.1)), 4 * math.pi * area_px / max(perim ** 2, 1e-9),
        (lens[1] / lens[0]) if lens and lens[0] > 0 else 1.0,
    ], dtype=float)
    return vec, min(coverage, 1.0)


# ---------- model ----------

def classify(req: RooftopRequest, data: bytes, rules: RuleSet) -> RooftopResponse:
    from sklearn.ensemble import RandomForestClassifier
    from sklearn.metrics import precision_recall_fscore_support
    from sklearn.model_selection import StratifiedKFold, cross_val_predict

    cfg = _config(rules)
    max_px = int(cfg.get("max_pixels", 120_000_000))
    raster = read_geotiff(data, max_px) if req.imagery_kind == "geotiff" else read_tiles(data, max_px)
    min_cov = float(cfg.get("min_coverage", 0.8))
    vecs: dict[str, np.ndarray] = {}
    outside = 0
    for b in req.buildings:
        v, cov = features(raster, b)
        if v is None or cov < min_cov:
            outside += 1
            continue
        vecs[b.id] = v

    labelled = [b for b in req.buildings if b.confirmed_type and b.id in vecs]
    counts: dict[str, int] = {}
    for b in labelled:
        counts[b.confirmed_type] = counts.get(b.confirmed_type, 0) + 1  # type: ignore[index]
    min_per = int(cfg["min_per_type"])
    kept = sorted(t for t, n in counts.items() if n >= min_per)
    left_out = sorted(t for t in counts if t not in kept)
    train = [b for b in labelled if b.confirmed_type in kept]
    min_acc = float(cfg["min_accuracy"])

    def report(used: bool, reason: str, acc: float | None = None, per: dict[str, TypeScore] | None = None) -> ModelReport:
        return ModelReport(used=used, reason=reason, trained_on=len(train), types={t: counts[t] for t in kept}, left_out_types=left_out,
                           accuracy=acc, per_type=per or {}, min_accuracy=min_acc)

    base = {"rules_hash": rules.hash, "imagery_label": req.imagery_label, "width": int(raster.rgb.shape[1]), "height": int(raster.rgb.shape[0]),
            "gsd_m": round(raster.gsd_m, 3), "outside_imagery": outside, "method": str(cfg.get("clause", ""))}
    if len(labelled) < int(cfg["min_confirmed"]):
        return RooftopResponse(**base, model=report(False, f"{len(labelled)} confirmed buildings inside the imagery; at least {cfg['min_confirmed']} are needed"),
                               signals=[])
    if len(kept) < 2:
        return RooftopResponse(**base, model=report(False, f"confirmed buildings cover fewer than two types with at least {min_per} examples each"),
                               signals=[])

    x = np.vstack([vecs[b.id] for b in train])
    y = np.array([b.confirmed_type for b in train])
    folds = max(2, min(int(cfg.get("folds", 5)), min(counts[t] for t in kept)))
    model = RandomForestClassifier(n_estimators=200, random_state=0, class_weight="balanced", min_samples_leaf=2)
    cv = cross_val_predict(model, x, y, cv=StratifiedKFold(n_splits=folds, shuffle=True, random_state=0))
    acc = float((cv == y).mean())
    prec, rec, _, sup = precision_recall_fscore_support(y, cv, labels=kept, zero_division=0)
    per = {t: TypeScore(precision=round(float(p), 3), recall=round(float(r), 3), examples=int(n)) for t, p, r, n in zip(kept, prec, rec, sup, strict=True)}
    if acc < min_acc:
        return RooftopResponse(**base, model=report(False, f"cross-validated accuracy {acc:.0%} is below the required {min_acc:.0%}", round(acc, 3), per), signals=[])

    model.fit(x, y)
    cap = float(cfg["max_confidence"])
    unlabelled = [b for b in req.buildings if not b.confirmed_type and b.id in vecs]
    signals: list[RoofSignal] = []
    if unlabelled:
        proba = model.predict_proba(np.vstack([vecs[b.id] for b in unlabelled]))
        for b, p in zip(unlabelled, proba, strict=True):
            k = int(np.argmax(p))
            t = str(model.classes_[k])
            conf = min(cap, float(p[k]) * per[t].precision)
            signals.append(RoofSignal(id=b.id, signal=Signal(source=f"rooftop:{req.imagery_label}", type=t, confidence=round(conf, 3))))
    return RooftopResponse(**base, model=report(True, f"cross-validated accuracy {acc:.0%} on {len(train)} confirmed buildings", round(acc, 3), per),
                           signals=signals)

