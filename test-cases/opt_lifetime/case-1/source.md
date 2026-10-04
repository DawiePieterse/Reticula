Hand calculation of the lifetime cost (plan 5.1), rules eskom/0.5.0, no demand growth.
Network: one 100 m ABC-35 feeder (R 1.08 ohm/km phase and neutral) to a 30 kVA balanced special load on a 50 kVA transformer.
- I = 30 000 / 230 / 3 = 43.478 A per phase, no variance; neutral current 0 (balanced).
- Line losses = 3 × 43.478² × 1.08 × 0.1 = 612.48 W.
- Transformer: I_rated = 50 000 / (3 × 230) = 72.464 A; load loss = 1.10 kW × (43.478/72.464)² = 1.10 × 0.36 = 0.396 kW; no-load 0.19 kW.
- LLF = 0.3 × 0.35 + 0.7 × 0.35² = 0.19075.
- Annual losses = 8760 × ((0.61248 + 0.396) × 0.19075 + 0.19) = 3349.53 kWh; cost at R 1.85 = R 6196.64 a year.
- PV factor (25 years, 8 %) = (1 − 1.08^−25)/0.08 = 10.67478.
- Lifetime cost = 100 000 + 6196.64 × 10.67478 = R 166 147.72.
