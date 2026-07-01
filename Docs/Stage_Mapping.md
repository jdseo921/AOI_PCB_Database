# AOI Monitor Stage Mapping

This document maps the current proof of concept to the staged delivery expectations. It is written for client and evaluator review.

## Stage 1 Implemented Features

Stage 1 currently includes:

- Windows WPF operator console.
- Local role selector for Operator, Engineer, and Admin.
- Main Inspection operator workflow with Start, Stop, Next Board, Save Result, auto-save, result indicator, defect overlay, defect list, and event log.
- Folder Camera Simulation for Top, Side, and Bottom view images.
- Simulated Robot / Handler panel with Load, Inspect, Unload, Reset, emergency-stop simulation, cycle timing, and audit-event logging.
- Image Library import and batch import.
- Managed local image vault.
- Local SQLite database initialization and persistence.
- Inspection result persistence, including selected engine and model version.
- Defect result persistence.
- Review and disposition event persistence.
- Factory-style audit trail persistence with UTC/local timestamps, user ID, role, station, action category, action detail, and related IDs/paths where available.
- Recipe editor with ROI drawing, thresholds, recipe revisions, and role restrictions.
- 2D calibration profile workflow for Stage 2 planning, including sample image loading, image/board point pairs, SQLite persistence, simple scale/offset transform, and approximate board-mm defect display in Main Inspection.
- Pixel Difference Prototype Engine as the default inspection engine.
- ONNX ML Model inference path with configurable model path, tensor names, input size, confidence threshold, and label map.
- Engineer/Admin model configuration readiness test with saved timestamp/result and audit-event recording.
- Readiness panel for database, image vault, inspection engine, camera, robot, and MES/ERP.
- AI Model Test batch validation with manifest CSV support.
- Confusion matrix and category counts.
- Per-image timing capture for image load, preprocessing, inference/comparison, overlay rendering, and total inspection time, with 1 second target warnings.
- Customer-facing validation report export with HTML output, sample annotated images, print-to-PDF instructions, prototype limitations, and signature/approval section.
- Annotated overlay export.
- Stage 1 customer evidence package export including the strengthened validation report, CSV evidence, overlays, summaries, README, and warnings for missing optional evidence.
- Stage 1 exit evidence CLI for repeatable dataset preflight, batch validation, model acceptance when an active ONNX model is ready, export verification, and Stage 1 factory readiness package generation.
- Admin-only local soak-test mode for repeated Folder Camera Simulation inspections with cancellation, timing, memory estimate, error capture, and HTML stability report export.
- Mock MES integration mode with MES-style traceability payload generation, optional mock REST POST, local JSON fallback, and SQLite upload-attempt audit records.
- Export history audit records.
- 3D Profile Viewer Sample Data Mode for `x,y,height` CSV files.
- Async progress and cancellation for long-running workflows.
- Non-UI automated tests using temporary folders and generated tiny images.
- Installation guide, user manual, stage mapping, and acceptance checklist.
- Hardware/MES/robot interface contracts with null implementations as planned integration boundaries.

## Stage 1 Remaining Gaps

The following items are not complete production functionality in Stage 1:

- No trained production ML model is included by default.
- No trained production ML model is bundled. ONNX ML Model inference is claimed only when a configured local model loads and inference succeeds.
- No real AOI camera hardware acquisition.
- No real lighting controller.
- No real 3D camera or live height-map acquisition.
- No real robot, handler, conveyor, PLC, or safety-circuit control. The current robot cycle is software simulation only.
- No MES/ERP authentication or production traceability. The current Mock MES feature is clearly labeled mock mode only.
- No centralized production database service.
- No production installer or auto-update mechanism.
- No hardened cybersecurity model beyond local role separation.
- No production calibration workflow for real optics, lighting, 3D height, or robot coordinates. The current 2D calibration profile feature is approximate Stage 2 preparation only.
- Remaining placeholder panels are labeled as demo/prototype data; SQLite-backed database health and summary counts use local PoC records where available.

## Stage 2 Planned Camera Work

Stage 2 should add real acquisition and optical integration. The current codebase has Stage 2 camera-pilot architecture, but not accepted real hardware integration.

Architecture already implemented:

