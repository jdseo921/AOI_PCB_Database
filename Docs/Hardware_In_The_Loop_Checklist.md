# Hardware-In-The-Loop Checklist

Use this checklist before claiming Stage 2 or later hardware readiness. Template adapters and simulated evidence are not real hardware validation.

## Camera Discovery

- Confirm each vendor camera appears in the vendor SDK discovery tool.
- Record vendor, model, serial number, interface type, IP address or USB path, firmware version, and driver version.
- Confirm the AOI adapter discovers the same device identifiers.
- Evidence required: discovery screenshot, adapter discovery JSON/log, network/USB configuration screenshot.

Pass criteria: every required camera is discovered by both vendor tooling and the AOI adapter with stable identifiers.

## Top / Side / Bottom Assignment

- Assign each physical camera to `Top`, `Side`, or `Bottom`.
- Capture a labeled test frame for every configured view.
- Confirm view labels are persisted in the camera settings and acceptance report.
- Evidence required: frame screenshots for each view, settings export, camera acceptance JSON/HTML.

Pass criteria: no view is missing, duplicated, or assigned to the wrong physical camera.

## Lighting Program Validation

- Map lighting program names for every view.
- Trigger each lighting program from the AOI lighting adapter.
- Verify intensity, channel, strobe timing, and program ID on the lighting controller.
- Evidence required: controller screenshot/photo, lighting acceptance report, per-view command log.

Pass criteria: every required view selects the expected lighting program and reports controller acknowledgement.

## Trigger-To-Frame Timing

- Run software or hardware trigger tests for each camera/view.
- Measure trigger command, strobe acknowledgement, frame timestamp, and frame received timestamp.
- Check timeout behavior by disconnecting or disabling one device in a controlled test.
- Evidence required: timing CSV/log, frame metadata, timeout/fault screenshot.

Pass criteria: trigger-to-frame latency stays within the customer cycle-time budget, and timeout failures are reported safely.

## 3D Profile Acquisition

- Acquire real 3D height/profile data from the configured sensor.
- Verify dimensions, unit, X/Y pitch, invalid-height count, and source kind.
- Confirm sample CSV profiles are labeled as simulation/sample evidence only.
- Evidence required: 3D profile acceptance JSON/HTML, height-map screenshot, sensor configuration screenshot.

Pass criteria: real 3D frames are acquired with valid dimensions, calibrated units, and acceptable invalid-height counts.

## Robot Load / Inspect / Unload

- Run load, move-to-inspect, inspection hold, unload, and reset steps.
- Verify board ID, lot, station, gripper/clamp status, and cycle timestamps.
- Confirm invalid transitions are rejected.
- Evidence required: robot acceptance report, robot controller log, cycle video or screenshots, audit log export.

Pass criteria: the robot completes the sequence within cycle-time limits, rejects invalid transitions, and records audit evidence.

## PLC Safety Interlock Tests

- Verify guard door, light curtain, board clamp, air pressure, servo ready, and safety fault inputs.
- Confirm robot motion is blocked when any required interlock is unsafe.
- Confirm reset requires the approved operator/safety sequence.
- Evidence required: PLC input screenshot, safety acceptance report, blocked-motion log.

Pass criteria: every unsafe interlock blocks motion and is visible in AOI safety status.

## E-Stop Test

- Trigger the emergency stop during a controlled robot cycle.
- Confirm motion stops, AOI records the e-stop state, and reset is required before motion resumes.
- Verify the event is logged with timestamp and operator.
- Evidence required: e-stop test video/screenshot, robot acceptance report, audit log export.

Pass criteria: e-stop blocks motion immediately and cannot be cleared without the documented reset sequence.

## Final Pass / Fail Criteria

Pass requires all of the following:

- Real camera discovery and frame acquisition completed for required views.
- Lighting program validation completed for required views.
- Trigger-to-frame timing is within the approved budget.
- Real 3D profile acquisition completed when in scope.
- Robot load/inspect/unload cycle completed when in scope.
- PLC safety and e-stop tests passed when in scope.
- Factory readiness package and factory acceptance checklist exported.
- All evidence clearly separates real hardware from simulated/template evidence.

Fail if any required real device is missing, mislabeled, simulated, timing out, unsafe, or unverified.

## Evidence Package

Attach or export:

- Camera discovery screenshots and adapter logs.
- Top/Side/Bottom frame screenshots.
- Lighting controller screenshots and command logs.
- Trigger-to-frame timing CSV/log.
- 3D profile report and screenshot.
- Robot acceptance report and cycle evidence.
- PLC safety and e-stop evidence.
- Factory readiness Go/No-Go package.
- Factory acceptance checklist.
- Audit trail covering operator, engineer, and admin actions.
