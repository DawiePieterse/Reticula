pandapower 3.5.5 IEC 60909 (calc_sc) against the hand calculation of test-cases/lv_fault/case-1.
Connection point 11 kV, S''kQ 250 MVA (max = min), R/X 0.1; 1 m of Hare to a 315 kVA Dyn transformer, uk 4.5 %, X/R 3.5.
Hand, at the LV terminals (Un 400 V):
- ZQ = cQ·Un²/S''kQ = 1.1 × 0.16/250 MVA = 0.704 mohm (R/X 0.1); ZT = 22.86 mohm (RT 6.27, XT 21.98 mohm), xT = 0.0433 pu.
- Maximum three-phase: KT = 0.95 × 1.05/(1 + 0.6 × 0.0433) = 0.9723; I''k3 = 1.05 × 400/(√3 × |ZQ + KT·ZT|) = 10.5815 kA.
- Minimum single-phase: Z1 = ZQ + ZT, Z0 = ZT (Dyn); I''k1 = √3 × 0.95 × 400/|2·Z1 + Z0| = 9.408 kA.
pandapower gives 10.5815 kA and 9.425 kA; the single-phase value differs by 0.2 %, inside the 0.5 % tolerance
(pandapower models the transformer's zero-sequence magnetising branch; the hand method does not).
