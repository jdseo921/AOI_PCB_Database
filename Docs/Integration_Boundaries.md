# Hardware, Robot, and MES Integration Boundaries

This document describes the planned integration contracts added to AOI Monitor. These contracts are architecture boundaries only. The current PoC does not control real hardware, write to MES/ERP, or monitor a real emergency-stop circuit.

## Status Vocabulary

Integration endpoints expose one of these statuses:

| Status | Meaning |
| --- | --- |
| Not Connected | No live integration is configured. This is the default safe PoC state. |
| Simulated | A future simulator or test double is active. This should not be presented as real hardware. |
| Error | The endpoint exists but reports a fault or unusable state. |
| Ready | A future implementation has connected and passed its own readiness checks. |

## Contracts

The contracts live under `AOI_Monitor/Services/IntegrationContracts.cs`.

Implemented boundaries:

- `ILightingController`
- `IRobotController`
- `IMesClient`
- `ITraceabilityUploader`
- `IEmergencyStopMonitor`

Default safe implementations:

- `NullLightingController`
- `NullRobotController`
- `NullMesClient`
- `NullTraceabilityUploader`
- `NullEmergencyStopMonitor`

The null implementations always report `NotConnected` and return non-accepted command results. They do not call vendor SDKs, open network connections, write to MES, or control equipment.

## Placeholder Commands

The current command models are deliberately small so later stages can map them to vendor- or customer-specific protocols:

- `LoadCommand`
- `InspectCommand`
- `UnloadCommand`
- `UploadResultCommand`
- `UploadImageCommand`

Robot/handler work is expected to use Load, Inspect, and Unload commands in Stage 3.

MES and traceability work is expected to use Upload Result and Upload Image commands in Stage 4.

Lighting work is expected to use recipe/view-based lighting program selection in Stage 2.

## Readiness Panel

The app readiness panel displays these planned integration areas:

- Lighting
- Robot
- MES / Traceability
- E-Stop Monitor

In the current PoC these show `Not Connected`. Tooltips explain that they are planned integration boundaries. The UI should not imply that real robot, MES, lighting, PLC, or safety hardware is connected until a future implementation replaces the null services and passes readiness checks.

## Future Implementation Guidance

Future stage implementations should:

1. Implement the existing interfaces in separate classes.
2. Keep vendor SDK code isolated behind those implementations.
3. Preserve the null implementations for offline demos and safe test runs.
4. Report truthful status values.
5. Return friendly command results instead of throwing for normal connection or validation failures.
6. Log operator-visible failures in the app event/review log.
7. Avoid enabling machine-control UI until the integration reports `Ready` and has passed acceptance testing.

## Stage Ownership

- Stage 2: lighting controller and live camera/3D hardware sources.
- Stage 3: robot, handler, PLC, emergency-stop/safety monitoring, and machine action handshakes.
- Stage 4: MES/ERP authentication, traceability upload, result upload, image upload, and production database integration.

