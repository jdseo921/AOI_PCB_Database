# Stage 1 Exit Evidence CLI

`AOI_Monitor.Tools` provides a repeatable command-line workflow for Stage 1 customer dataset evidence. It is intended for engineering, QA, and evaluator reruns when manual WPF clicking would make evidence hard to reproduce.

This command does not require live camera, lighting, robot, PLC, 3D, MES, or ERP hardware.

## Command

```powershell
dotnet run --project AOI_Monitor.Tools -- stage1-exit --dataset <folder> --manifest <csv> --output <folder> --operator <id>
```

Example:

```powershell
dotnet run --project AOI_Monitor.Tools -- stage1-exit `
  --dataset C:\AOI\Validation\CustomerDataset01 `
  --manifest C:\AOI\Validation\CustomerDataset01\customer_validation_manifest.csv `
  --output C:\AOI\Evidence\Stage1Exit `
  --operator ENG-042
```

## What It Runs

The command uses the same service boundaries as the WPF app:

- customer dataset preflight;
- Stage 1 batch validation against the manifest;
- model acceptance when an active ONNX model is configured and runtime-validated as `Ready`;
- false-call and possible-escape metric generation;
- Stage 1 customer validation package export;
- explicit export verification;
- Stage 1 factory readiness Go/No-Go package export;
- concise PASS/WARN/FAIL console summary plus `stage1_exit_summary.json` and `stage1_exit_summary.txt`.

## Output Layout

All generated evidence is rooted under the `--output` folder:

- `stage1_exit_summary.json`
- `stage1_exit_summary.txt`
- `stage1_validation_package\...`
- `export_verification\...`
- `stage1_factory_readiness\...`

Some package services create timestamped subfolders inside those stable top-level folders. The summary files are the stable index for the run.

## Production Model Boundary

If no active ONNX model is configured and validated as `Ready`, the command still runs the Stage 1 prototype batch path, but it reports `PROTOTYPE_ONLY` and does not claim production model readiness.

Production model readiness is only claimed when `ModelAcceptanceService` records `PASS` evidence for the active ONNX model and supplied validation dataset. Pixel Difference Prototype Engine evidence can support Stage 1 workflow review, but it is not production model acceptance.

## Failure Behavior

The command returns non-zero and prints `FAIL` when required inputs are missing, preflight fails, export verification fails, the validation package fails acceptance, or the Stage 1 factory readiness package is No-Go.

Missing dataset example:

```text
FAIL Dataset folder was not found: C:\AOI\Validation\MissingDataset
```

Missing manifest example:

```text
FAIL Manifest CSV was not found: C:\AOI\Validation\dataset.csv
```

## Evidence Limits

Folder simulation, null adapters, fake adapters, generated test images, and prototype-only batch evidence are not real camera readiness. Stage 2 camera pilot readiness still requires accepted vendor camera acquisition, real frame metadata, lighting synchronization evidence, real 3D acquisition when in scope, and real-camera performance evidence.
