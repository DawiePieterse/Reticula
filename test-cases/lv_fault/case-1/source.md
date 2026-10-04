Hand calculation, Reticula, 2026-10-04, IEC 60909-0 method; cross-checked with pandapower 3.5.5 calc_sc
(see test-cases/bulk_sc/case-1, same values to 4 figures).
Un = 400 V, c_max = 1.05, c_min = 0.95 (LV), cQ = 1.1 for the MV source (rules eskom/0.3.0 fault section).
MV source referred to LV: ZQ = cQ·Un²/S''kQ = 1.1·400²/250 MVA = 0.704 mohm, R/X 0.1.
Transformer 315 kVA, uk 4.5 %, X/R 3.5: ZT = 0.045·400²/315 kVA = 22.86 mohm; RT = 6.27 mohm, XT = 21.98 mohm;
xT = 0.0433 pu. Network-transformer correction KT = 0.95·c_max/(1 + 0.6·xT) = 0.9723 (IEC 60909-0 §6.3.3).
Maximum three-phase fault at the LV terminals: I''k3 = c_max·Un/(√3·|ZQ + KT·ZT|) = 10.5815 kA.
Minimum phase-neutral fault at the end of 200 m of ABC-95 (R 0.40, X 0.081 ohm/km at operating temperature, neutral the same):
Z1 = ZQ + ZT + ZL, Z0 = ZT + ZL + 3·ZL (Dyn transformer: no source in Z0; Z0T = Z1T), ZL = (0.40 + j0.081)·0.2 ohm.
I''k1 = √3·c_min·Un/|2·Z1 + Z0| = 1252.8 A.
