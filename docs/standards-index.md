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
| REDBOOK-VDROP | Apportionment of LV voltage drop: about 8 % on the LV distributor including service connections, 9 % or a little more where the regulated busbar is electrically close and its LDC follows the area's feeder; service cables near the feeder end under 2 % at the undiversified ADMD | CSIR *Guidelines for Human Settlement Planning and Design* (Red Book), ch. 12.1 Grid electricity | "Voltage drop" and its notes (no clause numbers) | to confirm (copy read 2026-10-07) | `eskom/*` rules from 0.9.0: `voltage.lv_max_drop_pct`, `voltage.lv_service_max_drop_pct` (via ESKOM-VDROP) | read 2026-10-07; a national guideline, not an Eskom standard; supports the engineer's 8 % |
| ENERGEX3064638-LVFUSE | Prospective bolted fault at the end of an LV feeder greater than 3 times the LV fuse rating; LV fuses not loaded above their rating and not above the ABC's rating | Energex (Queensland) *Standard for Pole Transformer Fusing*, Document 3064638 | §4 General fusing requirements; §5 (fuse loading) | Release 5, 19 December 2024 | `eskom/*` rules from 0.9.0: `lv_design.protection.min_fault_multiple` (via RETICULA-LVPROT) | read 2026-10-07; a foreign utility's rule, used as the engineering assumption until Eskom 240-57649065 or 240-56030637 Table 10 (ESKOM-LVPROT) is transcribed |
| IEC60269-FUSE | Low-voltage fuses: NH gG fuse links and their preferred current ratings | IEC 60269-2 | to confirm | 2013 (rules, unconfirmed) | `eskom/*` rules from 0.9.0: `lv_design.protection.fuse_ratings_a` (via RETICULA-LVPROT) | not held; ratings 63–400 A are the common NH00 to NH2 series, to confirm against what Eskom stocks |
| SANS10142-SUPPLY | Supply point requirements at the consumer's installation | SANS 10142-1 | to confirm | not listed in rules | Phase 2 service connections | not yet used |

### MV, transformers and bulk supply (Phases 3–4)

| Id | Check or value | Standard | Clause | Edition | Used by | Status |
| --- | --- | --- | --- | --- | --- | --- |
| SANS780-RATING | Standard ratings of distribution transformers | SANS 780 | to confirm | not listed in rules | `eskom/*` rules from 0.7.0: `mv_design.transformer_ratings_kva` (plan 2.0 placement); Phase 3.1 | preferred series 16–500 kVA transcribed from memory 2026-10-06; to confirm against the standard |
| SANS1019-VOLT | Standard system voltages and insulation levels | SANS 1019 | to confirm | not listed in rules | `voltage.mv_nominal_kv`; Phase 3 | not yet used |
| SANS1029-MINISUB | Miniature substation construction and ratings | SANS 1029 | – | – | – | withdrawn 2026-10-05: not used by the engineer; see ESKOM-MINISUB |
| SANS97-MVCABLE | MV cable construction and ratings | SANS 97 | to confirm | not listed in rules | Phase 3.3 | not yet used |
| NRS048-MVLIMIT | MV voltage limits and tap setting range | NRS 048-2 | to confirm | 2024 (rules, unconfirmed) | Phase 3.4 | not yet used |
| IEC60909-FAULT | Short-circuit currents: method, voltage factor c, 3-phase and 1-phase | IEC 60909-0 | to confirm | 2016 (rules, unconfirmed) | Phase 4.4 (pandapower) | not yet used |
| SANS182-ACSR | Bare overhead conductors for MV lines (ACSR and AAAC named Fox, Gopher, Hare, Mink, Oak, Rabbit, Squirrel, Wolf, Panther): resistance, rating, mass, breaking load | SANS 182 (ACSR) and SANS 1418 (AAAC); conductor names as in Eskom 240-87658920 §4.3.4 | to confirm | not listed in rules | `eskom/*` rules from 0.8.0: `mv_conductors`, `overhead.mechanical` | unverified: typical published values, every field marked placeholder |
| ESKOM-SUPPLY | Bulk supply: notified maximum demand steps, MV switchgear short-time rating, fault clearing times, IEC 60909 voltage factors | Eskom supply and protection standards, numbers to identify; IEC 60909-0 Table 1 for c | to confirm | to identify | `eskom/*` rules from 0.8.0: `bulk`; plan 4.4–4.5 | to identify; voltage factors are the well-known Table 1 values, to confirm against the edition held |

### Eskom standards (govern Eskom projects)

The engineer designs Eskom projects to Eskom's own standards. Where a document number is not yet known, the row names the subject. Numbers found in 240-56030637 §2.2.1 are recorded, but only that document is held so far (see **Documents held** below).

