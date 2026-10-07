"""FastAPI surface. Thin: validates, loads rules, calls calcs."""

from __future__ import annotations

import json
from typing import Annotated, get_args

from fastapi import FastAPI, File, Form, HTTPException, Request, Response, UploadFile
from fastapi.responses import JSONResponse
from pydantic import BaseModel, Field

from . import __version__, documents
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
from .cost import RateLibrary, default_library
from .design.optimise import OptimiseRequest, OptimiseResult
from .design.optimise import optimise as optimise_design
from .design.run import Design, DesignRequest
from .design.run import run as run_design
from .documents import DocumentRequest, PackRequest
from .documents import common as _documents_common  # noqa: F401 - loads documents.common
from .documents import pack as _documents_pack  # noqa: F401 - loads documents.pack
from .geo import crs as crs_mod
from .geo.importers import ImportResult, Kind, UnreadableFileError, import_file
from .geo.osm import OsmError, OsmKind, fetch_osm
from .geo.predict import PredictRequest, PredictResponse, predict
from .logging_setup import configure_logging, log_requests
from .lv.analysis import AnalyseRequest, Analysis, analyse
from .lv.conductors import Library, library
from .lv.loads import AllocateRequest, LoadAllocation, allocate
from .lv.network import BuildRequest, LvNetwork, build_network
from .lv.placement import Placement, PlacementRequest, place
from .maps.extract import ExtractRequest, MapSourceError, extract_configured
from .rules import RulesError, list_rules, load_rules

configure_logging()
app = FastAPI(title="Reticula calc", version=__version__)
app.middleware("http")(log_requests)


@app.exception_handler(RulesError)
def rules_error(request: Request, e: RulesError) -> JSONResponse:
    """A rules file that is missing or unusable: not found when it is named in the path, unprocessable when in the body."""
    return JSONResponse(status_code=404 if request.method == "GET" else 422, content={"detail": str(e)})


@app.exception_handler(AdmdInputError)
def admd_input_error(request: Request, e: AdmdInputError) -> JSONResponse:
    return JSONResponse(status_code=422, content={"detail": str(e)})


@app.get("/health")
def health() -> dict[str, str]:
    return {"status": "ok", "service": "reticula-calc", "version": app.version}


@app.get("/rules")
def rules_index() -> list[str]:
    return list_rules()


@app.get("/rules/{authority}/{version}")
def rules_info(authority: str, version: str) -> dict[str, str]:
    rs = load_rules(f"{authority}/{version}")
    return {"ref": rs.ref, "hash": rs.hash, "effective_date": rs.effective_date}


@app.get("/rules/{authority}/{version}/conductors")
def rules_conductors(authority: str, version: str) -> Library:
    """The conductor library: ratings by installation, short-circuit constant, and which values are still placeholders."""
    return library(load_rules(f"{authority}/{version}"))


@app.post("/calc/lv/voltage-drop")
def calc_voltage_drop(req: VoltageDropRequest) -> VoltageDropResult:
    return voltage_drop(req, load_rules(req.rules))


@app.post("/calc/lv/network")
def calc_lv_network(req: BuildRequest) -> LvNetwork:
    """Joins the LV routes and sites marked in the field into a node-branch network and checks that it is radial."""
    return build_network(req, load_rules(req.rules))


@app.post("/calc/lv/loads")
def calc_lv_loads(req: AllocateRequest) -> LoadAllocation:
    """Connects each building's load to the LV network and spreads single-phase loads over the phases of their feeder."""
    return allocate(req, load_rules(req.rules))


@app.post("/calc/lv/placement")
def calc_lv_placement(req: PlacementRequest) -> Placement:
    """Pre-design: proposes transformer sites, the loads each feeds, LV routes along roads and an MV route, for the field to confirm."""
    return place(req, load_rules(req.rules))


