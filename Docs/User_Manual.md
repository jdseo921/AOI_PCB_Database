# AOI Monitor User Manual

This manual explains how to operate the current AOI Monitor proof of concept during a client review or factory demo. It describes the behavior that is implemented today and calls out prototype boundaries.

## Current Prototype Boundaries

Implemented today:

- Local Windows WPF operator console.
- Local user and role selection.
- Local SQLite database and image vault.
- Operator-first Main Inspection screen.
- Folder-based camera simulator.
- Pixel-difference prototype inspection engine.
- ONNX Runtime path for configured local models, with safe `REVIEW` fallback on missing model, invalid model, or runtime failure.
- Image import, golden comparison, disposition logging, recipe revision storage, AI Model Test validation, customer package export, and 3D sample-data CSV review.

Not implemented as production integration:

- Real AOI camera acquisition.
- Real 3D camera hardware.
- PLC, robot, handler, conveyor, or line-stop control.
- MES/ERP authentication and traceability.
- Production database service.
- A bundled trained production ML model. ONNX inference is available only when a valid local model is configured and successfully runs.

## Roles And Permissions

The app uses a simple local role selector. It is not MES login.

| Role | Allowed |
| --- | --- |
| Operator | Run inspection, view results, save inspection result, view guide. |
| Engineer | Operator permissions plus edit recipes, run AI Model Test, change inspection thresholds. |
| Admin | Engineer permissions plus export logs, manage settings, change database/vault/model paths, and access maintenance actions. |

Restricted actions show permission-denied messages and are recorded in the local event log.

## Main Inspection Workflow

Main Inspection is the primary operator screen.

1. Open `Main Inspection`.
2. Confirm station, board model, lot ID, operator, engine, and model version.
3. Select the view: `Top`, `Side`, or `Bottom`.
4. Click `Start` to begin simulated inspection mode.
5. Click `Next Board` to load the next queued or simulated image frame.
6. Review the large image/live-feed area.
7. Inspect the overlay bounding boxes and labels.
8. Review the defect list table: No, Type, Score, Side, X, and Y.
9. Check the result indicator:
   - Green `OK`
   - Red `NG`
   - Yellow `REVIEW` or warning
10. Click `Save Result` to persist the inspection result and defects to SQLite.
11. Optional: enable auto-save to save each completed board automatically.
12. Click `Stop` to pause simulated inspection.

The alarm/event log updates as inspection starts, stops, advances to the next board, completes analysis, saves results, or encounters errors.

Main Inspection can use folder-simulated camera frames when configured. If no simulation folder is available, it can use imported images or a current workflow sample image. It does not connect to real camera hardware.

## Image Import Workflow

1. Open `Image Library`.
2. Click `Open Record`.
3. Select a PNG/JPG/JPEG image.
4. The app validates the image and copies it into the managed image vault.
5. The image record is stored in SQLite with original path, vault path, board model, lot ID, view type, timestamp, and file hash.
6. Duplicate images are detected by SHA-256 hash and are not copied again.

For bulk import:

1. Click `Batch Import`.
2. Select a folder.
3. The app imports supported images and skips unsupported or invalid files.
4. Progress and cancellation are shown during import.
5. Import issues are logged as local review events.

## Golden Comparison Workflow

Golden comparison is the Stage 1 prototype inspection path.

1. Import or select a sample image.
2. Click `Compare Golden`.
3. Select a golden/reference image.
4. The app runs the selected inspection engine.
5. With the default prototype engine, the app performs deterministic pixel-difference comparison.
6. Review score, confidence, verdict, suggested defect, decision reason, and evidence.
7. Review the hotspot/defect overlay.

Important boundary:

- The default engine is a prototype pixel-difference engine.
- It is useful for workflow validation and evidence generation.
- It is not a trained production AI model.
- ONNX configuration can be selected, but the app reports `REVIEW` with clear evidence if model loading or inference fails.

## Disposition Workflow

Disposition is used for human review of results.

1. Open `Disposition`.
2. Review the current analysis details.
3. Use the available disposition actions:
   - Confirm NG
   - Mark False Call
   - Mark Possible Escape
   - Hold for 2nd Review
   - Queue Candidate
4. The app applies confidence guardrails where configured.
5. The action is recorded in SQLite review events with user ID and role.

Disposition events are visible in `Log & Export`.

## Recipe Editor Workflow

Recipe editing is restricted to Engineer and Admin roles.

