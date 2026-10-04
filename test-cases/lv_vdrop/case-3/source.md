Hand calculation, Reticula, 2026-10-04, independent of the calc code (scipy beta quantile only).
Consumer: township_area (NRS 034 15-year, rules eskom/0.3.0 via 0.2.0): alpha 1.22, beta 5.86, c 60 A, so
mean = c·a/(a+b) = 10.339 A and variance = c²·a·b/((a+b)²(a+b+1)) = 63.38 A².
Conductor ABC-70: R 0.55, X 0.083 ohm/km, neutral the same; power factor 0.95, so
z = R cos phi + X sin phi = 0.5484 mohm/m for the phase and the neutral.
Beta fit of a sum on [L, U]: m = (mean − L)/(U − L), v = var/(U − L)², k = m(1−m)/v − 1, a' = m·k, b' = (1−m)·k;
design value = L + (U − L)·BetaInv(0.9; a', b').

Case 3: 50 m to node A (5 consumers on R), then 50 m on to node B (5 consumers on R).
At A all 10 consumers share the first 50 m: weight 0.05484 ohm each; sum of 10 → I90 136.72 A → dV_A = 7.498 V.
At B the 5 consumers at A weigh 0.05484 ohm and the 5 at B weigh 0.10968 ohm:
mean = 5·10.339·(0.05484 + 0.10968) = 8.505 V; var = 5·63.38·(0.05484² + 0.10968²) = 4.766 V²; L = 0, U = 5·60·0.16452 = 49.36 V.
90 % value: 11.400 V (4.96 %).
