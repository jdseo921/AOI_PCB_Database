# AOI Monitor Implemented Feature Documentation

## Purpose and Context

AOI Monitor is a Windows WPF desktop prototype for PCBA automated optical inspection (AOI) review workflows. The implemented program focuses on defect-image review, golden-image comparison, operator disposition, recipe/policy visibility, training-set export preparation, traceability exports, and local data-handling utilities.

The application is framed around PCBA production and quality workflows for a board program named `TBOX-MAIN`, station `AOI-LIB-01`, and model version `AOI_AI_0.8.1`. It uses defect concepts such as solder bridge, insufficient solder, polarity error, tombstone, pin-height error, false call, possible escape, verified NG, reference designator, FOV, ROI crop, AI result, ground truth, and review disposition.

This document describes the features currently implemented in the codebase. Static dashboard rows and prototype tables are documented as static prototype data, not as live factory or database integrations.

## Application Shell

The main window provides a factory-style navigation shell with six top-level modules:

- Main Inspection
- Recipe Editor
- AI Model Test
- Log & Export
- 3D Profile Viewer
- Settings / Guide

Main Inspection contains contextual access to the former Station Monitor, Disposition, Golden Compare, and Image Library workflows. Log & Export contains the former Reports functionality and links to database health. Settings / Guide contains the operator guide, settings, and installation/prototype notes. The 3D Profile Viewer is displayed as a disabled planned Stage 2 module. Camera and lighting integration are planned Stage 2, robot/handler integration is planned Stage 3, and MES/ERP integration is planned Stage 4.

The shell keeps a shared workflow summary visible while pages change. It shows the active detection policy, loaded sample image, loaded golden reference image, latest comparison score, and latest verdict. Page instances are cached after first creation, and page transitions use a short fade/slide animation.

Global actions include refresh, recipe lock/unlock, export, and opening the local export folder. The export shortcut delegates to applicable workflow pages.

## Shared Workflow State

The program uses a singleton `WorkflowState` object to coordinate page-to-page behavior. It stores:

- Current sample image path
- Current golden reference image path
- Latest analysis result
- Station ID, operator ID, board program, and model version
- Active recipe-lock state
- Detection priority policy
- Training-set export counters and status
- In-memory workflow history, capped at 500 entries

Pages subscribe to workflow state changes and update their UI when images, analysis results, policies, dispositions, training-set export state, or exported events change.

## Detection and Analysis

Image comparison is implemented in `ImageAnalysisService`. The current analysis flow is a deterministic image-difference prototype, not a trained production inspection model.

Implemented behavior:

- Loads a sample PCB image from disk.
- Optionally loads a golden reference image from disk.
- Converts images to BGRA32 for consistent pixel processing.
- Downscales both images to a maximum comparison size of 384 x 384.
- Calculates sample mean brightness.
- Computes mean absolute RGB difference between sample and golden images.
- Converts the image difference to a 0-100 percent difference score.
- Splits the comparison into an 8 x 8 grid and identifies the highest-difference hotspot as a normalized ROI rectangle.
- Applies threshold bands based on the active detection priority.
- Produces a verdict of `OK`, `REVIEW`, or `NG`.
- Adds a suggested defect label, confidence score, decision margin, decision reason, policy name, thresholds, hotspot, timestamp, and human-readable evidence lines.

If no golden image is supplied, the result remains `REVIEW` because the program does not have enough reference data for differential judgment.

Implemented policy thresholds:

| Detection Priority | Review Threshold | NG Threshold | Intent |
| --- | ---: | ---: | --- |
| Minimize False Positives | 12% | 24% | Conservative review mode |
| Balanced | 8% | 18% | Middle-ground review mode |
| Maximize Defect Recall | 5% | 14% | More sensitive defect-recall mode |

## Machine Interface Export

When an analysis result is accepted into workflow state, the program exports a machine-readable inspection decision through `MachineInterfaceExportService`.

This is file export only. It does not control a robot, PLC, conveyor, or machine interlock.

Files are written under:

```text
<application folder>/exports/machine_interface/
```

Implemented decision outputs:

- `latest_decision.json`: latest full decision contract
- `decision_yyyyMMdd_HHmmss.json`: timestamped decision snapshot
- `decision_history.ndjson`: append-only decision history

The exported contract includes schema version, inspection ID, UTC timestamp, station ID, board program, model version, policy, sample path, golden path, verdict, verdict code, confidence, score, thresholds, normalized hotspot, decision reason, evidence, machine-action hints, app version, and source name.

Verdict codes are:

| Verdict | Code |
| --- | ---: |
| OK | 0 |
| REVIEW | 1 |
| NG | 2 |

Machine hints indicate whether the part should be held for review, whether a stop-line recommendation applies, and whether human confirmation is required.

Disposition events are appended to:

