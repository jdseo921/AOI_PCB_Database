[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$AdapterFolder,

    [string]$SettingsJson = "",

    [Parameter(Mandatory = $true)]
    [string]$OutputFolder
)

$ErrorActionPreference = "Stop"

$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = (Resolve-Path (Join-Path $scriptRoot "..")).Path
$toolProject = Join-Path $repoRoot "AOI_Monitor.Tools\AOI_Monitor.Tools.csproj"

function Resolve-RequiredFolder {
    param([string]$PathValue)

    if ([string]::IsNullOrWhiteSpace($PathValue)) {
        throw "Path is required."
    }

    $resolved = Resolve-Path -LiteralPath $PathValue
    if ($null -eq $resolved) {
        throw "Path does not exist: $PathValue"
    }

    return $resolved.Path
}

function Resolve-OptionalFile {
    param([string]$PathValue)

    if ([string]::IsNullOrWhiteSpace($PathValue)) {
        return ""
    }

    $resolved = Resolve-Path -LiteralPath $PathValue
    if ($null -eq $resolved) {
        throw "SettingsJson does not exist: $PathValue"
    }

    return $resolved.Path
}

function Resolve-OutputFolder {
    param([string]$PathValue)

    if ([string]::IsNullOrWhiteSpace($PathValue)) {
        throw "OutputFolder is required."
    }

    if ([System.IO.Path]::IsPathRooted($PathValue)) {
        return [System.IO.Path]::GetFullPath($PathValue)
    }

    return [System.IO.Path]::GetFullPath((Join-Path (Get-Location) $PathValue))
}

function Get-CameraManifestPath {
    param([string]$Folder)

    $primary = Join-Path $Folder "camera_adapter_manifest.json"
    if (Test-Path -LiteralPath $primary -PathType Leaf) {
        return $primary
    }

    $matches = @(Get-ChildItem -LiteralPath $Folder -Filter "*.camera-adapter.json" -File | Sort-Object FullName)
    if ($matches.Count -gt 0) {
        return $matches[0].FullName
    }

    return ""
}

function Get-ManifestAssemblyPath {
    param(
        [string]$ManifestPath,
        [object]$Manifest
    )

    $assemblyFile = if ($Manifest.assemblyFile) { [string]$Manifest.assemblyFile } else { [string]$Manifest.assemblyPath }
    if ([string]::IsNullOrWhiteSpace($assemblyFile)) {
        return ""
    }

    if ([System.IO.Path]::IsPathRooted($assemblyFile)) {
        return [System.IO.Path]::GetFullPath($assemblyFile)
    }

    return [System.IO.Path]::GetFullPath((Join-Path (Split-Path -Parent $ManifestPath) $assemblyFile))
}

function Use-BuiltAdapterPackageIfNeeded {
    param(
        [string]$Folder,
        [string]$OutputRoot
    )

    $manifestPath = Get-CameraManifestPath $Folder
    if ([string]::IsNullOrWhiteSpace($manifestPath)) {
        return $Folder
    }

    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    $assemblyPath = Get-ManifestAssemblyPath $manifestPath $manifest
    if (![string]::IsNullOrWhiteSpace($assemblyPath) -and (Test-Path -LiteralPath $assemblyPath -PathType Leaf)) {
        return $Folder
    }

    $projects = @(Get-ChildItem -LiteralPath $Folder -Filter "*.csproj" -File | Sort-Object FullName)
    if ($projects.Count -ne 1) {
        return $Folder
    }

    $assemblyFileName = if ($manifest.assemblyFile) {
        [System.IO.Path]::GetFileName([string]$manifest.assemblyFile)
    } elseif ($manifest.assemblyPath) {
        [System.IO.Path]::GetFileName([string]$manifest.assemblyPath)
    } else {
        ""
    }

    if ([string]::IsNullOrWhiteSpace($assemblyFileName)) {
        return $Folder
    }

    $buildRoot = Join-Path $Folder "bin\Release"
    $builtAssembly = Get-ChildItem -LiteralPath $buildRoot -Recurse -Filter $assemblyFileName -File |
        Sort-Object LastWriteTimeUtc -Descending |
        Select-Object -First 1
    if ($null -eq $builtAssembly) {
        Write-Host "Adapter assembly is not beside the manifest; building source adapter project for validation staging."
        & dotnet build $projects[0].FullName -c Release --no-restore --nologo
        if ($LASTEXITCODE -ne 0) {
            exit $LASTEXITCODE
        }

        $builtAssembly = Get-ChildItem -LiteralPath $buildRoot -Recurse -Filter $assemblyFileName -File |
            Sort-Object LastWriteTimeUtc -Descending |
            Select-Object -First 1
    }

    if ($null -eq $builtAssembly) {
        throw "Build completed but adapter assembly was not found: $assemblyFileName"
    }

    $stage = Join-Path $OutputRoot "adapter_package_staging"
    New-Item -ItemType Directory -Force -Path $stage | Out-Null
    Copy-Item -LiteralPath $manifestPath -Destination (Join-Path $stage (Split-Path -Leaf $manifestPath)) -Force
    Get-ChildItem -LiteralPath $builtAssembly.Directory.FullName | ForEach-Object {
        Copy-Item -LiteralPath $_.FullName -Destination $stage -Recurse -Force
    }
    return $stage
}

function Get-ToolAssemblyPath {
    $toolBuildRoot = Join-Path $repoRoot "AOI_Monitor.Tools\bin\Release"
    $toolAssembly = if (Test-Path -LiteralPath $toolBuildRoot -PathType Container) {
        Get-ChildItem -LiteralPath $toolBuildRoot -Recurse -Filter "AOI_Monitor.Tools.dll" -File |
            Sort-Object LastWriteTimeUtc -Descending |
            Select-Object -First 1
    } else {
        $null
    }

    if ($null -ne $toolAssembly) {
        return $toolAssembly.FullName
    }

    Write-Host "AOI_Monitor.Tools Release assembly was not found; building with --no-restore."
    & dotnet build $toolProject -c Release --no-restore --nologo
    if ($LASTEXITCODE -ne 0) {
        exit $LASTEXITCODE
    }

    $toolAssembly = Get-ChildItem -LiteralPath $toolBuildRoot -Recurse -Filter "AOI_Monitor.Tools.dll" -File |
        Sort-Object LastWriteTimeUtc -Descending |
        Select-Object -First 1
    if ($null -eq $toolAssembly) {
        throw "AOI_Monitor.Tools build completed but the Release assembly was not found."
    }

    return $toolAssembly.FullName
}

$resolvedAdapterFolder = Resolve-RequiredFolder $AdapterFolder
$resolvedSettingsJson = Resolve-OptionalFile $SettingsJson
$resolvedOutputFolder = Resolve-OutputFolder $OutputFolder
New-Item -ItemType Directory -Force -Path $resolvedOutputFolder | Out-Null

$validationAdapterFolder = Use-BuiltAdapterPackageIfNeeded $resolvedAdapterFolder $resolvedOutputFolder
$toolAssemblyPath = Get-ToolAssemblyPath

$toolArgs = @(
    $toolAssemblyPath,
    "camera-adapter-validate",
    "--adapter-folder",
    $validationAdapterFolder,
    "--output-folder",
    $resolvedOutputFolder
)

if (![string]::IsNullOrWhiteSpace($resolvedSettingsJson)) {
    $toolArgs += @("--settings-json", $resolvedSettingsJson)
}

& dotnet @toolArgs
exit $LASTEXITCODE
