# Conductor and cable data: typical published values

Collected 2026-10-07 to fill the placeholder impedances and ratings in the conductor library (plan 2.3, `conductors` and `mv_conductors` in `rules/eskom/0.5.0.yaml` and `0.8.0.yaml`). Every value below is a manufacturer's published figure for a SANS product, and none is verified by the engineer. Record the engineer's decision in docs/standards-index.md.

**Encoded in `rules/eskom/0.9.0.yaml`** (2026-10-07), averaging suppliers where they differ:

- the LV cable impedances (index LV-CABLE-IMPEDANCE);
- the three-phase ABC ratings, as the mean of M-TEC, Voltex and CBi (still placeholders);
- the Airdac service cables below (index AIRDAC-SNE).

The MV cable data are not yet encoded. They wait on the MV resistance-temperature fix, because the MV calc does not yet use `r_ac_ohm_per_km`.

## What the library still needs

| Library entries | Placeholder or missing | Found below |
|---|---|---|
| `CU-4C-*`, `CU-2C-*` (LV PVC, SANS 1507-3) | resistance at 70 °C, reactance | yes, CBi; 2-core 16 to 50 mm² only |
| `AL-4C-*` (LV PVC, SANS 1507-3) | resistance at 70 °C, reactance | yes, CBi |
| `AL-2C-25`, `AL-2C-35` (services) | resistance at 70 °C, reactance | no sheet found |
| `ABC-3C-*` (SANS 1418) | current ratings and their basis | three sources compared, basis still the engineer's call |
| `ABC-1C-16` | short-circuit constant | consistent with the other sizes, see the note |
| `MV-AL-3C-*`, `MV-CU-3C-*` (11 kV XLPE) | everything | yes, CBi, including capacitance and zero sequence |

## Sources

