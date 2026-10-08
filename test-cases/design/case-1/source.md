Whole design run, rules eskom/0.8.0, overhead (plans 3.6 and 4.6). Placeholder rules values throughout: this checks the
arithmetic, not the standards.

Layout: a pole transformer 10 m south of an 80 m LV route with three 25 kVA three-phase special loads 10 m off it; an MV
line 1000 m long whose north end is 10 m from the transformer; the connection point 5 m beyond its south end at 11 kV,
2.5 kA three-phase, X/R 5, capacity 1000 kVA.

**Transformer (3.1).** Special loads are fixed currents: each phase carries 3 × 25 / 3 = 25 kVA, I = 25 000 / 230.94 =
108.253 A. S = 3 × 230.94 × 108.253 / 1000 = 75.000 kVA. With 10 % growth it needs 83.33 kVA: the
100 kVA pole-mounted rating, 15.00 kVA spare.

**MV drop and tap (3.6).** I = 75.000 / (√3 × 11) = 3.93648 A through CP link 5 m + route 1000 m + tee 10 m,
FOX R 0.782, X 0.38 Ω/km, cosφ 0.95: z = 0.782 × 0.95 + 0.38 × 0.31225 = 0.861555 Ω/km.
ΔV = √3 × 3.93648 × 0.861555 × 1.015 / 11 000 × 100 = 0.054203 %.
Regulation (4 %, X/R 2: ε_R = 1.78885 %, ε_X = 3.57771 %) at 75 % load: 0.75 × (1.78885 × 0.95 + 3.57771 × 0.31225) =
2.11241 %. The largest tap ≤ ΔV + reg = 2.1666 % is 0 %. LV at full load: 100 − 0.0542 − 2.1124 = 97.8334 %.

**Supply (4.5).** 75.000 / 0.9 = 83.33 kVA: the 100 kVA step, within 1000 kVA.

**Fault level (4.6), IEC 60909-0 maximum, at 0.4 kV.** c = 1.10 at MV, 1.05 at LV (+6 % tolerance).
- Grid: Z_Q = 1.10 × 11 / (√3 × 2.5) = 2.79438 Ω at 11 kV; referred by (0.4/11)²: R_Q = 0.0007247, X_Q = 0.0036233 Ω.
- MV line 1.015 km of FOX referred: R = 0.0010496, X = 0.0005100 Ω.
- Transformer 100 kVA, 4 %: Z_T = 0.06400 Ω, R_T = 0.028622, X_T = 0.057243 Ω; K_T = 0.95 × 1.05 / (1 + 0.6 × 0.03578) = 0.97654.
- Σ R = 0.029724, Σ X = 0.060034 Ω, |Z| = 0.066989 Ω.
I_k3 = 1.05 × 400 / (√3 × 0.066989) = 3619.8 A. At the connection point the fault is the authority's 2.5 kA.
