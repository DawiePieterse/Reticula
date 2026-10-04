# MV design (plan Phase 3)

The MV design takes the transformer sites, LV routes and MV route marked on site, and every building with its load,
and returns: which site serves which buildings, each transformer's unit and rating, each site's LV network, the MV
network with its conductors, and each transformer's tap. Rules `eskom/0.4.0` or later; every unverified value used is
listed with the result (docs/standards-index.md).

## 1. Placement and sizing (3.1)

1. The LV routes are noded and joined as for the LV design; each site and building is attached to the nearest point.
2. Each building goes to the site nearest **along the LV routes**, within `mv_design.max_lv_reach_m` (600 m); others
   are reported and fail the design.
3. A site's demand is the Herman-Beta demand of its buildings (single- and three-phase groups added, special loads at
   their kVA) plus `growth_allowance_pct` (0 %: the NRS 034 15-year table includes growth).
4. The rating is the smallest standard unit with demand ≤ rating × `max_utilisation_pct`.
5. Sites serving nobody are left out (reported).

## 2. Pole-mount or mini-sub (3.2)

A site marked as a transformer stays a SANS 780 pole-mount unit when the LV network is overhead and the demand fits
within `pole_mount_max_kva` (200 kVA); otherwise, and for sites marked as mini-subs, a SANS 1029 mini-sub is used. The
reason is shown with the site.

## 3. MV routing (3.3)

The MV routes are noded and joined. The supply point is, in order: the point the engineer gives; the authority's
connection point from imported network data; the nearest point of an imported existing MV line; or the end of the MV
routes furthest from the sites (an assumption stated with the result until plan 4.1 records the connection point).
Each site tees onto the nearest MV route (a tee longer than `max_tee_m` fails). A shortest-path tree makes the network
radial and branches feeding no transformer are removed.

MV conductors: ACSR Squirrel, Fox, Mink, Hare overhead; 35–185 mm² Al XLPE 11 kV cable underground (SANS 182, SANS 97;
values unverified).

## 4. Loading, voltage and taps (3.4)

- A branch carries the Herman-Beta demand of every consumer downstream of it as one statistical group, so diversity
  between transformers is counted; current I = S / (√3 · U).
- ΔV = √3 · I · (R cos φ + X sin φ) · L, accumulated from the supply point; limit `max_drop_pct` (5 %).
- Sizing as for LV: smallest allowed conductor, up-sized for loading then for the worst drop, never smaller towards
  the supply.
- Each site's LV network is designed (layout, sizing, checks) with its chosen transformer.
- Tap: with sending voltage V_s, MV drop ΔV_mv, transformer regulation ε = (S/S_r)·(R% cos φ + X% sin φ) and the
  site's worst LV drop ΔV_lv, the lowest customer voltage is V_s − ΔV_mv − ε − ΔV_lv + tap and the highest (no load)
  V_s + tap. The tap from `taps_pct` with the largest margin inside ±`supply_voltage_band_pct` (NRS 048-2, 10 %) is
  chosen; if none keeps everyone inside, the check fails.

## 5. Results (3.5)

**Project → MV design** (engineers start runs). Choose the sites (all by default) and the LV and MV construction. The
page shows the assumptions and unverified values, a map of the MV network, sites and LV networks, the transformer
table (unit, rating, design demand, utilisation, LV result, LV and MV drops, regulation, tap, customer voltage range),
the MV branches, the checks with clause references, and the indicative cost.

API: `POST /api/projects/{id}/mv-designs` `{siteIds, lvConstruction, mvConstruction, supply}` (engineer),
`GET /api/projects/{id}/mv-designs`, `GET /api/projects/{id}/mv-designs/{runId}`. Runs are stored like LV runs.

## 6. Validation (3.6)

`test-cases/mv_sizing` (Herman-Beta demand with a three-phase group → 200 kVA pole-mount), `mv_vdrop` (1 km of Fox),
`mv_tap` (tap +5 % from the drops). Each `source.md` shows the arithmetic.
