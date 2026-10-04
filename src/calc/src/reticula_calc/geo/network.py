"""Checks for the authority's existing network data: every asset needs a known type and the fields a design relies on.

Field names are matched case-insensitively against common aliases, so exports from different GIS systems import
without renaming columns. Values are normalised onto canonical keys in each feature's attributes.
"""

from __future__ import annotations

from dataclasses import dataclass, field
from typing import TYPE_CHECKING, Any

from shapely.geometry import shape

if TYPE_CHECKING:
    from .importers import ImportedFeature

MAX_SAMPLES = 10

ASSET_TYPES: dict[str, str] = {
    # canonical: geometry
    "mv_line": "line", "lv_line": "line", "transformer": "point", "minisub": "point", "pole": "point",
    "switch": "point", "substation": "point", "connection_point": "point",
}
TYPE_ALIASES: dict[str, str] = {
    "mv": "mv_line", "mv_line": "mv_line", "mvline": "mv_line", "mv line": "mv_line", "mv_cable": "mv_line", "mv_overhead": "mv_line",
    "11kv_line": "mv_line", "22kv_line": "mv_line",
    "lv": "lv_line", "lv_line": "lv_line", "lvline": "lv_line", "lv line": "lv_line", "lv_cable": "lv_line", "lv_overhead": "lv_line",
    "transformer": "transformer", "tx": "transformer", "trf": "transformer", "pole_mounted_transformer": "transformer",
    "minisub": "minisub", "mini-sub": "minisub", "mini_sub": "minisub", "mini substation": "minisub", "miniature_substation": "minisub",
    "pole": "pole", "structure": "pole",
    "switch": "switch", "isolator": "switch", "recloser": "switch", "ring_main_unit": "switch", "rmu": "switch",
    "substation": "substation", "sub": "substation",
    "connection_point": "connection_point", "point_of_supply": "connection_point", "pos": "connection_point", "supply_point": "connection_point",
}
FIELDS: dict[str, tuple[str, ...]] = {
    "asset_type": ("asset_type", "assettype", "asset", "type", "feature_type", "class"),
    "asset_id": ("asset_id", "assetid", "id", "asset_no", "number", "tag"),
    "voltage_kv": ("voltage_kv", "voltage", "kv", "nominal_kv", "volt_kv"),
    "conductor": ("conductor", "conductor_type", "cable", "cable_type", "cond"),
    "rating_kva": ("rating_kva", "kva", "rating", "capacity_kva", "size_kva"),
    "status": ("status", "state", "condition"),
    "name": ("name", "label", "description"),
    "capacity_kva": ("available_capacity_kva", "capacity_available_kva", "spare_kva"),
    "fault_level_ka": ("fault_level_ka", "fault_ka", "fault_level"),
}
REQUIRED: dict[str, tuple[str, ...]] = {
    "mv_line": ("voltage_kv", "conductor"),
    "lv_line": ("conductor",),
    "transformer": ("rating_kva", "voltage_kv"),
    "minisub": ("rating_kva", "voltage_kv"),
    "pole": (),
    "switch": ("voltage_kv",),
    "substation": ("voltage_kv",),
    "connection_point": ("voltage_kv",),
}
NUMERIC = ("voltage_kv", "rating_kva", "capacity_kva", "fault_level_ka")


@dataclass
class NetworkCheck:
    features: list[ImportedFeature]
    issues: list[dict[str, Any]] = field(default_factory=list)


def _lookup(attrs: dict[str, Any], aliases: tuple[str, ...]) -> Any:
    lower = {str(k).lower().strip(): v for k, v in attrs.items()}
    for a in aliases:
        v = lower.get(a)
        if v not in (None, ""):
            return v
    return None


def _num(v: Any) -> float | None:
    try:
        return float(str(v).lower().replace(",", ".").removesuffix("kva").removesuffix("kv").removesuffix("ka").strip())
    except (TypeError, ValueError):
        return None


def check_network(features: list[ImportedFeature]) -> NetworkCheck:
    unknown: list[str] = []
    missing: dict[str, list[str]] = {}
    mismatched: list[str] = []
    not_numeric: list[str] = []
    for f in features:
        raw = {k: _lookup(f.attributes, aliases) for k, aliases in FIELDS.items()}
        layer_type = f.attributes.get("layer") or f.attributes.get("block")
        type_text = str(raw["asset_type"] or layer_type or "").lower().strip()
        asset = TYPE_ALIASES.get(type_text) or TYPE_ALIASES.get(type_text.replace(" ", "_"))
        if asset is None:
            unknown.append(f"{f.ref}: {type_text or 'no type'}")
            continue
        geom_kind = "line" if shape(f.geometry).geom_type == "LineString" else "point"
        if ASSET_TYPES[asset] != geom_kind:
            mismatched.append(f"{f.ref}: {asset} drawn as a {geom_kind}")
        norm: dict[str, Any] = {"asset_type": asset}
        for key in FIELDS:
            if key == "asset_type" or raw[key] in (None, ""):
                continue
            if key in NUMERIC:
                n = _num(raw[key])
                if n is None:
                    not_numeric.append(f"{f.ref}: {key} = {raw[key]}")
                    continue
                norm[key] = n
            else:
                norm[key] = str(raw[key]).strip()
        for req in REQUIRED[asset]:
            if req not in norm:
                missing.setdefault(req, []).append(f.ref)
        f.subtype = asset
        f.name = norm.get("name") or f.name
        f.attributes = {**norm, "source_fields": {k: v for k, v in f.attributes.items() if isinstance(v, (str, int, float, bool))}}

    issues: list[dict[str, Any]] = []
    if unknown:
        issues.append({"severity": "error", "code": "network_type_unknown",
                       "message": f"Some assets have no recognised type. Use asset_type with one of: {', '.join(ASSET_TYPES)}.",
                       "count": len(unknown), "samples": unknown[:MAX_SAMPLES]})
    for req, refs in sorted(missing.items()):
        issues.append({"severity": "error", "code": "network_field_missing",
                       "message": f"Some assets are missing {req}, which their type requires.", "count": len(refs), "samples": refs[:MAX_SAMPLES]})
    if mismatched:
        issues.append({"severity": "error", "code": "network_geometry_mismatch",
                       "message": "Some assets have the wrong shape for their type: lines must be drawn as lines and equipment as points.",
                       "count": len(mismatched), "samples": mismatched[:MAX_SAMPLES]})
    if not_numeric:
        issues.append({"severity": "error", "code": "network_value_invalid",
                       "message": "Some numeric fields hold text that is not a number.", "count": len(not_numeric), "samples": not_numeric[:MAX_SAMPLES]})
    return NetworkCheck(features, issues)
