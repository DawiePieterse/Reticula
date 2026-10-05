"""Load, validate and hash authority rules files."""

from __future__ import annotations

import datetime
import hashlib
import json
import os
from dataclasses import dataclass
from functools import lru_cache
from pathlib import Path
from typing import Any

import jsonschema
import yaml


class RulesError(Exception):
    pass


def rules_dir() -> Path:
    env = os.environ.get("RETICULA_RULES_DIR")
    if env:
        return Path(env)
    # repo default: <repo>/rules relative to this package
    return Path(__file__).resolve().parents[5] / "rules"


@lru_cache(maxsize=1)
def _schema() -> dict[str, Any]:
    with open(rules_dir() / "schema.json", encoding="utf-8") as f:
        return json.load(f)


@dataclass(frozen=True)
class Conductor:
    code: str
    kind: str
    r_ohm_per_km: float
    x_ohm_per_km: float
    rating_a: float
    clause: str
    description: str = ""
    material: str | None = None
    size_mm2: float | None = None
    cores: int | None = None
    uses: tuple[str, ...] = ()
    ratings_a: tuple[tuple[str, float], ...] = ()
    """Rating by installation (ground, pipe, air), as pairs so the dataclass stays hashable."""
    fault_k: float | None = None
    placeholder: tuple[str, ...] = ()
    rating_clause: str = ""
    index: str = ""


@dataclass(frozen=True)
class RuleSet:
    authority: str
    version: str
    hash: str
    effective_date: str
    data: dict[str, Any]

    @property
    def ref(self) -> str:
        return f"{self.authority}/{self.version}"

    def conductor(self, code: str) -> Conductor:
        for c in self.data["conductors"]:
            if c["code"] == code:
                return _conductor(c)
        raise RulesError(f"conductor {code!r} not in rules {self.ref}")

    def conductors(self) -> list[Conductor]:
        return [_conductor(c) for c in self.data["conductors"]]


def _conductor(c: dict[str, Any]) -> Conductor:
    return Conductor(
        code=c["code"],
        kind=c["kind"],
        r_ohm_per_km=float(c["r_ohm_per_km"]),
        x_ohm_per_km=float(c["x_ohm_per_km"]),
        rating_a=float(c["rating_a"]),
        clause=c.get("clause", ""),
        description=c.get("description", ""),
        material=c.get("material"),
        size_mm2=float(c["size_mm2"]) if "size_mm2" in c else None,
        cores=c.get("cores"),
        uses=tuple(c.get("uses", ())),
        ratings_a=tuple((k, float(v)) for k, v in c.get("ratings_a", {}).items()),
        fault_k=float(c["fault_k"]) if "fault_k" in c else None,
        placeholder=tuple(c.get("placeholder", ())),
        rating_clause=c.get("rating_clause", ""),
        index=c.get("index", ""),
    )


def _deep_merge(base: dict[str, Any], override: dict[str, Any]) -> dict[str, Any]:
    out = dict(base)
    for k, v in override.items():
        if isinstance(v, dict) and isinstance(out.get(k), dict):
            out[k] = _deep_merge(out[k], v)
        else:
            out[k] = v
    return out


def _read(ref: str) -> tuple[dict[str, Any], bytes]:
    try:
        authority, version = ref.split("/")
    except ValueError as e:
        raise RulesError(f"rules ref must be authority/version, got {ref!r}") from e
    path = rules_dir() / authority / f"{version}.yaml"
    if not path.is_file():
        raise RulesError(f"rules file not found: {path}")
    raw = path.read_bytes()
    data = yaml.safe_load(raw)
    if not isinstance(data, dict):
        raise RulesError(f"rules file {path} is not a mapping")
    return _normalise(data), raw


def _normalise(value: Any) -> Any:
    """YAML parses bare dates into date objects; the schema wants ISO strings."""
    if isinstance(value, dict):
        return {k: _normalise(v) for k, v in value.items()}
    if isinstance(value, list):
        return [_normalise(v) for v in value]
    if isinstance(value, (datetime.date, datetime.datetime)):
        return value.isoformat()
    return value


@lru_cache(maxsize=32)
def load_rules(ref: str) -> RuleSet:
    """Load `authority/version`, resolving `base` overrides, validating and hashing."""
    data, raw = _read(ref)
    digest = hashlib.sha256(raw)
    base_ref = data.get("base")
    if base_ref:
        base = load_rules(base_ref)
        digest.update(base.hash.encode())
        merged = _deep_merge(base.data, {k: v for k, v in data.items() if k != "base"})
        merged["base"] = base_ref
        data = merged
    try:
        jsonschema.validate(data, _schema(), format_checker=jsonschema.FormatChecker())
    except jsonschema.ValidationError as e:
        raise RulesError(f"rules {ref} invalid: {e.message} at {'/'.join(str(p) for p in e.path)}") from e
    return RuleSet(
        authority=data["authority"],
        version=data["version"],
        hash=digest.hexdigest()[:16],
        effective_date=str(data["effective_date"]),
        data=data,
    )


def list_rules() -> list[str]:
    root = rules_dir()
    out: list[str] = []
    for auth in sorted(p for p in root.iterdir() if p.is_dir()):
        for f in sorted(auth.glob("*.yaml")):
            out.append(f"{auth.name}/{f.stem}")
    return out
