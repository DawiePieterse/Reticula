# Standards clause index

Every check and every rules-file value that comes from a standard points to an entry here (plan workstream 6A). A traced result names its clause; this index says which edition that clause is from and whether anyone has checked it.

**Status: draft, 2026-10-05.** No clause number below has been checked against the standards. Each row says *to confirm* until the engineer has read the clause in the current edition and filled in the number. The plan requires this before Phase 2 (open item 5).

**The engineer holds none of the cited documents (answered 2026-10-05).** No row can be confirmed until copies are obtained, so every value that comes from a standard stays *unverified* and is listed in the assumptions register as such. Getting the documents is the first step to closing open item 5.

## How it is used

- **Ids** have the form `STANDARD-TOPIC`, for example `NRS034-ADMD`. They never change once a design has used them. A withdrawn entry stays, marked *withdrawn*.
- **Rules files** carry the id in an `index` field beside each `clause` (from rules version 0.3.0, which sets it on the voltage, conductor and LV network sections; `rules/schema.json` accepts it). The calc tests check that every `index` in a rules file exists here, and that every standard a rules file lists under `standards:` has at least one entry here.
- **Editions** are set per rules file under `standards:`. The edition column here is what the current rules file (`eskom/0.3.0`) claims; it is unconfirmed until the status says otherwise.
- **Eskom projects follow Eskom's own standards.** Where an Eskom standard and an NRS or SANS document differ, the Eskom standard governs, and the `eskom/*` rules files point at the Eskom row. The NRS and SANS rows stay for municipal projects and for values Eskom does not set.
- **To confirm a row:** read the clause in the edition you hold, write the clause number and edition, set the status to *confirmed* with your initials and the date, and raise the rules file version if a value changes.

## Index

### Loads and demand (Phase 1, in use)

| Id | Check or value | Standard | Clause | Edition | Used by | Status |
| --- | --- | --- | --- | --- | --- | --- |
| NRS034-ADMD | ADMD per consumer class; load class parameters α, β and scaling current c | NRS 034-1 | to confirm | 2024 (rules, unconfirmed) | `load_tables.nrs034_15y`, `load_tables.nrs034_7y`; formula `load.admd.indicator-score.v1` | unverified: values transcribed from the ReticMaster help, see docs/load-data.md |
| SANS507-ADMD | Load class parameters C1–C12, 90 % confidence | SANS 507-1 | to confirm | not listed in rules | `load_tables.sans507_15y` | unverified; class C8 fails its own self-check and cannot be checked against the published table, so it stays excluded (the calc service refuses it) |
| NRS034-HB | Herman-Beta group demand at a stated confidence level | NRS 034-1 (with SANS 507-1) | to confirm | 2024 (rules, unconfirmed) | `income_admd.diversity`; formula `load.group.herman-beta.v1` | unverified; checked against a hand-worked case only |
| NRS034-CLASS | Mapping household income to a consumer class | NRS 034-1 | to confirm | 2024 (rules, unconfirmed) | `income_admd.income_bands` | unverified; the observation score that feeds it is a Reticula heuristic, not a standard |
| NRS034-SPECIAL | Default demand of special loads (school, shop, pump) | NRS 034-1 | to confirm whether the standard gives values | 2024 (rules, unconfirmed) | `income_admd.special_loads` | unverified: placeholder values |

### LV design (Phase 2)

| Id | Check or value | Standard | Clause | Edition | Used by | Status |
| --- | --- | --- | --- | --- | --- | --- |
| NRS048-VLIMIT | Supply voltage limits at the point of supply | NRS 048-2 | to confirm | 2024 (rules, unconfirmed) | `voltage.lv_nominal_v`, `voltage.lv_max_drop_pct` | unverified |
| NRS034-VDROP | Voltage drop allocation along the LV feeder and service connection | NRS 034-1 | to confirm | 2024 (rules, unconfirmed) | `voltage.lv_max_drop_pct`; formula `lv.vdrop.3ph.balanced.v1` | unverified; for Eskom projects see ESKOM-VDROP |
| NRS034-PHASE | Phase allocation and balancing of single-phase consumers | NRS 034-1 | to confirm | 2024 (rules, unconfirmed) | Phase 2.2 | not yet used |
| SANS1418-ABC | Aerial bundled conductor construction and ratings | SANS 1418 | to confirm | not listed in rules | `conductors[ABC-*]` | unverified: placeholder impedances |
| SANS1507-LVCABLE | LV cable construction and ratings | SANS 1507 | to confirm part and clause | 2024 (rules, unconfirmed) | `conductors[CU-PVC-*]` | unverified: placeholder impedances |
| SANS10198-DERATE | Cable current derating for soil thermal resistivity, depth and grouping | SANS 10198 | – | – | – | withdrawn 2026-10-05: not used by the engineer; see ESKOM-DERATE |
| SANS10280-OHL | Overhead line spans, sag and tension, clearances, poles and stays | SANS 10280 | – | – | – | withdrawn 2026-10-05: not used by the engineer; see ESKOM-OHL |
| NRS034-LVFAULT | Minimum fault level at the end of an LV feeder (protection operates) | NRS 034-1 | to confirm | 2024 (rules, unconfirmed) | Phase 2.4 | not yet used |
| SANS10142-SUPPLY | Supply point requirements at the consumer's installation | SANS 10142-1 | to confirm | not listed in rules | Phase 2 service connections | not yet used |

### MV, transformers and bulk supply (Phases 3–4)

