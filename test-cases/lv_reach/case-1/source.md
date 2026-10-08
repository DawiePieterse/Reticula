LV reach from the voltage drop limit (plan 2.0), rules eskom/0.8.1. Placeholder values: this checks the arithmetic.

Settings: the largest placement rating R = 500 kVA, growth 10 % and 4 feeders a transformer (mv_design, placeholders);
the default LV conductor ABC-3C-70 with AC resistance 0.568 Ω/km (M-TEC sheet, 90 °C) and X 0.083 Ω/km; power factor
0.98 (lv_design, placeholder); drop limit 7.5 % (voltage.lv_max_drop_pct); 400 V, so V_ph = 230.94 V.

Each feeder carries a quarter of the usable 500 × 0.9 = 450 kVA: I = 450 000 / (3 × 230.94 × 4) = 162.380 A per phase.
z = 0.568 × 0.98 + 0.083 × √(1 − 0.98²) = 0.55664 + 0.083 × 0.19900 = 0.573157 Ω/km.
ΔV = 7.5 % × 230.94 = 17.3205 V.

With the load spread evenly along the feeder the far-end drop is I·z·L/2, so
L = 2 × 17.3205 / (162.380 × 0.573157) km = 372.2 m, shorter than the rules' 400 m, so the placement uses 372.2 m.
