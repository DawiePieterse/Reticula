# Importing layout, survey, network and map data

Imports run per project from the project screen. Each file is checked first and nothing is stored until you choose Import. Files with errors cannot be imported.

## What can be imported

| Kind | Formats | What is read |
| --- | --- | --- |
| Stands | KML, KMZ, GeoJSON, DXF, shapefile | Polygons with an erf number and, if present, zoning |
| Buildings | Overpass JSON, GeoJSON, shapefile, or fetched from OpenStreetMap | Building footprints with their OpenStreetMap tags |
| Roads | KML, KMZ, GeoJSON, DXF, Overpass JSON, shapefile, or fetched from OpenStreetMap | Lines with a street name and class |
| Contours | GeoJSON, DXF, KML, shapefile, GeoTIFF | Lines with an elevation; GeoTIFF elevation models are traced to contours |
| Existing network | CSV, GeoJSON, DXF, KML, shapefile | The authority's assets: points and lines with a type and the fields each type needs |

Shapefiles are imported as one zip holding the `.shp`, `.dbf`, `.shx` and `.prj` files. The `.prj` sets the coordinate system.

**GeoTIFF elevation models** (contours only) are georeferenced rasters that are read via their ModelPixelScale, ModelTiepoint or ModelTransformation tags and the GeoKeyDirectory for the EPSG code. Contour lines are traced from the elevation grid at a specified interval (default 1 m, set on upload). Rasters larger than 4 million cells are downsampled for performance. The file must be geospatial (have CRS tags); non-geospatial TIFFs are rejected.

**Fetch from OpenStreetMap** (buildings and roads) asks OpenStreetMap's Overpass service for everything in the project area, then checks it like a file. Nothing is stored until you choose Import. The server it asks is set by `RETICULA_OVERPASS_URL` on the calc service; the main public server is the default.

- **Erf number** comes from the first property named erf, erf_no, stand, stand_no, name or label. In a DXF it is the text inside each closed polyline, preferring numbers.
- **Road name** comes from name, street, street_name or road_name; the **class** from highway (OpenStreetMap), class, type or category. In a DXF, open polylines and lines on the chosen layer become roads.
- **Contour elevation** comes from a property named elevation, elev, height, contour, z or level, or else from the line's z value (a DXF polyline's elevation, or 3D coordinates). A z of exactly 0 from CAD counts as not set. Closed contour rings are kept as lines.
- **Existing network**: each asset's type comes from a type field (type, asset_type, asset, kind, class), or in a DXF from the layer and block names. Words such as transformer, mini-sub, substation, pole, RMU, MV or 11 kV line, LV or ABC line, cable and point of supply are recognised. A CSV needs lon and lat (or x and y) columns, separated by commas or semicolons. A DXF text within 5 m of an asset becomes its label.
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
| wrong_geometry | warning | Features of the wrong shape were skipped, for example points in a roads file |
| road_names_missing | warning | No road has a name |
| elevation_missing | error if all, else warning | Contours without an elevation were skipped |
| elevation_implausible | warning | Elevations outside −100 to 4000 m, often a units problem |
| network_type_unknown | error if all, else warning | An asset's type could not be read |
| network_field_missing | warning | An asset lacks a field its type needs (below) |
| connection_point_incomplete | warning | A connection point lacks capacity or fault level |

## What each network asset needs

| Type | Needs | Why |
| --- | --- | --- |
| Transformer, mini-sub, substation | rating in kVA | Spare capacity and loading |
| MV or LV line or cable | voltage (LV lines default to 0.4 kV) | Which network it belongs to |
| Connection point | voltage, available capacity in kVA, fault level in kA | The bulk supply design (Phase 4) starts from these, and the app never guesses them |

Assets lacking a field are still imported. They are counted as incomplete, ringed in orange on the map, and the design will stop where it needs the missing value.

## Re-importing

Importing stands replaces all of the project's stands. Importing buildings replaces only buildings not yet inspected. Importing roads, contours or the existing network replaces that kind. Every import is kept as a batch with its file hash, coordinate system and issues.

## Predicted building types

Each building gets a type (house, shop, school or other), a confidence and the signals behind it. The signals are OSM tags, the stand's zoning and the footprint size. The rules file's `building_prediction` section holds every value, so an authority can tune them. Buildings below the low-confidence threshold are listed first for the field visit.
