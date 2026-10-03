# ADR 0001 – Stack and calculation boundary

Date: 2026-10-03 · Status: accepted

## Decision

A. Angular 22 PWA, .NET 10 API, PostgreSQL 17 + PostGIS, Python 3.12 calc service (FastAPI).
B. Every engineering calculation lives in the Python service. .NET and Angular store, orchestrate and display. The design assistant (Phase 8) calls the same endpoints and computes nothing.
C. Rules files are YAML per authority and version, schema-validated and hashed by the calc service. The hash is stamped on every result and document.
D. Every calculated value is a `Traced` record: value, unit, formula id, formula text, clause reference, rules hash and named inputs with sources.

## Why

The spec requires calculations in tested code, never the language model, with pandapower for load flow and IEC 60909. The engineer's working stack is .NET and Angular. Splitting the API from the calc service satisfies both and keeps the calculation boundary explicit and testable.

## Consequences

- Two back-end runtimes to deploy (docker-compose covers this).
- API-to-calc calls are HTTP; results are stored by the API, never recomputed.
- A pure-Python back end remains possible later by moving the API responsibilities into the calc service.
