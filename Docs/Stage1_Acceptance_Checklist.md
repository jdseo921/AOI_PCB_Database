# Stage 1 Acceptance Checklist

This checklist is for the current local WPF prototype. It separates features that are implemented in Stage 1 from functions that are intentionally planned for later stages.

## Test Setup

- [ ] Build succeeds with `dotnet build AOI_PCB_Database.slnx --configuration Release`.
- [ ] App launches on Windows with WPF desktop support.
- [ ] Readiness panel shows:
  - Database: `Connected`
  - Image Vault: `Available`
  - Inspection Engine: `Pixel Difference Prototype Engine`
  - Camera: `Folder Camera Simulation / Not Connected`
  - Robot: `Simulated Robot / Not Connected`
  - MES / ERP: `Mock MES / Not Connected`
- [ ] Demo images are available locally. See [SampleData/README.md](../SampleData/README.md).
- [ ] Optional service-level smoke succeeds with `pwsh Scripts/run-stage1-readiness-smoke.ps1`.

## Run Stage 1 Demo in 10 Minutes

Use this route for a management or customer software walkthrough with synthetic, non-confidential data:

1. Generate sample data: `pwsh SampleData/demo_dataset_generator.ps1`.
2. Launch AOI Monitor.
3. Open `AI / Models`.
4. Select `SampleData/DemoSet_Quick/images`.
5. Select `SampleData/DemoSet_Quick/customer_validation_manifest.csv`.
6. Click `Run Dataset Preflight`.
7. Click `Run Batch Inspection`.
8. Review rows, OK/NG/REVIEW counts, false calls, possible escapes, timing, and selected-row preview.
9. Export CSV and annotated images if needed.
10. Click `Export Stage 1 Validation Package`.
11. Open `Export & Trace > Performance Benchmark` and benchmark `SampleData/DemoSet_Quick/images`.
12. Open `Export & Trace > Stage 1 Readiness`, click `Refresh`, then `Export Report`.
13. Review `stage1_readiness_report.html`, `stage1_readiness_report.pdf`, `stage1_readiness_report.json`, `validation_summary.html`, `customer_validation_report.html`, `benchmark_report.html`, `benchmark_results.csv`, and `limitations.txt`.
14. Confirm every report keeps the claim scoped to Stage 1 uploaded-image validation and does not claim real camera, lighting, robot, MES, safety, or full factory automation readiness.

## Implemented Stage 1 Checks

### Image Import

