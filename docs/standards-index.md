# Standards clause index

Every check and rules-file value points to an index id here. Editions are what `rules/eskom/0.3.0.yaml` assumes; each
row must be confirmed against the current edition (plan open item: current editions) before a design is submitted.
"Unverified" means the rules file carries a typical value transcribed for the engine to run, not one checked
against the standard.

| Id | Topic | Standard (edition assumed) | Clause | Used by | Status |
| --- | --- | --- | --- | --- | --- |
| V-01 | Supply voltage limits and LV voltage-drop allocation | NRS 048-2 (2024), NRS 034-1 (2024) | TBD | `voltage.lv_max_drop_pct`, `lv_design.feeder_max_drop_pct`, `service_max_drop_pct` | Unverified |
| LV-01 | Herman-Beta statistical voltage drop, 90 % confidence | NRS 034-1 (2024) | TBD (statistical design method) | LV voltage drop, branch design currents | Method per NRS 034-1 / Herman & Gaunt; limits unverified |
| L-01 | Load parameters (α, β, c) per consumer class | NRS 034 load tables; SANS 507-1 | Load category tables | `load_tables` (eskom/0.2.0) | Transcribed via ReticMaster help; SANS 507 C8 unverified |
| C-01 | LV aerial bundled conductor data (R, X, rating, mechanical) | SANS 1418-1 (2024) | TBD | `conductors` ABC-* | Unverified |
| C-02 | LV PVC SWA cable data (R, X, buried rating) | SANS 1507-4 (2024) | TBD | `conductors` CU-PVC-* | Unverified |
| C-03 | Service connection cables and length | NRS 034-1 (2024) | TBD | `conductors` SC-*, `lv_design.max_service_length_m` | Unverified |
| OH-01 | Overhead clearances, conductor loading cases, spans | SANS 10280-1 (2024) | TBD | `overhead` | Unverified |
| OH-02 | Wood pole dimensions and strength | SANS 754 (2024) | TBD | `overhead.poles` | Unverified |
| UG-01 | Cable derating (soil resistivity, depth, ground temperature, grouping) | SANS 10198-4 (2024), SANS 1507-4 | TBD | `underground` | Unverified |
| F-01 | Short-circuit currents | IEC 60909-0 (2016) | §4 (method), Table 1 (voltage factor c) | LV fault levels | Method per IEC 60909; source and transformer data unverified |
| F-02 | Protection: minimum fault current to clear the feeder fuse | NRS 034-1 (2024) / authority practice | TBD | `fault.min_fault_multiple_of_fuse` | Unverified |
| MV-01 | MV voltage drop, supply voltage band, transformer sizing and taps | NRS 034-1 (2024), NRS 048-2 (2024) | TBD | `mv_design` | Unverified |
| T-01 | Pole-mount transformers (ratings, impedance) | SANS 780 (2024) | TBD | `mv_design.pole_mount` | Unverified |
| T-02 | Mini-substations (ratings, impedance) | SANS 1029 (2024) | TBD | `mv_design.minisub` | Unverified |
| C-04 | ACSR overhead conductors | SANS 182 (2024) | TBD | `conductors` MV-SQUIRREL…MV-HARE | Unverified |
| C-05 | 11 kV XLPE cables | SANS 97 / SANS 1339 (2024) | TBD | `conductors` MV-XLPE-* | Unverified |
| B-01 | Bulk supply: MV voltage band, switchgear fault rating, NMD step and margin, source and zero-sequence assumptions | NRS 048-2 (2024), IEC 60909-0 (2016), NRS 034-1 (2024) / authority supply application | TBD | `bulk` | Unverified |
| R-01 | Indicative installed costs | none (estimate) | n/a | `rates/indicative/2026-10.yaml` | Estimate only |
