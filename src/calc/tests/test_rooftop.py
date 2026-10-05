"""Rooftop-imagery signal (plan 1.3): synthetic imagery where roofs of each type look different."""

import io
import json
import math
import zipfile

import numpy as np
import pytest
import tifffile
from PIL import Image

from reticula_calc.geo.predict import BuildingInput, PredictRequest, Signal, predict
from reticula_calc.geo.rooftop import ImageryError, RoofBuilding, RooftopRequest, classify, read_geotiff
from reticula_calc.rules import load_rules
from reticula_calc.rules.loader import RulesError

RULES = load_rules("eskom/0.6.0")
LON0, LAT0 = 28.10, -25.52
PX = 0.000003  # degrees per pixel (about 0.3 m)
SIZE = 600
COLOURS = {"house": (0.55, 0.56, 0.58), "shop": (0.70, 0.25, 0.20), "school": (0.80, 0.80, 0.85)}


def world(seed: int = 1) -> tuple[np.ndarray, list[tuple[str, list[tuple[float, float]]]]]:
    """An image with a grid of roofs: grey houses, red shops, large pale schools, on a green-brown ground."""
    rng = np.random.default_rng(seed)
    img = np.empty((SIZE, SIZE, 3), dtype=np.float32)
    img[...] = (0.35, 0.40, 0.25)
    img += rng.normal(0, 0.03, img.shape).astype(np.float32)
    roofs = []
    types = ["house"] * 45 + ["shop"] * 20 + ["school"] * 10
    rng.shuffle(types)
    k = 0
    for gy in range(9):
        for gx in range(9):
            if k >= len(types):
                break
            t = types[k]
            k += 1
            w = 40 if t == "school" else 22 if t == "shop" else 18
            r0, c0 = 10 + gy * 65, 10 + gx * 65
            img[r0:r0 + w, c0:c0 + w] = np.array(COLOURS[t]) + rng.normal(0, 0.04, (w, w, 3))
            ring = [(LON0 + (c0 + dc) * PX, LAT0 - (r0 + dr) * PX) for dc, dr in ((1, 1), (w - 1, 1), (w - 1, w - 1), (1, w - 1), (1, 1))]
            roofs.append((t, ring))
    return np.clip(img, 0, 1), roofs


def geotiff(img: np.ndarray) -> bytes:
    keys = [1, 1, 0, 3, 1024, 0, 1, 2, 1025, 0, 1, 1, 2048, 0, 1, 4326]
    buf = io.BytesIO()
    tifffile.imwrite(buf, (img * 255).astype(np.uint8), photometric="rgb",
                     extratags=[(33550, "d", 3, (PX, PX, 0.0)), (33922, "d", 6, (0.0, 0.0, 0.0, LON0, LAT0, 0.0)), (34735, "H", len(keys), keys)])
    return buf.getvalue()


def buildings(roofs, confirmed: int, shuffle_labels: bool = False):
    labels = [t for t, _ in roofs]
    if shuffle_labels:
        labels = list(np.random.default_rng(7).permutation(labels))
    return [RoofBuilding(id=f"b{i}", footprint=[ring], confirmed_type=labels[i] if i < confirmed else None) for i, (_, ring) in enumerate(roofs)]


def request(bs, kind="geotiff"):
    return RooftopRequest(rules="eskom/0.6.0", imagery_kind=kind, imagery_label="NGI 2023 0.3 m", buildings=bs)


def test_learns_roof_types_and_signals_the_unconfirmed_buildings():
    img, roofs = world()
    r = classify(request(buildings(roofs, 55)), geotiff(img), RULES)
    assert r.model.used, r.model.reason
    assert r.model.accuracy >= 0.95 and r.model.trained_on == 55
    assert set(r.model.types) == {"house", "shop", "school"}
    truth = {f"b{i}": t for i, (t, _) in enumerate(roofs)}
    assert len(r.signals) == 20
    assert sum(s.signal.type == truth[s.id] for s in r.signals) >= 19
    assert all(0 < s.signal.confidence <= 0.85 and s.signal.source == "rooftop:NGI 2023 0.3 m" for s in r.signals)
    assert r.gsd_m == pytest.approx(0.3, rel=0.15)


def test_too_few_confirmed_buildings_or_too_little_accuracy_means_no_signal():
    img, roofs = world()
    few = classify(request(buildings(roofs, 12)), geotiff(img), RULES)
    assert not few.model.used and "at least 30" in few.model.reason and few.signals == []
    noise = classify(request(buildings(roofs, 60, shuffle_labels=True)), geotiff(img), RULES)
    assert not noise.model.used and "below the required 75%" in noise.model.reason and noise.signals == []


