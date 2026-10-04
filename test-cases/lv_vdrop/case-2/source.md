Hand calculation, Reticula, 2026-10-04, independent of the calc code (scipy beta quantile only).
Consumer: township_area (NRS 034 15-year, rules eskom/0.3.0 via 0.2.0): alpha 1.22, beta 5.86, c 60 A, so
mean = c·a/(a+b) = 10.339 A and variance = c²·a·b/((a+b)²(a+b+1)) = 63.38 A².
Conductor ABC-70: R 0.55, X 0.083 ohm/km, neutral the same; power factor 0.95, so
z = R cos phi + X sin phi = 0.5484 mohm/m for the phase and the neutral.
Beta fit of a sum on [L, U]: m = (mean − L)/(U − L), v = var/(U − L)², k = m(1−m)/v − 1, a' = m·k, b' = (1−m)·k;
design value = L + (U − L)·BetaInv(0.9; a', b').

Case 2: 100 m of ABC-70, 10 consumers on each phase at the far end (30 in all).
Phase R: weight 0.10968 ohm for the 10 on R, −0.02742 ohm for the 20 on W and B.
mean = 0.10968·103.39 − 0.02742·206.79 = 5.670 V; var = 0.10968²·633.8 + 0.02742²·1267.6 = 8.578 V²;
L = −0.02742·1200 = −32.90 V, U = 0.10968·600 = 65.81 V. 90 % value: 9.450 V (4.11 %). By symmetry the same on W and B.
The neutral halves the mean drop compared with case 1's same-phase loading, but not the spread.