```text
<application folder>/exports/machine_interface/disposition_events.ndjson
```

## Page Features

### Main Inspection

The Main Inspection module opens on the former Station Monitor dashboard and provides shortcuts to Disposition, Golden Compare, and Image Library. The dashboard renders static FOV/review cells, not live camera connections. Each cell displays sample count, review count, waiting count, yield gauge, detected percentage, false count, and status styling.

This page is currently a dashboard mockup backed by static station records. It does not poll hardware or a live AOI service.

### Disposition

The Disposition page supports human review of suspected PCBA defects and inspection disagreements.

Implemented features:

- Static review queue containing possible escapes, verified NG items, and false calls.
- AI overlay visibility toggle.
- Zoom toggle for the review canvas.
- Navigation to the Golden Compare page.
- ROI crop export from the currently loaded sample image.
- Recent workflow history popup.
- Disposition actions:
  - Confirm NG
  - Mark False Call
  - Mark Possible Escape
  - Hold for 2nd Review
- Queue current sample image for local training-set export.

Disposition guardrails are implemented:

- Confirming NG is blocked when confidence is below 70%.
- Marking a false call is blocked for high-confidence NG results at or above 85%.

Disposition logging writes to:

```text
<application folder>/exports/review_disposition_log.csv
```

The CSV includes UTC timestamp, station ID, operator ID, sample, golden image, verdict, confidence, policy, model version, decision reason, and action.

### Golden Compare

The Golden Compare page compares a defect/sample image against a golden reference image.

Implemented features:

- Shows loaded sample and golden image filenames.
- Shows latest difference score, verdict, suggested defect, and confidence.
- Displays decision evidence, score-vs-threshold details, and normalized hotspot ROI.
- Runs image comparison through `ImageAnalysisService`.
- Synchronizes zoom between defect and golden panels.
- Resets synchronized pan/zoom.
- Toggles AI/defect overlay opacity.
- Toggles golden/ground-truth overlay opacity.
- Exports a PNG snapshot of the comparison page.

Comparison snapshot export uses a save dialog and renders the current WPF view to PNG.

### Image Library

The Image Library page provides a prototype browser for defect records and schema rows.

Implemented features:

- Static defect records with sample ID, board, RefDes, defect type, severity, AI result, ground truth, risk, image link, and update time.
- Static schema table for `samples`, `annotations`, `ai_results`, `review_events`, and `image_index`.
- Open Record action for selecting a sample PCB image from disk.
- Image preview window for selected sample and golden images.
- Compare Golden action for selecting a golden reference image.
- Automatic analysis after loading the golden image.
- Automatic navigation to the Golden Compare page after analysis.
- Add current sample image to the local training-set export folder.
- Batch relabel event logging.
- Export selected record to CSV, including latest analysis score/verdict metadata when available.

Training-set export candidates are copied to the local app-data image vault:

```text
%LOCALAPPDATA%\AOI_Monitor\image_vault\training\
```

### Recipe Editor

The Recipe Editor page is a basic working editor for local recipe revision proof-of-concept data.

Implemented behavior:

- Load an image as the recipe background.
- Draw, select, move, resize, and delete rectangular ROI.
- Assign ROI types for Presence, Polarity, Solder Bridge, Height, and Anomaly.
- Edit AI score threshold, height min/max, and volume min/max parameters.
- Save recipe revisions to local SQLite with timestamp, board program, operator ID, and JSON ROI data.
- Reload the latest saved recipe revision for the active board program on startup.
- Run a local test inspection against the selected image using the current pixel-difference engine.
- Enforce the global recipe lock before unsafe edits and saves.

Active ROI is shown in yellow, saved ROI in green, and unsaved inactive ROI in blue.

### Log & Export

The Log & Export module combines log browsing, export utilities, and database-health access.

Implemented log behavior:

- Displays inspection history from SQLite.
- Displays review/disposition events from SQLite.
- Displays export history from SQLite.
- Filters by date range, board/model text, operator, and result.
- Sortable inspection, review, and export-history grids.
- Exports filtered inspection history CSV and review log CSV.
- Exports annotated image overlays.
- Exports customer validation packages.
- Records exports in `ExportHistory`.
- Includes copy-only archive indexing for older log rows in `LogArchive`; source records remain queryable.

Database health remains available as a secondary screen from Log & Export. It displays SQLite table counts and local health indicators. Some dashboard/SPC values remain static prototype data.

Additional local utilities:

Implemented features:

- Static package list for customer validation, false-negative review, false-call reduction, annotated image bundle, recipe revision evidence, and SQLite backup packages.
- Package export that creates a timestamped text file under `exports/packages`.
- Image-path verification report for exported image files plus currently loaded sample/golden paths.
- Reviewed-sample archive utility for `exports/training_set`.
- Database integrity check against SQLite `PRAGMA integrity_check` plus current local artifacts, including review log header validation, workflow-history volume, and training-folder write access.
- Image index rebuild that scans exported image files and writes `exports/image_index.csv`.
- Audit-trail export from workflow history.
- Active recipe lock/unlock.

