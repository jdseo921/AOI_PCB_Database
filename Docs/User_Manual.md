# AOI Monitor User Manual

This manual explains how to operate the current AOI Monitor proof of concept during a client review or factory demo. It describes the behavior that is implemented today and calls out prototype boundaries.

## Current Prototype Boundaries

Implemented today:

- Local Windows WPF operator console.
- Local user and role selection.
- Local SQLite database and image vault.
- Operator-first Main Inspection screen.
- Folder Camera Simulation.
- Pixel Difference Prototype Engine.
- ONNX Runtime path for configured local models, with safe `REVIEW` fallback on missing model, invalid model, or runtime failure.
- 2D calibration profile workflow for approximate image-to-board mapping in Stage 2 planning.
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

Restricted actions show permission-denied messages and are recorded in the local event log. Operator and Engineer roles can review `Log & Export` in read-only mode; exporting and deleting logs remain Admin-only.

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

Main Inspection can use Folder Camera Simulation frames when configured. If no simulation folder is available, it can use imported images or a current workflow sample image. It does not connect to real camera hardware.

The `Simulated Robot / Handler` panel is a software-only Stage 1 demonstration. It can run manual `Load`, `Inspect`, `Unload`, `Reset`, and `E-Stop Sim` actions, or a full `Run Cycle` sequence that loads a simulated board image, runs inspection, saves the result, and unloads the simulated board. Cycle time and every simulated robot event are written to the event/review log. This panel does not control a real robot, handler, PLC, conveyor, or safety circuit.

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
5. With the Pixel Difference Prototype Engine, the app performs deterministic image-difference comparison.
6. Review score, confidence, verdict, suggested defect, decision reason, and evidence.
7. Review the hotspot/defect overlay.

Important boundary:

- The default engine is the Pixel Difference Prototype Engine.
- It is useful for workflow validation and evidence generation.
- It is not a trained production ML model.
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
5. Choose ROI type, such as Presence, Polarity, Placement, Solder Bridge, Solder Volume, Height, Surface Defect, or Anomaly.
6. Set the AI score threshold and optional height/volume limits.
7. Set the Processing & Tolerance Rules that apply to the whole recipe: X/Y placement tolerance (mm), rotation tolerance (deg), IPC acceptability class, lighting profile, and false-call policy. These values persist with the recipe revision and are restored when the recipe is reloaded.
8. Click `Test Run` to analyze the loaded image against the current in-editor edits, including unsaved ROI and tolerance changes, before committing a revision. The status line notes that the run used current unsaved edits.
9. Click `Save Recipe` to write the recipe revision.
10. The revision is written to SQLite with board program, operator, role, detection priority, background image path, tolerance/IPC/lighting/false-call rules, and JSON ROI data.
11. Use recipe lock/unlock to prevent accidental edits during evaluation.

The recipe editor is a local Stage 1 recipe proof of concept. It is not yet synchronized with a production recipe server or MES.

## 2D Calibration Profile Workflow

Calibration is restricted to Engineer and Admin roles.

1. Open `Calibration`.
2. Load a sample calibration image.
3. Enter point pairs:
   - image X/Y
   - board X/Y in millimeters
4. Add at least two points to calculate an approximate 2D scale/offset transform.
5. Save the profile to SQLite.
6. Reopen or reload the profile to confirm the points and transform summary.
7. In `Main Inspection`, select the saved `2D Cal Profile` to show approximate board-mm coordinates beside detected defect centers.

This is labeled as `2D calibration profile / Stage 2 preparation`. It does not claim live camera calibration, robot coordinate validation, or production machine alignment.

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

The customer validation package export creates a concise `validation_summary.html`/PDF, the full customer validation report, CSV result files, benchmark CSV evidence when available, the source manifest copy when selected, a limitations file, and a small folder of sample annotated images. The report includes project/station information, operator role, model configuration, validation metrics, inspection performance summary, confusion matrix, failed samples, prototype limitations, and a signature/approval section.

The richer manifest format supports:

```text
image,ground_truth,golden_image,defect_type,side,refdes,lot_id,board_model,notes
```

Simpler CSV formats with image and ground-truth/label columns remain supported.

Bad files, missing images, unsupported images, invalid CSV rows, and database write failures are logged and skipped where possible.

## Log & Export Workflow

Log & Export is available for audit review and evidence generation. Operator and Engineer roles can open it and review Inspection History, Review/Disposition Events, Export History, and the Audit Trail in read-only mode. Export, delete, Mock MES upload, and Soak Test actions remain restricted to Admin.

Common actions:

1. Apply filters by date, board/model, operator, or result.
2. Review Inspection History.
3. Review Review/Disposition Events.
4. Review Export History.
5. Review Audit Trail.
6. Export inspection history CSV.
7. Export review log CSV.
8. Export audit trail CSV.
9. Export annotated overlays.
10. Run DB Integrity.
11. Rebuild image index.
12. Upload selected/latest result to Mock MES.
13. Run a local Soak Test.
14. Create Stage 1 Customer Package.

