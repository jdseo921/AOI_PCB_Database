OpenAI/Codex and numerous other coding agents will review your output once you are done.

# Calibration, Registration, and Coordinate Systems

Read this when you work on the Calibration window, image alignment/registration, overlay geometry, or Stage 2+ camera/lighting/3D planning. It documents current Stage 1 prototype behavior plus planned scope; the canonical rules are Docs/standard VOL10 §32–§33, cited but not restated here.

**Evidence boundary.** Stage 1 has no real camera, lighting, robot, or 3D hardware. Everything below involving optics is a software prototype on imported or simulated images, or planned scope. Nothing in this file claims validated optics capability.

## Coordinate systems the project keeps separate

Docs/standard VOL10 §33.2 defines the five canonical coordinate systems. Every geometric quantity must live in exactly one of them and say so:

| System | ID | Origin | Units |
| --- | --- | --- | --- |
| Pixel | `CS-PX` | top-left of delivered image (+X right, +Y down) | px |
| Sensor | `CS-SEN` | optical center of active sensor area | mm |
| Corrected-image | `CS-CIMG` | top-left after undistortion and orientation normalization | px |
| Board | `CS-BRD` | recipe-designated fiducial datum | µm |
| World | `CS-WLD` | station mechanical datum (fixture reference) | µm |

The planned transform chain is `CS-SEN → CS-PX → CS-CIMG → CS-BRD → CS-WLD`. Each hop is a stored, versioned transform record (`T-<SRC>2<DST> vN`); `T-CIMG2BRD` is the fiducial-registration step converting corrected-image pixels to board micrometers. Reportable defect positions live in `CS-BRD`; raw or corrected pixels are intermediates, never reportable measurements (VOL10 §33.2).

Two UI-side spaces sit on top of these: overlay coordinates (defect boxes/heatmaps drawn over an image) and screen coordinates. Repo rule: image pixels, corrected image coordinates, board/world coordinates, overlay coordinates, and screen coordinates stay separate and are never mixed (`AGENTS.md`).

Current-state honesty: no coordinate-system/transform registry exists in the code yet; pixel-to-physical conversion uses raw pitch fields only. Building the registry with transform stamping is a recorded migration obligation before any Stage 2 3D pilot (VOL10 §33.4, N-33-4).

## The Calibration window today (Stage 1 prototype)

The Calibration window implements a 2D calibration profile workflow for approximate image-to-board mapping. It is explicitly labeled `2D calibration profile / Stage 2 preparation`, restricted to Engineer and Admin roles, and does not claim live camera calibration, robot coordinate validation, or production machine alignment.

Operator procedure:

1. Open `Calibration`.
2. Load a sample calibration image.
3. Enter point pairs: image X/Y, and board X/Y in millimeters.
4. Add at least two points to calculate an approximate 2D scale/offset transform.
5. Save the profile to SQLite.
6. Reopen or reload the profile to confirm the points and transform summary.
7. In `Main Inspection`, select the saved `2D Cal Profile` to show approximate board-mm coordinates beside detected defect centers.

Profiles persist in the local SQLite tables `CalibrationProfiles` and `CalibrationPoints`, and the calibration profile summary is included in the Stage 1 customer package. Calibration lifecycle states are owned by Docs/standard VOL04 §20 (per the VOL10 boundary notes).

## Registration and alignment in the current image pipeline

- **Golden Compare.** The default engine is the Pixel Difference Prototype Engine: a deterministic image-difference comparison between a selected sample and a golden/reference image, producing score, confidence, verdict, suggested defect, decision reason, evidence, and a hotspot/defect overlay. It is Stage 1 workflow-validation evidence, not a trained production ML model and not proof of production model accuracy.
- **Image-only learning path.** Learning normal PCB appearance from Golden/Reference and OK Learning images records the alignment offsets used while learning in `alignment_summary.csv`, alongside `learned_reference.png` and `tolerance_map.png` (learned normal variation). Inspection outputs anomaly regions as normalized rectangles with score, confidence, area, verdict, and reason — normalized image coordinates, not board coordinates.
- **Recipe-level geometric tolerances.** Recipe Processing & Tolerance Rules carry X/Y placement tolerance (mm) and rotation tolerance (deg); they persist with the recipe revision and are restored on reload.
- **Fiducials.** The board datum is defined as the recipe-designated fiducial datum (VOL10 §33.2). No automated fiducial-registration transform exists in the Stage 1 code; board mapping today is the manual 2D point-pair profile above. Treat calibration, fiducials, registration, board orientation, lighting normalization, and recipe selection as first-class concepts in any new work (`AGENTS.md`).