1. CBi-electric African Cables, *Low Voltage PVC Bell Cables*, copper 4-core 1.5–16 mm², copper 4-core with ECC 25–400 mm², copper 2-core 1.5–50 mm², aluminium 4-core with ECC 25–300 mm²; SANS 1507, 600/1000 V; last updated May 2006. Published by the distributor Versalec ([copper 4-core](https://www.versalec.co.za/download-catalogue/low-voltage-cable-range/1kv-fr-pvc-copper-4-core-armoured.pdf), [copper 4-core ECC](https://www.versalec.co.za/download-catalogue/low-voltage-cable-range/1kv-pvc-copper-4-core-armoured-ecc.pdf), [copper 2-core](https://www.versalec.co.za/download-catalogue/low-voltage-cable-range/1kv-fr-pvc-copper-2-core-armoured.pdf), [aluminium 4-core ECC](https://www.versalec.co.za/download-catalogue/low-voltage-cable-range/1kv-pvc-aluminium-4-core-armoured-ecc.pdf)).
2. CBi-electric African Cables, *MV XLPE Cable Data Sheet*, 6350/11000 V 3-core, SANS 1339, last updated October 2016: [aluminium Type A armoured](https://www.versalec.co.za/download-catalogue/xlpe-cables/11kv-mv-xlpe-aluminium-3-core-type-a-armoured-pvc.pdf), [copper Type B](https://www.versalec.co.za/download-catalogue/xlpe-cables/11kv-mv-xlpe-copper-3-core-type-b-pvc.pdf).
3. CBi-electric African Cables, *Low Voltage ABC Cables*, aluminium 3 core + neutral, self-supporting, SANS 1418, last updated May 2006 ([data sheet](https://www.versalec.co.za/download-catalogue/aerial-bundle-cables-abc/1kv-lv-abc-aluminium-3-core-plus-insulated-neutral.pdf)).
4. Voltex *Cable & Wire Catalogue* (Aberdare-made products), with tables for PVC SWA 3- and 4-core cables to SANS 1507-3, ABC to SANS 1418 and 6.35/11 kV XLPE to SANS 1339 ([catalogue](https://www.specifile.co.za/wp-content/uploads/2018/10/Cable-Wire-Catalogue.pdf)). Used here as a cross-check.
5. Nexans Australia, *Aerial* catalogue, LV ABC to AS/NZS 3560 ([catalogue](https://www.nexans.com.au/en/dam/jcr:c6ca111d-2382-467d-94b6-0cddd21647cb/OLC12641_AerialCat%20LR.pdf)). It rates 2-core and 4-core bundles separately, which the South African sheets do not.
6. Eskom 240-56063805 Rev 1 (May 2013), *LV Power and Control Cable with Rated Voltage Standard* ([eTenders](https://www.etenders.gov.za/home/Download/?blobName=461e1b02-42d6-415e-abe1-b7287010992e.pdf&downloadedFileName=240-56063805+LV+Power+and+Control+Cable+with+Rated+Voltage+Standard.pdf)), and Eskom 240-56063792 Rev 2, *Specification for medium voltage XLPE and impregnated paper insulated cables* ([eTenders](https://www.etenders.gov.za/home/Download/?blobName=94890c19-3d87-40bd-ac54-75c95933780b.pdf&downloadedFileName=MWP1248DX+-+240-56063792+-+MV+Cable+Specification.pdf)).

## What the Eskom specifications say

Neither Eskom cable specification tabulates impedances or ratings. Both require the tenderer to supply them in Schedule B: the 50 Hz AC resistance at maximum operating temperature, reactance, capacitance, zero-sequence impedance, and ratings in ground, air and ducts (240-56063805 §3.2.6; 240-56063792 §3.2.6). So the supplier's SANS 1507 or SANS 1339 data sheet is the intended source. Plan 2.3 is not blocked on these documents. It needs the engineer to accept a supplier's data.

240-56063792 Table 1 lists the standard 3-core MV sizes for 11, 22 and 33 kV as 50, 95, 185, 300, 400 and 630 mm². The library's 35 and 70 mm² MV cables are not standard Eskom sizes. The library also cites SANS 97 for its XLPE cables. SANS 97 covers paper-insulated cables; XLPE cables are SANS 1339.

## LV PVC SWA cables, SANS 1507-3 (source 1)

Resistance is AC at 70 °C, the PVC operating temperature. Zero-sequence values are the sheet's own; it does not state the return path, and they are far above four times the phase resistance, so they likely assume return through the armour and earth rather than the neutral core.

**Copper, 4-core**

| mm² | R ac 70 °C Ω/km | X Ω/km | R0 Ω/km | X0 Ω/km | 1 s fault kA |
|---|---|---|---|---|---|
| 16 | 1.38 | 0.087 | 10.24 | 0.12 | 2.0 |
| 25 | 0.87 | 0.080 | 9.688 | 0.064 | 2.6 |
| 35 | 0.63 | 0.077 | 8.519 | 0.063 | 3.6 |
| 50 | 0.46 | 0.077 | 7.897 | 0.065 | 4.9 |
| 70 | 0.32 | 0.072 | 5.616 | 0.061 | 7.1 |
| 95 | 0.23 | 0.071 | 5.075 | 0.062 | 9.8 |
| 120 | 0.18 | 0.069 | 3.84 | 0.059 | 12.3 |
| 150 | 0.15 | 0.070 | 3.331 | 0.060 | 15.2 |
| 185 | 0.12 | 0.069 | 3.276 | 0.061 | 19.1 |
| 240 | 0.09 | 0.069 | 3.063 | 0.062 | 25.0 |
| 300 | 0.08 | 0.069 | 2.954 | 0.063 | 31.4 |

The sheet gives copper resistance to two decimals only. Source 4 gives the 70 °C impedance to four decimals and agrees within rounding: 0.8749 Ω/km at 25 mm², 0.3325 at 70 mm², 0.1220 at 240 mm².

**Aluminium, 4-core**

| mm² | R ac 70 °C Ω/km | X Ω/km | 1 s fault kA |
|---|---|---|---|
| 25 | 1.442 | 0.080 | 2.0 |
| 35 | 1.043 | 0.077 | 2.8 |
| 50 | 0.771 | 0.077 | 3.8 |
| 70 | 0.533 | 0.072 | 5.6 |
| 95 | 0.385 | 0.071 | 7.7 |
| 120 | 0.305 | 0.069 | 9.7 |
| 150 | 0.249 | 0.070 | 12.0 |
| 185 | 0.199 | 0.069 | 15.0 |
| 240 | 0.152 | 0.069 | 19.7 |
| 300 | 0.123 | 0.069 | 24.6 |

The aluminium sheet has no zero-sequence values. It misprints the 185 mm² DC resistance as 1.640; the 70 °C value of 0.199 matches 0.164 at 20 °C.

**Copper, 2-core (services)**

| mm² | R ac 70 °C Ω/km | X Ω/km | R0 Ω/km | X0 Ω/km |
|---|---|---|---|---|
| 16 | 1.38 | 0.087 | 9.195 | 0.119 |
| 25 | 0.87 | 0.065 | 7.430 | 0.069 |
| 35 | 0.63 | 0.061 | 6.795 | 0.068 |
| 50 | 0.46 | 0.061 | 5.641 | 0.071 |

The 25 to 50 mm² 2-core cables have D-shaped conductors, hence their lower reactance. No aluminium 2-core sheet was found. The aluminium conductor's resistance is the same as in the 4-core table, and the copper 2-core reactance is the nearest stand-in for the same geometry.

**Ratings.** The library's ratings come from Eskom 240-56030637 Rev 2 and should stay. The CBi ratings are within a few amps of them (copper 4-core 25 mm²: CBi 115 A in ground, 98 A in ducts, 110 A in air; library 119, 96 and 109 A).

## 6.35/11 kV 3-core XLPE cables, SANS 1339 (source 2)

Ratings are for a single circuit in isolation in soil at 1.2 K·m/W and 25 °C, 800 mm deep, with 30 °C air. Resistance is AC at 90 °C. Neither sheet gives a rating in ducts. The fault ratings are symmetrical, 1 s, to 250 °C.

**Aluminium (Type A, armoured)**

| mm² | R ac 90 °C Ω/km | X Ω/km | C nF/km | R0 Ω/km | X0 Ω/km | Ground A | Air, shade A | Air, sun A | Fault kA |
|---|---|---|---|---|---|---|---|---|---|
| 25 | 1.539 | 0.128 | 215 | 2.721 | 0.149 | 105 | 114 | 100 | 2.2 |
| 35 | 1.113 | 0.121 | 240 | 2.222 | 0.142 | 125 | 137 | 121 | 3.0 |
| 50 | 0.822 | 0.115 | 265 | 1.864 | 0.137 | 147 | 163 | 143 | 4.0 |
| 70 | 0.568 | 0.107 | 295 | 1.540 | 0.130 | 179 | 202 | 177 | 5.8 |
| 95 | 0.411 | 0.102 | 332 | 1.307 | 0.124 | 214 | 243 | 213 | 8.1 |
| 120 | 0.325 | 0.098 | 366 | 1.160 | 0.120 | 242 | 279 | 244 | 10.2 |
| 150 | 0.265 | 0.095 | 390 | 0.945 | 0.119 | 270 | 314 | 274 | 12.6 |
| 185 | 0.211 | 0.092 | 426 | 0.848 | 0.115 | 304 | 358 | 312 | 15.8 |
| 240 | 0.162 | 0.088 | 475 | 0.748 | 0.110 | 350 | 417 | 363 | 20.7 |
| 300 | 0.130 | 0.086 | 507 | 0.687 | 0.108 | 391 | 469 | 408 | 25.9 |

**Copper (Type B)**

| mm² | R ac 90 °C Ω/km | X Ω/km | C nF/km | Ground A | Air, shade A | Air, sun A | Fault kA |
|---|---|---|---|---|---|---|---|
| 25 | 0.927 | 0.126 | 215 | 140 | 152 | 134 | 3.4 |
| 35 | 0.668 | 0.118 | 240 | 167 | 184 | 162 | 4.7 |
| 50 | 0.494 | 0.112 | 265 | 197 | 220 | 194 | 6.4 |
| 70 | 0.342 | 0.106 | 295 | 240 | 272 | 239 | 9.2 |
| 95 | 0.247 | 0.100 | 332 | 285 | 325 | 286 | 12.8 |
| 120 | 0.196 | 0.096 | 366 | 323 | 375 | 329 | 16.2 |
| 150 | 0.159 | 0.094 | 390 | 362 | 423 | 371 | 20.0 |
| 185 | 0.128 | 0.091 | 426 | 408 | 484 | 424 | 25.0 |
| 240 | 0.099 | 0.088 | 475 | 472 | 569 | 497 | 32.8 |
| 300 | 0.080 | 0.086 | 507 | 528 | 644 | 562 | 41.2 |

Source 4's 11 kV table agrees on ground ratings within a few per cent and gives impedance only, for example aluminium 95 mm² at 0.4213 Ω/km against CBi's 0.423.

**Compared with the library's placeholders**, at aluminium 95 mm²: the library uses 0.320 Ω/km, which is the 20 °C DC value, against 0.411 at 90 °C. The MV drop calc (`mv/network.py`) uses that 20 °C value as it stands, unlike the LV calc, which corrects to operating temperature, so MV cable drop is currently understated by about a quarter. Its reactance of 0.11 Ω/km compares with 0.102, its capacitance of 310 nF/km with 332, and its rating in ground of 215 A with 214 A. Its short-circuit constant of 0.094 kA/mm² (the IEC 60949 value for aluminium XLPE) compares with 0.085 implied by CBi's 8.1 kA. For copper it is 0.143 against 0.135.

## Three-phase LV ABC ratings, SANS 1418 (sources 3, 4, 5)

The library's three-phase ratings are M-TEC's, which equal its single-phase ratings and state no conditions. The other sources compare as follows, in amps:

| mm² | Library (M-TEC 3C+N) | Source 4, 3C on supporting core | CBi 3C+N | Nexans 4-core, still air | Nexans 4-core, 1 m/s | Nexans 4-core, 2 m/s |
|---|---|---|---|---|---|---|
| 25 | 105 | 105 | 111 | 59 | 97 | 115 |
| 35 | 144 | 144 | 138 | 72 | 120 | 135 |
| 50 | 183 | 183 | 168 | 88 | 140 | 165 |
| 70 | 228 | 228 | 213 | 110 | 175 | 205 |
| 95 | 277 | 277 | 258 | 135 | 215 | 255 |
| 120 | 322 | 322 | 300 | 155 | 250 | 300 |
| 150 | 340 | 350 | 339 | 180 | 280 | 345 |

- **Source 4 states the basis M-TEC lacks.** Its three-phase supporting-core bundle matches M-TEC size for size except at 150 mm². It states 35 °C ambient and 80 °C maximum conductor temperature, with temperature factors of 1.11 at 25 °C, 1.05 at 30 °C, 0.94 at 40 °C and 0.88 at 45 °C. Its 1 s short-circuit ratings are to 130 °C.
- **CBi** gives its three-phase bundle the same ratings as its single-phase one, as M-TEC does. The single-phase sheet states still air in shade at 35 °C.
- **Nexans** rates at 80 °C conductor temperature. Its 4-core bundles are 5 to 7 % below its 2-core ones. Its still-air ratings are about half the South African figures, which match its 1 to 2 m/s wind ratings instead. The South African figures therefore look like wind-cooled or shaded ratings, which are optimistic for still, sunny conditions.

The engineer has to choose the rating basis: the South African figures as published, or a still-air basis with solar gain. Eskom 240-84758170 would settle it but is not public. Its Technical Schedules A and B are cited in 240-92934300.

**ABC-1C-16 short-circuit constant.** The library's 0.0875 kA/mm² for 16 mm² sits between source 4's 25 mm² figure (2.3 kA, or 0.092) and Nexans' 16 mm² figure (1.4 kA, or 0.0875). Keeping 0.0875 is consistent with both.

## Airdac SNE house service cable, SANS 1507-6 (encoded in eskom/0.9.0)

Airdac SNE is a copper phase conductor with split neutral and earth conductors and two pilot cores, XLPE insulated with a PE sheath, rated 600 V. It is the engineer's overhead house service. Sources:

- Aberdare Cables *Low Voltage Cable Range* (Airdac SNE house service connecting cable);
- the Alvern Cables Airdac data sheet ([pdf](https://www.alverncables.co.za/download/domestic-cables/airdac-cables.pdf)).

| | 10 mm² | 16 mm² | Source |
|---|---|---|---|
| Phase resistance, DC 20 °C, Ω/km | 1.90 | 1.20 | Aberdare |
| Rating in air, A | 50 | 70 | Aberdare and Alvern agree. Aberdare: 30 °C ambient, 90 °C conductor |
| Diameter, mm | 12.8 | 14.5 | Aberdare and Alvern agree |
| Mass, kg/km | 320 | 485 | Aberdare and Alvern (32.00 and 48.50 kg/100 m) agree |
| Breaking load (UTS), N | 3600 | 5760 | Aberdare |
| Maximum working tension, N | 900 | 1440 | Aberdare: 25 % of UTS |
| Installation sag, 50 m span, mm | 1110 | 1050 | Aberdare's table. It is m·g·L²/(8·MWT) with g = 10 m/s² |

Aberdare's sag table runs from 10 to 50 m and is reproduced by the formula at every span: 10 mm² at 45, 180, 400, 710 and 1110 mm. The calc uses g = 9.81 m/s², which gives sags about 2 % smaller.

- **Reactance** is not published by either supplier. The rules use 0.08 Ω/km, marked a placeholder.
- **JYTOP**, an exporter, rates the same sizes at 67 and 89 A with no stated basis. Those figures are about 30 % above both local makers, so they are not averaged in.

## Not covered

- **Zero-sequence impedance of LV ABC** was not found. The rules' ratio of 4 for resistance assumes return through a neutral of the same size, which suits ABC. The LV cable sheets' zero-sequence values appear to assume armour return instead.
- **MV duct ratings** are on no sheet. The library's `pipe` values stay placeholders.
- **Overhead MV conductors** (ACSR and AAAC to SANS 182) were not looked up. Versalec publishes data sheets for Squirrel, Fox, Mink, Hare, Wolf and Panther.
