# Client Image Learning Demo Guide

This guide explains how to create a client-facing evidence folder for image-only PCB learning. The client can review the output report without running tests or reading logs.

The package shows that AOI Monitor can learn normal PCB appearance from uploaded image groups, reduce false calls on OK Validation images, and display abnormal regions with heatmaps and boxes. It does not prove live camera readiness or customer acceptance unless the evidence was produced from the customer/evaluator dataset and reviewed in scope.

## Fastest Demo Command

Run this from the repository root:

```powershell
dotnet run --project AOI_Monitor.Tools -- client-image-learning-demo `
  --synthetic `
  --output TestResults/image-learning-demo `
  --operator ci-image-learning `
  --false-call-target 0.05
```

The command creates a synthetic image-only learning package under `TestResults/image-learning-demo`.

Synthetic mode must always be described as:

- Synthetic only.
- Not customer acceptance.
- Not production model certification.

## Customer Folder Mode

For real customer/evaluator images, use the folder convention:

```text
project_folder/
  golden/
  ok_learning/
  ok_validation/
  inspection/
  ng_validation/ optional
  image_truth.csv optional
```

Then run:

```powershell
dotnet run --project AOI_Monitor.Tools -- client-image-learning-demo `
  --project-folder C:\AOI\customer_image_project `
  --output C:\AOI\client_image_learning_evidence `
  --operator engineer01 `
  --false-call-target 0.05
```

No defect labels, bounding boxes, per-defect variables, model files, or camera hardware are required. `image_truth.csv` is image-level only and is used for false-call and possible-escape reporting.

## Output Folder

The evidence folder includes:

- `README_CLIENT_IMAGE_LEARNING_DEMO.txt`.
- `visual_learning_report.html`.
- `visual_learning_report.json`.
- `learned_reference.png`.
- `learned_tolerance_map.png`.
- `before_after_false_call_report.html`.
- `before_after_results.csv`.
- `threshold_sweep.csv`.
- `inspection_results.csv`.
- `annotated_overlays/`.
- `heatmaps/`.
- `example_images/`.
- `package_manifest.json`.

Open `visual_learning_report.html` first. It is written for a non-software reader and summarizes image groups, learned reference, tolerance map, false-call behavior, anomaly examples, possible-escape status, recommended threshold, and evidence limits.

## What The Client Should See

The report should clearly show:

- The program learned normal PCB appearance.
- The program learned normal variation from OK samples.
- The program ignored harmless lighting or position variation where calibration supports it.
- The program flagged unusual regions on inspection examples.
- False calls before learning and after learning.
- OK Validation image count used for false-call metrics.
- Whether NG Validation images were used for possible-escape evidence.
- A boundary statement that Stage 2 live camera validation remains separate.

If no NG Validation images are present, the report must say that missed-defect rate cannot yet be fully proven.

## What Not To Claim

Do not use the demo package to claim:

- Live camera validation.
- Robot, lighting, 3D, MES, or safety readiness.
- Customer acceptance when synthetic or internal demo images were used.
- Production model certification.
- Absolute defect detection.
- Absolute absence of false calls.

Claims about false-call reduction must include the OK Validation image count. Claims about defect detection must say whether NG Validation images and possible-escape evidence were used.

## Sending Evidence To A Client

The client does not need to run automated tests. For a review packet, send:

- `visual_learning_report.html`.
- `before_after_false_call_report.html`.
- Representative files from `annotated_overlays/` and `heatmaps/`.
- `learned_reference.png` and `learned_tolerance_map.png`.
- The README and package manifest.

Keep customer images and generated image payloads outside git. Share them only through the approved client data channel.
