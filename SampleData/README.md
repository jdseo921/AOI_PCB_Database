OpenAI/Codex and numerous other coding agents will review your output once you are done.

# Sample Data

This folder contains non-confidential Stage 1 validation templates and a local demo dataset generator. Do not commit customer images, private production data, or large generated datasets.

## Quick Generated Demo Dataset

Run this from the repository root:

```powershell
pwsh SampleData/demo_dataset_generator.ps1
```

The script creates `SampleData/DemoSet_Quick/` with:

- `images/` sample PNGs with OK and NG labels.
- `golden/` reference PNGs for the Pixel Difference Prototype Engine.
- `customer_validation_manifest.csv` with the required customer validation schema.
- `folder_camera/top`, `folder_camera/side`, and `folder_camera/bottom` folders for Folder Camera Simulation.

The default generated set is intentionally acceptance-sized for the current demo preflight gates: 20+ OK images, 20+ NG images, and at least three NG defect classes. It remains synthetic sample data and does not validate real camera, lighting, robot, PLC, MES, or production model readiness.

## Recommended Layout

```text
SampleData/
  DemoSet_Quick/
    images/
      tbox_lot07_0001_top_ok.png
      tbox_lot07_0025_top_solder_bridge.png
    golden/
      tbox_ref_top.png
      tbox_ref_bottom.png
      tbox_ref_side.png
    folder_camera/
      top/
      side/
      bottom/
    customer_validation_manifest.csv
  golden/
    golden_001.png
```

Guidelines:

- Use small PNG/JPG/JPEG files that are appropriate for sharing.
- Do not commit large image datasets to GitHub.
- Do not commit customer-confidential, production, or personally identifiable data.
- Keep large or private datasets outside the repository and import them locally through the app.
- The app copies imported images into `%LOCALAPPDATA%\AOI_Monitor\image_vault\`.
- Batch test folders can be selected from any local path; they do not need to live inside this repository.
- The generated demo set is safe to recreate or delete locally.

## Image-only Learning Demo Project

Run this from the repository root to create a folder-convention project for `AI / Models > AI Training Setup`, the `learn-from-images` command, and the `client-image-learning-demo` command:

```powershell
pwsh SampleData/generate_image_learning_demo_project.ps1 `
  -OutputRoot TestResults/image-learning-demo-project `
  -GoldenCount 3 `
  -OkLearningCount 40 `
  -OkValidationCount 30 `
  -InspectionCount 20 `
  -NgValidationCount 20 `
  -Seed 42
```

The generator creates PCB-like PNG images under `golden/`, `ok_learning/`, `ok_validation/`, `inspection/`, and `ng_validation/`. It does not create defect labels or bounding boxes for learning. `image_truth.csv` is image-level OK/NG/UNKNOWN truth for reporting metrics only.

To run the customer-image workflow against any folder with the same layout:

```powershell
dotnet run --project AOI_Monitor.Tools -- learn-from-images `
  --project-folder TestResults/image-learning-demo-project `
  --output TestResults/learn-from-images-output `
  --operator ci-demo `
  --false-call-target 0.05 `
  --board-model DEMO-PCB
```

The command imports images, learns normal appearance, calibrates on OK Validation images, inspects the `inspection/` group, uses optional `ng_validation/` images for possible-escape reporting, and writes a visual report plus overlays. It does not require defect labels, bounding boxes, per-defect variables, model files, or camera hardware.

Suggested workflow:

- `AI / Models`: select `DemoSet_Quick/images`, select `DemoSet_Quick/customer_validation_manifest.csv`, run preflight, run batch, and export CSV or validation package.
- `Export & Trace`: run `Performance Benchmark` against `DemoSet_Quick/images`.
- `Run Inspection`: use the three `folder_camera` subfolders as simulated camera inputs.

`customer_validation_manifest_template.csv` is the starting point for customer datasets. Keep large or private datasets outside the repository and import them locally through the app.
