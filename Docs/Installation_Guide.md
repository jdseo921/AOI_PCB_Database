# AOI Monitor Installation Guide

This guide describes how to install, build, run, and troubleshoot the current AOI Monitor proof of concept. It is intended for client evaluation and factory-demo preparation.

## Scope

AOI Monitor is a local Windows WPF desktop application for PCBA AOI review workflows. The current build uses local files, a local SQLite database, a managed image vault, a folder-based camera simulator, and a prototype inspection engine. It does not install real camera SDKs, robot/PLC drivers, or MES/ERP connectors.

## Requirements

- Windows 10 or Windows 11.
- A .NET SDK or runtime that supports Windows desktop/WPF for the project target framework.
- The project currently targets `net10.0-windows`.
- Local filesystem access for the app data folder, image vault, and export folders.

For development and evaluation, install the .NET SDK, not only the runtime. Confirm installation with:

```powershell
dotnet --info
```

## Build From Source

Open PowerShell at the repository root:

```powershell
dotnet build AOI_PCB_Database.slnx
```

Expected result:

- `AOI_Monitor` builds successfully.
- `AOI_Monitor.Tests` builds successfully.
- No customer images or production databases are required.

## Run The Application

From the repository root:

```powershell
dotnet run --project AOI_Monitor\AOI_Monitor.csproj
```

You can also run from the app folder:

```powershell
cd AOI_Monitor
dotnet run
```

If the application was already built, the debug executable is normally located at:

```text
AOI_Monitor\bin\Debug\net10.0-windows\AOI_Monitor.exe
```

## Run Tests

From the repository root:

```powershell
dotnet test AOI_PCB_Database.slnx
```

The test project uses isolated temporary folders and generated tiny images. It does not write into the real `%LOCALAPPDATA%\AOI_Monitor` runtime folder.

## Local Data Paths

By default, the application creates local PoC data under:

```text
%LOCALAPPDATA%\AOI_Monitor\
```

The default SQLite database path is:

```text
%LOCALAPPDATA%\AOI_Monitor\aoi_monitor.sqlite
```

The managed image vault path is:

```text
%LOCALAPPDATA%\AOI_Monitor\image_vault\
```

Training candidate images are copied under:

```text
%LOCALAPPDATA%\AOI_Monitor\image_vault\training\
```

When launched from the Debug build, local export files are commonly written under:

```text
AOI_Monitor\bin\Debug\net10.0-windows\exports\
```

Customer validation packages are written to the output folder chosen by the user in `Log & Export`.

Admin users can change selected local paths in Settings. In this PoC, those settings are local only and are not synchronized with MES or a central configuration server.

## First Launch Checklist

1. Start the application.
2. Confirm the readiness panel shows local database and image vault availability.
3. Confirm Inspection Engine status is clearly marked as either Prototype Engine or an ML configuration status.
4. Confirm Camera status is either Simulated, Not Connected, or Error. The UI should not imply real camera hardware is connected.
5. Select a local user and role from the shell.
6. Use small non-confidential PNG/JPG/JPEG images for evaluation.

## Optional Sample Camera Setup

The Stage 2 camera hardware connection is not implemented. For demo use, configure folder simulation:

1. Open `Settings / Guide`.
2. Use an Admin role.
3. Set Camera Source to `Folder Simulation`.
4. Select folders for Top, Side, and Bottom views.
5. Save settings.
6. Return to `Main Inspection` and use Start, Stop, Next Board, and the view selector.

## Troubleshooting

### Build fails with WPF or Windows targeting errors

- Build on Windows.
- Confirm the installed .NET SDK supports Windows desktop/WPF.
- Run `dotnet --info` and verify the expected SDK is available.

### App starts but database is unavailable

- Confirm the user account can write to `%LOCALAPPDATA%`.
- If a custom storage root was selected, confirm that folder exists and is writable.
- Use `Log & Export > DB Integrity` to generate a local health report.

### Imported images do not appear

- Use PNG, JPG, or JPEG for Image Library imports.
- Confirm the file is not locked by another process.
- Check `%LOCALAPPDATA%\AOI_Monitor\image_vault\`.
- Duplicate files are detected by hash and are not imported again.

### Batch validation skips files

- Stage 1 validation accepts PNG/JPG/JPEG images.
- Unsupported or unreadable files are logged and skipped.
- Invalid CSV manifests produce warnings instead of crashing the app.

### Export fails

- Confirm the export folder is writable.
- Close any open CSV, HTML, Markdown, or image files that may be locked by another program.
- Try exporting to a simple local folder such as `C:\Temp\AOI_Exports`.

### Camera shows Not Connected or Error

- This is expected unless folder simulation is configured.
- Real GigE/USB3 camera SDK integration is planned for Stage 2.

### MES/ERP shows Not Connected

- This is expected in the current PoC.
- MES authentication and production traceability are planned for Stage 4.

