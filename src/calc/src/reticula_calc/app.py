"""FastAPI surface. Thin: validates, loads rules, calls calcs."""

from __future__ import annotations

import json
from typing import Annotated, get_args

from fastapi import FastAPI, File, Form, HTTPException, Response, UploadFile
from pydantic import BaseModel, Field

from . import __version__
from .calcs.admd import (
    AdmdInputError,
    EstimateRequest,
    EstimateResult,
    GroupRequest,
    GroupResult,
    estimate,
    form_definition,
    group,
)
from .calcs.voltage_drop import VoltageDropRequest, VoltageDropResult, voltage_drop
from .geo import crs as crs_mod
from .geo.importers import ImportResult, Kind, UnreadableFileError, import_file
from .geo.osm import OsmError, OsmKind, fetch_osm
from .geo.predict import PredictRequest, PredictResponse, predict
from .logging_setup import configure_logging, log_requests
from .lv.network import BuildRequest, LvNetwork, build_network
from .maps.extract import ExtractRequest, MapSourceError, extract_configured
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


@app.post("/calc/lv/network")
def calc_lv_network(req: BuildRequest) -> LvNetwork:
    """Joins the LV routes and sites marked in the field into a node-branch network and checks that it is radial."""
    try:
        return build_network(req, load_rules(req.rules))
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
    if kind not in get_args(Kind):
        raise HTTPException(status_code=422, detail=f"kind must be one of: {', '.join(get_args(Kind))}")
    data = await file.read(MAX_IMPORT_BYTES + 1)
    if len(data) > MAX_IMPORT_BYTES:
        raise HTTPException(status_code=413, detail="File is larger than 50 MB")
    try:
        area_geojson = json.loads(area) if area else None
        return import_file(file.filename or "upload", data, kind, source_crs or None, layer or None, area_geojson)  # type: ignore[arg-type]
    except (UnreadableFileError, crs_mod.CrsError, ValueError) as e:
        raise HTTPException(status_code=422, detail=str(e)) from e


class OsmImportRequest(BaseModel):
    kind: OsmKind
    area: dict = Field(description="Project area as a GeoJSON Polygon")


@app.post("/geo/osm")
def geo_osm(req: OsmImportRequest) -> ImportResult:
    """Buildings or roads for the project area, fetched from OpenStreetMap and checked like an imported file."""
    try:
        data = fetch_osm(req.kind, req.area)
        return import_file("openstreetmap.json", data, req.kind, area=req.area)
    except (OsmError, UnreadableFileError) as e:
        raise HTTPException(status_code=422, detail=str(e)) from e


@app.post("/predict/building-types")
def predict_building_types(req: PredictRequest) -> PredictResponse:
    try:
        return predict(req, load_rules(req.rules))
    except RulesError as e:
        raise HTTPException(status_code=422, detail=str(e)) from e


@app.get("/calc/admd/form/{authority}/{version}")
def admd_form(authority: str, version: str) -> dict:
    try:
        return form_definition(load_rules(f"{authority}/{version}"))
    except RulesError as e:
        raise HTTPException(status_code=404, detail=str(e)) from e


@app.post("/calc/admd/estimate")
def admd_estimate(req: EstimateRequest) -> EstimateResult:
    try:
        return estimate(req, load_rules(req.rules))
    except (RulesError, AdmdInputError) as e:
        raise HTTPException(status_code=422, detail=str(e)) from e


@app.post("/calc/admd/group")
def admd_group(req: GroupRequest) -> GroupResult:
    try:
        return group(req, load_rules(req.rules))
    except (RulesError, AdmdInputError) as e:
        raise HTTPException(status_code=422, detail=str(e)) from e


@app.post("/maps/extract", response_class=Response, responses={200: {"content": {"application/vnd.pmtiles": {}}}})
def maps_extract(req: ExtractRequest) -> Response:
    """The basemap for an area as a PMTiles archive, for a tablet to keep offline."""
    try:
        pack = extract_configured(req)
    except MapSourceError as e:
        raise HTTPException(status_code=422, detail=str(e)) from e
    return Response(
        pack.data,
        media_type="application/vnd.pmtiles",
        headers={"X-Tile-Count": str(pack.tiles), "X-Max-Zoom": str(pack.max_zoom), "X-Map-Source": pack.source},
    )
