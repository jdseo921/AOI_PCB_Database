param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$OutputRoot = "",
    [switch]$SelfContained,
    [switch]$ValidationOnly,
    [switch]$IncludeTemplates,
    [switch]$IncludeSampleManifestTemplate,
    [switch]$IncludeDocs,
    [switch]$NoRestore
)

$ErrorActionPreference = "Stop"

$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = Resolve-Path (Join-Path $scriptRoot "..")
$projectPath = Join-Path $repoRoot "AOI_Monitor\AOI_Monitor.csproj"
$solutionPath = Join-Path $repoRoot "AOI_PCB_Database.slnx"
$docsPath = Join-Path $repoRoot "Docs"
$templatesPath = Join-Path $repoRoot "Templates"
$sampleManifestTemplatePath = Join-Path $repoRoot "SampleData\customer_validation_manifest_template.csv"
$featureDocPath = Join-Path $repoRoot "IMPLEMENTED_FEATURES.md"
$rootReadmePath = Join-Path $repoRoot "README.md"
$releaseRoot = if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    if ($ValidationOnly) {
        Join-Path ([System.IO.Path]::GetTempPath()) "AOI_Monitor_PublishValidation"
    } else {
        Join-Path $repoRoot "Release"
    }
} else {
    $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputRoot)
}

$timestamp = Get-Date -Format "yyyyMMdd_HHmmss"
$releaseName = "AOI_Monitor_PoC_$timestamp"
$releaseDir = Join-Path $releaseRoot $releaseName
$publishDir = Join-Path $releaseDir "app"
$releaseDocsDir = Join-Path $releaseDir "Docs"
$releaseTemplatesDir = Join-Path $releaseDir "Templates"
$releaseSampleDataDir = Join-Path $releaseDir "SampleData"

function Invoke-Step {
    param(
        [string]$Title,
        [string[]]$Command
    )

    Write-Host $Title
    & $Command[0] @($Command[1..($Command.Length - 1)])
    if ($LASTEXITCODE -ne 0) {
        throw "Command failed with exit code ${LASTEXITCODE}: $($Command -join ' ')"
    }
}

Write-Host "AOI Monitor publish script"
Write-Host "Repository: $repoRoot"
Write-Host "Release:    $releaseDir"
Write-Host "Runtime:    $Runtime"
Write-Host "Mode:       $(if ($SelfContained) { 'self-contained' } else { 'framework-dependent' })"
Write-Host "Validation: $(if ($ValidationOnly) { 'yes' } else { 'no' })"
Write-Host "Docs:       $(if ($IncludeDocs -or !$ValidationOnly) { 'include' } else { 'skip for validation-only package' })"
Write-Host "Templates:  $(if ($IncludeTemplates) { 'include adapter templates' } else { 'skip' })"
Write-Host "Sample manifest template: $(if ($IncludeSampleManifestTemplate) { 'include' } else { 'skip' })"
Write-Host "Restore:    $(if ($NoRestore) { 'skip; use existing restore assets' } else { 'allow dotnet commands to restore as needed' })"

if (!(Test-Path $projectPath)) {
    throw "Project file was not found: $projectPath"
}

if (!(Test-Path $solutionPath)) {
    throw "Solution file was not found: $solutionPath"
}

New-Item -ItemType Directory -Force -Path $releaseRoot | Out-Null
Get-ChildItem -LiteralPath $releaseRoot -Directory -Filter "AOI_Monitor_PoC_*" -ErrorAction SilentlyContinue |
    Remove-Item -Recurse -Force

if (Test-Path $releaseDir) {
    Remove-Item -LiteralPath $releaseDir -Recurse -Force
}

New-Item -ItemType Directory -Force -Path $releaseDir | Out-Null
New-Item -ItemType Directory -Force -Path $publishDir | Out-Null

$restoreOption = if ($NoRestore) { @("--no-restore") } else { @() }

if ($NoRestore) {
    Write-Host "Skipping dotnet clean because -NoRestore is set; clean can remove assets needed for offline validation."
} else {
    Invoke-Step "Cleaning old release build output..." @("dotnet", "clean", $projectPath, "-c", $Configuration)
}
Invoke-Step "Running non-UI tests..." (@("dotnet", "test", $solutionPath, "-c", $Configuration, "--nologo") + $restoreOption)
Invoke-Step "Building release configuration..." (@("dotnet", "build", $projectPath, "-c", $Configuration, "--nologo") + $restoreOption)

Write-Host "Publishing Windows desktop executable..."
$selfContainedValue = if ($SelfContained) { "true" } else { "false" }
Invoke-Step "dotnet publish..." (@(
    "dotnet",
    "publish",
    $projectPath,
    "-c",
    $Configuration,
    "-r",
    $Runtime,
    "--self-contained",
    $selfContainedValue,
    "-p:PublishSingleFile=false",
    "-p:IncludeNativeLibrariesForSelfExtract=true",
    "-o",
    $publishDir,
    "--nologo") + $restoreOption)

