# Rooftop imagery (plan 1.3)

A fourth signal for the building-type predictor (house, shop, school, other), next to OSM tags, zoning and footprint
area. It never replaces the field visit: it reorders which buildings are visited first and inspectors confirm every one.

## Imagery and licences

Imagery is only used under a licence that allows deriving data from it; each imagery records that licence.

| Source | How | Licence |
|---|---|---|
| Orthophoto GeoTIFF (NGI, municipal, drone survey, commercial) | Project page → Rooftop imagery → upload, up to 600 MB, cropped to the project | The engineer states the licence and declares it allows derived use |
| Google satellite | "Fetch Google satellite" (job `imagery.google`): Map Tiles API satellite tiles over the buildings at zoom 19 (about 0.3 m) | Off unless the installation sets `Imagery:Google:Enabled`, `ApiKey` and `LicenceReference` (the agreement allowing derived use; Google's standard terms do not) |

The GeoTIFF must carry its georeferencing (ModelPixelScale, ModelTiepoint) and EPSG code (or be in WGS84 degrees).
One imagery is in use per project; older ones stay listed and can be used again.

## The classifier (calc service, `geo/rooftop.py`)

1. Each building's footprint is cut out of the imagery; a building less than 80 % inside is counted as outside.
2. Colour, texture and shape measures per roof: mean and spread of red, green and blue, hue, saturation, brightness,
   brightness spread, edge texture, shares of bright, grey and reddish pixels, footprint area, compactness, elongation.
3. A random forest is trained on the project's own buildings confirmed in the field (status confirmed or new). Types
   with fewer than `min_per_type` (5) examples are left out; at least `min_confirmed` (30) confirmed buildings are needed.
4. Accuracy is measured by cross-validation (5 folds) on confirmed buildings the model was not trained on. Below
   `min_accuracy` (75 %) the model is not used and no signals are made.
5. Each unconfirmed building gets the signal `rooftop:<imagery name>`: the predicted type with confidence = model
   probability × cross-validated precision of that type, capped at `max_confidence` (0.85).
6. The predictor combines it with the other signals (agreeing signals add the rules' boost), and the buildings are
   re-predicted. Running it again after more buildings are confirmed improves it; a model that is not used removes its
   earlier signals.

Settings are in rules `building_prediction.rooftop` (eskom/0.6.0 and later), marked as a Reticula heuristic, not a standard.

## API

- `GET /api/projects/{id}/imagery` (imagery, last classifier report, whether Google is available)
- `POST …/imagery/orthophoto` (multipart: file, label, licence, declaration)
- `POST …/imagery/google` (job), `POST …/imagery/{iid}/activate`, `POST …/imagery/classify` (job `imagery.classify`)
