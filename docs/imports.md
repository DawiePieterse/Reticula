# Importing layout and map data

Imports run per project from the project screen. Each file is checked first and nothing is stored until you choose Import. Files with errors cannot be imported.

## What can be imported

| Kind | Formats | What is read |
| --- | --- | --- |
| Stands | KML, KMZ, GeoJSON, DXF, zipped shapefile | Polygons with an erf number and, if present, zoning |
| Buildings | Overpass JSON, GeoJSON, zipped shapefile, or fetched from OpenStreetMap | Building footprints with their OpenStreetMap tags |
| Roads | Overpass JSON, GeoJSON, KML, DXF, zipped shapefile, or fetched from OpenStreetMap | Lines with a class (OSM `highway`, or a class/type field) and a name |
| Contours | DXF, GeoJSON, zipped shapefile, GeoTIFF elevation model | Lines with an elevation |
| Existing network | GeoJSON, CSV, zipped shapefile, KML, DXF | The authority's lines and equipment, with required fields (below) |

- **Erf number** comes from the first property named erf, erf_no, stand, stand_no, name or label. In a DXF it is the text inside each closed polyline, preferring numbers.
- **Zoning** comes from a property named zoning, zone, land_use or landuse. A building takes the zoning of the stand it mostly sits on.
- **DXF** stands and buildings use closed polylines; roads and contours use open and closed polylines and lines; the network also uses points and block inserts (with their attributes). Arcs are flattened to 5 cm. Choose the layer once the check lists the layers.
- **Shapefiles** are uploaded as one .zip holding the .shp, .shx, .dbf and, ideally, the .prj. The .prj sets the coordinate system unless you choose one.
- **OpenStreetMap**: for buildings and roads, **Fetch from OpenStreetMap** asks the Overpass API for the project area (plus about 100 m) and checks the answer like a file. An export from overpass-turbo.eu with `out geom;` can still be imported as a file.

### Contours

The elevation comes from a field named elevation, elev, height, z, contour or level; else from the line's z values (3D lines, or a DXF polyline's elevation); else, in a DXF, from a number written within 5 m of the line. Lines without an elevation are skipped with a warning, or the file is refused if none has one.

A **GeoTIFF** elevation model (single band, with ModelPixelScale and ModelTiepoint tags) is contoured on import. Its GeoKeys set the coordinate system. The interval is the one entered (Contour interval) or picked to give about 40 levels, at least 0.5 m. Cells at the no-data value or below −1000 m are ignored. Models above 16 million cells must be cropped first.

### Existing network

Each asset needs a recognised **asset_type** and the fields its type requires. Field names are matched without regard to case, with common aliases (for example kv, voltage → voltage_kv; kva, rating → rating_kva; cable → conductor). In a DXF the layer or block name can serve as the type.

| asset_type | Shape | Required fields |
| --- | --- | --- |
| mv_line | line | voltage_kv, conductor |
| lv_line | line | conductor |
| transformer | point | rating_kva, voltage_kv |
| minisub | point | rating_kva, voltage_kv |
| pole | point | none |
| switch | point | voltage_kv |
| substation | point | voltage_kv |
| connection_point | point | voltage_kv (capacity_kva and fault_level_ka are read when present) |

Optional fields: asset_id, status, name. A **CSV** needs a WKT column (wkt or geometry) or coordinate columns (lon/lat or x/y); x/y in a projected system is detected like any other file.

## Coordinate systems

KML, KMZ and Overpass files are always longitude/latitude. For GeoJSON and DXF the system is detected from the coordinates, or you choose it.

| Choice | Meaning |
| --- | --- |
| WGS84 | Longitude and latitude in degrees |
| Lo15 … Lo33 | South African Lo system in the usual CAD convention: x = −Y (metres east of the central meridian), y = −X (metres north of the equator, so negative millions) |
| UTM 34S, 35S, 36S | WGS84 / UTM south zones |
| EPSG code | Any other system, when a shapefile .prj or GeoTIFF declares it |

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
| geometry_type_skipped | warning | Shapes of the wrong kind for what is imported, e.g. polygons in a road file |
| contour_elevation_missing | error if all, else warning | Contour lines without an elevation |
| elevation_implausible | warning | Elevations outside −500 to 6000 m, usually a units problem |
| contours_generated | warning | Contours were drawn from a GeoTIFF; shows the interval and range |
| network_type_unknown | error | Assets without a recognised asset_type |
| network_field_missing | error | Assets missing a field their type requires |
| network_geometry_mismatch | error | Lines drawn as points or equipment drawn as lines |
| network_value_invalid | error | Numeric fields holding text |
| rows_unreadable | warning | CSV rows without a readable geometry |

## Re-importing

Importing stands replaces all of the project's stands. Importing buildings replaces only buildings not yet inspected. Importing roads, contours or the network replaces that layer. Every import is kept as a batch with its file hash, coordinate system and issues.

## Predicted building types

Each building gets a type (house, shop, school or other), a confidence and the signals behind it. The signals are OSM tags, the stand's zoning and the footprint size. The rules file's `building_prediction` section holds every value, so an authority can tune them. Buildings below the low-confidence threshold are listed first for the field visit.