The Audit Trail tab supports filtering by date, user, role, and action type. The audit CSV includes UTC timestamp, local timestamp, user ID, user role, station ID, action category, action detail, and related record/image/path fields where available.

The Stage 1 customer package creates a timestamped folder containing an HTML customer validation report, print-to-PDF instructions, a Markdown companion report, batch CSV, sample annotated validation images, annotated inspection overlays, inspection history CSV, review log CSV, audit trail CSV, engine/model configuration summary, database health summary, recipe revision summary, calibration profile summary, README, and warnings.

If optional evidence is missing, the app writes a warning instead of failing the package.

The Soak Test tool is Admin-only. It repeatedly inspects images from a selected folder through Folder Camera Simulation for the requested duration, supports cancellation, and exports an HTML report with cycle counts, success/failure counts, timing, memory estimates, start/end time, and errors. Use a short duration such as 2 minutes before running an 8-hour evidence soak.

The Mock MES upload action is Admin-only and is not production MES/ERP integration. It creates a MES-style traceability payload with lot ID, board model, station, operator, result, timestamp, defect summary, and image path. In `Mock REST` mode the app attempts to POST the payload to the configured mock endpoint; if no endpoint is configured, it writes the payload to local JSON. Each attempt is recorded in SQLite.

### Data Retention

By default, AOI Monitor keeps live log rows for 30 days. At startup, rows older than the retention window are first copied into a recoverable local archive with their full row payload, then purged from the live tables. This keeps the database from growing without bound while the audit history stays recoverable. When the pre-purge warning is enabled, Log & Export shows an advisory a configurable number of days before the affected rows are archived and purged (default 7 days).

Retention is configured in `System Settings > Data Retention` by an Engineer or Admin: enable or disable automatic purge, set the retention window in days, and enable or disable the pre-purge warning and its lead time. Disabling purge keeps all live rows in place. The recoverable archive itself is retained indefinitely, so it can be used to reconstruct purged history for audits.

## 3D Sample-Data Workflow

The 3D Profile Viewer runs in Sample Data Mode only. It renders an interactive 3D height surface from a sample CSV. It does not connect to a real 3D camera.

1. Open `3D Profile Viewer`.
2. Confirm the screen clearly shows:
   - Sample Data Mode
   - 3D Camera Not Connected
   - Stage 2 hardware integration required for live 3D profile inspection
3. Leave the source set to `Sample CSV`, then click `Load Height CSV`.
4. Select a CSV with columns:

```text
x,y,height
```

5. Review the interactive 3D height surface:
   - Left-drag to rotate (yaw and pitch).
   - Mouse wheel to zoom.
   - Right-drag or middle-drag to pan.
   - Click `Reset View` to return to the default camera angle.
6. Use the small 2D top-down height map inset to see the whole board at once. Click the inset to select a point. The 3D surface, the inset, and the height slice stay in sync with the selected point.
7. Review the height legend (min/max), the selected-point height, and the height slice/profile line. Notable peaks along the slice are marked automatically.
8. Review the feature/defect list: Type, Height, Volume placeholder, X, and Y. Selecting a row highlights the matching point on the surface and inset; selecting a point on the surface or inset selects the matching row.
9. Click `Accept Defect` or `Reject Defect` for the selected feature.
10. The disposition is recorded as a SQLite review event.

Optional: `Run 3D Acceptance Test` records a sample-data acceptance evidence run and `Export 3D Report` writes the acceptance summary. Both are labeled as sample-data evidence, not live 3D sensor acceptance.

This workflow does not connect to a real 3D camera and does not claim live height inspection.

## Settings / Guide

Settings includes local engine, model, label-map, camera source, Mock MES, threshold, and path controls according to role permissions.

Use Settings to:

- Select the Pixel Difference Prototype Engine or a configured ONNX ML Model.
- Configure model file path, model version, label map path, confidence threshold, input size, and ONNX tensor names.
- Use `Test Model Configuration` to verify model file availability, label-map validity, tensor names, ONNX Runtime session creation, and generic detection output compatibility before running AI Model Test.
- Review the last model-check result and timestamp. `Ready` is shown only after the current configuration passes the readiness check.
- Configure Folder Camera Simulation.
- Review camera status.
- Configure MES mode as Not Connected, Mock REST, or Future Production planned.
- Configure a mock endpoint URL and upload timeout for mock REST tests. Leave the endpoint blank to generate local JSON payload evidence only.
- Change the local storage root where Admin permissions allow it. This controls the SQLite database, image vault, local settings, and local export storage root. Existing folders are left in place when a new root is selected.
- Configure Data Retention: enable or disable automatic archive-and-purge of old log rows, set the retention window in days (default 30), and enable or disable the pre-purge warning and its lead time (default 7 days). Old rows are archived to a recoverable local store before they are purged.

MES authentication and production ERP/MES writeback are planned for Stage 4 and are not implemented in the local role selector or mock upload tool.
