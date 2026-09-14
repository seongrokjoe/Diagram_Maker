# Diagram reliability and latency — internal.7

Approved implementation scope (2026-09-14):

1. Preserve C++ receiver types through casts, resolve proven internal calls, and ask only about genuinely ambiguous targets. Share resolution with Git analysis; do not treat casts as calls.
2. Let the parser own execution structure. Generate and independently review concise Korean annotations for stable semantic units in adaptive batches, shared across views. Remove the duplicate per-function execution-plan LLM pass.
3. Budget the complete masked/aliased request, including system and schema, before transport. Deduplicate and bound source excerpts, split complete regions with parent context, isolate oversized units, and retain bounded repair/checkpoint budgets.
4. Compile and persist static results before LLM work. Preserve verified annotations per unit and expose the best available result on completion, failure, cancellation, or the 900-second limit. Keep the canvas hidden during generation; warn at five minutes and continue.
5. Add synchronized line numbers to each code input. Emphasize generation status, elapsed time, stage, counts, and actual progress timestamp without announcing every clock tick.
6. Add backward-compatible coverage/progress metadata and bump parser/semantic cache versions. Preserve existing results, edits, settings, and the pre-feature checkpoint.
7. Add parsing, control-boundary, request-size, partial-result, checkpoint, cancellation, and latency regressions. Target at most 20 completion requests for the 1,212-line/40-function code fixture and 32 for Git. Package synthetic onsite measurements; actual model quality and the five-minute target require internal-PC runs.
8. Run the complete repository verification, build and smoke-test the offline Windows x64 internal.7 ZIP in a non-synchronized directory, verify its hash and contents, then remove previous DiagramMaker release ZIP/SHA pairs (preserving dependency caches). Update release links, commit, and push main.

Execution stages and test outcomes are recorded in CODE_BLOCK_DIAGRAM_PROGRESS.md. The five-minute target is a warning threshold; the existing 900-second continuation budget remains in force. Only genuinely ambiguous calls block for clarification.
