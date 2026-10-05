# Standards clause index

Every check and every rules-file value that comes from a standard points to an entry here (plan workstream 6A). A traced result names its clause; this index says which edition that clause is from and whether anyone has checked it.

**Status: draft, 2026-10-05.** No clause number below has been checked against the standards. Each row says *to confirm* until the engineer has read the clause in the current edition and filled in the number. The plan requires this before Phase 2 (open item 5).

**The engineer held none of the cited documents (answered 2026-10-05).** Later that day the engineer supplied Eskom 240-56030637 Rev 2 (LV cable systems) and confirmed it is the current revision; its clauses are located below. Every other row waits for its document, so every value that comes from a standard stays *unverified* and is listed in the assumptions register as such. Getting the documents is the first step to closing open item 5.

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
| SANS1418-ABC | Aerial bundled conductor construction and ratings | SANS 1418-1 (cores) and SANS 1418-2 (assembled bundles), as cited by Eskom 240-92934300 §2.2.1 | to confirm | not listed in rules | `conductors[ABC-*]` | unverified: placeholder impedances; for Eskom projects see ESKOM-CONDUCTOR (240-84758170) |
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

The engineer designs Eskom projects to Eskom's own standards. Where a document number is not yet known, the row names the subject. Numbers found in 240-56030637 §2.2.1 are recorded, but only that document is held so far (see **Documents held** below).