Exports are local filesystem artifacts generated under the application `exports` folder.

### 3D Profile Viewer

The 3D Profile Viewer is visible in the top-level navigation as a disabled planned Stage 2 module. It does not currently import height maps, render 3D surfaces, measure slices, or export profiles.

### Settings / Guide

Settings / Guide contains operator workflow guidance, local settings, and prototype installation notes. Installation notes are documentation only and are no longer a top-level production module.

Guide content covers:

- Local review sequence.
- Recipe/model/lot confirmation.
- Disposition priorities.
- Log & Export/database-health checks.
- Recipe lock and audit export reminders.
- Documentation boundaries for planned hardware, MES, service hosting, and 3D profile integrations.

This area is informational. It does not install a service or control AOI hardware.

Settings secondary view:

The Settings secondary view controls display preferences, detection policy, and local training-set export preparation.

Implemented features:

- Language visual preset for English/Korean labels.
- Font-size preset for compact, standard, and large display scaling.
- Detection-priority selection:
  - Minimize False Positives
  - Balanced
  - Maximize Defect Recall
- Recipe-lock enforcement for detection-priority changes.
- Training Set Export controls:
  - Prepare export
  - Validate list
  - Stop preparation
  - Open training folder
- Export status, queued-sample count, list-check count, and list-quality score display.

Training Set Export prepares local candidate files only. No production training, fine-tuning, or deployment pipeline is run.

## Data Handling Summary

The current implementation combines in-memory workflow state, static prototype records, user-selected image files copied into an app-data image vault, a local SQLite PoC database, and local filesystem exports.

In-memory data:

- Current sample and golden image paths
- Latest analysis result
- Detection priority and recipe lock
- Training-set export status/counters
- Workflow history
- Static dashboard, library, recipe, SPC, installation, and guide rows

User-selected file inputs:

- Sample PCB image
- Golden reference image

Generated local artifacts:

- SQLite database at `%LOCALAPPDATA%\AOI_Monitor\aoi_monitor.sqlite`
- Managed image vault at `%LOCALAPPDATA%\AOI_Monitor\image_vault\`
- Comparison PNG snapshots
- ROI crop PNG files
- Selected-record CSV exports
- Review disposition CSV log
- Machine decision JSON snapshots
- Machine decision NDJSON history
- Disposition NDJSON events
- Training-set export image copies
- Package export text files
- Image-path verification reports
- DB integrity reports
- Image index CSV
- Audit-trail CSV
- Reviewed-sample archive folders

Primary export root:

```text
<application folder>/exports/
```

## Current Boundaries and Limitations

The implemented application is a functional desktop prototype, but several production AOI capabilities are represented only as UI concepts or local-file workflows.

Current boundaries:

- SQLite is local-only PoC persistence, not a production database service.
- Several UI tables and health rows are still static prototype data.
- The image-analysis routine is pixel-difference based, not a production ML inference pipeline.
- There is no direct camera, PLC, robot, conveyor, or AOI machine control.
- Service hosting and hardware links are marked as planned/documentation only.
- Defect records and station metrics are static sample data.
- Some workflow state remains session-local, although key events, imports, analysis results, training-set candidates, and exports are persisted locally.
- Training Set Export is local file preparation only; no model training pipeline is run.

Despite these boundaries, the code already implements the core review loop: load sample image, load golden image, compute comparison, produce verdict/evidence, export a machine-readable JSON decision, record human disposition, collect training-set export candidates, and export audit/reporting artifacts.

## Source Map

Key implementation areas:

- `AOI_Monitor/MainWindow.xaml.cs`: navigation, global workflow summary, refresh/export/recipe-lock actions
- `AOI_Monitor/ViewModels/MainViewModel.cs`: navigation items and static dashboard seed data
- `AOI_Monitor/Services/WorkflowState.cs`: shared workflow state, event history, policy changes, training-set export state
- `AOI_Monitor/Services/ImageAnalysisService.cs`: image loading, comparison, thresholds, verdict/evidence generation
- `AOI_Monitor/Services/MachineInterfaceExportService.cs`: JSON/NDJSON machine-interface exports
- `AOI_Monitor/Data/AoiDatabase.cs`: SQLite schema initialization, database paths, image vault import, and persistence helpers
- `AOI_Monitor/Models/WorkflowModels.cs`: analysis result, detection policy, workflow event, training-set export state
- `AOI_Monitor/Models/MachineInterfaceContractModels.cs`: machine decision contract shape
- `AOI_Monitor/Views/*.xaml.cs`: page-specific AOI workflows and export utilities
