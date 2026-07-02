# Client Test Kit Guide

This guide is for a client or evaluator who receives a packaged AOI Monitor proof-of-concept build and wants to run the program, exercise the main workflows, and collect review evidence independently.

This package is for software evaluation and staged industrial-HMI/software-quality review. It is standards-aligned evidence only; it is not formal ISO, IEC, ISA, safety, cybersecurity, or production-equipment certification.

## What The Client Receives

A client test package should contain:

- `app/` with `AOI_Monitor.exe` and runtime files.
- `RUN_RELEASE.md` with package generation notes.
- `CLIENT_HANDOFF_README.md` with the shortest launch path.
- `Docs/` with user, installation, quality-gate, traceability, and acceptance documents.
- `SampleData/README.md`, `SampleData/customer_validation_manifest_template.csv`, and `SampleData/demo_dataset_generator.ps1` when prepared as a customer validation kit.

The package intentionally does not include customer images, generated demo images, local SQLite databases, image vaults, generated exports, model files, secrets, MES credentials, or production hardware adapters.

Clients do not need to run automated tests to review an image-only learning evidence packet. The internal AOI team can run the workflow and send `visual_learning_report.html` plus the related overlay and false-call comparison artifacts for review.

## Client PC Requirements

- Windows 10 or Windows 11, 64-bit.
- A local folder where the client can unzip and run the package, such as `C:\AOI\AOI_Monitor_ClientTest\`.
- Write access to a local storage folder. The default is `%LOCALAPPDATA%\AOI_Monitor\`; an Admin user can change this in Settings.
- Small non-confidential PNG/JPG/JPEG PCB images for testing.
- For framework-dependent packages only: the matching .NET Desktop Runtime/SDK. Self-contained packages include the runtime.

## First Launch

1. Unzip the package to a local folder.
2. Open `app/`.
3. Run `AOI_Monitor.exe`.
4. On first launch, complete the first-run wizard if it appears.
5. Use Demo Mode for software evaluation unless the team has configured LocalUsers authentication.
6. Select an Admin or Engineer role for setup and export tests.
7. Confirm the readiness banner clearly labels simulated, not-connected, mock, or not-validated features.

Expected first-run state:

- Database and image vault are local and should be available.
- Pixel Difference Prototype Engine is available by default.
- Real camera, robot, lighting, live 3D camera, and production MES are not validated unless the customer separately installed/configured accepted adapters and ran acceptance tests.

## Run Stage 1 Demo in 10 Minutes

Use this route for a quick software workflow evaluation with synthetic, non-confidential data:

1. Open PowerShell in the package folder.
2. Run `pwsh .\SampleData\demo_dataset_generator.ps1 -OutputRoot "$PWD\SampleData\DemoSet_Quick"`.
3. Open `app\AOI_Monitor.exe`.
4. Open `AI / Models`.
5. Select `SampleData\DemoSet_Quick\images`.
6. Select `SampleData\DemoSet_Quick\customer_validation_manifest.csv`.
7. Click `Run Dataset Preflight`.
8. Click `Run Batch Inspection`.
9. Review rows, OK/NG/REVIEW counts, false calls, possible escapes, timing, and selected-row preview.
10. Export CSV and annotated images if requested.
11. Click `Export Stage 1 Validation Package`.
12. Open `Export & Trace > Performance Benchmark` and benchmark `SampleData\DemoSet_Quick\images`.
13. Open `Export & Trace > Stage 1 Readiness`, click `Refresh`, then `Export Report`.
14. Review `stage1_readiness_report.html`, `stage1_readiness_report.pdf`, `stage1_readiness_report.json`, `validation_summary.html`, `customer_validation_report.html`, `benchmark_report.html`, `benchmark_results.csv`, and `limitations.txt`.

This demo can produce Stage 1 uploaded-image validation evidence. It does not validate real camera, lighting, robot, PLC safety, production MES, ERP, cybersecurity, or full factory automation.

## Review Image-Only Learning Evidence

For a visual review packet, ask the internal AOI team for:

- `visual_learning_report.html`.
- `before_after_false_call_report.html`.
- `learned_reference.png`.
- `learned_tolerance_map.png`.
- Representative files from `annotated_overlays/` and `heatmaps/`.
- The package README and manifest.

The client can read the HTML report directly. It explains which Golden / OK / Inspection image groups were used, how many OK Validation images supported false-call calibration, whether NG Validation images were available for possible-escape evidence, and what evidence limits remain.

Synthetic image-learning packets are workflow demonstrations only. Customer acceptance requires the customer/evaluator image set and review of the generated evidence.

## Suggested Independent Test Flow

Use small, non-confidential images. Keep customer/private datasets outside the application package and import them locally during the test.

### 1. Launch And Readiness

- Open the app.
- Confirm the readiness panel is readable.
- Confirm any simulated/mock/not-connected source is clearly labeled.
- Open `Factory Readiness`.
- Open `Standards & Quality Checklist`.
- Confirm missing evidence is shown as `Missing` or `Partial`, not hidden.

### 2. Image Import

- Open `Board & Images`.
- Click `Open Record`.
- Select a PNG/JPG/JPEG sample image.
- Confirm the record appears in the grid.
- Restart the app and confirm the image record is still present.

### 3. Golden Compare

- Select or import a sample image.
- Click `Compare Golden`.
- Select a golden/reference image.
- Confirm the result shows verdict, score, confidence, suggested defect, and overlay/hotspot evidence.
- Confirm the engine is labeled as Pixel Difference Prototype Engine unless a tested ONNX model is configured.

### 4. Review Disposition

- Open `Defect Review`.
- Record one review action, such as `Confirm NG`, `Mark False Call`, `Mark Possible Escape`, or `Hold for 2nd Review`.
- Confirm the action appears in logs.

### 5. AI / Models Batch Validation

- Open `AI / Models`.
- Select a folder with three to ten small demo images.
- Optionally select a ground-truth CSV.
- Run `Run Dataset Preflight`.
- Run `Run Batch Inspection`.
- Confirm metrics and result rows are visible.
- Export CSV, annotated images, and a Stage 1 validation package where available.

### 6. 3D Profile Sample CSV

- Open `3D Profile`.
- Load `SampleData/profile_height_map_sample.csv` or another non-confidential CSV with `x,y,height` columns.
- Confirm the page labels sample data / not live 3D camera mode.
- Confirm the height map, selected point, and slice/profile evidence update.

### 7. Logs, Exports, And Evidence

- Open `Export & Trace`.
- Export inspection history CSV.
- Export review log CSV.
- Export annotated overlays if inspection image paths are available.
- Run Performance Benchmark for image-folder evidence when evaluating Stage 1.
- Export the Stage 1 Readiness report.
- Export the Factory Readiness Go/No-Go package.
- Export the Standards Traceability Matrix from `Standards & Quality Checklist`.
- Export the Client Demo Readiness gate.

Expected exported evidence includes readable HTML/JSON/PDF/CSV/PNG where applicable. Export verification records should show whether the artifact was verified, warned, or failed.

## What To Send Back To The AOI Team

Ask the client to return:

- Screenshots of the readiness panel and any warning/alarm panels.
- The exported Stage 1 readiness report folder.
- The exported Stage 1 validation package folder.
- The exported Factory Readiness package.
- The exported Standards Traceability Matrix.
- Any exported client demo readiness gate report.
- Reproduction steps for any issue.
- Crash report folder if a crash occurs.
- Approximate hardware/software environment: Windows version, package folder, storage root, display resolution, DPI scaling, and whether data was local or networked.

Do not send confidential production images unless there is an approved data-sharing agreement.

## Reset Or Re-Test

Local runtime data is under `%LOCALAPPDATA%\AOI_Monitor\` by default or under the configured storage root. For a clean retest, either choose a new storage root in Settings or archive/delete the local test storage folder after confirming no needed evidence remains.

Do not delete client evidence folders until exported reports, crash reports, and issue screenshots have been reviewed.

## Known Evaluation Boundaries

- Folder Camera Simulation is not real camera validation.
- Sample CSV 3D Profile mode is not live 3D camera validation.
- Simulated/fake robot adapters are not real robot or PLC safety validation.
- Mock/local MES evidence is not production MES validation.
- Stage 1 customer data validation does not imply Stage 2/3/4 or Full Factory Automation readiness.
- Standards traceability and quality-gate reports are project evidence, not formal certification.
