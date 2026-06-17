# Stage 1 Acceptance Checklist

This checklist is for the current local WPF prototype. It separates features that are implemented in Stage 1 from functions that are intentionally planned for later stages.

## Test Setup

- [ ] Build succeeds with `dotnet build AOI_Monitor\AOI_Monitor.csproj`.
- [ ] App launches on Windows with WPF desktop support.
- [ ] Readiness panel shows:
  - Database: `Connected`
  - Image Vault: `Available`
  - Inspection Engine: `Pixel Difference Prototype Engine`
  - Camera: `Folder Camera Simulation / Not Connected`
  - Robot: `Simulated Robot / Not Connected`
  - MES / ERP: `Mock MES / Not Connected`
- [ ] Demo images are available locally. See [SampleData/README.md](../SampleData/README.md).

## Implemented Stage 1 Checks

### Image Import

- [ ] Open `Main Inspection`.
- [ ] Open `Image Library`.
- [ ] Click `Open Record`.
- [ ] Select a PNG/JPG/JPEG PCB sample image.
- [ ] Confirm the image is copied into `%LOCALAPPDATA%\AOI_Monitor\image_vault\`.
- [ ] Confirm the imported image appears in the Image Library grid after refresh or re-open.

### Batch Import

- [ ] Open `Image Library`.
- [ ] Click `Batch Import`.
- [ ] Select a folder containing small demo PNG/JPG/JPEG images.
- [ ] Confirm the import summary reports imported, duplicate, unsupported, missing, or invalid files.
- [ ] Confirm imported image rows are visible in the Image Library grid.

### Database Persistence

- [ ] Import at least one image.
- [ ] Close and restart the app.
- [ ] Confirm the imported image record reloads from SQLite.
- [ ] Run one comparison or recipe test run.
- [ ] Open `Log & Export`.
- [ ] Confirm inspection history and/or review events are loaded from SQLite.

### Pixel Difference Prototype Engine

- [ ] In `Image Library`, select or import a sample image.
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

- [ ] Open `Disposition`.
- [ ] Confirm current analysis details are visible when an inspection result exists.
- [ ] Try `Confirm NG`, `Mark False Call`, `Mark Possible Escape`, `Queue Candidate`, and `Hold for 2nd Review` as appropriate.
- [ ] Confirm guardrails display a warning when confidence policy blocks an action.
- [ ] Confirm disposition events are recorded in local logs.

### CSV Export

- [ ] Open `Log & Export`.
- [ ] Apply a date/result/operator/board filter if useful.
- [ ] Click `Inspection History CSV`.
- [ ] Confirm the export confirmation dialog appears.
- [ ] Save the CSV and open it in a spreadsheet/text editor.
- [ ] Repeat for `Review Log CSV`.
- [ ] Confirm each export appears in the Export History grid.

### Annotated Image Export

- [ ] Open `Log & Export`.
- [ ] Ensure filtered inspection rows include accessible sample image paths.
- [ ] Click `Annotated Overlays`.
- [ ] Confirm the export confirmation dialog appears.
- [ ] Select an output folder.
- [ ] Confirm PNG overlay images are written.
- [ ] Confirm the export appears in Export History.

### Customer Validation Batch Test

- [ ] Open `AI Model Test`.
- [ ] Select a folder containing small demo PCB images.
- [ ] Optionally select a ground-truth CSV if available.
- [ ] Click `Run Batch Inspection`.
- [ ] Confirm accuracy/precision/recall/false-call metrics update.
- [ ] Export CSV and annotated images if needed.
- [ ] Open `Log & Export` and create a `Customer Package` using filtered logs.

### 3D Profile Sample Data

- [ ] Open `3D Profile Viewer`.
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
