Hand calculation, Reticula, 2026-10-04, IEC 60909-0 method.
Un = 400 V, c_max = 1.05, c_min = 0.95 (rules eskom/0.3.0 fault section).
MV source referred to LV: Zq = c·Un²/S''kQ = 1.05·400²/250 MVA = 0.672 mohm; Xq = 0.995·Zq, Rq = 0.1·Xq.
Transformer 315 kVA, uk 4.5 %, X/R 3.5: Zt = 0.045·400²/315 kVA = 22.86 mohm; Rt = Zt/√(1+3.5²) = 6.27 mohm, Xt = 21.98 mohm.
Maximum three-phase fault at the LV terminals: I''k3 = c_max·Un/(√3·|Zq + Zt|) = 10.310 kA.
Minimum phase-neutral fault at the end of 200 m of ABC-95 (R 0.40, X 0.081 ohm/km, neutral the same):
Z1 = Zq + Zt + Zl, Z0 = Zt + Zl + 3·Zl (Dyn transformer: no source in Z0; Z0T = Z1T), Zl = (0.40 + j0.081)·0.2 ohm.
I''k1 = √3·c_min·Un/|2·Z1 + Z0| = 1252.8 A.
