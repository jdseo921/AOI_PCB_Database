# AOI Monitor Factory Acceptance Test Plan

This plan defines evidence needed for management or customer review. It separates Stage 1 data validation from camera, lighting, robot, MES, and full factory automation readiness. Do not treat simulation evidence as real equipment validation.

## Deployment profiles

Use the Settings deployment target before exporting a Go/No-Go package:

- Stage 1 Customer Data Validation
- Stage 2 Camera Pilot
- Stage 3 Robot Cell Pilot
- Stage 4 MES Traceability Pilot
- Full Factory Automation

The selected profile controls the readiness gates. The same evidence can be acceptable for Stage 1 and still be No-Go for later stages.

## Stage 1 dataset validation checklist

Required inputs:

- Customer-labeled image folder or dataset manifest.
- Ground-truth labels for OK and NG examples.
- Golden/reference images when the Pixel Difference engine is used.
- Defect class, side/view, ROI, refdes, lot, and board model fields when available.

Checklist:

- Confirm dataset quality status is PASS or explicitly reviewed as Conditional.
- Confirm total image count meets configured criteria.
- Confirm OK and NG examples are both present.
- Confirm defect class coverage meets configured criteria.
- Confirm unknown-label and missing-golden rates are within limits.
- Run Stage 1 batch validation.
- Review aggregate accuracy, precision, recall, false-call rate, possible escapes, and review burden.
- Review per-class, per-side, and per-ROI breakdown.
- Export the customer validation package.
- Verify exported artifacts and manifest.

Acceptance output:

- Validation package manifest.
- Customer validation HTML report.
- Validation results CSV.
- Validation breakdown CSV.
- Dataset quality summary.
- Export verification record.

## False positive / possible escape acceptance criteria

False positive reduction must not hide possible escapes. Use the False Call Reduction Workbench with customer-labeled data.

Recommended review:

- Compare candidate thresholds.
- Record false call rate.
- Record possible escape count and rate.
- Record review burden estimate.
- Check that the recommendation status is VALID for the selected operating mode.
- Apply thresholds only as Engineer/Admin and only after confirmation.

Typical gates should include:

- Maximum allowed false call rate.
- Maximum allowed possible escape rate.
- Minimum known OK sample count.
- Minimum known NG sample count.
- Management review of limitations when data coverage is insufficient.

Insufficient ground truth must be INVALID or CONDITIONAL. It must not be presented as PASS evidence.

## Stage 2 camera/lighting acceptance

Camera acceptance should be run for every required view in the selected deployment profile.

Camera checklist:

- Select intended camera source and view configuration.
- Run N frames per required view.
- Verify connect time.
- Verify first-frame latency.
- Verify average frame interval.
- Verify dropped frame count and rate.
- Verify trigger failures and timeouts.
- Verify frame metadata: frame ID, camera ID, view type, timestamp, width, height, pixel format, and source kind.
- Confirm real hardware runs are distinguishable from folder/fake/null evidence.
- Export camera acceptance JSON/HTML.

Lighting sync checklist:

- Configure lighting mode explicitly.
- Configure per-view program names.
- Confirm timeout and command template.
- Run lighting sync test for each required view.
- Record command latency.
- Record trigger-to-frame latency when camera source supports it.
- Export lighting acceptance JSON/HTML.

Stage 2 acceptance requires camera and lighting acceptance for the selected profile. Simulated evidence may support workflow review, but does not validate real camera or lighting hardware.

## Stage 3 robot/e-stop acceptance

Robot acceptance must evaluate the robot cycle state machine and emergency-stop behavior available to the app boundary.

Checklist:

- Confirm selected robot controller status and source kind.
- Run load, move-to-inspect, inspection, unload, and reset sequence.
- Measure each transition.
- Verify invalid transitions are rejected.
- Verify emergency-stop blocking evidence when available.
- Confirm reset returns to Idle.
- Confirm audit events were recorded.
- Export robot acceptance JSON/HTML.

Stage 3 evidence may be simulated or real depending on controller status. Simulation evidence is not safety certification and is not proof of production robot movement.

## Stage 4 MES traceability acceptance

MES acceptance must prove that failed uploads remain visible and that production REST is explicitly configured.

Checklist:

- Confirm MES mode: Not Connected, Mock, or REST.
- Confirm REST base URL, upload paths, timeout, authentication mode, max retry count, and retry backoff.
- Confirm redacted settings do not expose secrets.
- Send a controlled traceability payload.
- Confirm success records an upload attempt.
- Simulate a failed REST upload and confirm a spool item is created.
- Retry eligible items.
- Confirm retry success marks Sent.
- Confirm retry failure increments retry count and stores last error.
- Confirm Admin-only abandon behavior where applicable.
- Export MES queue report.

Stage 4 acceptance requires MES REST/spool readiness for the selected profile. Mock/local MES evidence is interface evidence only.

## 8-hour soak test procedure

Use the Factory PoC soak profile for full factory automation evidence.

Procedure:

1. Select deployment profile Full Factory Automation when evaluating full readiness.
2. Prepare the intended image source or camera source.
3. Select the Factory PoC soak-test profile.
4. Confirm duration is 480 minutes.
5. Confirm output folder has enough free space.
6. Start the soak test.
7. Monitor live progress: elapsed time, estimated remaining, pass count, fail count, and current status.
8. Do not close the app unless cancelling the run.
9. At completion, export or retain HTML and JSON reports.
10. Confirm the persisted run is non-canceled, completed, has no critical errors, and records p95/max/avg inspection time and memory start/end/max.

Acceptance criteria:

- Completed 8-hour requested duration.
- Not canceled.
- No critical iteration errors.
- Iterations persisted to SQLite.
- HTML and JSON reports exported.
- Source kind clearly states simulated source or real camera source.
- Full Factory Automation readiness does not accept simulated source as real camera evidence.

## Required deliverables for management signoff

Minimum deliverables by profile:

- Stage 1: validation package, dataset quality summary, validation report, validation CSV, breakdown CSV, export verification.
- Stage 2: Stage 1 deliverables plus camera and lighting acceptance reports.
- Stage 3: Stage 2 deliverables plus robot/e-stop acceptance report.
- Stage 4: Stage 3 deliverables plus MES queue/readiness report and REST/spool evidence.
- Full Factory Automation: Stage 4 deliverables plus 8-hour soak report and real hardware evidence where required.

Management signoff package:

- Factory readiness summary HTML.
- Factory readiness summary JSON.
- Package manifest.
- Latest validation manifest when available.
- Latest export verification summary.
- Latest camera, lighting, robot, MES, and soak evidence when available.
- README describing validated evidence, simulated evidence, unmet criteria, and known limitations.

Any No-Go category must have an owner, planned corrective action, and target date before pilot approval.
