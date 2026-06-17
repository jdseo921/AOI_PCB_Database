# AOI Monitor

AOI Monitor is a Windows WPF desktop prototype for PCBA automated optical inspection review workflows. It gives operators a simplified local console organized around Main Inspection, Recipe Editor, AI Model Test, Log & Export, a 3D Profile Viewer in Sample Data Mode, and Settings / Guide.

The application currently demonstrates the review loop with local files, local SQLite records, and clearly labeled demo placeholders where production data sources are not yet implemented. It can load a sample PCB image and a golden reference image, run a deterministic pixel-difference comparison, produce an `OK`, `REVIEW`, or `NG` verdict, record disposition actions, collect candidate samples for future training review, and write local export artifacts. It is not yet connected to live AOI hardware, cameras, PLCs, robots, conveyors, a centralized production database, or a trained ML inference pipeline.

The main window includes an explicit readiness panel for Database, Image Vault, Inspection Engine, Camera, Robot, and MES/ERP. Camera hardware, lighting, live 3D profile acquisition, robot/handler integration, MES/ERP integration, and real ML model training are marked as planned Stage 2/3/4 work unless a future implementation backs them.

For the detailed feature inventory, see [IMPLEMENTED_FEATURES.md](IMPLEMENTED_FEATURES.md).

Client/evaluator documents:

- [Installation Guide](Docs/Installation_Guide.md)
- [User Manual](Docs/User_Manual.md)
- [Stage Mapping](Docs/Stage_Mapping.md)
- [Integration Boundaries](Docs/Integration_Boundaries.md)
- [Stage 1 Acceptance Checklist](Docs/Stage1_Acceptance_Checklist.md)

## Project Layout

- `AOI_Monitor/` - WPF application source.
- `AOI_Monitor/AOI_Monitor.csproj` - .NET project file targeting `net10.0-windows`.
- `AOI_Monitor/Views/` - page-specific UI and workflow code.
- `AOI_Monitor/Services/` - shared workflow state, image analysis, and machine-interface exports.
- `AOI_Monitor/Models/` - AOI workflow and export contract models.
- `AOI_Monitor/Data/` - local SQLite initialization and image-vault persistence.
- `Docs/` - installation guide, user manual, stage mapping, acceptance checklists, and implementation notes.
- `SampleData/` - instructions for placing small local demo images.
- `AOI_Monitor/bin/` and `AOI_Monitor/obj/` - local build outputs.

## Repository Hygiene

This repository should contain source code, XAML, docs, and small non-confidential instructions only. Do not commit:

- `bin/` or `obj/` build outputs.
- `.vs/` and generated `.user` IDE files.
- Local SQLite database files or WAL/SHM sidecars.
- Image vault folders or training-set export folders.
- Generated export packages, CSV files, overlays, or machine-interface JSON files.
- Customer images, production images, or large sample datasets.

Local runtime data is generated automatically when the app starts or when imports/exports are run. Place small demo-image instructions in `SampleData/`; keep large or private datasets outside the repository.

## Run

Requirements:

- Windows
- .NET SDK with Windows desktop/WPF support for the target framework used by the project

From the repository root:

```powershell
cd AOI_Monitor
dotnet run
```

To build without launching:

```powershell
cd AOI_Monitor
dotnet build
```

If a previous build already exists, you can also launch the debug executable directly:

```powershell
.\AOI_Monitor\bin\Debug\net10.0-windows\AOI_Monitor.exe
```

## Tests

The repository includes `AOI_Monitor.Tests/` for non-UI logic. The tests use temporary folders/databases and generated tiny image files, so they do not depend on committed customer images.

From the repository root:

```powershell
dotnet test AOI_PCB_Database.slnx
```

The test fixture configures `AoiDatabase` with an isolated temp folder per test run, so tests do not write into the real `%LOCALAPPDATA%\AOI_Monitor` image vault or SQLite database.

## Basic Workflow

1. Open Main Inspection from the left navigation.
2. Use its shortcuts to open Image Library, Disposition, or Golden Compare as needed.
3. In Image Library, choose a sample PCB image with Open Record.
4. Choose a golden reference image with Compare Golden.
5. Review the generated score, verdict, evidence, and hotspot on Golden Compare.
6. Use Disposition to confirm, mark false calls, hold for review, or queue local candidate samples.
7. Use Log & Export to inspect SQLite history, create CSV exports, build customer packages, and open database health.

Generated files are written below the running application's `exports/` folder, usually under:

```text
AOI_Monitor/bin/Debug/net10.0-windows/exports/
```

## Stage 1 Demo Workflow

Use [Docs/Stage1_Acceptance_Checklist.md](Docs/Stage1_Acceptance_Checklist.md) as the formal verification script. Use [Docs/User_Manual.md](Docs/User_Manual.md) for operating steps, [Docs/Installation_Guide.md](Docs/Installation_Guide.md) for setup, and [SampleData/README.md](SampleData/README.md) to prepare small, non-confidential demo images.

Short walkthrough:

1. Build the app with `dotnet build AOI_Monitor\AOI_Monitor.csproj`.
2. Launch the app and confirm the readiness panel shows local Database and Image Vault availability.
3. Open `Main Inspection > Image Library`, import a sample image, then select a golden/reference image with `Compare Golden`.
4. Review the pixel-difference prototype result and overlay in `Golden Compare`.
5. Open `Disposition` and record a review action or queue a local training-set export candidate.
6. Open `AI Model Test`, select a small batch folder, and run the Stage 1 batch validation.
7. Open `Log & Export`, export inspection/review CSV files, annotated overlays, and a customer validation package.

Do not commit large demo image datasets. Keep customer/private images outside the repository and import them locally.

## Local Database and Image Vault

On startup, the app creates its local PoC persistence store automatically. On Windows the default location is:

```text
%LOCALAPPDATA%\AOI_Monitor\
```

The SQLite database is stored at:

```text
%LOCALAPPDATA%\AOI_Monitor\aoi_monitor.sqlite
```

Imported PCB sample and golden images are copied into the managed image vault:

```text
%LOCALAPPDATA%\AOI_Monitor\image_vault\
```

Candidate samples for future/offline training review are copied below:

```text
%LOCALAPPDATA%\AOI_Monitor\image_vault\training\
```

The database is initialized with tables for images, inspection results, defects, review events, recipe revisions, training samples, and export history. Image records include original path, vault path, filename, board model, lot ID, view type, import time, and SHA-256 file hash. If the OS local-app-data folder cannot be resolved, the app falls back to a `data/` folder beside the executable.

## Troubleshooting

- If `dotnet run` fails with a missing framework or SDK message, install a .NET SDK that supports Windows desktop/WPF and the project's `net10.0-windows` target.
- If WPF build errors mention Windows targeting, run the project on Windows and build from the `AOI_Monitor` folder.
- If images do not compare, make sure both the sample image and golden reference image are readable local image files.
- If exports are missing, check the `exports/` folder under the executable directory, not necessarily the repository root.
- If recipe or detection-priority controls seem locked, use the recipe lock/unlock action in the application shell or Log & Export page.
- If generated reports look sparse, confirm images, inspections, and review events have been imported or saved into the local SQLite database. Remaining placeholder panels are labeled `Demo Data` or `Prototype Data`.
- The 3D Profile Viewer supports Sample Data Mode only. Live 3D camera integration is planned Stage 2 work.

## Current State

This is a functional local prototype focused on operator review flows, local SQLite-backed evidence, and file-based exports. The main production gaps are centralized production database integration, real machine/hardware integration, expanded persistent workflow storage, and a production-grade inspection model.
