# ADR 0015 – Service cables, overhead service spans and LV feeder fuses

Date: 2026-10-07 · Status: accepted

## Context

The engineer answered on 2026-10-07:

- The LV drop limit from Eskom tenders is 8 to 10 %.
- Houses are served overhead with Airdac cable, in spans of at most 50 m.
- A 7 m service pole goes in where the sag becomes a problem.
- Engineering assumptions should be made for the LV protection settings, from the protection equipment available.
- Where suppliers' figures differ, their average is used.

Until now the drop limit stopped at the service connection on the feeder, services had no conductor or drop of their own, and the end-of-feeder fault level was reported but never checked.

## Decision

A. **The drop limit holds at the meter.** The drop at a customer is the Herman-Beta drop at its service connection, on the customer's own phase (the worst phase for a three-phase load), plus the drop in its service cable (`lv.vdrop.customer.v1`). The design run passes each service's drop to the LV analysis as `LoadAt.service_pct`. Feeder sizing therefore sees it too.

   Earlier, a connection point's `worst_pct` was the worst phase at that point. It is now the customer's own phase plus the service, because a customer on red does not see blue's drop.

B. **Limits (eskom/0.9.0).**
   - `voltage.lv_max_drop_pct` is 8 %. That is the engineer's lower figure and the Red Book's "approximately 8 % … including service connections". Because it is the engineer's own value, it is no longer reported as a placeholder.
   - `voltage.lv_service_max_drop_pct` is 2 %, the Red Book's limit for service cables at the undiversified ADMD. It stays a placeholder until the engineer confirms it.
   - `voltage.placeholder` lists which keys of the voltage section are placeholders. A file without the list keeps the old behaviour, where the drop limit counts as a placeholder.
   - The Red Book allows 9 % or a little more where the regulated busbar is electrically close. That is left to the engineer, as a later rules version or a per-project choice.

C. **Service conductor** (`design/services.py`).
   - Each service takes the first conductor in `services.<construction>.single_phase` (or `three_phase`, for three-phase loads) that does two things: carries the customer's own design current, and keeps the service drop within the limit.
   - The design current has no diversity. For a classed load it is c·B⁻¹(conf; α, β); otherwise it is kVA over the phase voltage.
   - The drop is k·I·ℓ·(R_hot·cosφ + X·sinφ), with k = 2 for single-phase and k = 1 for three-phase (`lv.service.drop.v1`).
   - If no conductor in the list works, the service gets the last one, fails, and raises `service_over_limit`.

D. **Overhead service spans** (`oh.service.span.v1`).
   - Services are strung at the supplier's maximum working tension, `tension_pct` = 25 % of the breaking load. The sag is therefore w·L²/(8·T), which is Aberdare's installation table: with g = 10 m/s² it reproduces Aberdare's figures exactly.
   - The support heights are the feeder pole's line attachment less `below_line_m`, a 7 m service pole set as an LV pole, and `house_attachment_m` on the building.
   - The lowest point of the parabola over flat ground must clear `min_clearance_m`.
   - Where a service is longer than `max_span_m` (50 m), or its low point is too low, service poles go in at equal spacing along the service. The design uses the fewest that make every span clear, and never makes a span shorter than the LV line's `min_span_m`.
   - Service poles are drawn as small LV poles tagged SP on the drawing and the map, exported as `service_pole` features, counted in `summary.service_poles`, and priced as `A-POLE-SERVICE-7`.
   - `lv_loads.max_service_m` is now the engineer's 50 m.

E. **LV feeder fuse** (`lv_design.protection`, `lv.protection.fuse.v1`).
   - Each feeder gets one NH gG fuse: the smallest standard rating I_n at or above the design current I_B (the most loaded section's worst phase), which must not exceed the lowest conductor rating on the feeder (I_B ≤ I_n ≤ I_z).
   - Every point on the feeder must see a phase-to-neutral fault of at least `min_fault_multiple` × I_n. The multiple is 3, following Energex 3064638 §4.
   - A feeder that fails either test steps up a conductor size in sizing, in the same way as one over the drop limit.
   - The checks are `lv_fuse` and `lv_fault` per feeder. The older single `min_end_fault_a` is still honoured for rules files without protection.

F. **Supplier data, averaged** (eskom/0.9.0 conductor list). These values come from manufacturers and still need the engineer's acceptance:
   - Underground LV cables now carry AC resistance at 70 °C and reactance from CBi. For copper 4-core, the resistance is the mean of CBi's figure and Voltex's. Voltex gives impedance only, so its resistance is taken as √(Z² − X²) using CBi's X.
   - Three-phase ABC ratings are the mean of three South African SANS 1418 suppliers. They stay placeholders because the rating basis (still air or wind, sun or shade) is unstated.
   - The two South African Airdac suppliers agree on rating, mass and diameter. JYTOP's higher ratings state no basis, so they are not averaged in.

## Assumptions (placeholders until the engineer or the Eskom document confirms them)

| Setting | Value | Basis |
| --- | --- | --- |
| `services.overhead.below_line_m` | 0.6 m | service clamp under the ABC on a feeder pole |
| `services.overhead.house_attachment_m` | 3.5 m | bracket on a single-storey house |
| `services.overhead.min_clearance_m` | 3.0 m | over the stand, pedestrians only; SANS 10280-1 and Eskom 240-75661043 not held |
| `services.overhead.three_phase` | ABC-3C-25, ABC-3C-35 | no three-phase Airdac |
| `services.underground.*` | Cu 2-core and 4-core 16–35 mm² | Eskom 240-56030637 allows 2- or 4-core services |
| `lv_design.protection.fuse_ratings_a` | 63–400 A NH series | IEC 60269-2 preferred ratings |
| `lv_design.protection.min_fault_multiple` | 3 | Energex 3064638 §4. A 5 s disconnection criterion would need about 5 to 6 × I_n for gG |
| AIRDAC-SNE x_ohm_per_km | 0.08 Ω/km | not published; concentric construction |

## Consequences

- With eskom/0.9.0, designs check drop at the meter, service drop, service clearance and the feeder fuse. Older rules versions run without services or fuses. Their results do change, though: they gain the new fields (as null), and connection drops are now taken on the customer's phase. A reproduction (plan 7.2) of a revision made before this change will therefore report those paths as different. Re-run such a revision before signing it off.
- Eskom 240-56030637 Table 10 (held by the engineer) gives the LV protection per cable size for underground networks. Transcribing it should replace assumption E for underground designs. Eskom 240-57649065 (the LV protection philosophy) is still not held.
- Ground is taken as flat under a service. Hot-conductor sag growth is not modelled, because services are small copper cables strung to the supplier's table.
