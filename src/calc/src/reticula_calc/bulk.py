"""Bulk supply (Phase 4): the whole network in pandapower, a load flow, an IEC 60909 fault study and the supply size.

**Hard stop.** Without the authority's connection point, its supply capacity and its three-phase fault level, nothing
here runs: a design without them is not fit to submit (plan 4.1).

**Model (4.2).** The connection point is an external grid of short-circuit power √3·U·I_k3 with its X/R; its
zero-sequence impedance follows from the single-phase fault level where given. MV sections and LV branches are lines
with their conductors' impedances (zero sequence as a multiple of the positive, from the rules file). Each sized
transformer joins its MV tap to its LV source, with its rated impedance, losses and off-load tap. Each transformer's
design demand is spread over its LV nodes in proportion to the connected kVA.

**Studies (4.3, 4.4).** A balanced Newton-Raphson load flow gives bus voltages and loadings at design demand. pandapower's
IEC 60909 short-circuit module gives the initial symmetrical current at every bus: three-phase at the maximum case,
single-phase at the minimum. Each conductor must withstand the maximum fault at its sending bus for the clearing time,
K·A/√t, and the MV fault level must stay within the switchgear rating.

**Supply sizing (4.5).** The Herman-Beta demand of every consumer in the project, with the growth allowance, sets the
notified maximum demand: the smallest supply step at or above it, which must be within the connection point's capacity.
"""

from __future__ import annotations

import math
import re
import warnings
from typing import Literal

from pydantic import BaseModel

from .design.demand import Demand, DemandModel
from .design.transformers import TransformerDesign
from .issues import Issue, issue
from .lv.loads import LoadAllocation
from .lv.network import LvNetwork
from .mv.network import ConnectionPointIn, MvAnalysis
from .rules import RuleSet
from .trace import Traced, traced

SUPPLY_ID = "bulk.supply.nmd.v1"
SUPPLY_FORMULA = "NMD = min{step ≥ S_project / (1 − growth)}; S_project = Herman-Beta demand of every consumer"
FAULT_ID = "bulk.fault.iec60909.v1"
FAULT_FORMULA = "I_k'' per IEC 60909-0 (pandapower shortcircuit): c·U_n / (√3·|Z_k|), with K_T on transformers"
WITHSTAND_FORMULA = "K·A/√t ≥ I_k''max at the sending bus"


class BusResult(BaseModel):
    id: str
    level: Literal["mv", "lv"]
    vm_pu: float | None
    ik3_max_ka: float | None
    ik1_min_ka: float | None


class BranchLoading(BaseModel):
    id: str
    level: Literal["mv", "lv"]
    conductor: str
    loading_pct: float | None
    ik_max_ka: float | None
    """Largest three-phase fault at the sending bus."""
    withstand_ka: float | None
    """K·A/√t for the clearing time."""
    passes: bool


class TransformerLoading(BaseModel):
    label: str | None
    loading_pct: float | None
    lv_vm_pu: float | None
    ik3_lv_ka: float | None


class Supply(BaseModel):
    demand_kva: float
    required_kva: float
    nmd_kva: float | None
    capacity_kva: float | None
    passes: bool


class BulkResult(BaseModel):
    clause: str
    index: str | None
    stopped: str | None = None
    """Why the studies did not run: the connection point data is incomplete."""
    converged: bool = False
    supply: Supply | None = None
    buses: list[BusResult] = []
    branches: list[BranchLoading] = []
    transformers: list[TransformerLoading] = []
    min_vm_pu: float | None = None
    max_vm_pu: float | None = None
    issues: list[Issue] = []
    supply_trace: Traced | None = None
    fault_trace: Traced | None = None
    placeholders: list[str] = []


def _supply(total: Demand, model: DemandModel, rules: RuleSet, cp: ConnectionPointIn | None) -> tuple[Supply, Traced]:
    b = rules.section("bulk", "eskom/0.8.0")
    growth = float(rules.section("transformers", "eskom/0.8.0")["growth_pct"]) / 100
    s = total.kva(model.conf, model.v_ph)
    need = s / (1 - growth)
    steps = sorted(float(x) for x in b["supply_steps_kva"])
    nmd = next((x for x in steps if x >= need - 1e-9), None)
    cap = cp.capacity_kva if cp else None
    ok = nmd is not None and cap is not None and nmd <= cap + 1e-9
    trace = traced(nmd or need, "kVA", formula_id=SUPPLY_ID, formula=SUPPLY_FORMULA, clause=b.get("clause", ""), rules_hash=rules.hash, inputs={
        "S_project": (round(s, 2), "kVA", f"{total.loads} consumers, Herman-Beta at {model.conf * 100:g} %"),
        "growth": (growth * 100, "%", f"rules {rules.ref} transformers (placeholder)"), "required": (round(need, 2), "kVA", "S / (1 − growth)"),
        "steps": (", ".join(f"{x:g}" for x in steps), "kVA", f"rules {rules.ref} bulk.supply_steps_kva (placeholder)"),
        "capacity": (cap if cap is not None else "not given", "kVA", "authority's connection point")})
    return Supply(demand_kva=round(s, 2), required_kva=round(need, 2), nmd_kva=nmd, capacity_kva=cap, passes=ok), trace


