# Natural-language and cost evaluation

**Goal:** Measure a bounded synthetic architectural task from natural-language brief through verified output, separating provider usage, local performance, and architectural acceptance.

- [x] Capture optional provider usage for streaming and ordinary completions; unknown usage stays unknown.
- [x] Freeze four synthetic prompts and their expected decisions. Cover feasible, infeasible and critical information absent.
- [x] Run identical offline workflows with full history and exact duplicate viewport image removal. Keep every tool result and the architectural approval gates.
- [x] Run four fresh synthetic prompts through the configured Magpie loopback, recording reported usage and timing separately from offline replay bytes.
- [x] Verify all four exact model-produced decisions in Rhino 9 scratch documents. Two feasible decisions generated native models (71 and 65 managed objects after reopen) and each passed 14 IFC4 readback checks; the infeasible and missing-input cases created no geometry.

`ModelingRunOptions.MeasureRun` and `CompactContext` are opt-in, independent options. `LastRunMetrics` stores counters only, in memory for the last run; the Rhino panel does not display them yet. `ModelCalls` counts logical requests; it does not count the compatibility retry after an explicit HTTP 400. `ApprovalChecks` counts callbacks (including ones that grant immediately), and `ToolErrors` counts unsuccessful results from dispatched tools. None of those counts measure a person's clicks. Wall time includes the byte-count serialization, whose contribution is available as `MeteringTimeMs`.

Next comparison: capture at least two unchanged and two changed real Rhino views in a shared synthetic session, pin the model route, and pair full vs compact requests with matching state, approval events, and terminal checks. Report gateway input tokens and cache hits separately from message bytes; do not interpret the current one-shot brief calls as an A/B trial. The first follow-up should exercise missing data, a genuine infeasible brief revised by the architect, and a local edit that invalidates and reruns review on the affected floor.
