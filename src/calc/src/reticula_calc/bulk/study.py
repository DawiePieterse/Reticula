"""Bulk supply study (plans 4.2–4.5).

Model (4.2). pandapower network from the MV design: the authority's connection point as an external grid (its voltage,
maximum and minimum fault level and R/X), a bus per MV node and an MV line per branch (R and X from the rules; zero
sequence R0 = R + earth-return resistance, X0 = factor × X, unverified assumptions), and per transformer site a Dyn
transformer on its tap with the site's Herman-Beta design demand at its LV bus (power factor from the rules).

Load flow (4.3). Balanced Newton-Raphson: bus voltages, line and transformer loading, losses, and the supply's P, Q.

Faults (4.4). IEC 60909 with pandapower: maximum three-phase and minimum single-phase initial symmetrical currents at
every bus (LV tolerance 6 %, so c_max = 1.05 at LV).

Supply (4.5). The demand at the connection point (studied, including losses) against the authority's available
capacity; the notified maximum demand to apply for is that demand plus the rules' margin, rounded up to the rules'
step. The bulk feeder is the first MV branch from the connection point.
"""

from __future__ import annotations

import math
import warnings
from typing import Literal

from pydantic import BaseModel, Field

from ..lv.analysis import Check
from ..lv.library import library
from ..mv.network import MvNetwork
from ..mv.placement import mv_config
from ..rules import RuleSet
from ..rules.loader import RulesError


class ConnectionPoint(BaseModel):
    lon: float
    lat: float
    voltage_kv: float = Field(gt=0)
    available_capacity_kva: float = Field(gt=0)
    fault_mva_max: float = Field(gt=0)
    fault_mva_min: float | None = Field(default=None, gt=0)
    x_r: float | None = Field(default=None, gt=0)
    sending_voltage_pct: float | None = Field(default=None, gt=0)
    reference: str | None = None


class SiteLoad(BaseModel):
    site_id: str
    rating_kva: float
    z_pct: float
    x_r: float
    tap_pct: float | None
    design_kva: float


class BulkStudyRequest(BaseModel):
    rules: str
    connection_point: ConnectionPoint
    mv_network: MvNetwork
    sites: list[SiteLoad] = Field(min_length=1)


class BusResult(BaseModel):
    id: str
    kind: Literal["supply", "mv", "lv"]
    vn_kv: float
    v_pct: float
    ikss3_max_ka: float | None
    ikss1_min_ka: float | None


class LineResult(BaseModel):
    id: str
    conductor: str
    loading_pct: float
    current_a: float
    losses_kw: float


class TransformerResult(BaseModel):
    site_id: str
    loading_pct: float
    tap_pos: int
    lv_v_pct: float
    losses_kw: float


class BulkStudyResult(BaseModel):
    rules: str
    rules_hash: str
    buses: list[BusResult]
    lines: list[LineResult]
    transformers: list[TransformerResult]
    supply_kva: float
    supply_kw: float
    losses_kw: float
    available_capacity_kva: float
    notified_max_demand_kva: float
    bulk_feeder: LineResult | None
    checks: list[Check]
    passed: bool
    assumptions: list[str]
    unverified: list[str]


def _config(rules: RuleSet) -> dict:
    cfg = rules.data.get("bulk")
    if not cfg:
        raise RulesError(f"rules {rules.ref} have no bulk section; use eskom/0.4.0 or later")
    return cfg


