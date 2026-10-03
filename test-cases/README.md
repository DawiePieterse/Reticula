# Hand-worked validation cases

`test-cases/<topic>/case-N/` holds `inputs.json`, `expected.json` and `source.md` (who worked it, from which standard or textbook).
The calc service test suite runs every case and fails on tolerance breach. CI runs this before any release.
