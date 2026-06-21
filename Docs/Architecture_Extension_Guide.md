# AOI Monitor Architecture Extension Guide

This guide is for engineers replacing prototype boundaries with real factory integrations. Keep every integration honest: simulated or null adapters can prove workflow shape, logs, and UI behavior, but they do not validate production equipment, production safety, production MES writeback, or production AI accuracy.

## Inspection engine extension

Inspection engines are selected through the inspection engine factory and return an `AnalysisResult`. A production engine should:

- Return `OK`, `REVIEW`, or `NG` using the same verdict contract.
- Fill timing fields in `InspectionTiming`.
- Populate defect evidence with class, confidence, ROI, side/view, and location fields when available.
- Set `ErrorCode` and `ErrorMessage` instead of throwing for expected image/model failures.
- Preserve existing batch metrics behavior so Stage 1 historical reports remain comparable.

Do not replace the Pixel Difference Prototype Engine wording with production claims. It is deterministic Stage 1 evidence, not proof of production model accuracy.

## ONNX model format expectations

Production ONNX support is intentionally explicit. A model must have:

- A configured model file path.
- Known input tensor name.
- Known output tensor name.
- Input width and height matching the model preprocessing expectation.
- A validation check recorded through Settings before readiness can treat it as ready.

The current ONNX path expects a local model file and local label map/configuration. If a model requires custom normalization, multiple tensors, non-image metadata, tiling, or post-processing, implement that behavior behind the inspection engine boundary and document it in the model metadata.

## Label map expectations

Label maps should be stable, versioned, and auditable. They should map model class IDs to operator-readable defect names. Recommended rules:

- Keep class IDs unique and deterministic across revisions.
- Include an explicit background/OK class if the model emits one.
- Use customer/factory defect taxonomy names where possible.
- Preserve old label maps with the model revision that used them.
- Do not silently reorder classes after a model has been validated.

If labels change, re-run model configuration validation, Stage 1 dataset validation, false-call reduction, and customer package generation.

## Camera adapter implementation via IVisionCameraAdapter

Real GigE/USB3 vendor integration belongs behind `IVisionCameraAdapter` in `AOI_Monitor/Services/VisionCameraAdapters.cs`.

An adapter must implement:

- `Connect` with device ID, view type, acquisition mode, exposure, gain, and timeout handling.
- `Start` and `Stop` acquisition.
- `Trigger` for software or hardware-trigger workflows.
- `TryGetFrame` returning a `CameraFrame` with valid metadata.
- `GetDiagnostics` with actionable status messages.

Frame metadata should include frame ID, camera ID, view type, capture timestamp, width, height, pixel format, source kind, and simulation flag. Real adapters must set `IsSimulated = false`; folder, fake, and null adapters must stay clearly labeled as simulation or not connected.

Do not add vendor SDK packages until the specific hardware is selected and license/deployment requirements are understood.

## Lighting controller implementation

Lighting integration belongs behind `ILightingController` in `IntegrationContracts.cs` and the lighting settings/service layer. A real controller should:

- Send no TCP/serial command unless mode and endpoint are explicitly configured.
- Use per-view program names.
- Enforce command timeouts.
- Return command latency and errors.
- Keep simulated lighting labeled as simulated evidence only.

Run the Lighting Sync Test after any real controller integration. If camera trigger-to-frame timing is part of the deployment, run lighting acceptance with a camera source attached.

## Robot controller implementation via IRobotController

Robot/handler integration belongs behind `IRobotController` in `IntegrationContracts.cs` and the `RobotCycleService` state machine. A real adapter should:

- Implement load, inspect-position, unload, and reset commands.
- Reject invalid transitions.
- Return useful command results and timing evidence.
- Preserve audit events for each controlled state transition.
- Distinguish real controller status from simulation.

Emergency stop evidence in this application is readiness evidence only. It is not a safety certification. Production safety validation requires the factory safety process, hardware safety circuit validation, and applicable regulatory review.

## MES REST configuration and spool behavior

MES REST settings are configured in Settings. The app supports:

- Not connected mode.
- Mock/local REST evidence.
- Explicit production REST mode.
- Authentication settings with redacted summaries.
- Failed REST upload spooling.

Failed REST uploads are queued in `MesSpoolQueue` and remain operationally visible until retried, sent, failed, or abandoned by an Admin. Retry behavior respects max retry count, backoff, next-attempt time, and last error. Queue reports must not expose raw passwords, API keys, bearer tokens, or other secrets.

Mock MES output is local interface evidence only. It is not production MES writeback.

## Factory readiness evidence model

Factory readiness is evaluated by deployment profile:

- Stage 1 Customer Data Validation: dataset quality, validation package, model readiness, and export verification.
- Stage 2 Camera Pilot: Stage 1 evidence plus camera and lighting acceptance.
- Stage 3 Robot Cell Pilot: Stage 2 evidence plus robot cycle and emergency-stop simulation or real validation evidence.
- Stage 4 MES Traceability Pilot: Stage 3 evidence plus MES REST/spool readiness.
- Full Factory Automation: all categories plus required real hardware evidence and 8-hour soak evidence.

The readiness dashboard reports Go, Conditional, or No-Go with blocking issues, warnings, unmet criteria, and recommended next actions. When Stage 1 is selected, the report must state that only Stage 1 is ready if higher-stage hardware/MES evidence is missing.

## What not to claim as production-ready

Do not claim any of the following as production-ready unless real validated evidence exists:

- Folder camera simulation or fake camera adapters.
- Null camera, lighting, robot, emergency-stop, or MES adapters.
- Simulated robot cycle evidence.
- Simulated emergency stop evidence.
- Mock MES payload generation.
- Pixel Difference Prototype Engine accuracy.
- ONNX model readiness before model configuration validation.
- Stage 1 customer package readiness as full factory automation readiness.

Use precise wording: "simulation evidence only", "Stage 1 evidence only", "not connected", "not validated", or "requires real hardware validation" as appropriate.
