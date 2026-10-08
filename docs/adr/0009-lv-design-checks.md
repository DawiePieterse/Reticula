# ADR 0009 – LV design checks: voltage drop, thermal loading and fault level

Date: 2026-10-05 · Status: accepted

## Decision

A. Building the LV network now runs three steps in the calc service: join the network (plan 2.1), connect the loads (plan 2.2), then check each radial feeder (`POST /calc/lv/analyse`). The API stores the results with the network and serves them with it. If the rules file has no `lv_design` section (before `eskom/0.6.0`), the checks are skipped with a `checks_skipped` issue and the network is still stored.
B. **Voltage drop is Herman-Beta, per phase.** Each residential consumer's current is a beta variable of its load class, from the rules file's design table (plan 1.10). A consumer on phase q changes the phase-p voltage at a point by its current times the impedance of the path they share:
   - (z_phase + z_neutral) when q = p
   - −½·z_neutral on the other phases, because their neutral currents are 120° apart.

   Here z = R_hot·cosφ + X·sinφ. The drop at a point is a weighted sum of independent beta variables, so its mean and variance add, and it is bounded by the consumers' c. The value at the rules file's confidence level is read from a beta fitted to those moments on those bounds, with a normal fallback when the fit is degenerate. Special loads, and three-phase loads split equally over the phases, add their current deterministically.
C. Drops are found at every network node and every service connection point. The worst phase is compared with `voltage.lv_max_drop_pct`. Points over the limit raise `drop_over_limit`. (From ADR 0015, a connection's drop is the customer's: its own phase plus its service cable.)
D. **Thermal loading.** The current in each section, per phase, is the Herman-Beta design current of the consumers beyond it plus their special loads. It is compared with the conductor's `rating_a` as normally installed. Sections over the rating raise `overload`. De-rating for installation is plan 2.6.
E. **Fault level.** The minimum phase-to-neutral fault current at each point is V_phase / |Z_source + Z_phase + Z_neutral|, with the conductors at their hot resistance. The rules file's `lv_design.source` (rating, impedance, X/R) stands in for the transformer until Phase 3 sizes it. Fault levels are reported but not checked while the rules file sets no `min_end_fault_a`; that gives a `fault_not_checked` warning.
F. Hot resistance is the conductor's `r_ac_ohm_per_km` where its source gives one, else the DC resistance at 20 °C corrected to `lv_design.conductor_temp_c` with the material's temperature coefficient.
G. Every branch uses `lv_design.default_conductor` until conductors can be chosen per branch, which is a later step. The request already takes a conductor per branch.
H. Unknown or unverified load classes fall back to ADMD with a `no_load_class` warning rather than failing the build.
I. The worst drop, the highest loading and the lowest fault current are traced values (`lv.vdrop.herman-beta.v1`, `lv.current.herman-beta.v1`, `lv.fault.phase-neutral.v1`).
J. Every placeholder input is listed in a `placeholders` warning. The project page shows it as "Not fit to submit" above the checks. Placeholders include the source transformer, the power factor, the drop limit, and conductors with placeholder fields.
K. The project page shows (a first cut of the plan 2.9 results view) a row per feeder: worst drop and where, highest loading and on which branch, lowest fault current and where, and pass or fail. A last row, "Source links", covers the links from transformers to their routes, which carry all of a source's feeders. The map rings nodes by drop (green under 80 % of the limit, amber up to it, red over it) and haloes overloaded branches in red.

## Settings (eskom/0.6.0, all placeholders)

| Setting | Value | Waits for |
| --- | --- | --- |
| `lv_design.default_conductor` | ABC-3C-70 | conductor choice per branch |
| `lv_design.power_factor` | 0,98 | Eskom 240-70465489 or the planning standard |
| `lv_design.conductor_temp_c` | 70 °C | 240-84758170 and 240-56063805 |
| `lv_design.source` | 100 kVA, 4 %, X/R 1,5 | transformer sizing, Phase 3 |
| `voltage.lv_max_drop_pct` | 7,5 % (from eskom/0.1.0) | Eskom 240-70465489 (index ESKOM-VDROP) |

## Validation

- The calc tests work small feeders out by hand and check drop, current and fault current against those workings. The feeders cover two consumers on one phase, balanced single-phase loads (no neutral current), a three-phase load, a connection mid-branch, another conductor on a branch, and loads without a class.
- The Herman-Beta mixing of load classes reproduces ReticMaster's group currents (`test-cases/hb_group`).
- Still needed: ReticMaster LV feeder results for a whole feeder, so drop at the ends can be checked as a validation case.

## Consequences

- Results are not fit to submit until 240-70465489 and the impedances of 240-56063805 are held, and the transformer is sized.
- Conductor choice per branch, and the overhead and underground comparison of plan 2.7, can use the same analysis.
- Underground kiosks will add another place where services connect. The analysis does not depend on poles, so it needs no change for them.
