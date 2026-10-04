"""Conductor sizing for an LV network (plan 2.4, "conductor sizing").

Every feeder starts on the smallest conductor the rules allow for its construction. Then, until every check passes
or nothing can be made larger:

1. a branch over its thermal rating moves up one size;
2. otherwise, for the worst voltage drop, the branch on the path to it that contributes most (design current ×
   impedance per size step) moves up one size;

and conductors never get smaller towards the transformer (each branch is at least as large as any below it), so a
feeder tapers outwards as built in practice. Services keep the rules' service conductor.
"""

from __future__ import annotations

import networkx as nx
from pydantic import BaseModel

from ..rules import RuleSet
from .analysis import Analysis, analyse
from .library import Construction, feeder_options, lv_config
from .model import LvNetwork

MAX_ROUNDS = 400
SIZING_CODES = ("thermal", "feeder_vdrop", "supply_vdrop", "service_vdrop", "min_fault", "fuse_protection")


class SizingResult(BaseModel):
    network: LvNetwork
    analysis: Analysis
    rounds: int
    converged: bool


def size_network(network: LvNetwork, rules: RuleSet, construction: Construction, transformer_kva: float | None = None,
                 site: dict[str, float] | None = None, source: dict[str, float] | None = None) -> SizingResult:
    options = [c.code for c in feeder_options(rules, construction)]
    rank = {code: i for i, code in enumerate(options)}
    cfg = lv_config(rules)
    service = str(cfg["service_conductor"])
    service_options = list(cfg.get("service_conductors", [service]))
    s_rank = {code: i for i, code in enumerate(service_options)}
    r_of = {o.code: o.r_ohm_per_km for o in feeder_options(rules, construction)}
    net = network.model_copy(deep=True)
    for b in net.branches:
        b.conductor = service if b.kind == "service" else options[0]
        b.construction = construction
    g = net.graph()
    by_id = {b.id: b for b in net.branches}
    parent_branch = {b.to_id: b for b in net.branches}
    # Customer id -> its service branch; a thermal failure names the branch id directly.
    service_of = {cu.id: parent_branch[cu.node_id] for cu in net.customers}
    service_of.update({b.id: b for b in net.branches if b.kind == "service"})

    def path_to(node: str) -> list[str]:
        ids = []
        while node in parent_branch:
            b = parent_branch[node]
            ids.append(b.id)
            node = b.from_id
        return ids

    def taper() -> None:
        for n in reversed(list(nx.topological_sort(g))):
            b = parent_branch.get(n)
            if b is None or b.kind != "feeder":
                continue
            below = [g.edges[n, ch]["branch"] for ch in g.successors(n) if g.edges[n, ch]["branch"].kind == "feeder"]
            biggest = max((rank[x.conductor] for x in below), default=0)
            if rank[b.conductor] < biggest:
                b.conductor = options[biggest]

    a = analyse(net, rules, transformer_kva, site, source)
    for rounds in range(1, MAX_ROUNDS + 1):
        failing = [c for c in a.checks if not c.passed and c.code in SIZING_CODES]
        if not failing:
            return SizingResult(network=net, analysis=a, rounds=rounds - 1, converged=True)
        # An overloaded feeder branch, or a feeder whose fuse would be too big for its first conductor, moves up a size.
        thermal = [c for c in failing if c.code in ("thermal", "fuse_protection") and by_id[c.subject].kind == "feeder"
                   and rank[by_id[c.subject].conductor] < len(options) - 1]
        services = {service_of[c.subject].id: service_of[c.subject] for c in failing
                    if c.code == "service_vdrop" or (c.code == "thermal" and by_id[c.subject].kind == "service")}
        services = [b for b in services.values() if s_rank.get(b.conductor, len(service_options)) < len(service_options) - 1]
        changed = False
        if thermal or services:
            for bid in dict.fromkeys(c.subject for c in thermal):
                b = by_id[bid]
                b.conductor = options[rank[b.conductor] + 1]
            for b in services:
                b.conductor = service_options[s_rank[b.conductor] + 1]
            changed = True
        else:
            worst = max((c for c in failing if c.code not in ("thermal", "service_vdrop", "fuse_protection")),
                        key=lambda c: (c.value / c.limit) if c.code != "min_fault" else (c.limit / max(c.value, 1)), default=None)
            if worst is not None:
                node = next(cu.node_id for cu in net.customers if cu.id == worst.subject) if worst.code == "supply_vdrop" else worst.subject
                current = {br.id: br.design_current_a for br in a.branches}
                candidates = [by_id[i] for i in path_to(node) if by_id[i].kind == "feeder" and rank[by_id[i].conductor] < len(options) - 1]
                if candidates:
                    best = max(candidates, key=lambda b, cur=current: cur[b.id] * b.length_m * (r_of[b.conductor] - r_of[options[rank[b.conductor] + 1]]))
                    best.conductor = options[rank[best.conductor] + 1]
                    changed = True
        if not changed:
            return SizingResult(network=net, analysis=a, rounds=rounds, converged=False)
        taper()
        a = analyse(net, rules, transformer_kva, site, source)
    return SizingResult(network=net, analysis=a, rounds=MAX_ROUNDS, converged=False)
