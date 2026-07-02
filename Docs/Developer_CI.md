# Developer CI and Release Checks

This project uses the same local commands in CI that maintainers should run before sharing changes.

## Local Build and Test

From the repository root:

```powershell
dotnet restore AOI_PCB_Database.slnx
dotnet build AOI_PCB_Database.slnx --configuration Release --no-restore
dotnet test AOI_PCB_Database.slnx --configuration Release --no-build
```

For a quick application-only build:

```powershell
dotnet build AOI_Monitor/AOI_Monitor.csproj
```

## Repository Hygiene

Runtime and customer data must stay out of git. Before committing, run:

```powershell
pwsh Scripts/check-repo-hygiene.ps1
```

The script fails when tracked or staged files include build output, SQLite databases, image vaults, generated exports, release packages, local settings, customer dataset folders, SampleData image/archive payloads, or large tracked/staged image files.

Do not commit:

- Customer, production, or large demo images.
- Local SQLite databases, WAL, or SHM sidecars.
- `image_vault`, training-set, export, package, or MES payload folders.
- Local settings such as `inspection_model_config.json`, `camera_source_settings.json`, or `storage_root_settings.json`.
- `bin`, `obj`, `.vs`, or generated `Release` output.

Small non-confidential instructions can live in `SampleData/`, but image payloads should be kept outside the repository and imported locally.

## Milestone Evidence CLI Smoke

CI builds `AOI_Monitor.Tools` and exercises the milestone evidence commands with generated tiny PNG files only. The smoke path is intentionally simulation-only:

```powershell
dotnet build AOI_Monitor.Tools/AOI_Monitor.Tools.csproj --configuration Release --no-restore

$env:AOI_MONITOR_STORAGE_ROOT = "TestResults/simulation-dry-run/runtime-storage-not-acceptance"

dotnet run --project AOI_Monitor.Tools/AOI_Monitor.Tools.csproj --configuration Release --no-build -- `
  stage1-exit `
  --dataset TestResults/simulation-dry-run/stage1-sample-dataset `
  --manifest TestResults/simulation-dry-run/stage1-sample-dataset/customer_validation_manifest.csv `
  --output TestResults/simulation-dry-run/stage1-evidence-package `
  --operator ci-simulation-dry-run `
  --allow-simulation

dotnet run --project AOI_Monitor.Tools/AOI_Monitor.Tools.csproj --configuration Release --no-build -- `
  stage2-camera-pilot `
  --output TestResults/simulation-dry-run/stage2-camera-pilot-package `
  --operator ci-simulation-dry-run `
  --allow-simulation
```

The CI dataset is generated during the workflow from tiny synthetic PNG bytes and a synthetic manifest. It is not customer data, does not exercise a production ONNX model acceptance gate, does not load a vendor camera SDK, and does not validate real lighting synchronization or real 3D acquisition hardware.

`AOI_MONITOR_STORAGE_ROOT` is set only for the simulation smoke so the CLI writes its SQLite/runtime state to an isolated CI folder. Do not upload that runtime storage as acceptance evidence; only the exported dry-run packages are published.

CI uploads these milestone artifacts:

- `stage1-simulation-dry-run-evidence-package`
- `stage2-camera-pilot-simulation-dry-run-evidence-package`

The `simulation-dry-run` wording is part of the artifact name by design. These packages prove that evidence-package generation, export verification, and Stage 2 aggregation plumbing are still executable in CI. They must not be interpreted as customer dataset acceptance, production model readiness, real camera readiness, lighting readiness, 3D readiness, or factory acceptance.

## Image-Only Learning CI Smoke

After the authoritative quality gates pass, CI runs a synthetic image-only learning smoke through `AOI_Monitor.Tools`:

```powershell
dotnet run --project AOI_Monitor.Tools/AOI_Monitor.Tools.csproj --configuration Release --no-build -- `
  client-image-learning-demo `
  --synthetic `
  --output TestResults/image-learning-demo `
  --operator ci-image-learning `
  --false-call-target 0.05
```

The command generates a synthetic image-only learning project under `TestResults`, imports image groups, trains Learned PCB Visual Model v1, calibrates false calls, runs inspection, exports heatmaps/overlays, and writes `visual_learning_report.html`.

CI uploads:

- `image-learning-demo-report-synthetic-not-customer-acceptance`.
- `image-learning-demo-overlays-synthetic-not-customer-acceptance`.

Each uploaded image-learning artifact includes a README stating that the evidence is synthetic only, not customer acceptance, and not production model certification. The smoke proves that the workflow executes; the existing quality gates remain the authoritative CI health check.

## Publish Package

Create a local Windows x64 PoC package:

```powershell
pwsh Scripts/publish.ps1
```

Create a self-contained package:

```powershell
pwsh Scripts/publish.ps1 -SelfContained
```

Validate package creation without writing to the repository `Release/` folder:

```powershell
pwsh Scripts/publish.ps1 -ValidationOnly
```

CI uses `-ValidationOnly -NoRestore` with an explicit temp output folder, then uploads the generated package artifact only for successful `main` branch pushes. When using `-NoRestore` locally, restore the app runtime assets first:

```powershell
dotnet restore AOI_Monitor/AOI_Monitor.csproj --runtime win-x64
pwsh Scripts/publish.ps1 -ValidationOnly -NoRestore
```

## GitHub Actions

The workflow in `.github/workflows/dotnet-ci.yml` runs on `push` and `pull_request` using `windows-latest` and .NET SDK `10.0.x`.

The job runs:

1. `dotnet restore AOI_PCB_Database.slnx`.
2. `dotnet build AOI_Monitor.Tools/AOI_Monitor.Tools.csproj --configuration Release --no-restore`.
3. `Scripts/check-pr-quality.ps1`.
4. `Scripts/check-code-quality.ps1`.
5. `Scripts/run-quality-gates.ps1 -Configuration Release -ResultsDirectory TestResults`.
6. Generated tiny-image Stage 1 dataset and manifest creation under `TestResults/simulation-dry-run`.
7. `AOI_Monitor.Tools stage1-exit ... --allow-simulation`.
8. `AOI_Monitor.Tools stage2-camera-pilot ... --allow-simulation`.
9. Synthetic image-only learning smoke with `client-image-learning-demo --synthetic`.
10. Test/result/audit artifact uploads.
11. Simulation dry-run and image-learning evidence uploads with synthetic/non-acceptance wording in the artifact names.
12. Package artifact upload for successful `main` branch pushes.

Keep the quality-gate step as the authoritative CI health check. The simulation dry-run milestone packages are supporting workflow evidence only, not a substitute for customer data, model acceptance, or real hardware acceptance.