def test_types_with_too_few_examples_are_left_out():
    img, roofs = world()
    bs = buildings(roofs, 75)
    schools = [b for b in bs if b.confirmed_type == "school"]
    for b in schools[2:]:
        b.confirmed_type = None
    r = classify(request(bs), geotiff(img), RULES)
    assert r.model.left_out_types == ["school"]
    assert "school" not in r.model.types


def test_buildings_outside_the_imagery_are_counted_not_guessed():
    img, roofs = world()
    bs = buildings(roofs, 55)
    bs.append(RoofBuilding(id="far", footprint=[[(LON0 + 1, LAT0), (LON0 + 1.0001, LAT0), (LON0 + 1.0001, LAT0 + 0.0001), (LON0 + 1, LAT0)]]))
    r = classify(request(bs), geotiff(img), RULES)
    assert r.outside_imagery == 1 and all(s.id != "far" for s in r.signals)


def test_xyz_tiles_work_like_an_orthophoto():
    """The same world rendered as Web Mercator tiles (e.g. Google satellite under a licence allowing derived use)."""
    img, roofs = world()
    zoom = 19
    n = 2 ** zoom

    def tile_xy(lon, lat):
        lat_r = math.radians(lat)
        return (lon + 180) / 360 * n, (1 - math.log(math.tan(lat_r) + 1 / math.cos(lat_r)) / math.pi) / 2 * n

    gx0, gy0 = tile_xy(LON0, LAT0)
    gx1, gy1 = tile_xy(LON0 + SIZE * PX, LAT0 - SIZE * PX)
    buf = io.BytesIO()
    with zipfile.ZipFile(buf, "w") as z:
        for tx in range(int(gx0), int(gx1) + 1):
            for ty in range(int(gy0), int(gy1) + 1):
                tile = np.zeros((256, 256, 3), dtype=np.float32)
                cols = np.arange(256)
                lon = (tx + (cols + 0.5) / 256) / n * 360 - 180
                lat = np.degrees(np.arctan(np.sinh(np.pi * (1 - 2 * (ty + (cols + 0.5) / 256) / n))))
                c = np.clip(((lon - LON0) / PX).astype(int), 0, SIZE - 1)
                r = np.clip(((LAT0 - lat) / PX).astype(int), 0, SIZE - 1)
                tile[:, :] = img[r[:, None], c[None, :]]
                png = io.BytesIO()
                Image.fromarray((tile * 255).astype(np.uint8)).save(png, "PNG")
                z.writestr(f"{zoom}/{tx}/{ty}.png", png.getvalue())
    r = classify(request(buildings(roofs, 55), "tiles"), buf.getvalue(), RULES)
    assert r.model.used and r.model.accuracy >= 0.9, r.model.reason


def test_unreadable_imagery_and_old_rules_are_refused():
    with pytest.raises(ImageryError):
        read_geotiff(b"not a tiff", 10_000)
    buf = io.BytesIO()
    tifffile.imwrite(buf, np.zeros((10, 10, 3), dtype=np.uint8))
    with pytest.raises(ImageryError, match="georeferencing"):
        read_geotiff(buf.getvalue(), 10_000)
    img, roofs = world()
    with pytest.raises(RulesError):
        classify(request(buildings(roofs, 55)), geotiff(img), load_rules("eskom/0.5.0"))


def test_the_rooftop_signal_joins_the_other_signals():
    r = predict(PredictRequest(rules="eskom/0.6.0", buildings=[
        BuildingInput(id="a", tags={"building": "yes"}, extra_signals=[Signal(source="rooftop:NGI", type="shop", confidence=0.8)]),
        BuildingInput(id="b", tags={"building": "yes"}, extra_signals=[Signal(source="rooftop:NGI", type="castle", confidence=0.9)]),
    ]), RULES)
    a, b = r.predictions
    assert (a.type, a.source) == ("shop", "rooftop:NGI") and any(s.source == "osm:building=yes" for s in a.signals)
    assert b.type == "house"  # an unknown type is ignored


def test_endpoint(client):
    img, roofs = world()
    res = client.post("/predict/rooftop", files={"imagery": ("ortho.tif", geotiff(img), "image/tiff")},
                      data={"request": request(buildings(roofs, 55)).model_dump_json()})
    assert res.status_code == 200, res.text
    assert res.json()["model"]["used"] is True
    bad = client.post("/predict/rooftop", files={"imagery": ("x.tif", b"junk", "image/tiff")}, data={"request": json.dumps({"rules": "eskom/0.6.0"})})
    assert bad.status_code == 422
