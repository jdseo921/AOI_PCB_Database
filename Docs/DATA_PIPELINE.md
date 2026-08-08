OpenAI/Codex and numerous other coding agents will review your output once you are done.

# AOI Monitor Data Pipeline

End-to-end data flow of AOI Monitor — image intake through inspection, storage, export, and central sync — read before changing the pipeline, schema, image vault, or exports. Stage 1 boundary: this build has no live camera, lighting, robot, or MES connection; every image enters as a file, and simulated/mock/demo paths are visibly labeled.

## Overview

1. **Image intake** — operator imports (Add Images / Open Folder), project folders, or CLI import copy images into the vault; no live camera acquisition in Stage 1.
2. **Registration / alignment** — aligned against the reference; offsets recorded in `alignment_summary.csv`.
3. **Inspection engines** — one of three engines produces detections or anomaly regions.
4. **Results and disposition** — verdicts, anomaly regions, review events, and calibration metrics persisted.
5. **Storage** — SQLite versioned via `SchemaInfo`, plus the image vault under the configured storage root.
6. **Export and central sync** — verified evidence packages; optional central payloads. Local SQLite stays the offline source of truth.

## Image intake

Supported image formats are PNG, JPG, and JPEG. Unsupported or unreadable files are skipped with warnings. Imported files are copied to the managed image learning vault and hashed for duplicate detection. Source customer folders are not deleted or modified by archive/delete project metadata actions.

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

`image_truth.csv` is optional and image-level only:

```text
image,truth,notes
ok_validation/board_001.png,OK,good validation sample
ng_validation/bridge_001.png,NG,known-bad validation sample
inspection/sample_001.png,UNKNOWN,inspection sample
```

Truth values are `OK`, `NG`, or `UNKNOWN`. This file is for metrics and reporting only; it is not used as per-defect training labels.

Synthetic demo projects (demo evidence only): `pwsh SampleData/generate_image_learning_demo_project.ps1 -OutputRoot <folder>`.

## Inspection engines

Engine choices (active inspection source, set in Settings or AI Training Setup):

- **Pixel Difference Prototype Engine** — Stage 1 statistical prototype (workflow review, not production model acceptance).
- **ONNX ML Model** — accepts anomaly heat-map models (anomalib PatchCore/PaDiM/FastFlow exports) in addition to detection-row models; `AnomalyHeatmapOutputParser` converts map regions into defect detections automatically.
- **Learned PCB Visual Model** — the image-only learned model described below.

When Learned PCB Visual Model is active, Run Inspection uses the learned tolerance map and recommended threshold, Golden Compare shows learned reference/tolerance/anomaly views, and AI Model Test includes false-call and possible-escape metrics where validation data exists.

## Image-only PCB learning workflow

This workflow lets an Engineer or Admin train a learned PCB visual model from image groups only. It does not require manual defect classes, bounding boxes, per-defect variables, model files, or camera hardware. The program learns what a good PCB normally looks like from Golden / Reference and OK Learning images, uses OK Validation images to calibrate false calls, and uses Inspection images to show anomaly regions for review.

Evidence boundary:

- Image-only Stage 1 learning is software workflow evidence.
- It is not live camera validation.
- It is not robot, lighting, 3D, MES, safety, or full factory automation evidence.
- Formal acceptance requires customer/evaluator images and reviewer signoff.
- Synthetic or internal demo data must be labeled as demo evidence and must not be treated as customer acceptance.

### Image groups

| Image group | Required for learning | Purpose |
| --- | --- | --- |
| Golden / Reference | Yes, unless at least five OK Learning images exist | Best-known reference boards. |
| OK Learning | Yes, unless at least one Golden / Reference image exists | Good board images used to learn normal appearance and harmless variation. |
| OK Validation | Required for false-call calibration | Good board images used to measure and reduce false calls. |
| Inspection | Required for sample inspection/reporting | Images inspected after learning. |
| Optional NG Validation | Optional but recommended | Known-bad images used only to estimate possible escapes. |

Minimum training requirement: at least one Golden / Reference image or at least five OK Learning images. OK Validation images are required before false-call calibration can be reported.

### Operator workflow

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

### Learned outputs

Training produces visible artifacts:

