# Importing layout and map data

Imports run per project from the project screen. Each file is checked first and nothing is stored until you choose Import. Files with errors cannot be imported.

## What can be imported

| Kind | Formats | What is read |
| --- | --- | --- |
| Stands | KML, KMZ, GeoJSON, DXF | Polygons with an erf number and, if present, zoning |
| Buildings | Overpass JSON, GeoJSON | Building footprints with their OpenStreetMap tags |

- **Erf number** comes from the first property named erf, erf_no, stand, stand_no, name or label. In a DXF it is the text inside each closed polyline, preferring numbers.
- **Zoning** comes from a property named zoning, zone, land_use or landuse. A building takes the zoning of the stand it mostly sits on.
- **DXF** uses closed polylines only. Arcs are flattened to 5 cm. Choose the layer that holds the stand outlines once the check lists the layers.
- **OpenStreetMap** cannot be fetched from inside the app yet. Export buildings from overpass-turbo.eu with `out geom;` and import the JSON.

## Coordinate systems

KML, KMZ and Overpass files are always longitude/latitude. For GeoJSON and DXF the system is detected from the coordinates, or you choose it.

| Choice | Meaning |
| --- | --- |
| WGS84 | Longitude and latitude in degrees |
| Lo15 … Lo33 | South African Lo system in the usual CAD convention: x = −Y (metres east of the central meridian), y = −X (metres north of the equator, so negative millions) |
| UTM 34S, 35S, 36S | WGS84 / UTM south zones |

Lo coordinates look the same in every zone. When a file looks like Lo, the zone nearest the project area is suggested. Check it against the drawing's title block.

If a drawing uses a different convention, for example positive southings, the check reports that no features fall inside the project area.

## What the check flags

| Code | Severity | Meaning |
| --- | --- | --- |
| crs_unknown | error | The coordinate system could not be worked out; choose it |
| no_features | error | No polygons found, or the wrong DXF layer |
| outside_area | error if all, else warning | Features fall outside the project area |
| erf_missing, erf_duplicate | warning | Stands without an erf number, or numbers used twice |
| geometry_repaired, geometry_dropped | warning | Outlines that crossed themselves |
| open_polylines | warning | DXF polylines that are not closed were skipped |
| tiny_features | warning | Implausibly small outlines, often a units problem |
| osm_relations_skipped | warning | OSM multipolygon buildings are not imported yet |

## Re-importing

Importing stands replaces all of the project's stands. Importing buildings replaces only buildings not yet inspected. Every import is kept as a batch with its file hash, coordinate system and issues.

## Predicted building types

Each building gets a type (house, shop, school or other), a confidence and the signals behind it. The signals are OSM tags, the stand's zoning and the footprint size. The rules file's `building_prediction` section holds every value, so an authority can tune them. Buildings below the low-confidence threshold are listed first for the field visit.
