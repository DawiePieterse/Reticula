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

The engineer builds the LV network from these marks on the project screen (ADR 0006), so draw LV routes the way the cables would run:

- **End a route on the route it joins.** Ends within 2 m of a route are joined to it. Ends that stop 2 to 6 m short are flagged, and any further away are left apart.
- **Mark the transformer or mini-sub within 30 m of its route**, and poles within 5 m.
- **Keep LV routes radial.** A loop, or a route that joins two transformers, is reported for the engineer to decide where the open point goes.
- **Mark every LV pole that will carry services.** Houses connect to poles through service distribution boxes, at most 2 boxes of 4 houses a pole, so a pole serves up to 8 houses within 40 m. Houses with no pole within reach, or only full ones, are reported (ADR 0007).

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

## Working offline

The field screen works without a connection (ADR 0004).

- **Before going out**, open the project's **Field inspection** once while online. The tablet keeps its stands, buildings, candidates, loads, the income and ADMD form and the photo counts. When offline, the **You are offline** screen lists the projects on the tablet with links to their field screens.
- **Every change is saved on the tablet first** and shows at once: confirmations, corrections, new buildings, candidates, loads and photos. The header chip shows what is waiting ("3 on this tablet", "3 to sync") or "All synced". The app header shows the count across projects.
- **Loads saved offline** keep the observations, class choice and any override. The kVA is worked out by the calc service when the load syncs, and shows then.
- **Changes sync on their own** as soon as the server can be reached, in the order they were made. After reconnecting, the project is fetched again so other people's changes show. **Sync now** in the sync panel tries at once.
- **If someone else changed the same item**, your change is held, never applied over theirs. The building panel says so. **Decide** opens the sync panel, which shows your version next to the server's. **Keep mine** sends yours over theirs. **Keep theirs** drops yours.
- **If the server refuses a change** (for example a new building outside the project area), the sync panel gives the reason. **Try again** sends it again; **Discard** drops it, together with any later changes to a building or candidate it would have added.
- **Signing out** keeps unsynced changes on the tablet. They sync the next time the same person signs in. Each person's changes are kept apart.
- Any request can be sent twice without being applied twice: buildings, inspections, candidates, loads and photos carry ids made on the device.

## Offline map

The background map needs a connection unless the project's offline map is saved on the tablet (ADR 0005).

- In the field screen, with nothing selected, **Offline map** shows the state. **Prepare offline map** builds the map for the project area plus 500 m on the server, then saves it on the tablet. A township of a few kilometres is a few MB.
- Once saved, the field map draws from the tablet's copy, online or not. Streets, water and place names show; stands, buildings and candidates draw on top.
- **Rebuild from the latest map data** makes a new map from the current map source. When the server has a newer map than the tablet, **Update** saves it.
- **Remove from this tablet** frees the space. The offline screen lists which projects have a map saved.
- The map source is set up once by whoever runs the server (docs/operations.md). Without it, **Prepare offline map** says what is missing.

Not offline: the project list and settings, the **Loads** page and the CSV export. The engineer's confirm and clear actions are online only.
