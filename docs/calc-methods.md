# Calculation methods reference (ReticMaster help)

Transcribed on 2026-10-04 from the ReticMaster help page, Calculations → Calculation Methodologies. This is a third-party summary: use it to plan and to compare results, but check each formula against the standard it comes from before it goes into a rules file.

## How it lines up with Reticula

| Topic | ReticMaster | Reticula now | Where it lands |
| --- | --- | --- | --- |
| Herman-Beta parameters | α, β, Icb; I_admd = α/(α+β)·Icb | The same (`_moments` in `calcs/admd.py`) | Done (1.7) |
| Normal to beta conversion | α = I(Icb·I − I² − σ²)/(Icb·σ²), β = (Icb − I)(Icb·I − I² − σ²)/(Icb·σ²) | The same as the moment fit in `_group_herman_beta` | Done (1.7) |
| 1-phase and 3-phase domestic connections | Separate homogeneous groups, each with its own diversity; the currents are added (superposition). For Herman-Beta a 3-phase connection counts as three 1-phase connections. | Not modelled: every consumer is 1-phase and all are pooled in one beta fit | 2.2 |
| Transformer diversity | The same superposition: 1-phase and 3-phase groups diversified separately and added | Group demand pools everything | 3.1 |
| Phase connection strategy | Service connections rotate across phases in pairs per pole (W W R R B B …) | Balanced share, n/3 per phase | 2.2 |
| Statistical voltage drop | Herman-Beta voltage drop at a confidence level (90 %): the drop not exceeded more than 10 % of the time | Not yet | 2.4 |
| Empirical voltage drop | V_final = V_balanced · UCF(N) · DCF(N) | Not offered (decision 1) | Reference only |
| Load model | Constant power (PF 0.6 to 0.8) or constant current (PF close to 1) | Not yet | 2.1 / 2.4 |
| Transformer voltage drop | V_trfr = R·cos φ + X·sin φ | Not yet | 3.4 |
| LV fault levels | Sequence-impedance formulas with contact and earth resistance | Not yet | 2.4 (LV ends), 4.4 (IEC 60909) |

## Herman-Beta check example

From the help page: α = 1.65, β = 7.35, Icb = 60 A.

- I_admd = 1.65 / 9.00 × 60 = 11.00 A.
- The page prints 10.98 A, because it rounds the mean to 0.183 first (0.183 × 60 = 10.98). Its "method 2" gives 10.99 A.
- `test_herman_beta.py::test_reticmaster_example` checks the 11.00 A.

The σ formula on the page, √(αβ / ((α+β)²(α+β+1))), is for the normalised load on [0, 1]. Multiply it by Icb to get amps, as `_moments` does.

## Empirical correction factors

These apply only to the empirical method. Herman-Beta does not use them.

The ADMD is taken at N = 1000 consumers and corrected for smaller N, where N counts the households at a node and downstream:

- ADMD(N) = ADMD(1000) · DCF(N)
- I = ADMD · N · DCF(N) / V

**Diversity correction factor DCF(N)**

| Method | DCF(N) |
| --- | --- |
| British | 1 + k / (ADMD₁₀₀₀ · N), with k = 8 if ADMD ≤ 5 kVA, else k = 12 |
| AMEU | 1 + 2 / N |
| DT | Like British, with the switch at ADMD ≤ 4 kVA. Experimental: do not use. |
| User | 1 + m · N^c |

**Unbalance correction factor UCF(N)**

| Method | UCF(N) |
| --- | --- |
| British (UK) | 1 + 4.14 / √N |
| AMEU | 1 + 2.8 / √N |
| DT | 1 + 2.8 / √N, capped at 2. Experimental. |
| User | 1 + m · N^c |
| Neutral | Neutral current found by vector algebra (needs an unbalanced model) |

Points from the page:

1. The household voltage drop is ΔV_house = UCF(N) · I_house · Z_section. The bulk load drop is ΔV_bulk = I_bulk · Z_section.
2. ReticMaster clips the DCF so that the current never exceeds the consumer breaker rating Imax.
3. It recommends DCF = AMEU and UCF = Neutral. It notes the British method over-designs low-ADMD rural areas.
4. Decimal points are lost in the page's images ("414", "28"). The values above assume 4.14 and 2.8, which matches the British UCF in the text. Check these against the AMEU source.

## Fault level formulas (as printed)

Notes from the page:

- Rc is the fault contact resistance, and Rc_ØØG the contact resistance for phase-phase-ground faults.
- Re is the earth electrode resistance. It applies only to phase-ground and phase-phase-ground faults.
- Z_neutral upstream is reset to zero at every transformer.

| Fault | Impedance | Current |
| --- | --- | --- |
| Phase to ground | Z = Z₀ + 2Z₊ + 3(Z_contact + Z_earth) | I = 3V / Z |
| Phase to neutral | Z = Z₀ + 2Z₊ + 3(Z_contact + Z_neutral) | I = 3V / Z |
| Three phase | Z = Z₊ + Z_contact | I = V / Z |
| Phase to phase | Z = 2Z₊ + Z_contact | I = √3 · V / Z |

**Phase to phase to ground:**

- Za = Z₊ + Z_contact
- Zb = Z₀ + Z_contact + 3(Z_contact + Z_earth). The image is unclear here.
- Zc = Z₀ + Z₊ + 2Z_contact + 3(Z_contact + Z_earth)
- Z = Za + Za·Zb / Zc
- Ib = V / Z, Ia = (Za + Zc) / Ib, Ic = (Zb + Zc) / Ib, as printed. These are dimensionally odd, so derive them again before use.
- I′ = Ia + a²Ib + aIc and I″ = Ia + a²Ic + aIb. I_fault = max(I′, I″).

Before implementing, check every fault formula against IEC 60909-0 and the authority's LV earthing practice.

## Decisions (engineer, 2026-10-04)

1. **Herman-Beta only.** Reticula does not offer the empirical DCF/UCF method. The empirical formulas above stay as reference only.
2. **3-phase domestic connections are needed.** Each load point carries a connection type (1-phase or 3-phase). Group demand and transformer demand diversify the 1-phase and 3-phase groups separately and add the currents. For Herman-Beta, a 3-phase connection counts as one consumer on each phase.
3. **ReticMaster's paired rotation.** Service connections rotate across phases in pairs per pole: W W R R B B, then repeat.
