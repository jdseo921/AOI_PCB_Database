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

## Run Stage 1 Batch Validation

1. Launch `AOI_Monitor`.
2. Open `AI / Models`.
3. Set `Test Image Folder` to `SampleData/DemoSet_Quick/images`.
4. Set `Ground Truth CSV` to `SampleData/DemoSet_Quick/customer_validation_manifest.csv`.
5. Click `Check Dataset Before Test`.
6. Click `Run Batch Inspection`.
7. Review accuracy, precision, recall, OK/NG/REVIEW counts, false calls, possible escapes, timing, result rows, and selected-image preview.
8. Use `Export CSV`, `Export Annotated Images`, or `Export Validation Package`.

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