- `learned_reference.png`: learned normal board appearance.
- `tolerance_map.png`: learned normal variation map.
- `anomaly_threshold_map.png`: learned anomaly threshold visualization.
- `learning_summary.json`: model metadata, counts, skipped image warnings, and evidence boundary.
- `alignment_summary.csv`: alignment offsets used while learning.
- `threshold_sweep.csv`: false-call and possible-escape threshold sweep.

Inspection produces anomaly regions with normalized rectangles, score, confidence, area, verdict, and reason. Overlay exports can show the original image, heatmap, annotated boxes, reference-vs-inspected image, and baseline-vs-learned comparison.

### False-call calibration

Calibration runs the learned model against OK Validation images and chooses a recommended threshold for the configured false-call target, default `0.05`.

Reports must include the OK Validation image count when making false-call reduction claims. If NG Validation images exist, threshold selection must not hide known-bad samples above the allowed possible-escape limit. If NG Validation images are not provided, the report must say that missed-defect rate cannot yet be fully proven.

### CLI packages

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

Synthetic output proves workflow capability only. It is not customer acceptance and not production model certification. Full CLI surface: `Docs/API_SPEC.md`.

## Training a real anomaly model for the ONNX slot

This is the upgrade path from the statistical prototype engines to a learned detector — trained from **OK images only**, same as the Stage 1 workflow, no defect labels needed.

### One-time environment (Windows, CPU-only)

```powershell
winget install astral-sh.uv
uv python install 3.11
mkdir C:\AOI\ml; cd C:\AOI\ml
uv venv --python 3.11 .venv
uv pip install --python .venv\Scripts\python.exe torch torchvision --index-url https://download.pytorch.org/whl/cpu
uv pip install --python .venv\Scripts\python.exe anomalib onnx onnxruntime pillow
```

### Train and export

Dataset layout is the same as `learn-from-images` (`ok_learning/`, `ok_validation/`, `ng_validation/`). For a synthetic dry run: `pwsh SampleData/generate_image_learning_demo_project.ps1 -OutputRoot C:\AOI\ml\dataset`.

```powershell
cd C:\AOI\ml
.venv\Scripts\python.exe <repo>\Scripts\ml\train_patchcore.py    # trains + exports ONNX
.venv\Scripts\python.exe <repo>\Scripts\ml\evaluate_onnx.py      # held-out false-call/escape table
```

Output: `C:\AOI\ml\export\weights\onnx\model.onnx` (~17 MB with resnet18 + 5% coreset). The evaluator mirrors the app's methodology: threshold calibrated on the even half of ok_validation, rates reported on the untouched odd half.

### Wire it into the app

Settings → AI → ONNX model configuration:

| Field | Value |
|---|---|
| Model path | `C:\AOI\ml\export\weights\onnx\model.onnx` |
| Input tensor | `input` |
| Output tensor | `anomaly_map` |
| Input width / height | 256 / 256 |
| Confidence threshold | 0.5 (anomalib exports embed normalization; 0.5 = learned threshold) |

Run the Settings readiness test — it should report *"output shape […] (parsed as anomaly heat map)"*. Formal adoption still goes through the model-acceptance gate with a labeled validation set, same as any model.

### Benchmark (synthetic 640px demo dataset, identical data + methodology)

| Metric | Statistical learned engine | PatchCore ONNX |
|---|---|---|
| Held-out false calls | 1/15 (6.7%) | **0/15 (0%)** |
| Possible escapes | 0/20 | 0/20 |
| OK vs NG score margin | threshold-tuned | **0.40 normalized gap** |
| Test AUROC | — | 0.9999 |

Synthetic-data evidence proves the pipeline, not customer acceptance — rerun both on real customer images before claiming production accuracy.

## SQLite storage and image vault

### Schema version

The SQLite database is versioned through the `SchemaInfo` table.

- `SchemaInfo.Key = SchemaVersion`
- Current schema version: `30` (`AoiDatabaseMigrations.LatestVersion`). Migration `1` is the consolidated baseline ("Current AOI Monitor schema baseline and compatibility repairs."); migrations `2`–`30` layer feature persistence on top.
- Runtime database path: `%LOCALAPPDATA%\AOI_Monitor\aoi_monitor.sqlite` by default, or the configured storage root.