| Id | Check or value | Standard | Clause | Edition | Used by | Status |
| --- | --- | --- | --- | --- | --- | --- |
| ESKOM-PLANNING | Residential electrification planning and design: ADMD, design horizon, phasing; overrides NRS 034-1 where they differ | Eskom distribution standard, number to identify | to confirm | to identify | `eskom/*` rules: `income_admd`, `load_tables` | to identify |
| ESKOM-VDROP | LV voltage drop allocation and limits for Eskom networks; overrides NRS034-VDROP | Eskom 240-70465489 *Distribution voltage regulation and apportionment limits* (cited by 240-56030637 §3.10.5) | to confirm | to identify | `eskom/*` rules: `voltage.lv_max_drop_pct` (index set from eskom/0.3.0); `lv_design` (from eskom/0.6.0: power factor, conductor temperature, source transformer, all placeholders); formula `lv.vdrop.herman-beta.v1` | number identified 2026-10-05; document not held |
| ESKOM-CONDUCTOR | Standard LV and MV conductors and cables, with impedances and ratings | Underground: Eskom 240-56063805 (LV power and control cable 600/1000 V), per 240-56030637 §3.9.1 a). Overhead ABC: Eskom 240-84758170 (*Aerial bundled conductors with bare or insulated neutral supporting conductor*), Technical Schedules A and B; product codes D-DT 3141; per 240-92934300 §2.2.2 | to confirm | to identify | `eskom/*` rules: `conductors` (index set from eskom/0.3.0) | numbers identified 2026-10-05; documents not held. Underground cable ratings: see ESKOM-LVCABLE-RATING |
| ESKOM-DERATE | Cable current derating: standard installation conditions, grouping, cables in pipes | Eskom 240-56030637 *General information and requirements for LV cable systems* | §3.9.1 o)–p) (standard conditions: 70 °C conductor, 25 °C soil, 30 °C air, 1,2 K·m/W, 0,5 m deep); §3.9.6 i) (pipe rating where over 10 % is in pipe); de-rating annex (grouping, in ground and in ducts) | Rev 2, Aug 2021 | Phase 2.6 | clause located 2026-10-05; Rev 2 confirmed current by the engineer; values to confirm when transcribed into a rules file. The standard gives grouping factors only; soil and depth factors come from SANS 10198-4, which it cites |
| ESKOM-OHL | Overhead line spans, sag and tension, clearances, poles and stays | Eskom distribution standard, number to identify | to confirm | to identify | Phase 2.5 | to identify |
| ESKOM-MINISUB | Transformer and mini-sub selection and standard sizes | Eskom 240-56062752 (MV miniature substations 11–22 kV), per 240-56030637 §2.2.1 | to confirm | to identify | Phases 3.1–3.2 | number identified 2026-10-05; document not held. Feeders per mini-sub: see ESKOM-LVPROT |
| ESKOM-SERVICE | Service connections and phasing: longest service, where services connect, when a supply is three-phase | Eskom 240-75661043 Part 8 Section 3 (outdoor LV services for SPU and LPU), per 240-56030637 §2.2.1 | to confirm | to identify | `eskom/*` rules: `lv_loads` (from eskom/0.4.0) | number identified 2026-10-05; document not held. 2 boxes a pole, 4 loads a box (filled before the next box is started) and the second box on another phase are the engineer's stated practice (2026-10-05); 40 m and 15 kVA are placeholders |
| ESKOM-LVCABLE-RATING | Continuous current rating of 600/1000 V armoured LV cables, Cu and Al, 16–240 mm², in ground, in pipes and in air; feeder cables four-core, services two- or four-core | Eskom 240-56030637 *General information and requirements for LV cable systems* | §3.9.1 h)–k), n); Tables 6 (Cu) and 7 (Al) | Rev 2, Aug 2021 | `eskom/*` rules from 0.5.0: `conductors[*].ratings_a`, `rating_a`; plan 2.3 | transcribed 2026-10-05 into eskom/0.5.0, checked by the calc tests against Tables 1 and 2; Rev 2 confirmed current; engineer to confirm the transcription |
| ESKOM-LVCABLE-FAULT | Short-circuit withstand of LV cables: I = K·A/√t, K 0,115 (Cu) and 0,076 (Al); phase and earth fault levels | Eskom 240-56030637 *General information and requirements for LV cable systems* | §3.9.1 m); Tables 1–5 | Rev 2, Aug 2021 | `eskom/*` rules from 0.5.0: `conductors[*].fault_k`; formula `lv.cable.withstand.v1` | transcribed 2026-10-05 into eskom/0.5.0, checked by the calc tests against Tables 1 and 2; Rev 2 confirmed current; engineer to confirm the transcription |
| ESKOM-LVPROT | LV feeder protection per cable size (MCCB and fuse ratings); at most 5 LV feeders from a Type A mini-sub, 6 from a Type B | Eskom 240-56030637 *General information and requirements for LV cable systems* | §3.6; §3.9.12 a); Table 10 and its note | Rev 2, Aug 2021 | Phases 2.4 and 3.2 | clause located 2026-10-05; Rev 2 confirmed current by the engineer; values to confirm when transcribed into a rules file. LV protection philosophy is in 240-57649065 (not held) |
| ESKOM-KIOSK | Underground supply: LV feeders supply metering kiosks, never customers directly; at each kiosk, MCBs on one phase grouped, at most 4 per phase, and the kiosk balanced across phases | Eskom 240-56030637 *General information and requirements for LV cable systems* | §2.3.1 (LV feeder cable); §3.5.3 d)–f); §3.10 e) | Rev 2, Aug 2021 | Underground load allocation (kiosks not yet modelled: plan 2.2 covers overhead only; see ADR 0007) | clause located 2026-10-05; Rev 2 confirmed current by the engineer; values to confirm when transcribed into a rules file |

### Later and supporting

| Id | Check or value | Standard | Clause | Edition | Used by | Status |
| --- | --- | --- | --- | --- | --- | --- |
| NRS097-EG | Embedded generation limits on LV networks (PV hosting) | NRS 097-2-1 | to confirm | not listed in rules | later: PV and EV hosting | not yet used |
| SANS10098-LIGHT | Public lighting levels and layout | SANS 10098 | to confirm part | not listed in rules | later: public lighting | not yet used |
| REDBOOK-LAYOUT | Township layout and servitude guidance for services | Red Book (Guidelines for Human Settlement Planning and Design) | to confirm chapter | not listed in rules | later: servitude and clash checks | not yet used |

### Manufacturer data

Product data for equipment made to a listed standard, used where the governing Eskom specification is not yet held. Each row names the data sheet and its date; values are to be confirmed against the Eskom specification once it is held.

