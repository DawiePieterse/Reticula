"""LV conductor sizing (plans 2.3, 2.4): the smallest conductor per feeder that passes the voltage drop and loading checks.

Every feeder starts on the smallest feeder conductor of its construction (four-core ABC overhead, four-core cable
underground). The LV checks run; a feeder with an overloaded branch, a point over the drop limit or a fuse it cannot
take (plan 2.4 protection) steps up one size, and they run again, until every feeder passes or is on the largest
conductor. A source's link to its route (the LV tails to the board) carries all its feeders and is sized on its own,
by loading only. The engineer may fix a feeder's conductor; it is checked, not changed. Tapering a feeder is left to
plan 5.3's search.
"""

from __future__ import annotations

from collections.abc import Callable
from typing import Literal

from pydantic import BaseModel

from ..lv.analysis import AnalyseRequest, Analysis, LoadAt, SourceIn, analyse
from ..lv.network import LvNetwork
from ..rules import RuleSet
from ..rules.loader import Conductor


class SizingStep(BaseModel):
    group: str
    """A feeder id, or `link:<branch>` for a source's link."""
    conductor: str
    reason: str


class Sizing(BaseModel):
    conductors: dict[str, str]
    """Conductor per branch."""
    feeders: dict[str, str]
    """Conductor per feeder (and per link group)."""
    steps: list[SizingStep]
    runs: int


def default_conductor(rules: RuleSet, construction: Literal["overhead", "underground"]) -> str:
    if construction == "underground":
        return rules.section("underground", "eskom/0.8.0")["default_conductor"]
    return rules.section("lv_design", "eskom/0.6.0")["default_conductor"]


def library(rules: RuleSet, construction: Literal["overhead", "underground"]) -> list[Conductor]:
    """Three-phase feeder conductors of the construction, in the default conductor's material, smallest first."""
    material = rules.conductor(default_conductor(rules, construction)).material
    out = [c for c in rules.conductors() if c.kind == construction and "feeder" in c.uses and (c.cores or 4) >= 4
           and c.material == material]
    return sorted(out, key=lambda c: (c.size_mm2 or 0, c.code))


def size_lv(net: LvNetwork, loads: list[LoadAt], rules: RuleSet, construction: Literal["overhead", "underground"],
            sources: dict[str, SourceIn], fixed: dict[str, str] | None = None,
            ratings: Callable[[dict[str, str]], dict[str, float]] | None = None) -> tuple[Sizing, Analysis]:
    """Sizes the feeders. `ratings` gives de-rated branch ratings for a conductor assignment (underground)."""
    lib = library(rules, construction)
    if not lib:
        raise ValueError(f"rules {rules.ref} has no {construction} three-phase feeder conductors")
    fixed = fixed or {}
    groups: dict[str, list[str]] = {}
    for b in net.branches:
        if b.kind == "link":
            groups.setdefault(f"link:{b.id}", []).append(b.id)
        elif b.feeder:
            groups.setdefault(b.feeder, []).append(b.id)
    pick: dict[str, int] = {}
    for g in groups:
        code = fixed.get(g)
        pick[g] = next((i for i, c in enumerate(lib) if c.code == code), 0)
    steps: list[SizingStep] = [SizingStep(group=g, conductor=lib[i].code, reason="fixed by the engineer" if g in fixed else "smallest")
                               for g, i in sorted(pick.items())]
    branch_group = {bid: g for g, bids in groups.items() for bid in bids}

    def assignment() -> dict[str, str]:
        return {bid: lib[pick[g]].code for g, bids in groups.items() for bid in bids}

    runs = 0
    while True:
        conds = assignment()
        req = AnalyseRequest(rules=rules.ref, network=net, loads=loads, conductors=conds, sources=sources,
                             ratings=ratings(conds) if ratings else {})
        result = analyse(req, rules)
        runs += 1
        changed = False
        hot = {branch_group.get(b.id) for b in result.branches if not b.passes}
        for g in sorted(x for x in hot if x):
            if g not in fixed and pick[g] < len(lib) - 1:
                pick[g] += 1
                changed = True
                steps.append(SizingStep(group=g, conductor=lib[pick[g]].code, reason="overloaded"))
        weak = {f.feeder: f"voltage drop over {result.limit_pct:g} %" if f.max_drop_pct > result.limit_pct
                else "no fuse rating between the design current and the conductor's rating"
                if f.fuse_a is None or f.fuse_a > (f.conductor_rating_a or 0) else "fault current at the far end too low for the fuse"
                for f in result.feeders if f.max_drop_pct > result.limit_pct or f.protected is False}
        for g, reason in sorted(weak.items()):
            if g in groups and g not in fixed and g not in hot and pick[g] < len(lib) - 1:
                pick[g] += 1
                changed = True
                steps.append(SizingStep(group=g, conductor=lib[pick[g]].code, reason=reason))
        if not changed or runs > len(lib) * max(len(groups), 1) + 1:
            break
    return Sizing(conductors=assignment(), feeders={g: lib[i].code for g, i in pick.items()}, steps=steps, runs=runs), result
