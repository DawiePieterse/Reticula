# LV design (plan Phase 2)

The calc service designs the LV network for one transformer from the field data: the transformer site and LV routes
marked on site, the buildings and their loads. All numbers come from the rules file (`eskom/0.3.0` or later); every
value used that is still marked unverified is listed with the result. See docs/standards-index.md.

## 1. Network model (2.1)

`reticula_calc/lv/model.py`: a radial tree of nodes (transformer, poles or kiosks and joints, customer connection
points) and branches (feeder spans and services) with conductor, length and WGS84 geometry, and the customers with
their phases and load class. It is plain JSON, so a design run stores and reloads it unchanged.

## 2. Layout and load allocation (2.2)

1. The LV routes are noded where they cross and split at every bend (local transverse Mercator, metres).
2. The transformer is placed on the nearest route (reported if it moved more than 1 m).
3. A shortest-path tree from the transformer makes the network radial; routes that close a loop are opened (reported).
4. Overhead: poles at every bend and at no more than the maximum span. Underground: joints at the same spacing, kept
   only where a service connects (a kiosk) or the route bends.
5. Each building connects to the nearest pole or joint within the maximum service length; others are an error.
6. Branches that feed no one are removed.
7. Phases follow ReticMaster's paired rotation W W R R B B in feeder order (engineer decision 2026-10-04); a 3-phase
   connection takes all three phases and, for Herman-Beta, counts as one consumer on each.

## 3. Conductor library (2.3)

`conductors` in the rules file: R and X (and the neutral's) at operating temperature, rating, short-circuit constant,
feeder fuse and, for overhead, mass, diameter, breaking load, modulus and expansion. `lv_design.feeder_conductors`
lists the sizes allowed per construction; `service_conductors` the service sizes.

## 4. Voltage drop, loading and fault level (2.4)

**Herman-Beta voltage drop.** Each consumer's current is a beta variable scaled to its class's c. The drop to a node
on phase p is a sum over consumers: one on phase p weighs the shared path's (z_phase + z_neutral); one on another
phase weighs −z_neutral/2, because the neutral current projected on phase p is I_p − (I_q + I_r)/2. With
z = R cos φ + X sin φ, the sum's mean, variance and bounds fix a beta whose 90 % value is the design drop. The moments
are accumulated in one pass from the transformer, so large networks are quick. Special loads are constant currents.

**Loading.** A branch's design current is the 90 % value of its downstream current on the worst phase, against the
conductor rating (underground: derated, section 6).

**Fault level (IEC 60909).** MV source (rules `fault.mv_fault_mva` until the authority's value is entered, plan 4.1)
and transformer impedance give the maximum three-phase fault at the LV terminals. The minimum phase-neutral fault at
every feeder end, I''k1 = √3·c_min·Un/|2Z1 + Z0| (Dyn transformer), must reach the rules' multiple of the feeder fuse.
The feeder fuse is the smallest standard rating (`fault.fuse_ratings_a`) that carries the feeder's design current, and
may not exceed the largest fuse that protects the feeder's first conductor (`fuse_a`).

**Transformer.** The smallest standard rating in the rules at or above 3 × V_ph × the worst phase's 90 % current.

**Sizing.** Feeders start on the smallest allowed size. Overloaded branches and services move up; otherwise the branch
on the path to the worst voltage drop (or weakest end fault) with the largest current × impedance step moves up.
Conductors never get smaller towards the transformer. A load that no allowed size can carry is reported, not hidden.

## 5. Overhead checks (2.5)

Spans are strung at the everyday tension; the parabolic change-of-state equation gives the maximum-load tension
(limit: share of breaking load) and the maximum-temperature sag. The lowest point must clear the ground, or a road
where the span crosses one (from the imported roads). Ground is taken as level between poles. The shortest pole that
gives the clearance is chosen, then the first of that length strong enough for the conductor resultant. Terminals and
deviations over the rules' angle get a stay (a second one if needed; more is a failure).

## 6. Underground derating (2.6)

Rating × soil resistivity × depth × ground temperature × grouping factors, interpolated from the rules' tables, with
the site's conditions or the rules' design defaults.

## 7. Overhead or underground (2.7)

Both constructions can be designed in one run and compared: pass/fail, worst voltage drop, loading, end fault,
route length, poles and stays or kiosks, and an indicative cost from `rates/indicative/2026-10.yaml` (labelled an
estimate with its rate date).

## 8. Not inspected (2.8)

Buildings connected without a site inspection, a transformer not at a marked site and branches outside the project
area are reported with the design.

## 9. Running a design and reading the results (2.9)

**Project → LV design** (engineers start runs; inspectors can read them). Choose the transformer or mini-sub site
marked on the field screen, overhead and/or underground, and optionally a fixed transformer size. The run is a
background job (`design.lv`): it gathers the site, every LV route, every building present with its load class (or
special load kVA) and connection phases, and the imported roads, and stores the calc service's full result on a
design run with the rules version and hash and the exact input. Every building present needs a load; a run fails
with the list otherwise. Results are never edited; a new run replaces the view of an old one, and old runs stay in
the history.

The page shows a banner listing every unverified rules value the design used, the layout issues, the overhead/
underground comparison, a map coloured by loading or voltage drop with failed items outlined, the checks (failures
first) with their clause references, the per-branch table and the indicative cost lines.

A building more than the service length from every route fails the design, like any check.

API: `POST /api/projects/{id}/lv-designs` `{transformerCandidateId, constructions, transformerKva}` (engineer),
`GET /api/projects/{id}/lv-designs`, `GET /api/projects/{id}/lv-designs/{runId}`.

## 10. Validation (2.10)

Hand-worked cases in `test-cases/`: `lv_vdrop` (same-phase load, balanced load with the neutral, two-node feeder),
`lv_fault` (IEC 60909), `oh_sag` (change of state), `ug_derating`. Each `source.md` shows the arithmetic.
