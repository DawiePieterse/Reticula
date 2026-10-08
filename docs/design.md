# Design, documents and sign-off

From the field's marked routes, sites and loads to a signed submission. Open the project's design page from the project screen's **Design** button (`/projects/:id/design`). Engineers run designs, generate documents and sign off. Inspectors can view the design and its documents.

## Connection point

The bulk supply studies need the authority's connection point: location, voltage, supply capacity, three-phase fault level, and preferably the minimum three-phase fault level, single-phase fault level and X/R. Record where the values came from (the authority's letter) and when they were received. Without capacity and fault level the load flow and fault study do not run, and the design is not fit to submit (plan 4.1). When none is saved, the form suggests the location from the imported authority network.

## Running a design

**Run the design** sends every project input to the calc service: the routes and sites, the loads with their classes, the contours, the connection point, the rates and the project's rules. The options are construction (overhead, underground, or compare both), MV line construction, objective, and any conductor or transformer rating you want to fix. The run is a background job; its progress shows on the page.

The result lists the transformers, the LV and MV networks, the bulk supply, the cost and every check with its clause. The map draws the network with the Eskom drawing-practice symbols. Every value comes with its traceability record: formula, clause, inputs and the rules hash.

A run is **stale** when the project's inputs change after it. The page names what changed (routes and sites, loads, contours, connection point, rates or rules).

**Placeholders.** Every rules value still marked `PLACEHOLDER` that the design used becomes an open assumption on the project. Confirm one only once you have checked the value against the standard (docs/standards-index.md).

## Optimising

**Optimise: three options** searches for the lowest capital cost, the lowest lifetime cost (capital plus losses), and the most spare transformer capacity within a capital ceiling. Every option is a full design, checked against every rule. The compare view shows them side by side. Options whose capital cost ranges overlap are flagged as too close to call. Adopting an option makes it the project's design without designing it again.

The search never prefers a design that leaves loads or transformers unsupplied, or whose bulk study stops, whatever its cost. It moves transformers only to marked routes and never invents a route.

## Rates

**Rates** (engineers) holds the rate library: import a supplier price list as CSV, and set your own rate for any item with its date and reason. Without an imported library the indicative rates are used, and every cost and BoQ says **ESTIMATE**.

## Documents

The **Documents** tab generates the whole set from the current design in one job. Each document is numbered and stamped (project, revision, design date, rules version and hash, rate date, inputs hash):

| Document | Format |
|---|---|
| Reticulation layout drawing (A1 sheet, Eskom 240-87658920 layers and symbols) | DXF |
| Design report: summary, the engineer's report sections, every network part, cost, checks and traceability appendices | PDF |
| Bill of quantities | Excel and PDF |
| Load schedule | Excel |
| Network for GIS | GeoJSON, KML, zipped shapefile |
| Submission pack: every document, the drawing and document registers and the authority checklist | ZIP |

A design that is not fit to submit prints **NOT FIT TO SUBMIT** with its reasons on every document. Generating again supersedes the earlier set but keeps it. A document is marked stale when a newer run replaced its design or the inputs changed after it.

DWG output, authority title-block templates and the authority's own CAD symbol libraries are not available yet (ADR 0012).

## Review and sign-off

- **Assumptions.** Every estimate, override and placeholder is listed. Clear it, or confirm it with a note. Sign-off needs none open.
- **Report sections.** Write the report's own text (scope, notes); approve each section for it to appear in the report. Editing an approved section returns it to draft.
- **Revisions.** A revision is a numbered issue of the current design. **Reproduce** runs its stored request again and passes only when the result is identical bit for bit.
- **Sign-off.** Only the engineer can sign off, with an ECSA registration number on their account (an engineer adds it under **Users**). All of these must hold: the design is fit to submit, no assumption is open, the inputs are unchanged since the revision, the revision has reproduced, and its documents are generated. The page lists anything still in the way. Signing regenerates the documents with the sign-off on the stamp and locks them.
- **Audit trail.** Every change to the project's records, with who, when, and the values before and after.
- **Export.** The whole project as one zip of GeoJSON, JSON and files, readable without Reticula.

## Design assistant

When the server turns it on (`Assistant:Enabled` with an API key, ADR 0014), the **Assistant** tab answers questions about the design from its stored results and traceability records. It can propose run parameters or draft a report section. A proposal does nothing until you accept it, and a drafted section still needs your approval. The assistant cannot change the connection point, rates, rules, loads, assumptions, revisions or sign-off.