1. Open `Recipe Editor`.
2. Load a background image.
3. Draw rectangular ROIs.
4. Select and adjust ROI position and size.
5. Choose ROI type, such as Presence, Polarity, Solder Bridge, Height, or Anomaly.
6. Set AI score threshold and optional height/volume limits.
7. Save the recipe revision.
8. The revision is written to SQLite with board program, operator, role, detection priority, background image path, and JSON ROI data.
9. Use recipe lock/unlock to prevent accidental edits during evaluation.

The recipe editor is a local Stage 1 recipe proof of concept. It is not yet synchronized with a production recipe server or MES.

## AI Model Test Workflow

AI Model Test is restricted to Engineer and Admin roles.

1. Open `AI Model Test`.
2. Select a validation image folder.
3. Optionally select a ground-truth CSV manifest.
4. Click `Run Batch Inspection`.
5. Watch progress and cancel if needed.
6. Review metrics:
   - Accuracy
   - Precision
   - Recall
   - False call rate
   - TP, TN, FP, FN
   - OK, NG, REVIEW, false call, possible escape, unknown/unlabeled counts
   - Average, max, and min inspection time, plus count over the 1 second target
7. Export CSV results if needed.
8. Export annotated images if needed.
9. Generate a customer validation report.

The customer validation report export creates a browser-readable HTML report, a small folder of sample annotated images, and a text file explaining how to print the HTML report to PDF. The report includes project/station information, operator role, model configuration, validation metrics, inspection performance summary, confusion matrix, failed samples, prototype limitations, and a signature/approval section.

The richer manifest format supports:

```text
image,ground_truth,golden_image,defect_type,side,refdes,lot_id,board_model,notes
```

Simpler CSV formats with image and ground-truth/label columns remain supported.

Bad files, missing images, unsupported images, invalid CSV rows, and database write failures are logged and skipped where possible.

## Log & Export Workflow

Log & Export is available for audit review and evidence generation. Some export actions are restricted to Admin.

Common actions:

1. Apply filters by date, board/model, operator, or result.
2. Review Inspection History.
3. Review Review/Disposition Events.
4. Review Export History.
5. Export inspection history CSV.
6. Export review log CSV.
7. Export annotated overlays.
8. Run DB Integrity.
9. Rebuild image index.
10. Run a local Soak Test.
11. Create Stage 1 Customer Package.

The Stage 1 customer package creates a timestamped folder containing an HTML customer validation report, print-to-PDF instructions, a Markdown companion report, batch CSV, sample annotated validation images, annotated inspection overlays, inspection history CSV, review log CSV, engine/model configuration summary, database health summary, recipe revision summary, README, and warnings.

If optional evidence is missing, the app writes a warning instead of failing the package.

The Soak Test tool is Admin-only. It repeatedly inspects images from a selected folder through the folder camera simulator for the requested duration, supports cancellation, and exports an HTML report with cycle counts, success/failure counts, timing, memory estimates, start/end time, and errors. Use a short duration such as 2 minutes before running an 8-hour evidence soak.

## 3D Sample-Data Workflow

The 3D Profile Viewer is implemented only in Sample Data Mode.

1. Open `3D Profile Viewer`.
2. Confirm the screen clearly shows:
   - Sample Data Mode
   - 3D Camera Not Connected
   - Stage 2 hardware integration required for live 3D profile inspection
3. Click `Load Height CSV`.
4. Select a CSV with columns:

```text
x,y,height
```

5. Review the 2D color-coded height map.
6. Use hover or click to select a point.
7. Review min height, max height, selected point height, height legend, and height slice/profile line.
8. Review defect details: Type, Height, Volume placeholder, X, and Y.
9. Click `Accept Defect` or `Reject Defect`.
10. The action is recorded as a SQLite review event.

This workflow does not connect to a real 3D camera and does not claim live height inspection.

## Settings / Guide

Settings includes local engine, model, label-map, camera source, threshold, and path controls according to role permissions.

Use Settings to:

- Select the prototype engine or a configured ONNX Runtime model.
- Configure model file path, model version, label map path, confidence threshold, input size, and ONNX tensor names.
- Use `Test Model Configuration` to verify model file availability, label-map validity, tensor names, ONNX Runtime session creation, and generic detection output compatibility before running AI Model Test.
- Review the last model-check result and timestamp. `Ready` is shown only after the current configuration passes the readiness check.
- Configure folder-based camera simulation.
- Review camera status.
- Change local paths where Admin permissions allow it.

MES authentication is planned for Stage 4 and is not implemented in the local role selector.
