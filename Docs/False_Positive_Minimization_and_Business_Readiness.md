# False-Positive Minimization And Business Readiness

This assessment explains how AOI Monitor currently controls false positives while preserving the more important possible-escape gate. It is business-readiness guidance, not a certification claim.

## Current Controls

- Dataset preflight requires a labeled manifest, image evidence, golden references, OK/NG balance, defect-class coverage, side/view metadata, ROI/refdes completeness, and duplicate-hash checks.
- Batch validation reports accuracy, precision, recall, false-call rate, false-call count, possible-escape count, review count, TP/TN/FP/FN, category metrics, and timing.
- `FalseCallReductionService` sweeps operating thresholds and only marks a recommendation `VALID` when configured false-call and possible-escape constraints are both met.
- Threshold changes are role-gated and audited. Applied recommendations are labeled as Stage 1 labeled-data evidence, not universal production accuracy proof.
- `ModelAcceptanceService` blocks production model acceptance unless an active ONNX model is selected, runtime-validated as `Ready`, tested against the validation dataset, and passes configured metrics, dataset-quality, false-call, possible-escape, review-rate, and inference-time gates.
- Customer validation packages and factory readiness packages include limitations and export verification so evidence can be reviewed outside the app.

## Business Acceptance Position

For business review, minimizing false positives means reducing good-board false calls without hiding real defects. A Stage 1 exit package should be treated as acceptable only when these are true:

- customer/evaluator dataset preflight is `PASS` or explicitly accepted with documented warnings;
- false-call rate is within the configured acceptance criterion;
- possible-escape rate and possible-escape count are reviewed and not hidden by threshold tuning;
- precision, recall, and review burden meet the agreed customer/evaluator thresholds;
- threshold profiles are linked to the false-call reduction run that produced them;
- model acceptance is not claimed unless the active ONNX model has `PASS` evidence;
- generated exports verify successfully and contain the prototype/hardware limitations.

## Camera Integration Boundary

Stage 2 camera-pilot architecture is implemented through camera adapter interfaces, plugin loading, camera acceptance, lighting acceptance, 3D acceptance, and factory readiness profiles. That architecture is business-useful because it defines how evidence will be collected.

It is not real hardware acceptance until the selected customer/vendor adapter produces accepted real frames with stable camera IDs, view assignments, frame IDs, timestamps, dimensions, pixel format, source kind, lighting timing, and real-camera performance evidence.

Folder Camera Simulation, null adapters, fake adapters, sample CSV profiles, and generated test images are workflow evidence only. They must not be used to claim real hardware readiness.

## Forward Quality Expectations

For future changes, keep these checks active:

- run the Stage 1 exit CLI or WPF evidence workflow for customer validation reruns;
- run false-call reduction after dataset, model, recipe, threshold, camera, or lighting changes;
- keep false calls and possible escapes as separate metrics in UI, CSV, reports, readiness packages, and dashboards;
- keep camera, lighting, robot, 3D, MES, and central sync evidence separated by real/simulated status;
- run repository hygiene, Release build/test, quality gates, HMI layout audit, and navigation performance checks before readiness claims;
- do not commit customer images, generated evidence packages, local databases, vendor SDK binaries, or runtime exports.

## Current Open Evidence

- Real customer dataset evidence is still required for a true Stage 1 exit claim.
- A production model acceptance claim still requires active ONNX `PASS` evidence from `ModelAcceptanceService`.
- Stage 2 camera readiness still requires a real vendor adapter and accepted real camera, lighting, and 3D acquisition evidence.
- Simulation evidence remains valuable for development and smoke testing, but it is not factory hardware readiness.
