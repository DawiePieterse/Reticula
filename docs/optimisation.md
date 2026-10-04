# Optimisation and comparison (plan Phase 5)

The option search takes one marked transformer site with the same field data as the LV design (LV routes, buildings
and loads, roads, connection-point fault level) and returns three options: lowest capital cost, lowest lifetime cost
and most spare capacity under a capex ceiling. Every candidate is designed and checked in full by the LV design engine;
a design that fails any check never wins. Rules `eskom/0.5.0` or later (`optimisation` section, index O-01).

## 1. Cost model (5.1)

- **Rates** `rates/<name>/<date>.yaml`, dated, labelled estimates. A rate is a number or `{assembly: CODE}`.
- **Material library** `materials`: code, description, unit, rate (supply price or labour).
- **Assemblies** `assemblies`: materials × quantities (e.g. an erected LV pole = pole + hardware + labour). Cost lines
  carry the assembly code and its components.
- **Lifetime cost** (`opt/lifetime.py`, formula `opt.lifetime-cost.v1`, traced):
  - Expected peak losses from the Herman-Beta moments: per branch Σ_p (μ_p² + σ_p²)·R·L plus the neutral
    (|Σ a^k μ_k|² + Σσ²)·R_n·L; transformer load loss P_k·Σ_p E[I_p²]/(3 I_r²) and no-load loss P_0 (rules).
  - Annual energy = 8760 × (P_load × LLF + P_0), LLF = 0.3·LF + 0.7·LF² (LF from the rules).
  - Present value over N years at discount rate d: load losses grow with (1+g)², no-load losses do not.
  - Defaults (rate list `lifetime`): 25 years, 8 %, R 1.85/kWh, 0 % growth; each run may override them.

## 2. Objectives (5.2)

| Objective | Minimises | Tie-break |
|---|---|---|
| Lowest capital cost | installed cost | lifetime cost |
| Lowest lifetime cost | capex + PV of losses | capex |
| Most spare capacity | −min(transformer, thermal, voltage-drop headroom) | capex; designs above the ceiling are excluded |

Without a ceiling the spare-capacity option may cost up to `spare_ceiling_pct` (20 %) more than the cheapest design found.

## 3. Search (5.3)

Decision variables: transformer position (attachment points on the LV routes within `move_radius_m`, plus the load
centre), construction, transformer rating, smallest feeder conductor, phase-rotation start, and per-branch upsizing.

1. Constructive start: the marked site and the load centre in each allowed construction, sized by the LV sizing.
2. Local search per objective: best improving single change (move, construction, rating ±1, smallest feeder ±1,
   phase start +1…+3); when none improves, pairs of changes (a move with a phase start or feeder change). The radial
   tree is rebuilt from each new position (re-route). Budget `max_evaluations` (120) designs, shared and cached.
3. Lifetime refinement: the lossiest feeder branches go one size up (upstream follows, feeders taper) while the
   lifetime cost falls and every check passes.

LV sizing speed-ups made for the search: the beta quantile uses `scipy.special.betaincinv` directly, and failures on
independent feeders are upsized in the same sizing round.

## 4. Compare view (5.4)

The Options page shows the three options side by side (checks, construction, transformer and position, feeders,
capex, lifetime cost, losses, spare capacity, worst voltage drop, the search trail, notes and runner-up), the best
value per row in bold, and the selected option on the map. Options whose capex or lifetime cost differ by less than
the rate list's `uncertainty_pct` (15 %) are listed as **too close to call**; an option whose runner-up for its own
objective is that close carries the badge.

## 5. Runs (5.5)

`POST /api/projects/{id}/option-searches` (engineer) with the site, constructions, objectives, capex ceiling, lifetime
overrides, whether the transformer may move and how far, and the evaluation budget; runs as job `design.options`.
`GET .../option-searches` lists the history; `GET .../{runId}` returns the stored result.

## 6. Validation (5.6)

- `test-cases/opt_lifetime/case-1,2`: hand-worked losses, PV factor and lifetime cost (with and without growth).
- `test-cases/opt_bench/case-1,2`: street benchmarks whose optimum was found by exhaustive search (1872 and 2304
  designs, all checks); the heuristic reaches the same capex with 79 and 73 evaluations.