def bulk_study(req: BulkStudyRequest, rules: RuleSet) -> BulkStudyResult:
    import pandapower as pp
    import pandapower.shortcircuit as sc

    cfg = _config(rules)
    mv = mv_config(rules)
    lib = library(rules)
    clause = cfg.get("clause", "")
    cp = req.connection_point
    pf = float(mv["power_factor"])
    sin = math.sqrt(1 - pf * pf)
    zs = cfg["zero_sequence"]
    assumptions = [
        f"Zero-sequence line impedance taken as R0 = R + {zs['r0_add_ohm_per_km']} ohm/km and X0 = {zs['x0_factor']} × X (no line data from the authority).",
        "Each transformer carries its site's Herman-Beta design demand as a balanced load; LV detail is in the LV design.",
    ]
    if cp.x_r is None:
        assumptions.append(f"Connection point R/X taken as {cfg.get('source_r_x', 0.1)} (not given by the authority).")
    if cp.fault_mva_min is None:
        assumptions.append("Minimum fault level at the connection point taken equal to the maximum (not given by the authority).")
    rx = 1 / cp.x_r if cp.x_r else float(cfg.get("source_r_x", 0.1))
    send = (cp.sending_voltage_pct or float(mv["sending_voltage_pct"])) / 100

    net = pp.create_empty_network()
    bus_of: dict[str, int] = {}
    for n in req.mv_network.nodes:
        bus_of[n.id] = pp.create_bus(net, vn_kv=cp.voltage_kv, name=n.id)
    pp.create_ext_grid(net, bus_of[req.mv_network.supply_id], vm_pu=send, s_sc_max_mva=cp.fault_mva_max, s_sc_min_mva=cp.fault_mva_min or cp.fault_mva_max,
                       rx_max=rx, rx_min=rx, x0x_max=zs.get("source_x0_x1", 1.0), r0x0_max=zs.get("source_r0_x0", 0.1),
                       x0x_min=zs.get("source_x0_x1", 1.0), r0x0_min=zs.get("source_r0_x0", 0.1))
    line_idx: dict[int, str] = {}
    for b in req.mv_network.branches:
        cable = lib.get(b.conductor)
        if cable is None:
            raise RulesError(f"MV conductor {b.conductor!r} is not in rules {rules.ref}")
        i = pp.create_line_from_parameters(
            net, bus_of[b.from_id], bus_of[b.to_id], length_km=max(b.length_m, 1.0) / 1000, r_ohm_per_km=cable.r_ohm_per_km, x_ohm_per_km=cable.x_ohm_per_km,
            c_nf_per_km=0.0, max_i_ka=cable.rating_a / 1000, r0_ohm_per_km=cable.r_ohm_per_km + float(zs["r0_add_ohm_per_km"]),
            x0_ohm_per_km=cable.x_ohm_per_km * float(zs["x0_factor"]), c0_nf_per_km=0.0, endtemp_degree=20, name=b.id)
        line_idx[i] = b.id
    trafo_site: dict[int, SiteLoad] = {}
    lv_bus: dict[str, int] = {}
    un_lv = float(rules.data["voltage"]["lv_nominal_v"]) / 1000
    for s in req.sites:
        node = f"SITE:{s.site_id}"
        if node not in bus_of:
            continue
        lv = pp.create_bus(net, vn_kv=un_lv, name=f"LV:{s.site_id}")
        lv_bus[s.site_id] = lv
        vkr = s.z_pct / math.sqrt(1 + s.x_r * s.x_r)
        tap_pos = round(-(s.tap_pct or 0) / 2.5)
        t = pp.create_transformer_from_parameters(
            net, bus_of[node], lv, sn_mva=s.rating_kva / 1000, vn_hv_kv=cp.voltage_kv, vn_lv_kv=un_lv, vkr_percent=vkr, vk_percent=s.z_pct,
            pfe_kw=0.0, i0_percent=0.0, vector_group="Dyn", vk0_percent=s.z_pct, vkr0_percent=vkr, mag0_percent=100, mag0_rx=0, si0_hv_partial=0.9,
            tap_side="hv", tap_neutral=0, tap_min=-2, tap_max=2, tap_step_percent=2.5, tap_pos=tap_pos, tap_changer_type="Ratio",
            name=s.site_id)
        trafo_site[t] = s
        pp.create_load(net, lv, p_mw=s.design_kva * pf / 1000, q_mvar=s.design_kva * sin / 1000, name=s.site_id)

    with warnings.catch_warnings():
        warnings.simplefilter("ignore")
        pp.runpp(net, algorithm="nr", calculate_voltage_angles=False)
        v_pct = {int(i): float(r.vm_pu) * 100 for i, r in net.res_bus.iterrows()}
        lines = [LineResult(id=line_idx[int(i)], conductor=next(b.conductor for b in req.mv_network.branches if b.id == line_idx[int(i)]),
                            loading_pct=round(float(r.loading_percent), 2), current_a=round(float(r.i_ka) * 1000, 2), losses_kw=round(float(r.pl_mw) * 1000, 3))
                 for i, r in net.res_line.iterrows()]
        trafos = [TransformerResult(site_id=trafo_site[int(i)].site_id, loading_pct=round(float(r.loading_percent), 2), tap_pos=int(net.trafo.at[int(i), "tap_pos"]),
                                    lv_v_pct=round(v_pct[lv_bus[trafo_site[int(i)].site_id]], 3), losses_kw=round(float(r.pl_mw) * 1000, 3))
                  for i, r in net.res_trafo.iterrows()]
        eg = net.res_ext_grid.iloc[0]
        supply_kw = float(eg.p_mw) * 1000
        supply_kva = math.hypot(float(eg.p_mw), float(eg.q_mvar)) * 1000
        losses = float(net.res_line.pl_mw.sum() + net.res_trafo.pl_mw.sum()) * 1000

        sc.calc_sc(net, case="max", fault="3ph", lv_tol_percent=6)
        i3 = {int(i): float(r.ikss_ka) for i, r in net.res_bus_sc.iterrows()}
        sc.calc_sc(net, case="min", fault="1ph", lv_tol_percent=6)
        i1 = {int(i): float(r.ikss_ka) for i, r in net.res_bus_sc.iterrows()}

    buses = []
    for i, r in net.bus.iterrows():
        idx = int(i)
        name = str(r["name"])
        kind = "supply" if name == req.mv_network.supply_id else "lv" if name.startswith("LV:") else "mv"
        buses.append(BusResult(id=name, kind=kind, vn_kv=float(r.vn_kv), v_pct=round(v_pct[idx], 3), ikss3_max_ka=round(i3.get(idx, float("nan")), 3),
                               ikss1_min_ka=round(i1.get(idx, float("nan")), 3)))

    checks: list[Check] = []
    band = float(cfg["mv_voltage_band_pct"])
    for b in buses:
        if b.kind == "lv":
            continue
        checks.append(Check(code="lf_mv_voltage", subject=b.id, passed=abs(b.v_pct - 100) <= band + 1e-9, value=b.v_pct, limit=100 - band, unit="%",
                            message=f"MV bus {b.id} at {b.v_pct:.2f} % in the load flow", clause=clause))
    for ln in lines:
        checks.append(Check(code="lf_line_loading", subject=ln.id, passed=ln.loading_pct <= 100, value=ln.loading_pct, limit=100, unit="%",
                            message=f"MV line {ln.id} ({ln.conductor}) loaded {ln.loading_pct:.1f} %", clause=lib[ln.conductor].clause))
    for t in trafos:
        checks.append(Check(code="lf_transformer_loading", subject=t.site_id, passed=t.loading_pct <= 100, value=t.loading_pct, limit=100, unit="%",
                            message=f"Transformer {t.site_id} loaded {t.loading_pct:.1f} % (tap position {t.tap_pos})", clause=mv.get("clause", "")))
    checks.append(Check(code="supply_capacity", subject="connection point", passed=supply_kva <= cp.available_capacity_kva, value=round(supply_kva, 1),
                        limit=cp.available_capacity_kva, unit="kVA",
                        message=f"Demand at the connection point {supply_kva:.0f} kVA (losses {losses:.1f} kW included) against {cp.available_capacity_kva:.0f} kVA available",
                        clause=clause))
    sw = float(cfg["mv_switchgear_fault_ka"])
    lv_max = float(rules.data.get("fault", {}).get("max_lv_terminal_fault_ka", 1e9))
    for b in buses:
        lim = lv_max if b.kind == "lv" else sw
        if b.ikss3_max_ka is not None and not math.isnan(b.ikss3_max_ka):
            checks.append(Check(code="sc_max", subject=b.id, passed=b.ikss3_max_ka <= lim, value=b.ikss3_max_ka, limit=lim, unit="kA",
                                message=f"Maximum three-phase fault at {b.id} is {b.ikss3_max_ka:.2f} kA", clause=clause))

    step = float(cfg["nmd_step_kva"])
    nmd = math.ceil(supply_kva * (1 + float(cfg.get("nmd_margin_pct", 0)) / 100) / step - 1e-9) * step
    first = next((b for b in req.mv_network.branches if b.from_id == req.mv_network.supply_id), None)
    bulk_feeder = next((ln for ln in lines if first and ln.id == first.id), None)
    unverified = sorted({x for x, sec in (("bulk", cfg), ("mv_design", mv)) if sec.get("status") == "unverified"})
    return BulkStudyResult(rules=rules.ref, rules_hash=rules.hash, buses=buses, lines=lines, transformers=trafos, supply_kva=round(supply_kva, 2),
                           supply_kw=round(supply_kw, 2), losses_kw=round(losses, 3), available_capacity_kva=cp.available_capacity_kva,
                           notified_max_demand_kva=nmd, bulk_feeder=bulk_feeder, checks=checks, passed=all(c.passed for c in checks),
                           assumptions=assumptions, unverified=unverified)
