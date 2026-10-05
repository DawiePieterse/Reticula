# Standards clause index

Every check and every rules-file value that comes from a standard points to an entry here (plan workstream 6A). A traced result names its clause; this index says which edition that clause is from and whether anyone has checked it.

**Status: draft, 2026-10-05.** No clause number below has been checked against the standards. Each row says *to confirm* until the engineer has read the clause in the current edition and filled in the number. The plan requires this before Phase 2 (open item 5). Where a standard is named here but not in the requirements, it is marked *suggested*: confirm it applies, or remove the row.

## How it is used

- **Ids** have the form `STANDARD-TOPIC`, for example `NRS034-ADMD`. They never change once a design has used them. A withdrawn entry stays, marked *withdrawn*.
- **Rules files** carry the id in an `index` field beside each `clause` (from rules version 0.3.0; `rules/schema.json` accepts it). The calc tests check that every `index` in a rules file exists here, and that every standard a rules file lists under `standards:` has at least one entry here.
- **Editions** are set per rules file under `standards:`. The edition column here is what the current rules file (`eskom/0.2.0`) claims; it is unconfirmed until the status says otherwise.
- **To confirm a row:** read the clause in the edition you hold, write the clause number and edition, set the status to *confirmed* with your initials and the date, and raise the rules file version if a value changes.

## Index

### Loads and demand (Phase 1, in use)

| Id | Check or value | Standard | Clause | Edition | Used by | Status |
| --- | --- | --- | --- | --- | --- | --- |
| NRS034-ADMD | ADMD per consumer class; load class parameters α, β and scaling current c | NRS 034-1 | to confirm | 2024 (rules, unconfirmed) | `load_tables.nrs034_15y`, `load_tables.nrs034_7y`; formula `load.admd.indicator-score.v1` | unverified: values transcribed from the ReticMaster help, see docs/load-data.md |
| SANS507-ADMD | Load class parameters C1–C12, 90 % confidence | SANS 507-1 | to confirm | not listed in rules | `load_tables.sans507_15y` | unverified; class C8 fails its own self-check |
| NRS034-HB | Herman-Beta group demand at a stated confidence level | NRS 034-1 (with SANS 507-1) | to confirm | 2024 (rules, unconfirmed) | `income_admd.diversity`; formula `load.group.herman-beta.v1` | unverified; checked against a hand-worked case only |
| NRS034-CLASS | Mapping household income to a consumer class | NRS 034-1 | to confirm | 2024 (rules, unconfirmed) | `income_admd.income_bands` | unverified; the observation score that feeds it is a Reticula heuristic, not a standard |
| NRS034-SPECIAL | Default demand of special loads (school, shop, pump) | NRS 034-1 | to confirm whether the standard gives values | 2024 (rules, unconfirmed) | `income_admd.special_loads` | unverified: placeholder values |

### LV design (Phase 2)

| Id | Check or value | Standard | Clause | Edition | Used by | Status |
| --- | --- | --- | --- | --- | --- | --- |
| NRS048-VLIMIT | Supply voltage limits at the point of supply | NRS 048-2 | to confirm | 2024 (rules, unconfirmed) | `voltage.lv_nominal_v`, `voltage.lv_max_drop_pct` | unverified |
| NRS034-VDROP | Voltage drop allocation along the LV feeder and service connection | NRS 034-1 | to confirm | 2024 (rules, unconfirmed) | `voltage.lv_max_drop_pct`; formula `lv.vdrop.3ph.balanced.v1` | unverified |
| NRS034-PHASE | Phase allocation and balancing of single-phase consumers | NRS 034-1 | to confirm | 2024 (rules, unconfirmed) | Phase 2.2 | not yet used |
| SANS1418-ABC | Aerial bundled conductor construction and ratings | SANS 1418 | to confirm | not listed in rules | `conductors[ABC-*]` | unverified: placeholder impedances |
| SANS1507-LVCABLE | LV cable construction and ratings | SANS 1507 | to confirm part and clause | 2024 (rules, unconfirmed) | `conductors[CU-PVC-*]` | unverified: placeholder impedances |
| SANS10198-DERATE | Cable current derating for soil thermal resistivity, depth and grouping | SANS 10198 (*suggested*) | to confirm | not listed in rules | Phase 2.6 | not yet used |
| SANS10280-OHL | Overhead line spans, sag and tension, clearances, poles and stays | SANS 10280 (*suggested*) | to confirm part and clause | not listed in rules | Phase 2.5 | not yet used |
| NRS034-LVFAULT | Minimum fault level at the end of an LV feeder (protection operates) | NRS 034-1 | to confirm | 2024 (rules, unconfirmed) | Phase 2.4 | not yet used |
| SANS10142-SUPPLY | Supply point requirements at the consumer's installation | SANS 10142-1 | to confirm | not listed in rules | Phase 2 service connections | not yet used |

### MV, transformers and bulk supply (Phases 3–4)

| Id | Check or value | Standard | Clause | Edition | Used by | Status |
| --- | --- | --- | --- | --- | --- | --- |
| SANS780-RATING | Standard ratings of distribution transformers | SANS 780 | to confirm | not listed in rules | Phase 3.1 | not yet used |
| SANS1019-VOLT | Standard system voltages and insulation levels | SANS 1019 | to confirm | not listed in rules | `voltage.mv_nominal_kv`; Phase 3 | not yet used |
| SANS1029-MINISUB | Miniature substation construction and ratings | SANS 1029 (*suggested*) | to confirm | not listed in rules | Phase 3.2 | not yet used |
| SANS97-MVCABLE | MV cable construction and ratings | SANS 97 | to confirm | not listed in rules | Phase 3.3 | not yet used |
| NRS048-MVLIMIT | MV voltage limits and tap setting range | NRS 048-2 | to confirm | 2024 (rules, unconfirmed) | Phase 3.4 | not yet used |
| IEC60909-FAULT | Short-circuit currents: method, voltage factor c, 3-phase and 1-phase | IEC 60909-0 | to confirm | 2016 (rules, unconfirmed) | Phase 4.4 (pandapower) | not yet used |

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

## Open questions for the engineer

1. Which editions of NRS 034-1, NRS 048-2, SANS 1507 and IEC 60909 do you hold? The rules file's years are placeholders.
2. Are SANS 10198 (cable selection and derating), SANS 10280 (overhead lines) and SANS 1029 (miniature substations) the standards you design to? They are suggested here, not in the requirements.
3. Does Eskom's own distribution standard override any of these for Eskom projects (for example voltage drop allocation)? If so, it needs its own rows.
4. For the SANS 507-1 table, can the C8 "Urban town house II" row be checked against the published table? Its ADMD does not match its own parameters.