- `GenericVisionCameraSource` can run a vendor adapter behind the existing camera-source workflow.
- `IVisionCameraAdapter`, `IVisionCameraAdapterFactory`, and `IVisionDeviceDiscovery` define the GigE/USB3 vendor SDK boundary.
- Manifest-based camera adapter plugin loading exists through `VisionCameraPluginLoader` / `CameraAdapterPluginService`.
- Camera acceptance records per-view frame acquisition, metadata validation, frame timing, dropped frames, trigger failures, and whether evidence is real hardware or simulation.
- Lighting acceptance records per-view lighting command timing and trigger-to-frame timing where a camera source is supplied.
- 3D profile acceptance records source kind, frame dimensions, units, pitch, invalid-height counts, timing, and simulated-vs-real factory readiness status.
- Factory readiness has a `Stage2CameraPilot` profile that evaluates camera, lighting, 3D, export verification, build/test, and limitation evidence.

Real hardware work still planned or blocked:

- Vendor SDK integration for the selected GigE/USB3 cameras through an external adapter plugin.
- Accepted Top, Side, and Bottom camera acquisition from physical devices.
- Stable real camera metadata: physical camera IDs, view assignment, frame IDs, UTC capture timestamps, dimensions, pixel format, and non-simulated source kind.
- Real hardware connection status and diagnostics from the selected vendor adapter.
- Trigger synchronization using the selected camera and lighting hardware.
- Exposure, gain, acquisition settings, and error recovery validated against the selected camera model.
- Physical lighting controller integration and controller acknowledgement evidence.
- Production image calibration and coordinate mapping using validated hardware images and calibration fixtures.
- Real 3D camera integration for live height and coplanarity inspection.
- 3D profile acquisition, slice measurement, and calibrated height units from a real sensor.

Folder Camera Simulation is intended to keep Stage 1 workflows testable while preserving a clean structure for Stage 2 hardware sources. It is not real camera validation.

The current lighting boundary and acceptance service can exercise null, simulated, TCP, or serial controller paths according to configuration, but real lighting readiness requires physical controller evidence. Null or simulated lighting evidence does not control or validate real lighting hardware.

For the current milestone assessment, see [Stage 1 Exit + Stage 2 Camera Pilot Milestone Status](Milestone_Status_Stage1_Exit_Stage2_Camera_Pilot.md). For repeatable Stage 1 evidence generation, see [Stage 1 Exit Evidence CLI](Stage1_Exit_Evidence_CLI.md). For false-positive tradeoff governance, see [False-Positive Minimization and Business Readiness](False_Positive_Minimization_and_Business_Readiness.md).

## Stage 3 Planned Robot / Handler Work

Stage 3 should integrate machine movement and handling. Planned work includes:

- Robot or handler communication layer.
- PLC or machine interface integration.
- Board-present and trigger handshakes.
- Pass/fail/review decision output to machine control.
- Interlock and stop-line policy support.
- Safe retry and fault recovery behavior.
- Mapping inspection coordinates to robot or handler coordinates.
- Production event logging for machine actions.

Current machine-interface JSON exports are evidence artifacts only. They do not control hardware.

The current `IRobotController` and `IEmergencyStopMonitor` contracts include null implementations and a clearly labeled software simulator. The simulator can demonstrate Load, Inspect, Unload, Reset, emergency-stop interruption, and cycle timing in Main Inspection, but it does not send load, inspect, unload, or safety commands to real equipment.

## Stage 4 Planned MES / ERP Work

Stage 4 should connect AOI Monitor to production identity and traceability systems. Planned work includes:

- MES authentication or single sign-on.
- Replacement of local role selector with production identity.
- Work order, lot, serial number, and board route validation.
- Production recipe download and revision control.
- Inspection result upload.
- Defect and disposition upload.
- Audit trail export to production systems.
- ERP or quality-system reporting hooks.
- Centralized configuration and path management.
- Production database integration.

The current local user/role model records user ID and role in audit rows, but it is not MES authentication.

The current `IMesClient` and `ITraceabilityUploader` contracts include null implementations and a mock REST implementation for Stage 1 architecture demonstration. Mock mode can generate local JSON traceability payloads and optionally POST to a configured test endpoint, but it is not production MES/ERP authentication, traceability, or writeback.

## Boundary Statement

The current PoC is suitable for Stage 1 workflow validation, customer evidence review, and offline demonstration with local files. It is not a production AOI controller until the planned hardware, machine, MES/ERP, security, and validated ML-inference stages are implemented and accepted.

For interface details, see [Integration Boundaries](Integration_Boundaries.md).
