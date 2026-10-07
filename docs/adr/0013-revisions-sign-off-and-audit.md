# ADR 0013 – Revisions, sign-off and the audit trail

Date: 2026-10-07 · Status: accepted

## Decision

A. **A revision is a numbered snapshot of one design run.** It keeps the run's rules reference and hash, its inputs hash and its result hash. Reproducing a revision is a `reproduce` design run (ADR 0011). It sends the stored request again and passes only if the new result hash equals the stored one; when it differs, it lists where.
B. **Sign-off is gated.** Only an engineer can sign off, and only when every check below holds:
   - The engineer's account has an ECSA registration number.
   - The run is fit to submit. The reasons come from the calc service's facts: failed checks, placeholders, uninspected elements, bulk studies not run.
   - No project assumption is open.
   - The project's inputs still hash to the revision's inputs hash.
   - The revision has reproduced.
   - All nine document kinds exist for it.

   A refused sign-off is a 409 with every reason. On sign-off, the documents are generated again with the signed stamp and locked.
C. **The audit trail is written in the same save.** An EF Core `SaveChangesInterceptor` calls `DetectChanges` and then writes an `AuditEntry` for every add, change and removal of a domain entity. Each entry records who, when, and the values before and after, with long values capped. The actor is the signed-in user, set by middleware; for a background job it is the user who asked for it. Derived and bulk-imported records are skipped, because their import batch or design run is the audited fact: networks, stands, roads, contours, network assets and map packs. Buildings are imported in bulk, so only changes to them are recorded. Set-based `ExecuteUpdate`/`ExecuteDelete` bypass the interceptor, so they are used only for derived records.
D. **The export is open-format.** `GET /api/projects/{id}/export` returns one zip that is readable without Reticula. It holds the layout as GeoJSON, the project records as JSON, the photos and documents as files, the design runs the design and revisions rest on, and the audit trail.

## Consequences

- Signing off is impossible on placeholder rules values. Until the engineer verifies the standards (docs/standards-index.md), no revision can be signed.
- A change made through a set-based statement on an audited entity would go unrecorded. Reviews should reject one.
