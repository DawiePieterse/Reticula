Hand calculation, Reticula, 2026-10-04, independent of the calc code (scipy beta quantile only).
Consumer: township_area (NRS 034 15-year, rules eskom/0.3.0 via 0.2.0): alpha 1.22, beta 5.86, c 60 A, so
mean = c·a/(a+b) = 10.339 A and variance = c²·a·b/((a+b)²(a+b+1)) = 63.38 A².
Conductor ABC-70: R 0.55, X 0.083 ohm/km, neutral the same; power factor 0.95, so
z = R cos phi + X sin phi = 0.5484 mohm/m for the phase and the neutral.
Beta fit of a sum on [L, U]: m = (mean − L)/(U − L), v = var/(U − L)², k = m(1−m)/v − 1, a' = m·k, b' = (1−m)·k;
design value = L + (U − L)·BetaInv(0.9; a', b').

Case 1: 100 m of ABC-70, 10 consumers all on phase R at the far end.
Phase R: every consumer on R, weight (z_ph + z_n)·100 m = 0.10968 ohm. Sum of 10: mean 103.39 A, var 633.8 A², on [0, 600 A].
I90 = 136.72 A (as test-cases/hb_group/case-1), so dV_R = 0.10968 × 136.72 = 14.996 V (6.52 %).
Phases W and B: no consumers; the neutral current of phase R raises their voltage: weight −z_n·100/2 = −0.02742 ohm per R consumer,
sum on [−16.45 V, 0]; the 90 % value is −1.980 V.
