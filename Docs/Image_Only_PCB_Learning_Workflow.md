# Image-Only PCB Learning Workflow

This workflow lets an Engineer or Admin train a learned PCB visual model from image groups only. It does not require manual defect classes, bounding boxes, per-defect variables, model files, or camera hardware.

The program learns what a good PCB normally looks like from Golden / Reference and OK Learning images. It then uses OK Validation images to calibrate false calls and uses Inspection images to show anomaly regions for review.

## Evidence Boundary

- Image-only Stage 1 learning is software workflow evidence.
- It is not live camera validation.
- It is not robot, lighting, 3D, MES, safety, or full factory automation evidence.
- Formal acceptance requires customer/evaluator images and reviewer signoff.
- Synthetic or internal demo data must be labeled as demo evidence and must not be treated as customer acceptance.

## Image Groups

| Image group | Required for learning | Purpose |
| --- | --- | --- |
| Golden / Reference | Yes, unless at least five OK Learning images exist | Best-known reference boards. |
| OK Learning | Yes, unless at least one Golden / Reference image exists | Good board images used to learn normal appearance and harmless variation. |
| OK Validation | Required for false-call calibration | Good board images used to measure and reduce false calls. |
| Inspection | Required for sample inspection/reporting | Images inspected after learning. |
| Optional NG Validation | Optional but recommended | Known-bad images used only to estimate possible escapes. |

Minimum training requirement: at least one Golden / Reference image or at least five OK Learning images. OK Validation images are required before false-call calibration can be reported.

## Operator Workflow

Use `AI / Models > AI Training Setup` for the guided GUI workflow.

1. Create a training project.
2. Add Golden / Reference images.
3. Add OK Learning images.
4. Add OK Validation images.
5. Add Inspection images.
6. Optionally add known NG Validation images.
7. Learn normal PCB appearance.
8. Calibrate false calls.
9. Inspect samples.
10. Export the client visual learning report.

Each role card shows a short explanation, image count, Add Images, Open Folder, and Preview actions. Operators can view the workflow. Engineer and Admin roles can import images, run learning, calibrate, inspect, export evidence, and set the learned visual model as the active inspection source.

## Folder Convention

The CLI and folder-import service accept this layout:

```text
project_folder/
  golden/
  ok_learning/
  ok_validation/
  inspection/
  ng_validation/ optional
  image_truth.csv optional
```

Supported image formats are PNG, JPG, and JPEG. Unsupported or unreadable files are skipped with warnings. Imported files are copied to the managed image learning vault and hashed for duplicate detection. Source customer folders are not deleted or modified by archive/delete project metadata actions.

`image_truth.csv` is optional and image-level only:

```text
image,truth,notes
ok_validation/board_001.png,OK,good validation sample
ng_validation/bridge_001.png,NG,known-bad validation sample
inspection/sample_001.png,UNKNOWN,inspection sample
```

Truth values are `OK`, `NG`, or `UNKNOWN`. This file is for metrics and reporting only; it is not used as per-defect training labels.

## Learned Outputs

Training produces visible artifacts:

- `learned_reference.png`: learned normal board appearance.
- `tolerance_map.png`: learned normal variation map.
- `anomaly_threshold_map.png`: learned anomaly threshold visualization.
- `learning_summary.json`: model metadata, counts, skipped image warnings, and evidence boundary.
- `alignment_summary.csv`: alignment offsets used while learning.
- `threshold_sweep.csv`: false-call and possible-escape threshold sweep.

Inspection produces anomaly regions with normalized rectangles, score, confidence, area, verdict, and reason. Overlay exports can show the original image, heatmap, annotated boxes, reference-vs-inspected image, and baseline-vs-learned comparison.

## False-Call Calibration

Calibration runs the learned model against OK Validation images and chooses a recommended threshold for the configured false-call target, default `0.05`.

Reports must include the OK Validation image count when making false-call reduction claims. If NG Validation images exist, threshold selection must not hide known-bad samples above the allowed possible-escape limit. If NG Validation images are not provided, the report must say that missed-defect rate cannot yet be fully proven.

## Active Inspection Source

Settings and AI Training Setup can set a learned visual model as the active inspection source. The normal inspection engine choices are:

- Pixel Difference Prototype Engine.
- ONNX ML Model.
- Learned PCB Visual Model.

When Learned PCB Visual Model is active, Run Inspection uses the learned tolerance map and recommended threshold, Golden Compare shows learned reference/tolerance/anomaly views, and AI Model Test includes false-call and possible-escape metrics where validation data exists.

## CLI Commands

Create a client evidence package from customer image folders:

```powershell
dotnet run --project AOI_Monitor.Tools -- learn-from-images `
  --project-folder C:\AOI\customer_image_project `
  --output C:\AOI\learning_output `
  --operator engineer01 `
  --false-call-target 0.05 `
  --board-model CUSTOMER-PCB
```

Create a synthetic internal demo package:

```powershell
dotnet run --project AOI_Monitor.Tools -- client-image-learning-demo `
  --synthetic `
  --output TestResults/image-learning-demo `
  --operator ci-image-learning `
  --false-call-target 0.05
```

Synthetic output proves workflow capability only. It is not customer acceptance and not production model certification.

## Repository Hygiene

Do not commit generated images, customer images, learned models, image vault contents, overlays, reports, or demo output folders. `TestResults/`, generated `SampleData` image payloads, image vaults, exports, and runtime SQLite files are ignored by git.
