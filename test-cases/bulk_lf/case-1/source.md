Load flow (pandapower Newton-Raphson) against the hand voltage-drop method used by the MV design.
11 kV at the connection point, 1 km of Fox (R 0.778, X 0.380 ohm/km), a 200 kVA transformer (uk 4.5 %, X/R 3, tap 0)
carrying 180 kVA at pf 0.95.
Hand: I = 180/(√3 × 11) = 9.448 A; MV drop = √3 × 9.448 × (0.778 × 0.95 + 0.380 × 0.3122) × 1 km = 14.04 V = 0.1276 %;
regulation = 0.9 × (1.4230 × 0.95 + 4.2691 × 0.3122) = 2.4164 %; LV bus ≈ 100 − 0.128 − 2.416 = 97.456 %.
The exact load flow agrees within the tolerance (the hand method drops second-order terms).
