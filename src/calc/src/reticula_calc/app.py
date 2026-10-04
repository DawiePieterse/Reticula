"""FastAPI surface. Thin: validates, loads rules, calls calcs."""

from __future__ import annotations

from fastapi import FastAPI, HTTPException

from . import __version__
from .calcs.voltage_drop import VoltageDropRequest, VoltageDropResult, voltage_drop
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