| Id | Check or value | Standard | Clause | Edition | Used by | Status |
| --- | --- | --- | --- | --- | --- | --- |
| SANS780-RATING | Standard ratings of distribution transformers | SANS 780 | to confirm | not listed in rules | Phase 3.1 | not yet used |
| SANS1019-VOLT | Standard system voltages and insulation levels | SANS 1019 | to confirm | not listed in rules | `voltage.mv_nominal_kv`; Phase 3 | not yet used |
| SANS1029-MINISUB | Miniature substation construction and ratings | SANS 1029 | – | – | – | withdrawn 2026-10-05: not used by the engineer; see ESKOM-MINISUB |
| SANS97-MVCABLE | MV cable construction and ratings | SANS 97 | to confirm | not listed in rules | Phase 3.3 | not yet used |
| NRS048-MVLIMIT | MV voltage limits and tap setting range | NRS 048-2 | to confirm | 2024 (rules, unconfirmed) | Phase 3.4 | not yet used |
| IEC60909-FAULT | Short-circuit currents: method, voltage factor c, 3-phase and 1-phase | IEC 60909-0 | to confirm | 2016 (rules, unconfirmed) | Phase 4.4 (pandapower) | not yet used |

### Eskom standards (govern Eskom projects)

The engineer designs Eskom projects to Eskom's own standards. Their document numbers and editions are still to be identified, so each row names the subject, not the document.

| Id | Check or value | Standard | Clause | Edition | Used by | Status |
| --- | --- | --- | --- | --- | --- | --- |
| ESKOM-PLANNING | Residential electrification planning and design: ADMD, design horizon, phasing; overrides NRS 034-1 where they differ | Eskom distribution standard, number to identify | to confirm | to identify | `eskom/*` rules: `income_admd`, `load_tables` | to identify |
| ESKOM-VDROP | LV voltage drop allocation and limits for Eskom networks; overrides NRS034-VDROP | Eskom distribution standard, number to identify | to confirm | to identify | `eskom/*` rules: `voltage.lv_max_drop_pct` (index set from eskom/0.3.0) | to identify |
| ESKOM-CONDUCTOR | Standard LV and MV conductors and cables, with impedances and ratings | Eskom distribution standard, number to identify | to confirm | to identify | `eskom/*` rules: `conductors` (index set from eskom/0.3.0) | to identify |
| ESKOM-DERATE | Cable current derating for soil, depth and grouping | Eskom distribution standard, number to identify | to confirm | to identify | Phase 2.6 | to identify |
| ESKOM-OHL | Overhead line spans, sag and tension, clearances, poles and stays | Eskom distribution standard, number to identify | to confirm | to identify | Phase 2.5 | to identify |
| ESKOM-MINISUB | Transformer and mini-sub selection and standard sizes | Eskom distribution standard, number to identify | to confirm | to identify | Phases 3.1–3.2 | to identify |
| ESKOM-SERVICE | Service connections and phasing: longest service, where services connect, when a supply is three-phase | Eskom distribution standard, number to identify | to confirm | to identify | `eskom/*` rules: `lv_loads` (from eskom/0.4.0) | to identify: 2 boxes a pole, 4 loads a box and the second box on another phase are the engineer's stated practice (2026-10-05); 40 m and 15 kVA are placeholders |

### Later and supporting

| Id | Check or value | Standard | Clause | Edition | Used by | Status |
| --- | --- | --- | --- | --- | --- | --- |
| NRS097-EG | Embedded generation limits on LV networks (PV hosting) | NRS 097-2-1 | to confirm | not listed in rules | later: PV and EV hosting | not yet used |
| SANS10098-LIGHT | Public lighting levels and layout | SANS 10098 | to confirm part | not listed in rules | later: public lighting | not yet used |
| REDBOOK-LAYOUT | Township layout and servitude guidance for services | Red Book (Guidelines for Human Settlement Planning and Design) | to confirm chapter | not listed in rules | later: servitude and clash checks | not yet used |

### Not standards

These values are Reticula's own and say so in their `clause` text. They are listed so the register is complete.

| Id | Value | Source | Used by | Status |
| --- | --- | --- | --- | --- |
| RETICULA-PREDICT | Building-type prediction from OSM tags, zoning and footprint | Reticula heuristic v1 | `building_prediction` | heuristic: tune per authority |
| RETICULA-SCORE | Income indicator scoring and thresholds | Reticula heuristic v1 | `income_admd` indicator points and bands | heuristic: needs calibrating (docs/load-data.md) |
| RETICULA-LVMODEL | Drawing tolerances for joining marked LV routes and sites into a network: join, near-miss, source and pole reach | Reticula LV network tolerances v1 | `lv_network` (from eskom/0.3.0); plan 2.1 | own values: tune to how routes are drawn in the field |

## Answers from the engineer (2026-10-05)

1. **Editions held:** none of NRS 034-1, NRS 048-2, SANS 1507 or IEC 60909. No clause can be confirmed yet.
2. **SANS 10198, SANS 10280 and SANS 1029:** not used. Their rows are withdrawn.
3. **Eskom projects:** designed to Eskom's own standards, which override NRS and SANS where they differ (the Eskom rows above).
4. **SANS 507-1 class C8:** cannot be checked. It stays excluded from designs.

## Still open

- **Eskom document numbers and editions** for the six Eskom rows, and copies of them.
- **Copies of NRS 034-1, NRS 048-2, SANS 1507, SANS 1418, SANS 780, SANS 1019, SANS 97 and IEC 60909**, for municipal projects and for values Eskom does not set.

Until these are in hand, Phase 2 can be built and tested against hand-worked cases, but every rules value stays *unverified* and no design is fit to submit.
