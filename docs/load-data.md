# Load data and Herman-Beta

## Where the numbers come from

`rules/eskom/0.2.0.yaml` carries three load tables. They were transcribed on 2026-10-04 from the ReticMaster help page (Calculations, Load Category Tables), which reproduces:

| Table id | Source | Horizon | Classes |
| --- | --- | --- | --- |
| nrs034_15y | NRS 034 load parameters | 15 years | 8 consumer classes with LSM and income ranges |
| nrs034_7y | NRS 034 load parameters | 7 years | the same 8 classes |
| sans507_15y | SANS 507-1 load parameters | 15 years | C1 to C12, 90 % confidence |

Each class holds the beta-distribution shape α and β, the scaling current c (the consumer circuit breaker), and the published ADMD. The NRS 034 classes also hold the published mean μ and standard deviation σ in amps.

These are transcriptions of a third-party reproduction. Check them against the original NRS 034-1 and SANS 507-1 tables before a design is submitted.

## The self-check

For a beta load, the published numbers must agree with α, β and c:

- ADMD = c · α / (α + β) × 230 V
- μ = c · α / (α + β)
- σ = c · √(αβ / ((α + β)² (α + β + 1)))

`src/calc/tests/test_load_tables.py` checks every class to within 1.5 %, so a mistyped digit fails CI. All NRS 034 classes pass, and so do eleven SANS 507 classes.

**SANS 507 C8 "Urban town house II" fails the check.** Its published ADMD of 5.64 kVA does not match its α 1.448, β 2.92 and c 60 A, which give 4.58 kVA. It is marked `unverified`, and the calc service refuses to use it until the original table is checked.

## Decision: design table

On 2026-10-04 the engineer chose the NRS 034 15-year table (`nrs034_15y`) for Eskom projects. Its consumer classes carry income ranges, which the site-observation score maps onto. The SANS 507-1 table stays encoded for reference.

## How a load gets its class

1. The inspector's observations give an indicator score. The score thresholds are a Reticula heuristic and still need calibrating.
2. The rules file's `income_bands` map the score to a class in the design table. `eskom/0.2.0` uses `nrs034_15y`.
3. The engineer can choose any usable class directly instead. The choice is stored and shown in the assumptions register.
4. The load's ADMD is the class's published ADMD, unless it is overridden with a reason.

## Group demand (Herman-Beta)

For residential loads, the calc service:

1. Shares consumers equally over three phases (balanced allocation). Fewer than three consumers stay on one phase.
2. Adds up, per phase, the consumers' means and variances and their scaling currents C.
3. Fits a beta distribution on [0, C] with the same mean and variance.
4. Takes its value at the confidence level (90 %) as the design current per phase.
5. Gives demand as phases × 230 V × that current. Special loads are added at their kVA.

The per-consumer demand falls towards the ADMD as groups grow; a test checks this. A hand-worked case of 30 township-area consumers is in `test-cases/hb_group/case-1`: 136.72 A per phase and 94.34 kVA. It is checked independently with a Cornish-Fisher estimate.

**Checked against ReticMaster 21.** Its test procedure "Mixed Domestic Loads" (15 August 2021, supplied by the engineer) gives two worked mixes of load classes:

- **Herman-Beta:** ReticMaster pools mixed consumers into one class. LSM 3-4 with LSM 5-6 gives α 1.066, β 6.95, c 60 (`hb_group/case-2`). LSM 3-4 with LSM 7 gives α 1.077, β 5.007 (`hb_group/case-3`). Pooling keeps the sum's mean and variance, which is what Reticula adds up directly. The design currents agree to 0.02 %, the rounding of ReticMaster's α and β.
- **Empirical:** ReticMaster's default correction factor is DCF(n) = 1 + 2/n (k = 2). Its per-phase currents at 231 V for two and four mixed consumers add up to the group demand Reticula gives with k = 2 (`tests/test_admd.py`). Reticula's starter rules use k = 1.5.

Voltage drop along feeders by Herman-Beta follows in Phase 2 (LV design). It will reuse these class parameters.

## Rules versions

`eskom/0.1.0` keeps its placeholder bands and diversity formula and its original hash, so anything computed with it can still be reproduced. Projects choose their rules version when they are created.