## Camera, lighting, and frames today (simulated)

- Camera input is Folder Camera Simulation, imported images, or a current workflow sample image; there is no real AOI camera acquisition. Camera source keys are `none`, `folder-simulation`, and `generic-vision-adapter`; unknown keys fail closed to a null source (VOL10 §32.1).
- Every `CameraFrame` carries frame ID, camera ID, view type, capture timestamp, width, height, pixel format, source kind, and an `IsSimulated` flag. Folder, fake, and null adapters must stay labeled simulation or not-connected; frame normalization preserves `IsSimulated` unchanged — a simulated frame can never be silently relabeled as real hardware evidence (VOL10 §32.1).
- Lighting: TCP/serial text controllers exist, but writes are fire-and-forget with no ACK read today — a recorded nonconformity (VOL10 §32.6, N-32-3). Simulated lighting stays labeled as simulated evidence only.
- 3D: the 3D Profile Viewer runs in Sample Data Mode only (`x,y,height` CSV), shows `3D Camera Not Connected` and that Stage 2 hardware integration is required for live 3D profile inspection, and simulated 3D acceptance runs are forced to `NOT VALIDATED` status (VOL10 §33.1).

## Stage 2+ real calibration scope (planned, governed by VOL10 §32–§33)

Real optics work — camera profiles, lighting normalization, 3D metrology — must follow the standard's canonical sections. Consult them before implementing. Outline:

- **§32 Camera and Lighting Architecture (CAM-001–CAM-045):** vendor SDK isolation and plugin integrity, SDK pinning and bitness, controlled native-library loading, camera identity and network placement, connection lifecycle/state machine with bounded backoff, triggering with correlation IDs and bounded timeouts, versioned immutable lighting profiles as the optical contract under which frames are comparable (exposure/gain bounds, settle time; every result records the profile version in force, §32.5), frame metadata/sequencing/evidence integrity, bounded frame queues and decode limits, calibration linkage and health, and the simulation/replay/hardware-in-the-loop evidence boundary.
- **§33 3D Metrology and Coordinate-System Integrity (THD-001–THD-022):** the coordinate/transform registry above, units/resolution/rounding, invalid-data and confidence handling, versioned measurement algorithms, numeric policy and measurement capability, and hard viewer/measurement separation. The measurement record contract (§33.3) makes every 3D value self-describing: value, unit, coordinate-system ID, transform-chain versions, calibration profile ID, algorithm ID and version, height-reference definition, valid-sample fraction, and inherited `IsSimulated` classification.

Known gaps are recorded in the §32.6 and §33.4 nonconformity tables (examples: no frame sequence numbers, unsigned plugin loading, no lighting-profile versioning, no transform registry); they must close before the corresponding Stage 2 pilot evidence can count. Real GigE/USB3 vendor integration belongs behind `IVisionCameraAdapter`; lighting belongs behind `ILightingController` with per-view program names and command timeouts, with the Lighting Sync Test run after any real controller integration. Stage 2 Camera Pilot readiness is Stage 1 evidence plus camera and lighting acceptance — simulated or null adapters prove workflow shape only, never production equipment validation. Simulation states remain visibly labeled per the HMI rules (purple simulation labeling, Docs/standard VOL12 §36).

## Related documents

- `Docs/standard/VOL10_Camera_Lighting_3D.md` — canonical camera/lighting/3D rules (§32–§33); see also VOL04 §20 (calibration lifecycle), VOL08 §29 (image parsing security), VOL12 §36 (simulation labeling).
- `Docs/USER_MANUAL.md` — full operator workflows surrounding the calibration steps.
- `Docs/ARCHITECTURE.md` — adapter and integration boundaries.
- `Docs/DATA_PIPELINE.md` — image vault and learning artifacts.
- `Docs/ROADMAP.md` — stage ladder and readiness evidence.
