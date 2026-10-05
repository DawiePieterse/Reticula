# ADR 0006 – LV network model

Date: 2026-10-05 · Status: accepted

## Decision

A. The LV network is built from what was marked in the field (plan 1.5). LV routes are lines, and transformer, mini-sub and pole sites are points. MV routes are left for Phase 3.
B. The calc service builds it (`POST /calc/lv/network`) with shapely and networkx, in metres in the Lo zone nearest the routes. Lengths are geodesic on WGS84, the same as for imported layers.
C. Routes drawn on a tablet seldom meet exactly, so they are joined within tolerances from the rules file's `lv_network` section (from `eskom/0.3.0`; index id RETICULA-LVMODEL). The tolerances are Reticula's own, not values from a standard, and are tunable per authority:
   - **Route ends:** ends within the join distance (2 m) become one node. An end that close to another route tees onto it. An overshoot past a route folds back onto it.
   - **Crossings:** routes that cross are joined where they cross.
   - **Sources:** a transformer or mini-sub within reach of a route (30 m) feeds it by a link to the nearest point on the route. That point is the source's LV board.
   - **Poles:** a pole site within reach of a route (5 m) becomes a node on it, splitting the route into spans. A pole within the join distance of an existing node becomes that node. A pole marked at a transformer is that transformer's pole (pole-mounted).
D. LV networks run radially. Each connected part should have exactly one source and no loops. A part that breaks this is reported, not fixed, because where the open point goes is the engineer's call:
   - **Errors:** a loop (naming a branch that closes it), and sources tied together.
   - **Warnings:** parts with no source, sources and poles out of reach, routes drawn on top of each other, and route ends that stop just short of another route (within 6 m). A gap is reported once, and only while an end is left dangling.
E. A radial part with one source is oriented away from the source. Each node gets its distance along the network. Each route leaving the board starts a feeder, numbered clockwise from north (TX1-F1, TX1-F2). Sites are labelled TX, MS and P, numbered in the order they were marked.
F. The model is plain data: nodes, branches, feeders, issues and a summary. The API stores nodes and branches in PostGIS (`lv_nodes`, `lv_branches`) under one `lv_networks` row per project. Feeders, issues and the summary are stored as returned. Building again replaces the stored network. Later steps send the stored model back to the calc service, which turns it into a graph with `LvNetwork.graph()`.
G. Building takes well under a second, even for a 25 × 25 street grid, so it runs in the request rather than as a background job. Only engineers build. Everyone signed in can view.
H. The API reports a stored network as out of date when LV routes or sites were marked, moved or removed after it was built, or when the project moved to other rules.

## Consequences

- Topology problems show up before any electrical check. Loads (2.2), conductors (2.3) and the voltage-drop, thermal and fault checks (2.4) build on a network that is known to be radial.
- Feeders follow how routes leave the board, so they depend on how the engineer marked the routes. Splitting a feeder in two needs a second route from the board.
- Overhead services will attach to poles once loads are allocated (2.2) and poles are placed (2.5). For now a pole is only a node on the route.
- The 2 m, 6 m, 30 m and 5 m tolerances are a first guess. They should be tuned once routes from real field visits are in.
