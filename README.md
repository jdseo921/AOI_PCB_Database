# AOI Monitor

AOI Monitor is a Windows WPF desktop prototype for PCBA automated optical inspection review workflows. It gives operators a local console for station monitoring, defect disposition, golden-image comparison, image-library review, recipe visibility, SPC-style status, reports, settings, and workflow guidance.

The application currently demonstrates the review loop with local files and static prototype data. It can load a sample PCB image and a golden reference image, run a deterministic pixel-difference comparison, produce an `OK`, `REVIEW`, or `NG` verdict, record disposition actions, collect training candidates, and write local export artifacts. It is not yet connected to live AOI hardware, cameras, PLCs, robots, conveyors, a production database, or a trained ML inference pipeline.

For the detailed feature inventory, see [IMPLEMENTED_FEATURES.md](IMPLEMENTED_FEATURES.md).

## Project Layout

- `AOI_Monitor/` - WPF application source.
- `AOI_Monitor/AOI_Monitor.csproj` - .NET project file targeting `net10.0-windows`.
- `AOI_Monitor/Views/` - page-specific UI and workflow code.
- `AOI_Monitor/Services/` - shared workflow state, image analysis, and machine-interface exports.
- `AOI_Monitor/Models/` - AOI workflow and export contract models.
- `AOI_Monitor/bin/` and `AOI_Monitor/obj/` - local build outputs.

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

## Basic Workflow

1. Open the app and use the left navigation to move between AOI workflow pages.
2. In Image Library, choose a sample PCB image with Open Record.
3. Choose a golden reference image with Compare Golden.
4. Review the generated score, verdict, evidence, and hotspot on Golden Compare.
5. Use Disposition to confirm, mark false calls, hold for review, or send samples to the local training set.
6. Use Reports to create local audit, package, image-index, and integrity-check artifacts.

Generated files are written below the running application's `exports/` folder, usually under:

```text
AOI_Monitor/bin/Debug/net10.0-windows/exports/
```

## Troubleshooting

- If `dotnet run` fails with a missing framework or SDK message, install a .NET SDK that supports Windows desktop/WPF and the project's `net10.0-windows` target.
- If WPF build errors mention Windows targeting, run the project on Windows and build from the `AOI_Monitor` folder.
- If images do not compare, make sure both the sample image and golden reference image are readable local image files.
- If exports are missing, check the `exports/` folder under the executable directory, not necessarily the repository root.
- If recipe or detection-priority controls seem locked, use the recipe lock/unlock action in the application shell or Reports page.
- If generated reports look sparse, remember that station metrics, database rows, recipes, and many dashboard records are static prototype data.

## Current State

This is a functional local prototype focused on operator review flows and file-based evidence export. The main production gaps are live database integration, real machine/hardware integration, persistent workflow storage, and a production-grade inspection model.