def study(rules: RuleSet, cp: ConnectionPointIn | None, model: DemandModel, total: Demand, mv_net: LvNetwork | None,
          mv: MvAnalysis | None, lv_net: LvNetwork, lv_conductors: dict[str, str], alloc: LoadAllocation,
          transformers: list[TransformerDesign], taps: dict[str, float]) -> BulkResult:
    b = rules.section("bulk", "eskom/0.8.0")
    clause = b.get("clause", "")
    supply, supply_trace = _supply(total, model, rules, cp)
    placeholders = ["supply steps, switchgear rating, clearing times and zero-sequence ratios (bulk section placeholders)"]
    missing = [] if cp else ["the connection point"]
    if cp:
        missing += [w for w, v in (("its supply capacity", cp.capacity_kva), ("its three-phase fault level", cp.fault_3ph_ka)) if v is None]
    if missing:
        reason = "The authority's " + ", ".join(missing) + (" is" if len(missing) == 1 else " are") + " missing, so the load flow and fault study did not run."
        return BulkResult(clause=clause, index=b.get("index"), stopped=reason, supply=supply, supply_trace=supply_trace,
                          issues=[Issue(severity="error", code="connection_point_incomplete", message=reason + " Enter the connection point data.")],
                          placeholders=placeholders)
    if mv_net is None or mv is None or not mv.taps:
        reason = "No MV route joins the connection point to the transformers, so the load flow and fault study did not run."
        return BulkResult(clause=clause, index=b.get("index"), stopped=reason, supply=supply, supply_trace=supply_trace,
                          issues=[Issue(severity="error", code="no_mv_network", message=reason + " Mark the MV routes.")], placeholders=placeholders)

    import pandapower as pp
    import pandapower.shortcircuit as sc

    v_kv = cp.voltage_kv or float(rules.data["voltage"].get("mv_nominal_kv", 11))
    v_lv = float(rules.data["voltage"]["lv_nominal_v"]) / 1000
    xr = cp.x_over_r or 10.0
    if cp.x_over_r is None:
        placeholders.append("connection point X/R taken as 10 (not given)")
    net = pp.create_empty_network(sn_mva=1.0)
    c_max = float(b["c_max_mv"])
    z1 = c_max * v_kv / (math.sqrt(3) * cp.fault_3ph_ka)
    if cp.fault_1ph_ka:
        z0 = 3 * c_max * v_kv / (math.sqrt(3) * cp.fault_1ph_ka) - 2 * z1
        x0x = max(z0 / z1, 0.1)
    else:
        x0x = 1.0
        placeholders.append("connection point zero-sequence impedance taken equal to the positive (single-phase fault level not given)")
    mv_bus: dict[str, int] = {}
    for n in mv_net.nodes:
        mv_bus[n.id] = pp.create_bus(net, vn_kv=v_kv, name=f"MV:{n.id}")
    source = next(n.id for n in mv_net.nodes if n.kind == "source")
    s_max = math.sqrt(3) * v_kv * cp.fault_3ph_ka
    s_min = math.sqrt(3) * v_kv * (cp.fault_3ph_min_ka or cp.fault_3ph_ka)
    pp.create_ext_grid(net, mv_bus[source], vm_pu=1.0, s_sc_max_mva=s_max, s_sc_min_mva=s_min, rx_max=1 / xr, rx_min=1 / xr,
                       x0x_max=x0x, r0x0_max=1 / xr, x0x_min=x0x, r0x0_min=1 / xr)
    zs = b["zero_sequence"]
    line_ids: list[tuple[int, str, str, str, object]] = []
    for br in mv_net.branches:
        code = mv.conductors.get(br.id)
        if code is None:
            continue
        c = rules.conductor(code)
        z = zs["mv_cable" if c.kind == "underground" else "mv_overhead"]
        lid = pp.create_line_from_parameters(
            net, mv_bus[br.from_node], mv_bus[br.to_node], max(br.length_m, 0.1) / 1000, c.r_ohm_per_km, c.x_ohm_per_km,
            c.c_nf_per_km or 0.0, c.rating_a / 1000, name=br.id, r0_ohm_per_km=c.r_ohm_per_km * float(z["r0_ratio"]),
            x0_ohm_per_km=c.x_ohm_per_km * float(z["x0_ratio"]), c0_nf_per_km=(c.c_nf_per_km or 0.0) * 0.6, endtemp_degree=80.0)
        line_ids.append((lid, "mv", br.id, code, br.from_node))

    lv_bus: dict[str, int] = {}
    for n in lv_net.nodes:
        lv_bus[n.id] = pp.create_bus(net, vn_kv=v_lv, name=f"LV:{n.id}")
    tap_node = {n.label: n.id for n in mv_net.nodes if n.kind == "tap"}
    # pandapower takes the vector group without its clock number, and the clock number as a phase shift.
    vg = re.fullmatch(r"([A-Za-z]+)(\d*)", str(b["transformer_vector_group"]))
    group, shift = (vg.group(1), int(vg.group(2) or 0) * 30) if vg else ("Dyn", 330)
    trafo_ids: list[tuple[int, TransformerDesign]] = []
    for t in transformers:
        hv = tap_node.get(t.label)
        if hv is None:
            continue
        z, xr_t = t.impedance_pct, t.x_over_r
        vkr = z / math.sqrt(1 + xr_t * xr_t)
        tap = taps.get(t.label or "", 0.0)
        tid = pp.create_transformer_from_parameters(
            net, mv_bus[hv], lv_bus[t.id], sn_mva=t.rating_kva / 1000, vn_hv_kv=v_kv * (1 - tap / 100), vn_lv_kv=v_lv,
            vkr_percent=vkr, vk_percent=z, pfe_kw=t.no_load_loss_w / 1000, i0_percent=0.5, shift_degree=shift,
            vector_group=group, vk0_percent=z, vkr0_percent=vkr, mag0_percent=100, mag0_rx=0, si0_hv_partial=0.9,
            name=t.label)
        trafo_ids.append((tid, t))
    zlv = zs["lv"]
    for br in lv_net.branches:
        if br.feeder is None and br.kind != "link":
            continue
        code = lv_conductors.get(br.id)
        if code is None:
            continue
        c = rules.conductor(code)
        r = c.r_at(float(rules.data["lv_design"]["conductor_temp_c"]), rules.data["lv_design"]["temperature_coefficients"])
        lid = pp.create_line_from_parameters(
            net, lv_bus[br.from_node], lv_bus[br.to_node], max(br.length_m, 0.1) / 1000, r, c.x_ohm_per_km, 0.0, c.rating_a / 1000,
            name=br.id, r0_ohm_per_km=r * float(zlv["r0_ratio"]), x0_ohm_per_km=c.x_ohm_per_km * float(zlv["x0_ratio"]), c0_nf_per_km=0.0,
            endtemp_degree=80.0)
        line_ids.append((lid, "lv", br.id, code, br.from_node))

    # Each transformer's design demand, spread over its nodes by connected kVA.
    pf = float(rules.data["lv_design"]["power_factor"])
    sin = math.sqrt(max(0.0, 1 - pf * pf))
    branches = {x.id: x for x in lv_net.branches}
    source_of = {f.id: f.source for f in lv_net.feeders}
    at_node: dict[str, dict[str, float]] = {}
    for a in alloc.allocations:
        s = source_of.get(a.feeder or "")
        br = branches.get(a.branch)
        if s is None or br is None:
            continue
        node = a.node or (br.to_node if a.offset_m > br.length_m / 2 else br.from_node)
        at_node.setdefault(s, {}).setdefault(node, 0.0)
        at_node[s][node] += a.kva
    for t in transformers:
        nodes_kva = at_node.get(t.id, {})
        tot = sum(nodes_kva.values())
        for nid, kva in nodes_kva.items():
            share = t.demand_kva * kva / tot / 1000 if tot else 0.0
            pp.create_load(net, lv_bus[nid], p_mw=share * pf, q_mvar=share * sin, name=nid)

    issues: list[Issue] = []
    converged = True
    with warnings.catch_warnings():
        warnings.simplefilter("ignore")
        try:
            pp.runpp(net, numba=False)
        except Exception as e:  # noqa: BLE001 - pandapower raises LoadflowNotConverged, numpy and its own errors
            converged = False
            issues.append(Issue(severity="error", code="loadflow_failed", message=f"The load flow did not converge: {e}"))
        lv_tol = 6 if abs(float(b["c_max_lv"]) - 1.05) < 1e-9 else 10
        sc.calc_sc(net, fault="3ph", case="max", lv_tol_percent=lv_tol, ip=False)
        ik3 = net.res_bus_sc["ikss_ka"].to_dict()
        try:
            sc.calc_sc(net, fault="1ph", case="min", lv_tol_percent=lv_tol, ip=False)
            ik1 = net.res_bus_sc["ikss_ka"].to_dict()
        except Exception as e:  # noqa: BLE001 - the zero-sequence model may be incomplete; report, do not fail the design
            ik1 = {}
            issues.append(Issue(severity="warning", code="no_1ph_fault", message=f"The single-phase fault study did not run: {e}"))

    vm = net.res_bus["vm_pu"].to_dict() if converged else {}
    buses = [BusResult(id=name, level="mv" if name.startswith("MV:") else "lv", vm_pu=_r(vm.get(i), 4), ik3_max_ka=_r(ik3.get(i), 4),
                       ik1_min_ka=_r(ik1.get(i), 4)) for i, name in net.bus["name"].items()]
    loading = net.res_line["loading_percent"].to_dict() if converged else {}
    out_branches: list[BranchLoading] = []
    weak: list[str] = []
    for lid, level, bid, code, from_node in line_ids:
        c = rules.conductor(code)
        bus = mv_bus[from_node] if level == "mv" else lv_bus[from_node]
        t = float(b["mv_clearing_s"] if level == "mv" else b["lv_clearing_s"])
        withstand = c.fault_k * c.size_mm2 / math.sqrt(t) if c.fault_k and c.size_mm2 else None
        ik = ik3.get(bus)
        ok = withstand is None or ik is None or ik <= withstand + 1e-9
        if not ok:
            weak.append(bid)
        out_branches.append(BranchLoading(id=bid, level=level, conductor=code, loading_pct=_r(loading.get(lid), 2), ik_max_ka=_r(ik, 4),
                                          withstand_ka=_r(withstand, 3), passes=ok and (loading.get(lid) or 0) <= 100 + 1e-6))
    tl = net.res_trafo["loading_percent"].to_dict() if converged else {}
    tx_out = [TransformerLoading(label=t.label, loading_pct=_r(tl.get(tid), 2), lv_vm_pu=_r(vm.get(lv_bus[t.id]), 4),
                                 ik3_lv_ka=_r(ik3.get(lv_bus[t.id]), 4)) for tid, t in trafo_ids]

    mv_max = max((ik3.get(i, 0.0) for i in mv_bus.values()), default=0.0)
    switchgear = float(b["mv_switchgear_ka"])
    if mv_max > switchgear + 1e-9:
        issues.append(issue("error", "switchgear_rating", f"The MV fault level of {mv_max:.2f} kA is above the switchgear rating of {switchgear:g} kA.", []))
    if weak:
        issues.append(issue("error", "fault_withstand", "These conductors cannot withstand the fault current at their sending end for the "
                            "clearing time. Use a larger conductor or faster protection.", weak))
    if not supply.passes:
        issues.append(issue("error", "supply_capacity", f"The notified maximum demand of {supply.nmd_kva or supply.required_kva:g} kVA is beyond "
                            f"the connection point's capacity of {supply.capacity_kva:g} kVA." if supply.capacity_kva is not None else
                            "The supply could not be sized.", []))
    rise = float(rules.data["voltage"].get("lv_max_rise_pct", 10)) / 100
    lv_vm = [x.vm_pu for x in buses if x.level == "lv" and x.vm_pu is not None]
    if lv_vm and (min(lv_vm) < 1 - rise - 1e-9 or max(lv_vm) > 1 + rise + 1e-9):
        issues.append(issue("error", "voltage_band", f"LV bus voltages at design demand leave the ±{rise * 100:g} % band.",
                            [x.id for x in buses if x.level == "lv" and x.vm_pu is not None and abs(x.vm_pu - 1) > rise + 1e-9]))
    worst_bus = max(mv_bus.values(), key=lambda i: ik3.get(i, 0.0))
    return BulkResult(
        clause=clause, index=b.get("index"), converged=converged, supply=supply, buses=buses, branches=out_branches, transformers=tx_out,
        min_vm_pu=_r(min(vm.values()), 4) if vm else None, max_vm_pu=_r(max(vm.values()), 4) if vm else None, issues=issues,
        supply_trace=supply_trace,
        fault_trace=traced(ik3.get(worst_bus, 0.0), "kA", formula_id=FAULT_ID, formula=FAULT_FORMULA, clause=clause, rules_hash=rules.hash, inputs={
            "bus": (net.bus.at[worst_bus, "name"], "", "largest MV fault"), "U_n": (v_kv, "kV", "connection point"),
            "I_k3_grid": (cp.fault_3ph_ka, "kA", "authority's connection point"), "X/R": (xr, "", "authority's connection point"),
            "c_max": (c_max, "", f"rules {rules.ref} bulk (IEC 60909-0 Table 1, to confirm)"),
            "switchgear": (switchgear, "kA", f"rules {rules.ref} bulk (placeholder)")}),
        placeholders=placeholders,
    )


def _r(v: float | None, n: int) -> float | None:
    if v is None or (isinstance(v, float) and math.isnan(v)):
        return None
    return round(float(v), n)