@app.post("/calc/lv/analyse")
def calc_lv_analyse(req: AnalyseRequest) -> Analysis:
    """Voltage drop (Herman-Beta, per phase), thermal loading and fault level for the connected LV network."""
    return analyse(req, load_rules(req.rules))


@app.post("/calc/design/run")
def calc_design_run(req: DesignRequest) -> Design:
    """The whole design: LV network with poles or kiosks, loads, transformers, conductors, overhead line or cable de-rating,
    MV network, bulk studies and cost, with every check listed. `construction: compare` designs both and returns the
    preferred one with the other summarised."""
    return run_design(req, load_rules(req.rules))


@app.post("/calc/design/optimise")
def calc_design_optimise(req: OptimiseRequest) -> OptimiseResult:
    """Three options (lowest capital cost, lowest lifetime cost, most spare capacity under a cost ceiling) by local search
    and MILP siting from the same inputs, each a full checked design, with the compare rows."""
    return optimise_design(req, load_rules(req.design.rules))


@app.get("/rates/default")
def rates_default() -> RateLibrary:
    """The indicative rate library used when the API sends none."""
    return default_library()


MAX_IMPORT_BYTES = 50 * 1024 * 1024


@app.post("/geo/import")
async def geo_import(
    file: Annotated[UploadFile, File()],
    kind: Annotated[str, Form()],
    source_crs: Annotated[str | None, Form()] = None,
    layer: Annotated[str | None, Form()] = None,
    area: Annotated[str | None, Form(description="Project area as a GeoJSON Polygon")] = None,
    interval_m: Annotated[float | None, Form(description="Contour interval in metres (GeoTIFF only)")] = None,
) -> ImportResult:
    if kind not in get_args(Kind):
        raise HTTPException(status_code=422, detail=f"kind must be one of: {', '.join(get_args(Kind))}")
    data = await file.read(MAX_IMPORT_BYTES + 1)
    if len(data) > MAX_IMPORT_BYTES:
        raise HTTPException(status_code=413, detail="File is larger than 50 MB")
    try:
        area_geojson = json.loads(area) if area else None
        return import_file(file.filename or "upload", data, kind, source_crs or None, layer or None, area_geojson, interval_m)  # type: ignore[arg-type]
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
    return predict(req, load_rules(req.rules))


@app.get("/calc/admd/form/{authority}/{version}")
def admd_form(authority: str, version: str) -> dict:
    return form_definition(load_rules(f"{authority}/{version}"))


@app.post("/calc/admd/estimate")
def admd_estimate(req: EstimateRequest) -> EstimateResult:
    return estimate(req, load_rules(req.rules))


@app.post("/calc/admd/group")
def admd_group(req: GroupRequest) -> GroupResult:
    return group(req, load_rules(req.rules))


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


@app.post("/calc/documents/pack", response_class=Response, responses={200: {"content": {"application/zip": {}}}})
def calc_documents_pack(req: PackRequest) -> Response:
    """The submission pack: the documents given, the drawing and document registers and the authority checklist, zipped."""
    name = documents.common.filename(req.meta, "pack")
    return Response(documents.pack.render(req), media_type="application/zip", headers={"Content-Disposition": f'attachment; filename="{name}"'})


@app.post("/calc/documents/{kind}", response_class=Response)
def calc_document(kind: str, req: DocumentRequest) -> Response:
    """A design document: drawing_dxf, report_pdf, boq_xlsx, boq_pdf, load_schedule_xlsx, geojson, kml or shapefile_zip."""
    if kind not in documents.KINDS:
        raise HTTPException(status_code=404, detail=f"Unknown document kind '{kind}'; known: {', '.join(documents.KINDS)}.")
    if kind == "load_schedule_xlsx" and req.rows is None:
        raise HTTPException(status_code=422, detail="The load schedule needs its rows.")
    name = documents.common.filename(req.meta, kind)
    return Response(documents.render(kind, req), media_type=documents.KINDS[kind][1],
                    headers={"Content-Disposition": f'attachment; filename="{name}"'})