| Id | Check or value | Standard | Clause | Edition | Used by | Status |
| --- | --- | --- | --- | --- | --- | --- |
| ESKOM-PLANNING | Residential electrification planning and design: ADMD, design horizon, phasing; overrides NRS 034-1 where they differ | Eskom distribution standard, number to identify | to confirm | to identify | `eskom/*` rules: `income_admd`, `load_tables` | to identify |
| ESKOM-VDROP | LV voltage drop allocation and limits for Eskom networks; overrides NRS034-VDROP | Eskom 240-70465489 *Distribution voltage regulation and apportionment limits* (cited by 240-56030637 §3.10.5) | to confirm | to identify | `eskom/*` rules: `voltage.lv_max_drop_pct` (index set from eskom/0.3.0); `lv_design` (from eskom/0.6.0: power factor, conductor temperature, source transformer, all placeholders); from eskom/0.9.0 also `voltage.lv_service_max_drop_pct`; formulas `lv.vdrop.herman-beta.v1`, `lv.vdrop.customer.v1`, `lv.service.drop.v1` | number identified 2026-10-05; document not held, and not found on eTenders (2026-10-07). From eskom/0.9.0 the LV limit is 8 % from the board to the meter, service included: the engineer's value (2026-10-07, "8–10 %" from Eskom tenders), taken at the low end to match REDBOOK-VDROP. Not a placeholder, but the Eskom clause stays unverified. The 2 % service limit is the Red Book's guidance and stays a placeholder until the engineer confirms it |
| ESKOM-CONDUCTOR | Standard LV and MV conductors and cables, with impedances and ratings | Underground: Eskom 240-56063805 (LV power and control cable 600/1000 V), per 240-56030637 §3.9.1 a). Overhead ABC: Eskom 240-84758170 (*Aerial bundled conductors with bare or insulated neutral supporting conductor*), Technical Schedules A and B; product codes D-DT 3141; per 240-92934300 §2.2.2 | to confirm | to identify | `eskom/*` rules: `conductors` (index set from eskom/0.3.0) | numbers identified 2026-10-05; documents not held. Underground cable ratings: see ESKOM-LVCABLE-RATING |
| ESKOM-DERATE | Cable current derating: standard installation conditions, grouping, cables in pipes | Eskom 240-56030637 *General information and requirements for LV cable systems* | §3.9.1 o)–p) (standard conditions: 70 °C conductor, 25 °C soil, 30 °C air, 1,2 K·m/W, 0,5 m deep); §3.9.6 i) (pipe rating where over 10 % is in pipe); de-rating annex (grouping, in ground and in ducts) | Rev 2, Aug 2021 | Phase 2.6 | clause located 2026-10-05; Rev 2 confirmed current by the engineer; values to confirm when transcribed into a rules file. The standard gives grouping factors only; soil and depth factors come from SANS 10198-4, which it cites |
| ESKOM-OHL | Overhead line spans, sag and tension, clearances, poles and stays | Eskom distribution standard, number to identify | to confirm | to identify | Phase 2.5 | to identify |
| ESKOM-MINISUB | Transformer and mini-sub selection and standard sizes | Eskom 240-56062752 (MV miniature substations 11–22 kV), per 240-56030637 §2.2.1 | to confirm | to identify | Phases 3.1–3.2 | number identified 2026-10-05; document not held. Feeders per mini-sub: see ESKOM-LVPROT |
| ESKOM-SERVICE | Service connections and phasing: longest service, where services connect, when a supply is three-phase | Eskom 240-75661043 Part 8 Section 3 (outdoor LV services for SPU and LPU), per 240-56030637 §2.2.1 | to confirm | to identify | `eskom/*` rules: `lv_loads` (from eskom/0.4.0) | number identified 2026-10-05; document not held. 2 boxes a pole, 4 loads a box (filled before the next box is started) and the second box on another phase are the engineer's stated practice (2026-10-05); from eskom/0.9.0 the reach is the engineer's 50 m service span (2026-10-07; see RETICULA-SERVICE); 15 kVA is a placeholder |
| ESKOM-LVCABLE-RATING | Continuous current rating of 600/1000 V armoured LV cables, Cu and Al, 16–240 mm², in ground, in pipes and in air; feeder cables four-core, services two- or four-core | Eskom 240-56030637 *General information and requirements for LV cable systems* | §3.9.1 h)–k), n); Tables 6 (Cu) and 7 (Al) | Rev 2, Aug 2021 | `eskom/*` rules from 0.5.0: `conductors[*].ratings_a`, `rating_a`; plan 2.3 | transcribed 2026-10-05 into eskom/0.5.0, checked by the calc tests against Tables 1 and 2; Rev 2 confirmed current; engineer to confirm the transcription |
| ESKOM-LVCABLE-FAULT | Short-circuit withstand of LV cables: I = K·A/√t, K 0,115 (Cu) and 0,076 (Al); phase and earth fault levels | Eskom 240-56030637 *General information and requirements for LV cable systems* | §3.9.1 m); Tables 1–5 | Rev 2, Aug 2021 | `eskom/*` rules from 0.5.0: `conductors[*].fault_k`; formula `lv.cable.withstand.v1` | transcribed 2026-10-05 into eskom/0.5.0, checked by the calc tests against Tables 1 and 2; Rev 2 confirmed current; engineer to confirm the transcription |
| ESKOM-LVPROT | LV feeder protection per cable size (MCCB and fuse ratings); at most 5 LV feeders from a Type A mini-sub, 6 from a Type B | Eskom 240-56030637 *General information and requirements for LV cable systems* | §3.6; §3.9.12 a); Table 10 and its note | Rev 2, Aug 2021 | Phases 2.4 and 3.2 | clause located 2026-10-05; Rev 2 confirmed current by the engineer; values to confirm when transcribed into a rules file. LV protection philosophy is in 240-57649065 (not held) |
| ESKOM-KIOSK | Underground supply: LV feeders supply metering kiosks, never customers directly; at each kiosk, MCBs on one phase grouped, at most 4 per phase, and the kiosk balanced across phases | Eskom 240-56030637 *General information and requirements for LV cable systems* | §2.3.1 (LV feeder cable); §3.5.3 d)–f); §3.10 e) | Rev 2, Aug 2021 | Underground load allocation (kiosks not yet modelled: plan 2.2 covers overhead only; see ADR 0007) | clause located 2026-10-05; Rev 2 confirmed current by the engineer; values to confirm when transcribed into a rules file |
| ESKOM-DRAW | Drawing practice for electrification networks: symbols (Annex B legend), MV red and LV green (§4.3.4), underground MV magenta and LV blue (Annex D), transformer zones (§4.3.8), MV and LV line styles by conductor and phase (§4.3.4), pole tags (§4.3.5) | Eskom 240-87658920 (alt. DST 34-195) *Standard drawing practice for CAD users ... and for electrification networks* | Annex B, Annex C, Annex D, §4.3.4–4.3.8 | Rev 1, September 2009; stabilised March 2019 | Web: `src/web/src/app/shared/symbols.ts` (map markers, tool buttons, legends). Later: DXF/DGN export (plan 6) must use ElecCell.cel and ElecLines.rsc, which the document names but does not contain | document held (2026-10-06); symbols transcribed as SVG from Annex B; line styles per phase and conductor not yet drawn |

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

