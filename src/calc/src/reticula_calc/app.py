"""FastAPI surface. Thin: validates, loads rules, calls calcs."""

from __future__ import annotations

import json
from typing import Annotated

from fastapi import FastAPI, File, Form, HTTPException, UploadFile

from . import __version__
from .calcs.voltage_drop import VoltageDropRequest, VoltageDropResult, voltage_drop
from .geo import crs as crs_mod
from .geo.importers import ImportResult, UnreadableFileError, import_file
from .geo.predict import PredictRequest, PredictResponse, predict
from .logging_setup import configure_logging, log_requests
from .rules import RulesError, list_rules, load_rules

configure_logging()
app = FastAPI(title="Reticula calc", version=__version__)
app.middleware("http")(log_requests)


@app.get("/health")
def health() -> dict[str, str]:
    return {"status": "ok", "service": "reticula-calc", "version": app.version}


@app.get("/rules")
def rules_index() -> list[str]:
    return list_rules()


@app.get("/rules/{authority}/{version}")
def rules_info(authority: str, version: str) -> dict[str, str]:
    try:
        rs = load_rules(f"{authority}/{version}")
    except RulesError as e:
        raise HTTPException(status_code=404, detail=str(e)) from e
    return {"ref": rs.ref, "hash": rs.hash, "effective_date": rs.effective_date}


@app.post("/calc/lv/voltage-drop")
def calc_voltage_drop(req: VoltageDropRequest) -> VoltageDropResult:
    try:
        return voltage_drop(req, load_rules(req.rules))
    except RulesError as e:
        raise HTTPException(status_code=422, detail=str(e)) from e


MAX_IMPORT_BYTES = 50 * 1024 * 1024


@app.post("/geo/import")
async def geo_import(
    file: Annotated[UploadFile, File()],
    kind: Annotated[str, Form()],
    source_crs: Annotated[str | None, Form()] = None,
    layer: Annotated[str | None, Form()] = None,
    area: Annotated[str | None, Form(description="Project area as a GeoJSON Polygon")] = None,
) -> ImportResult:
    if kind not in ("stands", "buildings"):
        raise HTTPException(status_code=422, detail="kind must be 'stands' or 'buildings'")
    data = await file.read(MAX_IMPORT_BYTES + 1)
    if len(data) > MAX_IMPORT_BYTES:
        raise HTTPException(status_code=413, detail="File is larger than 50 MB")
    try:
        area_geojson = json.loads(area) if area else None
        return import_file(file.filename or "upload", data, kind, source_crs or None, layer or None, area_geojson)  # type: ignore[arg-type]
    except (UnreadableFileError, crs_mod.CrsError, ValueError) as e:
        raise HTTPException(status_code=422, detail=str(e)) from e


@app.post("/predict/building-types")
def predict_building_types(req: PredictRequest) -> PredictResponse:
    try:
        return predict(req, load_rules(req.rules))
    except RulesError as e:
        raise HTTPException(status_code=422, detail=str(e)) from e
