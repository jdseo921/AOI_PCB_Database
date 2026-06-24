# Sample Dataset Performance Demo

This walkthrough shows the Stage 1 uploaded-image validation demo using generated, non-confidential sample data. It is software workflow evidence only. It does not claim real camera, lighting, robot, PLC, production MES, or production model readiness.

## Generate The Demo Data

From the repository root:

```powershell
pwsh SampleData/demo_dataset_generator.ps1
```

The script creates:

- `SampleData/DemoSet_Quick/images`
- `SampleData/DemoSet_Quick/golden`
- `SampleData/DemoSet_Quick/customer_validation_manifest.csv`
- `SampleData/DemoSet_Quick/folder_camera/top`
- `SampleData/DemoSet_Quick/folder_camera/side`
- `SampleData/DemoSet_Quick/folder_camera/bottom`

The manifest columns are:

```text
image,ground_truth,golden_image,defect_type,side,refdes,roi_id,roi_type,lot_id,board_model,notes
```

## Run The Readiness Smoke

From the repository root:

```powershell
pwsh Scripts/run-stage1-readiness-smoke.ps1
```

The smoke script generates the synthetic dataset unless `-SkipGenerate` is passed, verifies the manifest template and generated manifest, runs the focused Stage 1 preflight/benchmark service smoke, and writes:

- `TestResults/stage1_readiness_smoke_report.txt`
- `TestResults/stage1_readiness_smoke_report.json`
- `TestResults/stage1_readiness_smoke.trx`

This is a service-level check. It does not launch WPF and does not validate real camera, lighting, robot, PLC safety, production MES, ERP, or factory automation.
The script uses `dotnet test --no-restore` by default for offline repeatability after the normal build/restore path; pass `-Restore` on a fresh machine if dependencies have not been restored yet.

## Quick Demo Checklist

Use this short pass before a customer walkthrough:

- Generate sample data with `pwsh SampleData/demo_dataset_generator.ps1`.
- Confirm `SampleData/DemoSet_Quick/images`, `golden`, and `customer_validation_manifest.csv` exist.
- Confirm `folder_camera/top`, `folder_camera/side`, and `folder_camera/bottom` exist for Folder Camera Simulation.
- Open `AI / Models`, select the generated `images` folder and manifest, then run `Run Dataset Preflight`.
- Run `Run Batch Inspection` and confirm rows, OK/NG/REVIEW counts, false calls, possible escapes, and preview update.
- Run `Export & Trace > Performance Benchmark` against the generated `images` folder.
- Export the validation package from `AI / Models`.
- Open `Export & Trace > Stage 1 Readiness`, click `Refresh`, then `Export Report`.
- Open `validation_summary.html`, `customer_validation_report.html`, `benchmark_report.html`, `stage1_readiness_report.html`, and `limitations.txt`.
- Confirm every report describes the data as synthetic/demo Stage 1 evidence and does not claim real camera, lighting, robot, MES, production model, or factory validation.

## Run Stage 1 Batch Validation

1. Launch `AOI_Monitor`.
2. Open `AI / Models`.
3. Set `Test Image Folder` to `SampleData/DemoSet_Quick/images`.
4. Set `Ground Truth CSV` to `SampleData/DemoSet_Quick/customer_validation_manifest.csv`.
5. Click `Run Dataset Preflight`.
6. Click `Run Batch Inspection`.
7. Review accuracy, precision, recall, OK/NG/REVIEW counts, false calls, possible escapes, timing, result rows, and selected-image preview.
8. Use `Export CSV`, `Export Annotated Images`, or `Export Stage 1 Validation Package`.

## Run The Performance Benchmark

1. Open `Export & Trace`.
2. Click `Performance Benchmark`.
3. Choose `Image folder`.
4. Select `SampleData/DemoSet_Quick/images`.
5. Keep the default run count or enter a higher count for repeated timing.
6. Click `Run`.

Benchmark outputs are written under the benchmark export folder and include:

- `benchmark_report.html`
- `benchmark_report.pdf`
- `benchmark_report.json`
- `benchmark_results.csv`
- latest benchmark summary JSON

The benchmark report includes p50, p95, p99, max frame-to-overlay time, images per minute, over-one-second count, and separated p95 load/preprocess/inference/overlay/persistence timings.

## Export The Stage 1 Readiness Report

1. Complete dataset preflight, batch validation, false-call review, validation package export, and benchmark.
2. Open `Export & Trace`.
3. Select the `Stage 1 Readiness` tab.
4. Click `Refresh`.
5. Confirm overall status, missing evidence, preflight summary, batch summary, benchmark summary, package path, and next action.
6. Click `Export Report`.

The export folder contains:

- `stage1_readiness_report.html`
- `stage1_readiness_report.pdf`
- `stage1_readiness_report.json`

The Stage 1 readiness report is the handoff gate for uploaded-image validation only. It can pass with no real camera/lighting/robot/MES evidence, but the report must keep that limitation visible.

## Run Folder Camera Simulation

1. Open `Run Inspection`.
2. Set the view to Top, Side, or Bottom.
3. Select these folders when prompted:
   - Top: `SampleData/DemoSet_Quick/folder_camera/top`
   - Side: `SampleData/DemoSet_Quick/folder_camera/side`
   - Bottom: `SampleData/DemoSet_Quick/folder_camera/bottom`
4. Click `Start` and use `Next Board`.

This path must remain labeled as Folder Camera Simulation. It is useful for Stage 2 workflow preparation, but it does not validate real GigE/USB3 cameras, lighting, robot motion, PLC safety, or MES traceability.

## Client Validation Package

After a batch run, click `Export Validation Package` in `AI / Models`. The package includes:

- `validation_summary.html`
- `validation_summary.pdf`
- `customer_validation_report.html`
- `customer_validation_report.pdf`
- `validation_results.csv`
- `validation_breakdown.csv`
- `benchmark_results.csv`
- `customer_validation_manifest.csv` when a manifest was selected
- `dataset_preflight_summary.json`
- `validation_manifest.json`
- `limitations.txt`
- `README.txt`
- annotated overlay images when available

The summary is written for non-technical review and calls out total images, OK/NG/REVIEW counts, accuracy, precision, recall, false calls, possible escapes, p95 benchmark or latency evidence, over-one-second count, engine/model identity, and Stage 1 limitations.

## Limitations

- Generated sample images are synthetic and non-production.
- Pixel Difference Prototype Engine evidence is deterministic prototype workflow evidence, not a trained production ML model claim.
- ONNX readiness is claimed only when a configured local ONNX model loads and succeeds.
- Folder-camera simulation is not live camera validation.
- Real camera, lighting, robot, PLC, MES, ERP, safety, cybersecurity, and factory acceptance require separate validated evidence.