- [ ] Open `Board & Images`.
- [ ] Click `Open Record`.
- [ ] Select a PNG/JPG/JPEG PCB sample image.
- [ ] Confirm the image is copied into `%LOCALAPPDATA%\AOI_Monitor\image_vault\`.
- [ ] Confirm the imported image appears in the Board & Images grid after refresh or re-open.

### Batch Import

- [ ] Open `Board & Images`.
- [ ] Click `Batch Import`.
- [ ] Select a folder containing small demo PNG/JPG/JPEG images.
- [ ] Confirm the import summary reports imported, duplicate, unsupported, missing, or invalid files.
- [ ] Confirm imported image rows are visible in the Image Library grid.

### Database Persistence

- [ ] Import at least one image.
- [ ] Close and restart the app.
- [ ] Confirm the imported image record reloads from SQLite.
- [ ] Run one comparison or recipe test run.
- [ ] Open `Export & Trace`.
- [ ] Confirm inspection history and/or review events are loaded from SQLite.

### Pixel Difference Prototype Engine

- [ ] In `Board & Images`, select or import a sample image.
- [ ] Click `Compare Golden`.
- [ ] Select a golden/reference image.
- [ ] Confirm analysis completes with an `OK`, `REVIEW`, or `NG` verdict.
- [ ] Confirm the UI shows score, confidence, suggested defect, and evidence.
- [ ] Confirm the default path is presented as Pixel Difference Prototype Engine inference; ONNX ML Model inference is presented as active only when a configured model successfully runs.

### Defect Overlay Display

- [ ] Open `Golden Compare` after a comparison.
- [ ] Confirm the normalized hotspot/overlay appears on the sample image.
- [ ] Use overlay/zoom controls and confirm the visual remains usable.
- [ ] Export a comparison snapshot if needed for evidence.

### Review Disposition

- [ ] Open `Defect Review`.
- [ ] Confirm current analysis details are visible when an inspection result exists.
- [ ] Try `Confirm NG`, `Mark False Call`, `Mark Possible Escape`, `Queue Candidate`, and `Hold for 2nd Review` as appropriate.
- [ ] Confirm guardrails display a warning when confidence policy blocks an action.
- [ ] Confirm disposition events are recorded in local logs.

### CSV Export

- [ ] Open `Export & Trace`.
- [ ] Apply a date/result/operator/board filter if useful.
- [ ] Click `Inspection History CSV`.
- [ ] Confirm the export confirmation dialog appears.
- [ ] Save the CSV and open it in a spreadsheet/text editor.
- [ ] Repeat for `Review Log CSV`.
- [ ] Confirm each export appears in the Export History grid.

### Annotated Image Export

- [ ] Open `Export & Trace`.
- [ ] Ensure filtered inspection rows include accessible sample image paths.
- [ ] Click `Annotated Overlays`.
- [ ] Confirm the export confirmation dialog appears.
- [ ] Select an output folder.
- [ ] Confirm PNG overlay images are written.
- [ ] Confirm the export appears in Export History.

### Customer Validation Batch Test

- [ ] Open `AI / Models`.
- [ ] Select a folder containing small demo PCB images.
- [ ] Select a ground-truth CSV or customer validation manifest if available.
- [ ] Click `Run Dataset Preflight` and resolve blocking failures.
- [ ] Click `Run Batch Inspection`.
- [ ] Confirm accuracy/precision/recall/false-call metrics update.
- [ ] Export CSV and annotated images if needed.
- [ ] Click `Export Stage 1 Validation Package`.
- [ ] Open `Export & Trace > Performance Benchmark` and run a benchmark against the same image folder.
- [ ] Open `Export & Trace > Stage 1 Readiness` and export the Stage 1 readiness report.

### 3D Profile Sample Data

- [ ] Open `3D Profile`.
- [ ] Confirm the page clearly shows `Sample Data Mode` and `3D Camera Not Connected`.
- [ ] Load a CSV with columns `x,y,height`.
- [ ] Confirm the 2D height map, height legend, min/max height, selected point height, and slice/profile line update.
- [ ] Accept or reject a selected sample-data defect.
- [ ] Confirm the action is recorded in review/disposition events.

## Planned Stage 2 / 3 / 4 Checks

These are not implemented in Stage 1 and should remain clearly labeled as planned or not connected:

- [ ] Camera status is marked `Folder Camera Simulation / Not Connected`; no real camera hardware is implied.
- [ ] Lighting control is marked planned Stage 2.
- [ ] Live 3D camera profile inspection is marked planned Stage 2. The current 3D Profile Viewer is sample CSV mode only.
- [ ] Simulated Robot is labeled software-only; production robot/handler control is marked planned Stage 3.
- [ ] Mock MES is labeled mock/not connected; production MES / ERP integration is marked planned Stage 4.
- [ ] Training Set Export is local file preparation only; no model training, fine-tuning, or deployment is run.
- [ ] Inspection Engine reports `Pixel Difference Prototype Engine` by default, `ML Model Missing` for absent ONNX files, `Model Not Tested` for unverified ONNX settings, and `Ready` only after the current local ONNX configuration passes the readiness test.
- [ ] `Test Model Configuration` records a model-check event in the review/audit log.

## Evidence to Capture

- Screenshot of readiness panel.
- Screenshot of imported image list.
- Screenshot of Golden Compare result with overlay.
- Screenshot of Log & Export filtered rows.
- Exported inspection CSV.
- Exported review CSV.
- Annotated overlay PNG.
- Customer validation package folder contents.
- Stage 1 readiness report folder with HTML/PDF/JSON.
- Benchmark report folder with p95 and over-one-second evidence.
