# ADR 0010 – Pre-design placement: the design proposes, the field verifies

Date: 2026-10-06 · Status: proposed

## Context

Phases 1 and 2 as built make the inspector do the design on the tablet: mark every service pole within 5 m of a route, draw LV routes whose ends meet within 2 m, place transformer sites, then the engineer builds the network from those marks (ADR 0006, ADR 0007). On a township of 2 000 stands that is at least 250 poles and kilometres of freehand lines, drawn with a finger on a basemap that is thin in townships, with a GPS that is less accurate than the tolerances. It is not achievable at that scale, and the engineer then has to clear one register entry per load before sign-off.

The inputs to propose a layout exist before the visit: the stands and buildings (plan 1.1), a load class per building (plan 1.3 once it predicts class as well as type, or a class the engineer assigns by zone), the roads, and the authority's connection point. What a transformer can feed is known from the same Herman-Beta sum the load schedule uses.

## Decision

A. **The design proposes first.** A new calc step, `POST /calc/lv/placement` (`lv/placement.py`), takes the loads (each with ADMD and load class), the roads and the connection point, and proposes transformer sites, the loads each feeds, LV routes along the roads and an MV route back to the connection point. Its settings are the rules file's `mv_design` section (from `eskom/0.7.0`).
B. **Proposals are candidates.** The API stores the proposal as candidates with `source: proposed`, alongside `field` and `imported`. The LV network builder (ADR 0006) consumes all three. A proposed candidate carries the "not inspected" flag (plan 2.8) until the field confirms or moves it. Nothing downstream changes.
C. **The field verifies exceptions.** The tablet shows the proposal. The inspector confirms a site or moves it, marks obstacles and no-go ground, and marks only what the proposal missed. Poles come from plan 2.5 along the proposed routes, not from the field.
D. **Capacity is Herman-Beta, not an ADMD sum.** Whether a set of loads fits a transformer is decided by the same per-phase beta sum as `/calc/admd/group`, kept as a running total so adding a load is O(1). The largest standard rating × (1 − growth) is the capacity during placement; each site is then sized to the smallest standard rating that holds its demand with the growth allowance.
E. **Reach stands in for voltage drop.** A load further than `lv_reach_m` by road from a site is not assigned to it. This shapes the proposal; the LV checks of plan 2.4 are what pass or fail it. Once a feeder's reach can be derived from the conductor and the loads per metre, that derivation replaces the fixed value.
F. **The solver is constructive plus local search.** Open the site that covers the most unassigned loads per rand (transformer cost plus LV road length), reassign each load to its nearest open site with room, close any site whose loads fit elsewhere, repeat until nothing moves. The MV route is a road-following tree that joins each site by its shortest path to the tree, nearest first. This is the starting point plan 5.3 improves on; a MILP (`scipy.optimize.milp`, HiGHS) per cluster is the next step if the engineer wants a proven optimum on a sub-area.
G. **Costs are traced and marked placeholder** until the rate library (plan 5.1) carries them.
H. **It runs as a background job** (`lv.placement`, `PlacementJob`). A 1 600-house township takes about 17 s; the API runs placement through the job framework (ADR 0003) and reports progress. The result is one `lv_placements` row per project; the proposed sites and routes are candidates with `source: proposed`. A rerun archives the previous proposal's candidates and never touches candidates marked in the field. The connection point is the first imported `connection_point` network asset; without one, no MV route is proposed.

## Rules (eskom/0.7.0, placeholders except the ratings series)

| Setting | Value | Waits for |
| --- | --- | --- |
| `transformer_ratings_kva` | 16, 25, 50, 100, 200, 315, 500 | SANS 780 preferred series to confirm; Eskom 240-56062752 for mini-subs |
| `growth_pct` | 10 % | Eskom planning standard (ESKOM-PLANNING) |
| `lv_reach_m` | 400 m | derivation from the voltage drop limit (ESKOM-VDROP) |
| `max_feeders` | 4 | ESKOM-LVPROT (5 or 6 for mini-subs) |
| `costs` | indicative ZAR | rate library, plan 5.1 |

## Validation

- Synthetic street grids: every house fed within reach and capacity, each transformer within its rating with growth, the MV tree touching every site, fewer houses giving no more transformers, loads far from any road reported.
- The 90 % demand per house sits above the class mean and falls towards it as the group grows, the same behaviour the load schedule tests.
- Still needed: a real layout against the engineer's own placement, and a benchmark with a known optimum (plan 5.6).

## Consequences

- The field workflow changes shape: import → class by zone → placement → field verifies → build and check. Plan items 1.3 (predict class), 1.5 (candidates gain a source), 2.0 (this step) and 2.8 (not-inspected flag on proposed items) change accordingly.
- The greedy pass prefers the largest rating because the cost per load falls with size; the local search of plan 5.3 must trade rating against LV length and losses before the proposal is used for costing.
- Underground areas wait for kiosks (ADR 0007 J): placement there proposes sites and routes, but services cannot be allocated until kiosks are modelled.
- The MV route is radial. Rings and open points are an authority decision for Phase 3.
