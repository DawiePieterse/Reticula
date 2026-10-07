# Rate libraries

`indicative.json` is a placeholder rate library: rough South African rates invented to exercise the cost model (plan 5.1).
It is not a supplier price list or a tender rate, and every cost worked from it is marked an estimate.

A real library is managed in the app (plan 6.1): supplier price lists are imported there, rates can be overridden with a
date and a reason, and each design run records the library and rate date it used. The calc service only falls back to
this file when no library is sent. `RETICULA_RATES_DIR` overrides the directory.

Shape: `items` are priced things (code, description, unit, rate, category, optional uncertainty_pct); `assemblies` are
what the design's quantities map to, each a list of items with quantities. The design run asks for assembly codes such as
`A-POLE-LV-9`, `A-COND-ABC-3C-70`, `A-TX-POLE-100` and `A-MINISUB-315`; see `src/calc/src/reticula_calc/design/run.py`.
