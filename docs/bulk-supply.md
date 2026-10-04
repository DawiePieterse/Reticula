# Bulk supply (plan Phase 4)

The bulk supply study takes the latest finished MV design and the authority's connection point and returns: bus
voltages, line and transformer loading and losses from a load flow; maximum three-phase and minimum single-phase fault
currents at every bus; the demand at the connection point against the authority's capacity; the notified maximum
demand (NMD) to apply for; and the bulk feeder. Rules `eskom/0.4.0` or later (`bulk` section, index B-01); every
unverified value used is listed with the result.

## 1. Connection point (4.1)

Entered on the Bulk supply page from the authority's quotation or budget letter: location, supply voltage, available
capacity (kVA), maximum and minimum fault level (MVA), X/R, sending voltage (%) and the authority's reference.

- The MV design is fed from the connection point once it is entered (before imported network data and assumed points).
- The LV and MV designs use its fault levels as the source in place of the rules' defaults.
- **Hard stop:** the study does not start without the available capacity and the maximum fault level, without a
  finished MV design, or when the MV design was fed from a different point (run the MV design again).
- A missing minimum fault level, X/R or sending voltage is filled from the rules and stated as an assumption.

API: `GET/PUT /api/projects/{id}/connection-point`, `POST/GET /api/projects/{id}/bulk-studies`, `GET .../{runId}`.

## 2. Model (4.2)

pandapower 3.5.5, built in the calc service from the MV design:

- External grid at the connection point: voltage × sending voltage, S''k max and min, R/X (rules `bulk.source_r_x`
  when not given), source zero sequence from `bulk.zero_sequence`.
- One bus per MV node; one line per MV branch with R and X from the conductor library. Zero sequence
  R0 = R + `r0_add_ohm_per_km`, X0 = `x0_factor` × X (assumption: no line data from the authority).
- Per transformer site a Dyn transformer (rating, uk, X/R) on its MV design tap (HV side, ±2 × 2.5 %), with the site's
  Herman-Beta design demand at its LV bus at the rules' power factor.

## 3. Load flow (4.3)

Balanced Newton-Raphson. Checks: MV bus voltage within ± `mv_voltage_band_pct`, line and transformer loading ≤ 100 %.
LV unbalance stays with the LV design's Herman-Beta method (OpenDSS unbalanced study later).

## 4. Faults (4.4)

IEC 60909-0 with pandapower `calc_sc`: maximum three-phase (c max, LV tolerance 6 % so c = 1.05 at LV, KT applied) and
minimum single-phase (c min) at every bus. Checks: MV buses ≤ `mv_switchgear_fault_ka`, LV buses ≤
`fault.max_lv_terminal_fault_ka`. The LV design uses the same IEC 60909 method (cQ = 1.1 source, KT correction).

## 5. Supply sizing and bulk feeder (4.5)

- Demand at the connection point = the load flow's apparent power at the external grid (site demands plus losses).
- Check: demand ≤ the authority's available capacity.
- NMD = demand × (1 + `nmd_margin_pct`), rounded up to `nmd_step_kva`.
- Bulk feeder = the first MV branch from the connection point, with its current and loading.

## 6. Validation (4.6)

- `test-cases/bulk_sc/case-1`: pandapower faults against the IEC 60909 hand calculation (also used for
  `lv_fault/case-1`): three-phase exact, single-phase within 0.2 %.
- `test-cases/bulk_lf/case-1`: load flow against a hand calculation of the same network.
- To do: compare with a study the authority has accepted for the same input (needs a sample from the authority).
