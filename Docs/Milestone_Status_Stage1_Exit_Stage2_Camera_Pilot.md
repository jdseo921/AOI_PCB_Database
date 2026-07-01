# Stage 1 Exit + Stage 2 Camera Pilot Readiness

This milestone assessment separates implemented software architecture from accepted factory evidence. It is intended for client, evaluator, and engineering review before claiming Stage 1 exit readiness or beginning a Stage 2 camera pilot.

Current assessment: Stage 1 workflow capability is implemented in the local prototype, and Stage 2 camera-pilot architecture is present. Stage 1 exit and Stage 2 hardware readiness remain evidence-gated. Do not describe Stage 2 as complete.

## Evidence Boundary

Simulation, folder-source, null-adapter, fake-adapter, sample CSV, mock REST, and boundary-only evidence is useful for workflow smoke testing and architecture rehearsal. It is not real hardware readiness.

In particular:

- Folder Camera Simulation is not real AOI camera acquisition.
- `NullVisionCameraAdapter`, fake test adapters, and plugin-template adapters are not vendor camera acceptance.
- Simulated lighting, null lighting, or command-format tests without physical controller confirmation are not real lighting synchronization evidence.
- 3D Profile sample CSV evidence is not live 3D camera acquisition.
- Mock MES REST and local JSON traceability payloads are not production MES/ERP acceptance.
- Software-only robot/handler simulation is not robot, PLC, conveyor, or safety-circuit acceptance.

## Stage 1 Implemented Items

The current repository implements the Stage 1 local-image validation workflow and evidence shell:

- Windows WPF operator console with focused workflow windows for Home, Board & Images, Run Inspection, Golden Compare, Defect Review, Recipe Rules, AI / Models, Yield Analytics, Export & Trace, Calibration, 3D Profile, Hardware Readiness, and System Settings.
- Local Operator, Engineer, and Admin roles with route authorization and audited access-denied events.
- Board/image import into a managed local image vault with SQLite image records and file hashes.
- Pixel Difference Prototype Engine as the deterministic fallback inspection engine.
- Optional ONNX Runtime inference path when a valid local model, tensor configuration, threshold, and label map are supplied.
- Inspection result, defect, review/disposition, recipe, audit, export, readiness, and acceptance evidence persistence in local SQLite.
- Golden comparison, defect overlays, disposition logging, false-call and possible-escape workflow support, and candidate sample export review.
- Recipe ROI editing, threshold storage, recipe revisions, and local recipe lock behavior.
- AI / Models batch validation with manifest support, confusion metrics, timing capture, and customer validation report generation.
- Stage 1 customer evidence package export with reports, CSV evidence, overlays, summaries, README, and missing-evidence warnings.
- Export verification records and factory-style audit trail records.
- Build/test, HMI layout audit, navigation performance, standards traceability, and quality-gate evidence generation through repository scripts.
- Hardware, camera, lighting, robot, 3D, MES, central sync, and readiness service boundaries that keep Stage 1 local evidence separate from later-stage factory evidence.

## Stage 1 Exit Blockers

Stage 1 exit is not just feature presence. It requires current, reviewable evidence from the intended customer/evaluator dataset and release candidate.

Open blockers before Stage 1 exit can be claimed:

- Real customer dataset evidence: run the customer/evaluator image dataset through the Stage 1 validation workflow with a manifest, expected truth labels where available, and generated validation package evidence.
- Model acceptance evidence: record an accepted model or explicitly scope the exit to the Pixel Difference Prototype Engine / configured local model evidence. A configured ONNX path alone is not a production model claim.
- False-call and possible-escape evidence: produce review evidence for false calls, possible escapes, missed-defect annotations, operator dispositions, and any approved operating threshold profile.
- Export verification evidence: verify generated CSV, HTML, JSON, PNG, PDF, and package artifacts used for the handoff; unresolved export verification errors block exit.
- Build/test evidence: preserve passing hygiene, build, test, quality-gate, HMI layout audit, navigation performance, and package-validation artifacts for the release candidate.