| Id | Check or value | Source | Used by | Status |
| --- | --- | --- | --- | --- |
| CBI-ABC-1C | Single-phase LV ABC, 1 × Al XLPE phase + Al XLPE insulated neutral, 16–150 mm²: DC resistance at 20 °C, AC resistance at 90 °C, reactance, rating in air in shade (still air, 35 °C, SANS 10198-14), 1 s short-circuit rating at 250 °C | CBi-electric african cables data sheet F7CA 2nnn, *Low voltage ABC cable data sheet*, last updated February 2026; SANS 1418, 600/1000 V. 16 mm² from the May 2006 edition of the same sheet, whose resistance, reactance and ratings match the 2026 sheet for every shared size but whose short-circuit ratings are about 10 % lower (end temperature not stated); its 16 mm² short-circuit value is marked a placeholder | `eskom/*` rules from 0.5.0: `conductors[ABC-1C-*]` | transcribed 2026-10-05, supplied by the engineer; tests check the sheet's own consistency (volt drop = 2 × impedance, impedance from R and X, 90 °C resistance from 20 °C); to confirm against Eskom 240-84758170 |
| MTEC-ABC-3C | Three-phase LV ABC, 3 × Al XLPE phases + Al XLPE insulated neutral of the same size, 25–150 mm²: DC resistance at 20 °C, AC resistance at 90 °C, reactance, rating in air, 1 s symmetrical short-circuit rating | M-TEC *Aerial bundle conductor self support*, AS3x, SANS 1418, 600/1000 V, revision R06, 19 January 2022 | `eskom/*` rules from 0.5.0: `conductors[ABC-3C-*]` | transcribed 2026-10-05, supplied by the engineer. Resistance and reactance match the CBi sheets size for size. **Ratings are placeholders**: the sheet gives three-phase bundles the same rating as single-phase ones and states no conditions, and its single-phase ratings are 1–9 % above CBi's. Its three-phase volt-drop column has typos (50 mm² 0.14 for 1.43; 120 mm² 0.44 for 0.58) and is not used: volt drop is calculated. To confirm against Eskom 240-84758170 |

Data sheets reviewed and not used: Eland Cables *LV TNB IEC 60502-1 ABC 1 kV* (3+1 and 3+2 core). It is made to a Malaysian utility (TNB) specification, not SANS 1418, and its table is inconsistent: the resistance column is one size out of step, and the last 3+2 row repeats the first.

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

## Documents held

| Document | Revision | Received | Notes |
| --- | --- | --- | --- |
| Eskom 240-56030637 (alt. 34-1176) *General information and requirements for low-voltage cable systems* | Rev 2, August 2021 | 2026-10-05, from the engineer | Underground LV only. Its next review date (August 2026) has passed; the engineer confirmed on 2026-10-05 that Rev 2 is current. Controlled disclosure, Eskom copyright: the PDF is not kept in this repository; rules files carry only the values the calculations use, with clause references. |
| Eskom 240-92934300 *Technical evaluation criteria for LV ABC with bare or insulated supporting neutral* | Rev 2 (Rev 1 March 2015) | 2026-10-05, from the engineer (also on etenders.gov.za) | A tender evaluation report: no design values. It names the ABC specification, 240-84758170, whose Technical Schedules A and B carry the conductor data, and the product codes in D-DT 3141. |

## Still open

- **Eskom documents identified but not held:** 240-70465489 (voltage regulation and apportionment), 240-75661043 (LV services), 240-84758170 with D-DT 3141 (LV ABC conductors, for overhead impedances and ratings), 240-56063805 (LV cable specification), 240-56062752 (mini-substations), 240-57649065 (LV protection philosophy). Still unidentified: the planning standard (ESKOM-PLANNING) and the overhead line standard (ESKOM-OHL).
- **Copies of NRS 034-1, NRS 048-2, SANS 1507, SANS 1418, SANS 780, SANS 1019, SANS 97 and IEC 60909**, for municipal projects and for values Eskom does not set.

Until these are in hand, Phase 2 can be built and tested against hand-worked cases, but every rules value stays *unverified* and no design is fit to submit.
