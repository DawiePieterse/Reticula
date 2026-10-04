# Field inspection

The field screen opens from a project with **Field inspection**. It is built for a tablet: the map fills most of the screen and one building is worked at a time. Progress is always visible: buildings inspected, low-confidence buildings left, buildings without a load and open assumptions.

## Buildings

- **Start with lowest confidence** opens the least certain prediction. **Next to check** moves on.
- **Confirm** accepts the predicted type in one tap. The type buttons correct it. **Not present** records that nothing stands there.
- **+ Building** adds a building the map data missed, at a tapped point or the current GPS position.
- Every action stores the GPS position and accuracy, the device time, the inspector and any notes.
- **Take photo** opens the camera. The app re-encodes the photo as a JPEG of at most 1600 px before upload, which drops EXIF data such as embedded GPS and device details.

## Candidates

The tools add possible transformer, mini-sub and pole sites as points, and MV and LV routes as lines. A site can be placed at the GPS position. A route is tapped out point by point and then finished. Candidates must lie in or near the project area.

## Loads: the income and ADMD tool

- For a dwelling, the inspector records what can be seen: dwelling type, roof, walls, vehicles, appliances, stand size and occupants. The calc service scores these, picks the income band and category, and returns the ADMD.
- For a school, shop, borehole pump or other special load, the rules file's default kVA is used.
- Any value can be overridden. An override needs a reason, and the method's estimate is kept alongside it.
- Indicators left blank count as zero and are listed.
- Every option, score, band and special-load default comes from the rules file's `income_admd` section. In `eskom/0.2.0` the score picks an NRS 034 consumer class, or the engineer chooses the class directly. The class gives the ADMD and the Herman-Beta parameters. See `docs/load-data.md`.
- `eskom/0.1.0` keeps placeholder bands and a placeholder diversity formula, and is not for design use.
- Each load point keeps the calc service's full traced result. Records are kept per load point, never per person.

## Assumptions register

Each load estimate opens register entries:

| Code | Opened when |
| --- | --- |
| admd_estimated | A load is estimated from observations or a special-load default |
| admd_overridden | A load is overridden; the reason is in the text |
| indicators_missing | Indicators were left blank |

Re-estimating a load reopens its entries. When the engineer confirms a load, its entries clear. The engineer can also clear any entry with a note. Every open entry must be cleared before sign-off (plan item 7.1).

## Load schedule

The **Loads** page lists every building that is present, with its load or "No load recorded". It shows totals after diversity from the calc service. With `eskom/0.2.0` this is Herman-Beta at 90 % confidence over balanced phases, plus special loads. The CSV export carries the rules version and hash, the generation time and the totals. Cell values that would start a spreadsheet formula are prefixed with an apostrophe.

## Ready for offline sync

Offline capture is the next slice (plan items 1.8 and 1.9). The field data is already shaped for it:

- Building ids, inspection ids, candidate ids and photo ids are generated on the device. Sending the same record twice stores it once.
- Every building, candidate and load point carries a version. A stale write returns 409 with the current state, which the app shows instead of overwriting.
