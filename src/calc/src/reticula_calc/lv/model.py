"""The LV network model (plan 2.1): a radial tree from the transformer to every customer's point of supply.

Geometry is WGS84 for storage and display; lengths are metres measured on the ground. `graph()` gives the networkx
view used by the analysis. The model is plain JSON (pydantic) so the API can store and reload it unchanged.
"""

from __future__ import annotations

from typing import Literal

import networkx as nx
from pydantic import BaseModel, Field

NodeKind = Literal["source", "pole", "kiosk", "junction", "connection"]
Phase = Literal["R", "W", "B"]


class Node(BaseModel):
    id: str
    kind: NodeKind
    lon: float
    lat: float
    #: Pole type and stay chosen by the overhead checks; set after design.
    pole: str | None = None
    stay: bool = False
    stays: int = 0


class Branch(BaseModel):
    id: str
    from_id: str
    to_id: str
    kind: Literal["feeder", "service"]
    construction: Literal["overhead", "underground"]
    conductor: str
    length_m: float
    geometry: list[tuple[float, float]]
    #: The candidate route the branch was laid on (None for services and the link from the transformer).
    route_id: str | None = None
    crosses_road: bool = False


class Customer(BaseModel):
    """A load: one building's consumer(s) at a point of supply node."""

    id: str
    building_id: str
    node_id: str
    phases: list[Phase] = Field(min_length=1, max_length=3)
    kind: Literal["residential", "special"] = "residential"
    load_class: str | None = None
    special_kva: float | None = None
    inspected: bool = True
    erf: str | None = None


class LvNetwork(BaseModel):
    source_id: str
    nodes: list[Node]
    branches: list[Branch]
    customers: list[Customer]

    def node(self, node_id: str) -> Node:
        return next(n for n in self.nodes if n.id == node_id)

    def graph(self) -> nx.DiGraph:
        """Directed from the source; edges carry their branch."""
        g = nx.DiGraph()
        for n in self.nodes:
            g.add_node(n.id, node=n)
        for b in self.branches:
            g.add_edge(b.from_id, b.to_id, branch=b)
        return g

    def validate_radial(self) -> list[str]:
        """Problems that make the network unusable for a radial calculation (empty when fine)."""
        problems: list[str] = []
        g = self.graph()
        if self.source_id not in g:
            return ["the source node is missing"]
        if g.number_of_nodes() > 1 and not nx.is_tree(g.to_undirected()):
            problems.append("the network is not a single radial tree")
        unreachable = set(g.nodes) - set(nx.descendants(g, self.source_id)) - {self.source_id}
        if unreachable:
            problems.append(f"{len(unreachable)} nodes cannot be reached from the source")
        bad_in = [n for n in g.nodes if n != self.source_id and g.in_degree(n) != 1]
        if bad_in:
            problems.append(f"{len(bad_in)} nodes are not fed by exactly one branch")
        orphans = [c.id for c in self.customers if c.node_id not in g]
        if orphans:
            problems.append(f"{len(orphans)} customers are attached to missing nodes")
        return problems