### Migration policy

Schema changes must be added to `AOI_Monitor/Data/AoiDatabaseMigrations.cs` as ordered migrations. Each migration has a version, description, and transactional `Apply` step.

- Migrations must be idempotent. Re-running startup against the same database must be safe.
- Additive changes should use compatibility helpers such as `TableExists`, `ColumnExists`, `IndexExists`, and `AddColumnIfMissing`.
- Existing customer data must not be deleted or destructively rewritten during normal startup.
- The schema version is updated only after the migration transaction succeeds.
- New databases are created at the latest schema version.
- Unversioned existing databases are treated as version `0` and upgraded through every pending migration to the latest version.

### Tables

- `SchemaInfo` - key/value metadata for schema versioning.
- `Images` - imported image vault records and source metadata.
- `InspectionResults` - persisted inspection decisions and timing evidence.
- `Defects` - defect rows associated with inspections or images.
- `ReviewEvents` - operator review and disposition events.
- `AuditEvents` - traceable user and system actions.
- `RecipeRevisions` - saved recipe definitions and revision metadata.
- `TrainingSamples` - local training/evaluation sample references.
- `ImageLearningProjects` - image-only PCB learning project metadata and archive state.
- `ImageLearningProjectImages` - imported project images grouped by Golden / Reference, OK Learning, OK Validation, Inspection, or optional NG Validation role.
- `LearnedPcbVisualModels` - learned image-only visual model metadata, thresholds, calibration rates, evidence mode, and project counts.
- `LearnedPcbVisualModelArtifacts` - runtime artifact paths for learned reference, tolerance map, anomaly threshold map, learning summary, alignment summary, and threshold sweep outputs.
- `ImageLearningInspectionResults` - image-only inspection decisions produced after learning.
- `ImageLearningAnomalyRegions` - anomaly regions associated with image-only inspection results, including normalized rectangles, score, area, confidence, and reason; these are not required defect-class training labels.
- `ImageLearningCalibrationResults` - OK/NG validation calibration summaries for false-call and possible-escape estimates.
- `ImageLearningComparisonResults` - learned-model comparison summaries for inspected images.
- `CalibrationProfiles` - calibration profile summary records.
- `CalibrationPoints` - calibration point mappings for a profile.
- `BatchTestRuns` - AI model/batch validation run summaries.
- `BatchTestResults` - per-image validation results.
- `ModelRegistry` - locally registered ONNX model metadata and active selection state.
- `ExportHistory` - CSV/report/package export audit records.
- `ExportVerification` - export artifact verification status, SHA-256 checksums, and messages.
- `ValidationPackages` - generated customer validation package records.
- `MesUploadAttempts` - MES/mock/REST upload attempt audit trail.
- `MesSpoolQueue` - offline MES REST retry queue.
- `LogArchive` - recoverable archive for purged log rows; stores the full source-row payload as JSON (`PayloadJson`) so archived records stay queryable and restorable.
- `DefectTaxonomies`, `DefectTaxonomyEntries`, `DefectClassAliases`, `MesDefectCodeMappings` - governable defect taxonomy versions, entries, class aliases, and MES defect code mappings.
- `ThresholdProfiles`, `ThresholdProfileRules`, `ThresholdProfileDeployments` - versioned threshold profiles and deployment markers.
- `FalseCallReductionRuns`, `FalseCallReductionPoints` - false-call reduction recommendation persistence.
- `ValidationBreakdownMetrics` - validation breakdown evidence by class, side, and ROI.
- `CameraAcceptanceRuns`, `CameraAcceptanceFrames` - Stage 2 camera acceptance test persistence.
- `LightingAcceptanceRuns`, `LightingAcceptanceSteps` - lighting synchronization acceptance persistence.
- `RobotAcceptanceRuns`, `RobotAcceptanceSteps` - robot cell acceptance persistence, including PLC/safety evidence columns.
- `Profile3DAcceptanceRuns` - 3D profile acceptance evidence persistence.
- `TraceabilityTestReports` - MES traceability test signoff evidence.
- `SoakTestRuns`, `SoakTestIterations` - soak-test stability evidence, including factory-acceptance cycle metrics.
- `BuildTestEvidence` - local build and test evidence persistence.
- `ModelAcceptanceRuns`, `ModelAcceptanceMetrics`, `ModelReleasePackages` - model acceptance runs, normalized metrics, and release package evidence.
- `InspectionLatencyTraces` - end-to-end inspection latency traces and verdicts.
- `CentralSyncQueue`, `CentralSyncAttempts` - optional central data synchronization queue persistence.
- `LocalUsers`, `LocalUserSessions` - local authenticated users and session audit records.
- `CustomerPilotSessions`, `CustomerPilotSteps` - guided customer pilot wizard session persistence.
- `PilotIssues`, `PilotIssueEvents` - pilot issue tracking persistence.