## Stage 2 Readiness Items Already Implemented

The codebase has meaningful Stage 2 camera-pilot architecture. These items support a pilot, but they do not by themselves prove real hardware readiness:

- `GenericVisionCameraSource` wraps a vendor adapter behind the existing camera-source workflow without moving vendor logic into the UI.
- `IVisionCameraAdapter`, `IVisionCameraAdapterFactory`, and `IVisionDeviceDiscovery` define the GigE/USB3 vendor camera boundary.
- `VisionCameraPluginLoader` and `CameraAdapterPluginService` load manifest-based external camera adapter plugins and fail safely to diagnostic null adapters.
- `CameraAcceptanceTestService` records per-view frame acquisition, metadata validation, frame timing, dropped frames, trigger failures, and `IsRealHardware` / factory-readiness status.
- `LightingAcceptanceTestService` records per-view lighting command and trigger-to-frame timing while labeling simulated or no-camera evidence.
- `Profile3DAcceptanceTestService` records 3D profile source, frame dimensions, units, pitch, invalid-height counts, timing, and simulated-vs-real readiness status.
- `FactoryReadinessService` has deployment profiles including `Stage2CameraPilot`, with camera, lighting, 3D profile, export verification, build/test, and known-limitation categories.
- `CompletionAssessmentService` separately scores real camera acceptance, real lighting acceptance, real 3D profile acceptance, and simulated boundary exercise evidence.
- Vendor adapter templates and tests exist to guide external customer/vendor adapter implementation without committing vendor SDK binaries to the main app.

## Stage 2 Blockers

The following items block any claim that Stage 2 camera pilot hardware is accepted:

- Vendor camera adapter: no customer-selected vendor SDK adapter has been accepted in the repository. A real adapter must be implemented and packaged externally through the plugin boundary.
- Real camera metadata: no accepted real camera run proves stable physical camera IDs, view assignment, frame IDs, UTC capture timestamps, dimensions, pixel format, source kind, and non-simulated frame evidence for the required Top/Side/Bottom views.
- Real lighting sync evidence: no accepted physical lighting-controller run proves selected lighting programs, controller acknowledgement, command latency, and camera trigger-to-frame timing under the intended profile.
- Real 3D acquisition evidence: no accepted real 3D sensor run proves live height/profile acquisition, calibrated units, valid dimensions, pitch, invalid-height limits, and source diagnostics.
- Real performance benchmark: Stage 2 needs frame-to-overlay timing evidence with the accepted real camera source and selected inspection path.
- Customer/factory pilot package: Stage 2 evidence must be exported in a readiness package that keeps simulation evidence separate from real hardware evidence.

## Required Next Evidence

Recommended order before promoting a Stage 2 camera pilot:

1. Complete Stage 1 exit evidence for the customer dataset and release candidate.
2. Select the camera, lighting, and 3D hardware scope for the pilot.
3. Build and install vendor/customer adapter plugins outside the main app repository.
4. Run camera acceptance with real hardware for every required view.
5. Run lighting acceptance with the physical lighting controller and camera timing where applicable.
6. Run 3D profile acceptance with the real sensor if 3D is in pilot scope.
7. Run performance benchmark evidence against the real camera source.
8. Export and review the factory readiness package for `Stage2CameraPilot`.

## Related Documents

- [Stage Mapping](Stage_Mapping.md)
- [Requirements Traceability Matrix](Requirements_Traceability_Matrix.md)
- [Stage 1 Exit Evidence CLI](Stage1_Exit_Evidence_CLI.md)
- [False-Positive Minimization and Business Readiness](False_Positive_Minimization_and_Business_Readiness.md)
- [Factory Acceptance Test Plan](Factory_Acceptance_Test_Plan.md)
- [Hardware In The Loop Checklist](Hardware_In_The_Loop_Checklist.md)
- [Vendor Adapter Implementation Guide](Vendor_Adapter_Implementation_Guide.md)
- [Completion Assessment Methodology](Completion_Assessment_Methodology.md)
