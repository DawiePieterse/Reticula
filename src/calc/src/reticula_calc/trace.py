"""Traceability record: every calculated value carries its inputs, formula and clause."""

from __future__ import annotations

from pydantic import BaseModel, Field


class TraceInput(BaseModel):
    name: str
    value: float | str | bool
    unit: str = ""
    source: str = ""


class Traced(BaseModel):
    """A single calculated value with full provenance."""

    value: float
    unit: str
    formula_id: str
    formula: str
    clause: str
    rules_hash: str
    inputs: list[TraceInput] = Field(default_factory=list)


def traced(
    value: float,
    unit: str,
    *,
    formula_id: str,
    formula: str,
    clause: str,
    rules_hash: str,
    inputs: dict[str, tuple[float | str | bool, str] | tuple[float | str | bool, str, str]],
) -> Traced:
    """Build a Traced record. `inputs` maps name -> (value, unit[, source])."""
    return Traced(
        value=value,
        unit=unit,
        formula_id=formula_id,
        formula=formula,
        clause=clause,
        rules_hash=rules_hash,
        inputs=[
            TraceInput(name=k, value=v[0], unit=v[1], source=v[2] if len(v) > 2 else "")
            for k, v in inputs.items()
        ],
    )