The authoritative table inventory is the code (`AOI_Monitor/Data/AoiDatabase*.cs`): 60 tables including `SchemaInfo` as of schema version `30`.

### Indexes

Indexes are created with `CREATE INDEX IF NOT EXISTS` during initialization. They are part of the baseline schema and are safe to re-run. New index additions should either be included in a migration or added to the baseline after the migration that guarantees required columns exists.

### Data growth and retention boundary

Startup log retention (Settings > Data Retention) archives and purges only the four log tables: `InspectionResults`, `ExportHistory`, `ReviewEvents`, and `AuditEvents`. The alarm snapshot (`exports/alarm_events/alarm_events_state.json`) additionally drops resolved alarms older than 90 days at load and auto-resolves non-critical active alarms older than 14 days at startup.

Everything else grows without automatic pruning and is a known Stage-2 scalability boundary:

- `image_vault/` binaries (every imported image is copied into the vault) and `image_vault/training/`
- `Images`, `TrainingSamples`, `ImageLearningProjects`/`ImageLearningProjectImages` rows and their learned artifacts
- `exports/` package folders

For Stage 1 volumes (thousands of images) this is acceptable on workstation disks. Before pilot-line volumes (Stage 2+), plan vault retention: orphan-file sweep against the `Images` table, per-project artifact cleanup on project delete, and an export-folder age policy.

## Export and central sync

AOI Monitor keeps local SQLite as the offline source of truth. Central sync is optional and creates reporting payloads for management aggregation across stations.

Modes:

- Disabled: no central aggregation is attempted.
- FileDrop: writes JSON payloads to a configured folder.
- RestApi: boundary only in this build; no production client is installed.
- PostgreSqlBoundary: interface boundary only; no Npgsql package or production database writer is bundled.

Local-to-central mapping:

| Local SQLite source | Central payload type | Central reporting target |
| --- | --- | --- |
| InspectionResults | InspectionResult | station inspection results, model/version evidence, verdict, confidence, timing |
| Defects | Included with future InspectionResult detail expansion | defect detail / ROI evidence |
| ReviewEvents | ReviewEvent | review and disposition audit trail |
| ValidationPackages | ValidationPackage | customer validation package status and manifest references |
| ExportVerification | ExportVerification | package/export integrity evidence |
| Camera/Lighting/Robot/Profile3D/Soak/MES acceptance tables | Future acceptance report payloads | factory readiness evidence by station |

Raw customer images are not uploaded by default. Image and package paths can be redacted, and image references are included only when central sync settings explicitly allow image inclusion. Secrets and endpoints are redacted from exported queue reports when configured.

Central sync failures leave queue items pending for retry. Failed central connectivity must not remove or modify local SQLite records.

## Data handling and repository hygiene

Do not commit customer images, runtime SQLite databases, export packages, MES payloads, model files, learned models, image vault contents, overlays, reports, or demo output folders to git. `TestResults/`, generated `SampleData` image payloads, image vaults, exports, and runtime SQLite files are ignored by git. They may contain customer or process-sensitive data.

## Related documents

- `Docs/API_SPEC.md` — CLI reference, integration boundaries, export formats.
- `Docs/ARCHITECTURE.md` — layer and service boundaries.
- `Docs/VALIDATION.md` — evidence gates and model acceptance.
- Docs/standard VOL05 §21/§37 (data, storage, export), VOL04 §19 (AI model lifecycle), VOL09 §31 (AI/ML quality).
- `SampleData/README.md` — demo dataset generation.
