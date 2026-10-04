Hand calculation, Reticula, 2026-10-04.
30 township_area consumers (test-cases/hb_group/case-1): I90 = 136.72 A per phase, S = 3 × 230 × 136.72 = 94.34 kVA.
MV current at 11 kV: I = S / (√3 × 11 kV) = 4.9514 A.
1 km of Fox ACSR (rules eskom/0.4.0, unverified: R 0.778, X 0.380 ohm/km), pf 0.95 (sin 0.3122):
dV = √3 × 4.9514 × (0.778 × 0.95 + 0.380 × 0.3122) × 1 = 7.356 V = 0.06687 % of 11 kV.
