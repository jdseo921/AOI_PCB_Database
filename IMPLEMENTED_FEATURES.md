# AOI Monitor Implemented Feature Documentation

## Purpose and Context

AOI Monitor is a Windows WPF desktop prototype for PCBA automated optical inspection (AOI) review workflows. The implemented program focuses on defect-image review, golden-image comparison, operator disposition, recipe/policy visibility, training-sample collection, traceability exports, and local data-handling utilities.

The application is framed around PCBA production and quality workflows for a board program named `TBOX-MAIN`, station `AOI-LIB-01`, and model version `AOI_AI_0.8.1`. It uses defect concepts such as solder bridge, insufficient solder, polarity error, tombstone, pin-height error, false call, possible escape, verified NG, reference designator, FOV, ROI crop, AI result, ground truth, and review disposition.

This document describes the features currently implemented in the codebase. Static dashboard rows and prototype tables are documented as static prototype data, not as live factory or database integrations.

## Application Shell

The main window provides a factory-style navigation shell with ten pages:

- Station Monitor
- Disposition
- Golden Compare
- Image Library
- Recipe Matrix
- SPC / Database
- Reports
- Installation Plan
- Settings
- Guide

The shell keeps a shared workflow summary visible while pages change. It shows the active detection policy, loaded sample image, loaded golden reference image, latest comparison score, and latest verdict. Page instances are cached after first creation, and page transitions use a short fade/slide animation.

Global actions include refresh, recipe lock/unlock, export, and opening the local export folder. The export shortcut delegates to the active Compare or Library page when applicable.

## Shared Workflow State

The program uses a singleton `WorkflowState` object to coordinate page-to-page behavior. It stores:

- Current sample image path
- Current golden reference image path
- Latest analysis result
- Station ID, operator ID, board program, and model version
- Active recipe-lock state
- Detection priority policy
- Training-session counters and status
- In-memory workflow history, capped at 500 entries

Pages subscribe to workflow state changes and update their UI when images, analysis results, policies, dispositions, training state, or exported events change.

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

When an analysis result is accepted into workflow state, the program exports a machine-readable inspection decision through `RobotIntegrationService`.

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

### Station Monitor

The Station Monitor page renders static AOI station cards for cameras `CAM01` through `CAM08`. Each station displays sample count, review count, waiting count, yield gauge, detected percentage, false count, and status styling.

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
- Send current sample image to the training set.

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
- Add current sample image to the local training set.
- Batch relabel event logging.
- Export selected record to CSV, including latest analysis score/verdict metadata when available.

Training samples are copied to:

```text
<application folder>/exports/training_set/
```

### Recipe Matrix

The Recipe Matrix page displays static AOI rule and inspection-program data.

Implemented tables include:

- Defect threshold rows for solder bridge, missing component, polarity error, and pin height.
- A component/inspection matrix for presence, polarity, bridge, coplanarity, and volume checks.

The page is currently read-only. Recipe lock/unlock is implemented globally through the main shell and Reports page, and detection-priority changes are blocked when the recipe is locked.

### SPC / Database

The SPC / Database page displays static database-health rows for:

- Samples
- Annotations
- ROI crops
- Similar-image index
- Audit trail
- Unresolved conflicts

The view is currently a prototype health dashboard. It does not connect to a live SQLite database. Some supporting SPC values are also present in the main view model, such as first-pass yield, false-call ratio, escape-risk rate, annotation coverage, broken image links, and missing RefDes count.

### Reports

The Reports page provides local export and maintenance utilities.

Implemented features:

- Static package list for customer validation, false-negative review, false-call reduction, annotated image bundle, recipe revision evidence, and SQLite backup packages.
- Package export that creates a timestamped text file under `exports/packages`.
- Image-path verification report for exported image files plus currently loaded sample/golden paths.
- Reviewed-sample archive utility for `exports/training_set`.
- Database integrity check against current local artifacts, including review log header validation, workflow-history volume, and training-folder write access.
- Image index rebuild that scans exported image files and writes `exports/image_index.csv`.
- Audit-trail export from workflow history.
- Active recipe lock/unlock.

Reports are local filesystem artifacts generated under the application `exports` folder.

### Installation Plan

The Installation Plan page documents intended runtime boundaries using static rows.

Implemented content covers:

- Background inspection service concept
- GUI monitor/review console concept
- Shared local storage concept
- Manual service restart concept
- Prototype scope and non-implemented hardware/service boundaries

This page is informational. It does not install a service or control AOI hardware.

### Settings

The Settings page controls display preferences, detection policy, and training-session state.

Implemented features:

- Language visual preset for English/Korean labels.
- Font-size preset for compact, standard, and large display scaling.
- Detection-priority selection:
  - Minimize False Positives
  - Balanced
  - Maximize Defect Recall
- Recipe-lock enforcement for detection-priority changes.
- Training session controls:
  - Start training session
  - Run training epoch
  - Stop training session
  - Open training folder
- Training status, queued-sample count, epoch count, and validation score display.

Training epochs are simulated with deterministic validation-score updates based on the selected detection priority.

### Guide

The Guide page provides an operator workflow reference. It lists recommended AOI review steps such as confirming service state, checking recipe/model/lot information, reviewing possible escapes first, comparing overlays, checking historical defects, recording disposition, reviewing SPC/database health, locking recipe, and exporting audit evidence.

## Data Handling Summary

The current implementation combines in-memory workflow state, static prototype records, user-selected image files, and local filesystem exports.

In-memory data:

- Current sample and golden image paths
- Latest analysis result
- Detection priority and recipe lock
- Training status/counters
- Workflow history
- Static dashboard, library, recipe, SPC, installation, and guide rows

User-selected file inputs:

- Sample PCB image
- Golden reference image

Generated local artifacts:

- Comparison PNG snapshots
- ROI crop PNG files
- Selected-record CSV exports
- Review disposition CSV log
- Machine decision JSON snapshots
- Machine decision NDJSON history
- Disposition NDJSON events
- Training-set image copies
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

- No live SQLite database connection is implemented.
- Database tables and health rows are static prototype data.
- The image-analysis routine is pixel-difference based, not a production ML inference pipeline.
- There is no direct camera, PLC, robot, conveyor, or AOI machine control.
- The background inspection service shown in the UI is conceptual.
- Defect records and station metrics are static sample data.
- Workflow state is session-local unless exported to files.
- Training-session behavior is simulated; no model training pipeline is run.

Despite these boundaries, the code already implements the core review loop: load sample image, load golden image, compute comparison, produce verdict/evidence, export a machine-readable decision, record human disposition, collect training candidates, and export audit/reporting artifacts.

## Source Map

Key implementation areas:

- `AOI_Monitor/MainWindow.xaml.cs`: navigation, global workflow summary, refresh/export/recipe-lock actions
- `AOI_Monitor/ViewModels/MainViewModel.cs`: navigation items and static dashboard seed data
- `AOI_Monitor/Services/WorkflowState.cs`: shared workflow state, event history, policy changes, training state
- `AOI_Monitor/Services/ImageAnalysisService.cs`: image loading, comparison, thresholds, verdict/evidence generation
- `AOI_Monitor/Services/RobotIntegrationService.cs`: JSON/NDJSON machine-interface exports
- `AOI_Monitor/Models/WorkflowModels.cs`: analysis result, detection policy, workflow event, training state
- `AOI_Monitor/Models/RobotContractModels.cs`: machine decision contract shape
- `AOI_Monitor/Views/*.xaml.cs`: page-specific AOI workflows and export utilities
