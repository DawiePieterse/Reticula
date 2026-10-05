# ADR 0007 – Load allocation and phasing

Date: 2026-10-05 · Status: accepted

## Decision

A. Loads are connected when the LV network is built. **Build LV network** joins the routes (ADR 0006), then connects every building's load, so the two are never out of step. The network is reported out of date when buildings or their loads change.
B. The calc service does it (`POST /calc/lv/loads`), from the network model and the buildings, with the rules file's `lv_loads` section (from `eskom/0.4.0`, index ESKOM-SERVICE).
C. **Services come from LV poles through service distribution boxes** (`attach: pole_boxes`). This is Eskom overhead practice as the engineer described it:
   - A pole carries at most 2 boxes, and a box supplies at most 4 loads.
   - Every load on a box is on the box's phase.
   - A second box on a pole is on a different phase from the first.
   - Each load goes to the nearest pole with room within the service reach (40 m, a placeholder). Pairs are taken nearest first, so a full pole passes loads to the next nearest one.
   - A pole's loads fill its boxes in order around the pole, starting after the widest gap, so neighbours share a box.
D. A building is never connected to a bare route. With no pole within reach, or only full poles, it is reported for the engineer to mark another pole. A building more than the reach from the network is reported too.
E. Loads above `single_phase_max_kva` (15 kVA, a placeholder) are three-phase. Each gets its own service from the nearest pole, takes no box space, and counts a third of its kVA on each phase.
F. A building without a load estimate is reported and left out. Its load is never guessed.
G. **Phasing works per feeder, from the far end.** Each box goes to the phase with the least kVA so far, skipping the phase already used by the other box on its pole. This keeps the load beyond any point of the feeder as even as boxes allow. Ties go to the phase with fewer customers, then red, white, blue.
H. The balancing uses ADMD (kVA per consumer after diversity). Design currents per section, and the voltage drop they cause, are plan 2.4.
I. A connection is stored against a branch at a distance from its upstream end, plus the node when it is at a pole. The network itself is not split. Each building's connection goes in `lv_loads`, with its service line, box, feeder and phase. Boxes, per-phase totals and the summary are stored on the network row.
J. `attach: nearest_point` connects each load to the nearest point on a route and phases loads one by one. It is a stopgap. Eskom 240-56030637 Rev 2 says underground LV feeders supply metering kiosks and never customers directly (§2.3.1, §3.5.3). At each kiosk, MCBs on one phase are grouped, at most 4 per phase, and the kiosk is balanced (§3.10 e)). Underground networks need kiosks modelled the way poles and boxes are; until then `nearest_point` must not be used for an Eskom underground design.

## Consequences

- Poles must be marked in the field for services to connect. Until plan 2.5 places poles along spans, a building with no marked pole within 40 m is an error.
- Phasing in whole boxes is coarser than phasing house by house. A feeder of a dozen boxes typically ends 10–15 % from even. The panel shows each feeder's unbalance, without a pass or fail, because no limit has been given.
- The engineer cannot yet move a box to another phase by hand. That comes with the LV results screen (plan 2.9).
- The 40 m reach and 15 kVA three-phase limit are placeholders until the Eskom standard is identified (docs/standards-index.md).
