# ADR 0011 – Design runs and optimisation

Date: 2026-10-07 · Status: accepted

## Decision

A. **One run, one stored request.** A design run is a background job (`design.run`, ADR 0003). The API gathers every project input into the calc service's request shape: LV and MV routes and sites, loads with their classes, contours, the connection point, the rate library and the rules reference. It sends the request, then stores the exact request JSON and the calc service's result JSON. Neither is edited afterwards. Results stay in the calc service's snake_case; the API reads a few facts off them (fit to submit, checks, failures, capital and lifetime cost, placeholders) and never recomputes a value (ADR 0001).
B. **Modes.** `run` is one design with the engineer's options. `optimise` returns three options in one result. `adopted` copies one option's request and design out of an optimisation run without calling the calc service again. `reproduce` sends a revision's stored request again and compares the results (ADR 0013). The project's current design is the latest `run` or `adopted` run with a result.
C. **Canonical hashes.** Requests and results are hashed as canonical JSON: keys sorted, numbers in round-trip form, SHA-256. The inputs hash leaves out the run options. A per-part hash (rules, candidates, loads, contours, connection point, rates) is stored with each run, so a stale run can say which part changed.
D. **Options are checked before the calc service sees them.** Only the known keys are accepted. For a design these are construction, MV construction, objective, LV and MV conductors, transformer ratings, economics and underground conditions. For an optimisation they are objectives, evaluations (1 to 200), capital ceiling, move radius, moves per transformer, siting and constructions. Anything else is a 400.
E. **Placeholders become assumptions.** Each placeholder rules value a run used becomes an open project assumption with code `rules_placeholder:<sha16>`. One the next run no longer uses is cleared; one that comes back is reopened. A confirmed one stays confirmed.
F. **Optimisation lives in the calc service.** It starts from the marked sites and, where wanted, from a siting MILP (HiGHS). It then runs a local search where every neighbour is a full `design.run`, re-checked against every rule. Options are ranked by soundness first, then by failed checks, then by the objective. Soundness counts loads with no service or feeder, transformers the MV network does not tap, and a bulk study that stopped. Ranking by failed checks alone rewards breaking a design, because a part nothing feeds fails no check. Siting opens at most one transformer per connected LV network, because the network model has no open points. It also opens only sites within MV tap reach.

## Consequences

- A run can be repeated from its stored request alone, and a revision can prove that it reproduces.
- Adopting an option costs nothing and cannot differ from what the compare view showed.
- Changing a request or response shape means changing the calc models and `Reticula.Infrastructure/Calc/*Contracts.cs` together.
- Stored results are large (an optimisation holds three full designs). Listing endpoints project the facts and never load the result JSON.