| LV-CABLE-IMPEDANCE | 600/1000 V PVC/SWA/PVC LV cables, SANS 1507-3: AC resistance at 70 °C and reactance; copper 4-core 16–240 mm², copper 2-core 16–35 mm², aluminium 4-core 25–240 mm² | CBi-electric African Cables *Low Voltage PVC Bell Cables* sheets F4CC, F2CC and F4AC with ECC (May 2006, via Versalec); Voltex *Cable & Wire Catalogue* (Aberdare products), copper 3- and 4-core impedance | `eskom/*` rules from 0.9.0: `conductors[CU-*, AL-*].r_ac_ohm_per_km`, `x_ohm_per_km` | transcribed 2026-10-07, see docs/conductor-data.md. Copper 4-core R is the mean of CBi and Voltex (Voltex gives impedance only; its R is taken as √(Z² − X²) with CBi's X); other cables are CBi alone. Aluminium 2-core has no sheet: its values are derived and stay placeholders. Eskom 240-56063805 has no impedance table (it asks the tenderer for one), so supplier data is the intended source; the engineer is to accept it |
| AIRDAC-SNE | Airdac SNE overhead house service cable, 10 and 16 mm² Cu: DC resistance, rating in air, mass, diameter, breaking load (UTS), maximum working tension and installation sag table | Aberdare Cables *Low Voltage Cable Range* (Airdac SNE house service connecting cable); Alvern Cables *Airdac* data sheet (SNE10, SNE16); SANS 1507-6 | `eskom/*` rules from 0.9.0: `conductors[AIRDAC-SNE-*]`, `overhead.mechanical[AIRDAC-SNE-*]`, `services.overhead.tension_pct` | transcribed 2026-10-07. The two South African suppliers agree on rating (50 and 70 A), diameter and mass; resistance, UTS and working tension are Aberdare's alone. JYTOP (export) gives 67 and 89 A with no stated basis and is not averaged in. Reactance is not published: 0.08 Ω/km assumed, a placeholder |

Data sheets reviewed and not used: Eland Cables *LV TNB IEC 60502-1 ABC 1 kV* (3+1 and 3+2 core). It is made to a Malaysian utility (TNB) specification, not SANS 1418, and its table is inconsistent: the resistance column is one size out of step, and the last 3+2 row repeats the first.

### Not standards

These values are Reticula's own and say so in their `clause` text. They are listed so the register is complete.

| Id | Value | Source | Used by | Status |
| --- | --- | --- | --- | --- |
| RETICULA-PREDICT | Building-type prediction from OSM tags, zoning and footprint | Reticula heuristic v1 | `building_prediction` | heuristic: tune per authority |
| RETICULA-SCORE | Income indicator scoring and thresholds | Reticula heuristic v1 | `income_admd` indicator points and bands | heuristic: needs calibrating (docs/load-data.md) |
| RETICULA-LVMODEL | Drawing tolerances for joining marked LV routes and sites into a network: join, near-miss, source and pole reach | Reticula LV network tolerances v1 | `lv_network` (from eskom/0.3.0); plan 2.1 | own values: tune to how routes are drawn in the field |
| RETICULA-MVMODEL | MV network drawing tolerances (join, near miss, connection point and tee-off reach) and MV design defaults | Reticula setting, not a standard | – | – | `eskom/*` rules from 0.8.0: `mv_network`; plan 3.3 | Reticula's own; conductor and power factor defaults are placeholders |
| RETICULA-ECON | Default economic assumptions for lifetime cost: period, discount rate, energy cost, growth, loss load factor, rate uncertainty | Reticula setting, not a standard | – | – | `eskom/*` rules from 0.8.0: `economics`; plan 5.1 | Reticula's own; a design run may override every value |
| RETICULA-LOADCLASS | Load class of a building not yet inspected, from its stand's zoning | Reticula setting, not a standard | – | – | `eskom/*` rules from 0.8.1: `load_classes`; plans 1.3, 2.0 | Reticula's own; the engineer sets the zoning-to-class table for each area; the inspection replaces it |
| RETICULA-LVPROT | LV feeder fuse selection: one NH gG fuse per feeder, I_B ≤ I_n ≤ I_z, least fault on the feeder at least 3 × I_n | Engineering assumption (engineer 2026-10-07), after ENERGEX3064638-LVFUSE and IEC60269-FUSE | `eskom/*` rules from 0.9.0: `lv_design.protection`; formula `lv.protection.fuse.v1` | assumption, placeholders; replace with Eskom 240-56030637 Table 10 (held, underground, to transcribe) and 240-57649065 (not held). A 5 s disconnection criterion would need about 5 to 6 × I_n for gG fuses, stricter than 3 × |
| RETICULA-SERVICE | Overhead services: Airdac SNE, spans at most 50 m, a 7 m service pole where the sag is too low; clamp 0.6 m below the line, house attachment 3.5 m, clearance 3.0 m; strung at 25 % of UTS | Engineer 2026-10-07 (Airdac, 50 m spans, 7 m service pole); supplier tension (AIRDAC-SNE); the heights and clearance are engineering assumptions | `eskom/*` rules from 0.9.0: `services`, `lv_loads.max_service_m`; formulas `lv.service.drop.v1`, `oh.service.span.v1` | conductor, span and pole height from the engineer; heights, clearance and the three-phase and underground service conductors are placeholders (Eskom 240-75661043 and the service clearance standard not held) |

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
| Eskom 240-87658920 (alt. DST 34-195) *Standard drawing practice for CAD users in the power plant and control plant technologies environment and for electrification networks* | Rev 1, September 2009 (stabilised 2019-03-11) | 2026-10-06, from the engineer | Symbols, levels and colours for electrification drawings; the cell and line style libraries (ElecCell.cel, ElecLines.rsc) are separate Eskom files not held. Controlled disclosure: the PDF is not kept in this repository. |

## Still open

- **Eskom documents identified but not held:** 240-70465489 (voltage regulation and apportionment), 240-75661043 (LV services), 240-84758170 with D-DT 3141 (LV ABC conductors, for overhead impedances and ratings), 240-56063805 (LV cable specification), 240-56062752 (mini-substations), 240-57649065 (LV protection philosophy). Still unidentified: the planning standard (ESKOM-PLANNING) and the overhead line standard (ESKOM-OHL).
- **Copies of NRS 034-1, NRS 048-2, SANS 1507, SANS 1418, SANS 780, SANS 1019, SANS 97 and IEC 60909**, for municipal projects and for values Eskom does not set.

Until these are in hand, Phase 2 can be built and tested against hand-worked cases, but every rules value stays *unverified* and no design is fit to submit.
