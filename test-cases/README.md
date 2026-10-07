# Hand-worked validation cases

`test-cases/<topic>/case-N/` holds `inputs.json`, `expected.json` and `source.md` (who worked it, from which standard or textbook).
The calc service test suite runs every case and fails on tolerance breach. CI runs this before any release.

| Topic | Plan | What it checks |
|---|---|---|
| `voltage_drop` | 1.x | Balanced drop on one conductor |
| `admd`, `admd_group`, `hb_group` | 1.x | Load estimates and Herman-Beta group demand |
| `lv_drop` | 2.10 | LV feeder drop through the full analysis (phase and neutral paths) |
| `oh_span` | 2.10 | Pole placement, ruling span, state-change tensions, sag and clearance |
| `ug_derate` | 2.10 | Cable de-rating with interpolated factors |
| `design` | 3.6, 4.6 | Whole run: transformer size, MV drop and tap, supply step, IEC 60909 fault level at the LV board |

Cases worked against placeholder rules check the arithmetic, not the standards. When a rules value is confirmed,
rework the case against the new file version.
