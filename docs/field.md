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

## Offline

The field screen works without a connection for any project opened on the tablet while online.

**What is kept on the tablet** (IndexedDB, database `reticula`):

- `snapshots`: per project, the stands, buildings, candidates, load points, observation form (from the rules file) and the last progress figures. It is refreshed every time the field screen loads from the server and after every change made on the tablet.
- `outbox`: changes the server has not accepted yet, in the order they were made, including photo bytes.

The service worker caches the app itself, so the field screen reloads offline. When the server cannot be reached, the home screen lists the projects saved on the tablet.

**Writing.** A change goes straight to the server when the tablet is online and nothing is waiting. Otherwise it joins the outbox and the screen shows the expected result at once. The progress bar is then counted on the tablet; the open-assumptions figure stays at the server's last value, because the server raises assumptions.

**Loads offline.** The observations and any chosen class are saved, but no kVA is shown until the tablet syncs: the calc service works out every engineering number (plan decision B), and the tablet never does.

**Sync.** The outbox is sent in order when the connection returns, when the field screen opens, or on "Sync now". Rules:

1. Changes to one building, candidate or load are sent strictly in order. A load also waits for its building's changes, because the server refuses a load on a building marked not present.
2. After the server accepts a change, later changes to the same thing move onto the new version, because they were made on top of it.
3. Ids are generated on the device and every write is idempotent, so a change whose reply was lost is never applied twice. A 409 whose server copy already matches the change counts as done.
4. Sending stops quietly when the connection drops or the session needs signing in again, and resumes later.

**Conflicts.** When the server refuses a change because someone else changed the same thing first (409), the change is parked. The sync chip turns red ("1 to resolve") and opens a side-by-side view of the inspector's version and the server's, with differences highlighted. The inspector chooses:

- **Keep mine**: the change is sent again on top of the server's current version.
- **Keep the server's**: the change is dropped and the screen shows the server's version.

Nothing is overwritten automatically. Later changes to the same thing wait until the conflict is settled. A change the server rejects as invalid (400) shows the reason, with **Try again** and **Discard my change**.

## Offline map

Each project can have an offline base map: a PMTiles archive of the project area and a margin around it, built on the server from the configured tile source (docs/operations.md, Offline map tiles).

- The sync sheet's **Offline map** section builds the map ("Build offline map", showing the tile count and zoom range), downloads it to the tablet with a progress bar, offers **Download the newer map** when the server's copy changed, and removes it from the tablet.
- The archive is kept in IndexedDB (`tilepacks`). When the tablet is offline, the field map reads tiles straight from it; online, it uses the live tiles. Without an offline map, an offline field map shows a note and only the project's own data.
- A pack goes as deep as the tile budget allows: zoom levels are added from the minimum until the next level would exceed it.

## Server support for offline sync

- Building ids, inspection ids, candidate ids and photo ids are generated on the device. Sending the same record twice stores it once.
- Every building, candidate and load point carries a version. A stale write returns 409 with the current state, which the app shows instead of overwriting.
