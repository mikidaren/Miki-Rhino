# Architectural Modeling Loop Implementation Plan

**Goal:** Run a bounded two-storey architectural task through confirmed brief, native Rhino geometry, a reproducible plan check, scoped correction, save/reopen and IFC verification.

**Decisions:** Retain the existing modeling mode for general CAD. Structured BIM work opts into the stricter check automatically once a managed project is touched. Bind trusted evidence to the current Rhino document, exact source, and versioned brief; ordinary readback, model claims, or previous revisions do not replace it. Save the brief as document metadata; recheck geometry and planning after reopening. Offer useful structured results for failures and missing offline runtime. Preserve native geometry and existing undo.

- [x] Write red regressions for generic readback, prior evidence after mutation and rejected/absent check.
- [x] Reuse the offline Warehouse worker through the existing isolated Python launcher. Send only bounded diagnostic output to model. Bind evidence at the trusted local tool boundary.
- [x] Persist the confirmed brief and display review state and issue IDs in the panel. Keep old document schemas readable.
- [x] Develop and integrate the offline two-storey layout baseline with a separate candidate approval step.
- [x] Verify the existing isolated runtime and run native evidence with an owned scratch document. Save evidence and document limits. Public preview policy stays fail-closed when the runtime is missing; no new package published.

Research rationale: MANSION (CVPR 2026) for separating function graph from dimensional planning and reserving vertical cores; Hamieh et al. (Automation in Construction, 2020) for layered circulation checks; Warehouse's existing two-storey tests and RhinoCommon for exact model readback. See the workspace architecture study for paper links, source decisions and comparative benchmark plan.
