# Rules files

One YAML per authority and version: `rules/<authority>/<major.minor.patch>.yaml`, validated against `schema.json`.

- The calc service hashes the file (SHA-256, first 16 hex chars) and stamps the hash on every result and document.
- Values are never hard-coded in calculation code; every number comes from here and carries a `clause` reference.
- A municipal file may set `base: eskom/0.1.0` and override only the keys it changes.
- `0.1.0` files are starters with placeholder values and must not be used for a real design.
