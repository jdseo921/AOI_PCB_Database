# AOI Monitor Stage Mapping

This document maps the current proof of concept to the staged delivery expectations. It is written for client and evaluator review.

## Stage 1 Implemented Features

Stage 1 currently includes:

- Windows WPF operator console.
- Local role selector for Operator, Engineer, and Admin.
- Main Inspection operator workflow with Start, Stop, Next Board, Save Result, auto-save, result indicator, defect overlay, defect list, and event log.
- Folder-based camera simulator for Top, Side, and Bottom view images.
- Image Library import and batch import.
- Managed local image vault.
- Local SQLite database initialization and persistence.
- Inspection result persistence, including selected engine and model version.
- Defect result persistence.
- Review and disposition event persistence.
- Recipe editor with ROI drawing, thresholds, recipe revisions, and role restrictions.
- Default pixel-difference prototype inspection engine.
- ONNX Runtime inference path with configurable model path, tensor names, input size, confidence threshold, and label map.
- Engineer/Admin model configuration readiness test with saved timestamp/result and audit-event recording.
- Readiness panel for database, image vault, inspection engine, camera, robot, and MES/ERP.
- AI Model Test batch validation with manifest CSV support.
- Confusion matrix and category counts.
- Per-image timing capture for image load, preprocessing, inference/comparison, overlay rendering, and total inspection time, with 1 second target warnings.
- Customer-facing validation report export with HTML output, sample annotated images, print-to-PDF instructions, prototype limitations, and signature/approval section.
- Annotated overlay export.
- Stage 1 customer evidence package export including the strengthened validation report, CSV evidence, overlays, summaries, README, and warnings for missing optional evidence.
- Admin-only local soak-test mode for repeated folder-simulated inspections with cancellation, timing, memory estimate, error capture, and HTML stability report export.
- Export history audit records.
- 3D Profile Viewer Sample Data Mode for `x,y,height` CSV files.
- Async progress and cancellation for long-running workflows.
- Non-UI automated tests using temporary folders and generated tiny images.
- Installation guide, user manual, stage mapping, and acceptance checklist.
- Hardware/MES/robot interface contracts with null implementations as planned integration boundaries.

## Stage 1 Remaining Gaps

The following items are not complete production functionality in Stage 1:

- No trained production AI model is included by default.
- No trained production AI model is bundled. ONNX model inference is claimed only when a configured local model loads and inference succeeds.
- No real AOI camera hardware acquisition.
- No real lighting controller.
- No real 3D camera or live height-map acquisition.
- No robot, handler, conveyor, or PLC control.
- No MES/ERP authentication or production traceability.
- No centralized production database service.
- No production installer or auto-update mechanism.
- No hardened cybersecurity model beyond local role separation.
- No calibration workflow for real optics, lighting, 3D height, or robot coordinates.
- Remaining placeholder panels are labeled as demo/prototype data; SQLite-backed database health and summary counts use local PoC records where available.

## Stage 2 Planned Camera Work

Stage 2 should add real acquisition and optical integration. Planned work includes:

- Vendor SDK integration for selected GigE/USB3 cameras.
- Real camera source implementations behind the existing `ICameraSource` abstraction.
- Top, Side, and Bottom camera acquisition.
- Hardware connection status and diagnostics.
- Trigger synchronization.
- Exposure, gain, and acquisition settings.
- Lighting controller integration.
- Image calibration and coordinate mapping.
- Real 3D camera integration for live height and coplanarity inspection.
- 3D profile acquisition, slice measurement, and calibrated height units.
- Camera error recovery and operator-safe status messaging.

The current folder simulator is intended to keep Stage 1 workflows testable while preserving a clean structure for Stage 2 hardware sources.

The current `ILightingController` contract and `NullLightingController` implementation are boundary placeholders only. They do not control real lighting hardware.

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

The current `IRobotController` and `IEmergencyStopMonitor` contracts are boundary placeholders only. The null implementations do not send load, inspect, unload, or safety commands to real equipment.

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

The current `IMesClient` and `ITraceabilityUploader` contracts are boundary placeholders only. The null implementations do not upload results, images, lots, serials, or dispositions to MES/ERP systems.

## Boundary Statement

The current PoC is suitable for Stage 1 workflow validation, customer evidence review, and offline demonstration with local files. It is not a production AOI controller until the planned hardware, machine, MES/ERP, security, and validated ML-inference stages are implemented and accepted.

For interface details, see [Integration Boundaries](Integration_Boundaries.md).
