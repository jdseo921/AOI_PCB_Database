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

1. Repository hygiene check.
2. `dotnet restore AOI_PCB_Database.slnx`.
3. `dotnet build AOI_PCB_Database.slnx --configuration Release --no-restore`.
4. `dotnet test AOI_PCB_Database.slnx --configuration Release --no-build`.
5. Runtime restore for `AOI_Monitor/AOI_Monitor.csproj --runtime win-x64`.
6. `Scripts/publish.ps1 -ValidationOnly -NoRestore`.
7. Test log upload on failure.
8. Package artifact upload for successful `main` branch pushes.