Write-Host "Removing runtime/private data from publish output..."
$excludedRuntimeItems = @(
    "data",
    "exports",
    "image_vault",
    "training",
    "aoi_monitor.sqlite",
    "aoi_monitor.sqlite-wal",
    "aoi_monitor.sqlite-shm",
    "*.db",
    "*.sqlite",
    "*.sqlite-wal",
    "*.sqlite-shm"
)

foreach ($item in $excludedRuntimeItems) {
    Get-ChildItem -Path $publishDir -Filter $item -Recurse -Force -ErrorAction SilentlyContinue |
        Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
}

if ($IncludeDocs -or !$ValidationOnly) {
    Write-Host "Copying documentation..."
    Copy-Item -LiteralPath $docsPath -Destination $releaseDocsDir -Recurse -Force
    Copy-Item -LiteralPath $rootReadmePath -Destination (Join-Path $releaseDir "README.md") -Force
    if (Test-Path $featureDocPath) {
        Copy-Item -LiteralPath $featureDocPath -Destination (Join-Path $releaseDir "IMPLEMENTED_FEATURES.md") -Force
    }
} else {
    Write-Host "Skipping documentation copy. Pass -IncludeDocs to include docs during validation-only packaging."
}

if ($IncludeTemplates) {
    if (!(Test-Path $templatesPath)) {
        throw "Templates folder was requested but not found: $templatesPath"
    }

    Write-Host "Copying adapter templates..."
    Copy-Item -LiteralPath $templatesPath -Destination $releaseTemplatesDir -Recurse -Force
    Get-ChildItem -LiteralPath $releaseTemplatesDir -Directory -Recurse -Include bin,obj -ErrorAction SilentlyContinue |
        Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
    Get-ChildItem -LiteralPath $releaseTemplatesDir -Recurse -Include *.dll,*.exe,*.pdb,*.deps.json,*.runtimeconfig.json -ErrorAction SilentlyContinue |
        Remove-Item -Force -ErrorAction SilentlyContinue
}

if ($IncludeSampleManifestTemplate) {
    if (!(Test-Path $sampleManifestTemplatePath)) {
        throw "Sample manifest template was requested but not found: $sampleManifestTemplatePath"
    }

    Write-Host "Copying customer validation manifest template..."
    New-Item -ItemType Directory -Force -Path $releaseSampleDataDir | Out-Null
    Copy-Item -LiteralPath $sampleManifestTemplatePath -Destination (Join-Path $releaseSampleDataDir "customer_validation_manifest_template.csv") -Force
}

$releaseReadme = @"
# AOI Monitor Windows Desktop PoC Release

Generated: $(Get-Date -Format "yyyy-MM-dd HH:mm:ss")
Runtime: $Runtime
Publish mode: $(if ($SelfContained) { "Self-contained" } else { "Framework-dependent" })

## How to Run

1. Open the app folder.
2. Run AOI_Monitor.exe.
3. If this is a framework-dependent package, install a .NET Desktop Runtime/SDK that supports the app target framework before launching.

## Included

- app/ - Published Windows desktop application files.
$(if ($IncludeDocs -or !$ValidationOnly) { "- Docs/ - Installation guide, deployment package guide, user manual, stage mapping, integration boundaries, and acceptance checklist.`n- README.md - Repository-level overview and operating notes.`n- IMPLEMENTED_FEATURES.md - Current PoC feature inventory." } else { "- Docs were skipped for this validation-only package. Pass -IncludeDocs to include them." })
$(if ($IncludeTemplates) { "- Templates/ - Fake/no-op adapter template source projects for camera, lighting, and robot integrations." } else { "- Adapter templates were not included. Pass -IncludeTemplates for developer handoff packages." })
$(if ($IncludeSampleManifestTemplate) { "- SampleData/customer_validation_manifest_template.csv - Empty customer validation manifest template only; no customer images are included." } else { "- Sample manifest template was not included. Pass -IncludeSampleManifestTemplate when preparing customer validation kits." })

## Not Included

This release intentionally excludes local runtime/customer data:

- Local SQLite databases and WAL/SHM sidecars.
- Image vaults, training images, customer images, and production images.
- Generated export folders, customer packages, overlays, CSV reports, and machine-interface JSON.
- Local %LOCALAPPDATA%\AOI_Monitor data.

The app creates local PoC data on the target machine when it runs.

## Packaging

Zip this release folder for delivery:

Compress-Archive -Path "$releaseName" -DestinationPath "$releaseName.zip"
"@

Set-Content -LiteralPath (Join-Path $releaseDir "RUN_RELEASE.md") -Value $releaseReadme -Encoding UTF8

Write-Host "Release package created:"
Write-Host $releaseDir

if ($ValidationOnly) {
    Write-Host "Validation-only publish completed. Output was written outside the repository unless -OutputRoot was provided."
}
