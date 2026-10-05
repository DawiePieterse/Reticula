# ADR 0010 – LV overhead line checks: spans, sag and tension, clearance, poles and stays

Date: 2026-10-05 · Status: accepted

## Decision

A. Building the LV network now also runs the overhead line checks (`POST /calc/lv/overhead`), after the voltage-drop checks. They need only the network, not the loads. The API stores the results with the network. If the rules file has no `lv_overhead` section (before `eskom/0.7.0`), the checks are skipped with an `overhead_skipped` issue. Every LV route is taken as overhead, as agreed for plan 2.2; underground cables come with kiosks.
B. **Supports and spans.** Every marked pole and every source is a support. A span runs straight between two supports along a route, through any joints. Route ends and junctions not at a pole are checked as if a pole stood there, and flagged `no_support`: the conductor cannot end or branch in mid-air. A route that strays more than `bend_tolerance_m` from the straight line between its supports is flagged `bend_without_pole`. A span longer than `max_span_m` is flagged `span_too_long`.
C. **Roles.** A support with one span is a terminal, and one with three or more is a junction. With two spans, the deviation decides:
   - up to `intermediate_max_deg`: intermediate
   - up to `strain_angle_deg`: angle
   - beyond that: strain.

   Terminals, junctions, sources, strain angles and changes of conductor are strain supports. They split the line into strain sections.
D. **Tension.** Each section's ruling span is RS = √(Σl³/Σl). The tension in each loading case (hot, cold, wind) follows from the everyday state by the parabolic state change equation on RS. The load per metre is the bundle's weight combined with the wind on its diameter. The tensile stiffness is the area of all cores, since self-supporting ABC carries the tension on every core. A section is strung at the everyday tension (`tension_pct` of the conductor's maximum pull) unless the cold or wind case would then exceed `max_tension_pct` of it. In that case it is strung slack, so that the worse case just reaches the limit. Short spans are usually governed this way: they tighten most in the cold. The section reports which limit governed.
E. **Sag and clearance.** A span's sag is w·l²/(8·H) at the hot case. The clearance is the attachment height (pole length less planting depth less the attachment's distance below the top) minus the sag, on flat ground with both ends at the same height. Clearances below `min_ground_clearance_m` are flagged `clearance`.
F. **Pole loads and stays.**
   - **Load:** the horizontal load at the attachment is |Σ H·u| over the spans at the support, plus the wind on half of each span. The governing case is the largest of everyday, cold and wind.
   - **Pole class:** the lightest class that carries the load unstayed is chosen.
   - **Stays:** a pole is stayed when no class can carry its load, and every terminal is stayed when `stay.terminals` is set. A stayed pole takes the lightest class, and its stay carries the load at `stay.angle_from_ground_deg`. Stays over `stay.capacity_kn` are flagged `stay_overloaded`.
   - **Sources:** the transformer structure is designed with the transformer (Phase 3), so a source gets a load and a stay but no pole class.
G. **Mechanical data** lives in the rules file as `lv_overhead.conductors`, keyed by conductor code: mass, overall diameter and maximum pull force. This keeps the conductor list unchanged. Values for three-phase and single-phase self-supporting ABC come from the M-TEC SANS 1418 sheet (index ids MTEC-ABC-3C and MTEC-ABC-1C), as manufacturer data to confirm. Conductors without mechanical data are flagged `no_mech_data` and not checked.
H. The lowest clearance and the highest pole load are traced values (`lv.oh.clearance.v1`, `lv.oh.pole-load.v1`). Every placeholder input is listed in an `overhead_placeholders` warning and on the project page.
I. **Project page:** an "Overhead line" section shows:
   - the summary: spans, longest span, strain sections, stays, poles to mark, and poles by class
   - each strain section: ruling span, how it is strung, the tension in each case, and the maximum pull
   - the poles that carry line tension, heaviest first, up to 20.
J. **Map:** spans that fail are drawn as red dashed straight lines between their supports, and stayed supports get a dark ring.

## Settings (eskom/0.7.0, placeholders unless stated)

| Setting | Value |
| --- | --- |
| `max_span_m` | 50 m |
| `bend_tolerance_m` | 2 m |
| `min_ground_clearance_m` | 5,5 m |
| `intermediate_max_deg`, `strain_angle_deg` | 10°, 30° |
| `pole` | 9 m, planted 1,5 m, attachment 0,3 m below the top (attachment at 7,2 m) |
| `pole_classes` | 9m-140 2,7 kN, 9m-160 3,5 kN, 9m-180 4,5 kN (wood, worked from a groundline section modulus at about 20 MPa) |
| `stay` | every terminal; 45° from ground; 20 kN |
| `everyday` | 15 °C, no wind, 45 % of maximum pull; at most 100 % of maximum pull in the cold and wind cases |
| `cases` | hot 80 °C; cold −5 °C; wind 10 °C at 700 Pa |
| `materials.al` | 59 GPa, 23 × 10⁻⁶ /°C |
| `conductors` | M-TEC manufacturer data (not placeholders, to confirm) |

## Validation

- The calc tests check the state change against an independent solution of its cubic (`numpy.roots`).
- They also work out by hand the sag, clearance and section tensions of a straight line, and the loads on intermediate, terminal and angle poles (2·H·sin(θ/2) plus wind).
- Further tests cover: a sharp angle splitting a section, a junction, long spans, unmarked ends, bends without a pole, slack stringing of short spans, and conductors without mechanical data.
- Still needed: a hand-worked or ReticMaster sag-tension case from the engineer (plan 2.10).

## Consequences

- Results are not fit to submit until the Eskom overhead line standard (ESKOM-OHL) is identified and held. It should give the spans, clearances, pole schedule, stays, loading cases and stringing tensions.
- Not modelled yet:
  - **Terrain:** ground is taken as flat, though imported contours could give a profile.
  - **Roads:** road crossings have no clearance of their own.
  - **Services:** service drops do not load the poles.
  - **Pole lengths:** every pole is the same length.
  - **Sources:** transformer structures are not given a pole class.
