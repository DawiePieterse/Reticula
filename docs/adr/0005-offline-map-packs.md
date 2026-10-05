# ADR 0005 – Offline map packs

Date: 2026-10-05 · Status: accepted

## Decision

A. The offline basemap is a vector PMTiles pack per project, as plan decision G set out. OpenStreetMap's raster tile servers forbid bulk downloading, so their tiles are never stored. They stay the online basemap for projects without a pack.
B. Packs are cut from one large PMTiles vector basemap in the Protomaps schema. The calc service's `RETICULA_MAP_SOURCE` names it: an https URL read with range requests, or a local file. The source comes from configuration only, so no request can make the service fetch an arbitrary URL. The recommended source is a South Africa extract of a Protomaps build in our own storage (docs/operations.md).
C. The calc service does the cutting (`POST /maps/extract`) with the official `pmtiles` library. It already does the file and geometry work, and the cut is map data, not an engineering number.
D. The API builds a pack as a background job (`map.pack`). The area is the project area plus 500 m, at zooms 0 to 15, and areas wider than 0.25° are refused. The pack is stored in the file store with a `map_packs` row; a project has one pack, and a rebuild replaces it and its file. Engineers and inspectors can both build one.
E. The tablet downloads the whole pack once and keeps it in IndexedDB (`reticula-maps`). Map data is public, so packs are shared by everyone who signs in on the tablet. When the tablet has a pack, the field map draws from it, online or not, so the map looks the same everywhere.
F. The vector style is Protomaps' light flavour. Its glyphs (Noto Sans, Latin ranges) and sprites are shipped in `public/map-assets` and pre-cached by the service worker, so labels and icons draw offline.
G. Build progress arrives over SignalR, with a 3-second poll beside it for networks that block WebSockets.

## Consequences

- An inspector must save the map on the tablet before going out. The field screen and the offline screen say when a project has no saved map.
- A pack shows the map as it was when built. **Rebuild from the latest map data** refreshes it, and the tablet offers the newer pack.
- Labels in non-Latin scripts would need more glyph ranges in `public/map-assets`.
- Satellite imagery stays out: its licence question is open (plan section 7).
