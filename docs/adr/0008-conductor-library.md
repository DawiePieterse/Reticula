# ADR 0008 – Conductor library

Date: 2026-10-05 · Status: accepted

## Decision

A. The conductor library lives in the rules file, as before. From `eskom/0.5.0` each conductor may also carry:
   - its material, size, cores and use (feeder or service)
   - its continuous rating by installation (`ratings_a`: in ground, in pipe, in air)
   - its short-circuit constant `fault_k`
   - a `placeholder` list of the fields whose values do not yet come from the governing standard.

   `rating_a` stays the rating as normally installed; for an underground cable that is in ground. Older rules files keep their simple entries and still load.
B. Underground cable ratings are transcribed from Eskom 240-56030637 Rev 2, Tables 6 (Cu) and 7 (Al). Sizes are 16 to 240 mm², two-core for services and four-core for feeders, at the standard conditions of §3.9.1 o). The engineer confirmed Rev 2 is current.
C. Short-circuit withstand is I = K·A/√t, with K = 0,115 for copper and 0,076 for aluminium (§3.9.1 m)). The calc tests check that it reproduces the standard's Tables 1 and 2, which also checks the transcription.
D. Resistances and reactances are placeholders, because the standard does not give them:
   - Resistance is the IEC 60228 class 2 maximum DC resistance at 20 °C.
   - Reactance is 0,08 Ω/km.

   They stay placeholders until the Eskom cable specification 240-56063805 is held. The ABC entry stays a placeholder in every field until the ABC specification 240-84758170 is held.
E. Rating and withstand are traced values (`lv.cable.rating.v1`, `lv.cable.withstand.v1`). A trace input that uses a placeholder says so in its source.
F. The calc service serves the library at `GET /rules/{authority}/{version}/conductors`. The API serves the project's library at `/api/projects/{id}/conductors`. The project page lists it, with placeholder values in grey italics.

## Consequences

- Voltage drop (plan 2.4) can use these cables now, but its results depend on placeholder impedances and are not fit to submit until 240-56063805 is held.
- Resistance at operating temperature, de-rating for soil, depth and grouping (plan 2.6), and overhead ratings are still to come.
- Values transcribed from a standard are checked by tests against the standard's own tables wherever it gives a second way to get them.
