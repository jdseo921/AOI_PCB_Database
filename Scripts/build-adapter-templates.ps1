[CmdletBinding()]
param(
    [string]$Configuration = "Release",
    [string]$OutputRoot = "",
    [switch]$NoRestore
)

$ErrorActionPreference = "Stop"

$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = (Resolve-Path (Join-Path $scriptRoot "..")).Path
$templatesRoot = Join-Path $repoRoot "Templates"
$testRoot = if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    Join-Path ([System.IO.Path]::GetTempPath()) "AOI_Monitor_AdapterTemplatePlugins"
} else {
    $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputRoot)
}

$templates = @(
    @{ Name = "CameraAdapterTemplate"; Project = "CameraAdapterTemplate.csproj"; Manifest = "camera_adapter_manifest.json" },
    @{ Name = "LightingAdapterTemplate"; Project = "LightingAdapterTemplate.csproj"; Manifest = "lighting_adapter_manifest.json" },
    @{ Name = "RobotPlcAdapterTemplate"; Project = "RobotPlcAdapterTemplate.csproj"; Manifest = "robot_plc_adapter_manifest.json" }
)

Write-Host "Building AOI adapter templates"
Write-Host "Repository: $repoRoot"
Write-Host "Configuration: $Configuration"
Write-Host "Plugin test output: $testRoot"
Write-Host "Restore: $(if ($NoRestore) { 'skip; use existing restore assets' } else { 'allow dotnet build to restore as needed' })"

New-Item -ItemType Directory -Force -Path $testRoot | Out-Null

foreach ($template in $templates) {
    $templateDir = Join-Path $templatesRoot $template.Name
    $projectPath = Join-Path $templateDir $template.Project
    if (!(Test-Path -LiteralPath $projectPath -PathType Leaf)) {
        throw "Template project not found: $projectPath"
    }

    Write-Host "Building $($template.Name)..."
    $buildArgs = @("build", $projectPath, "-c", $Configuration, "--nologo")
    if ($NoRestore) {
        $buildArgs += "--no-restore"
    }

    & dotnet @buildArgs
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet build failed for $($template.Name)"
    }

    $targetFrameworkDir = Get-ChildItem -LiteralPath (Join-Path $templateDir "bin\$Configuration") -Directory |
        Sort-Object Name -Descending |
        Select-Object -First 1
    if ($null -eq $targetFrameworkDir) {
        throw "Could not find build output for $($template.Name)"
    }

    $pluginDir = Join-Path $testRoot $template.Name
    New-Item -ItemType Directory -Force -Path $pluginDir | Out-Null
    Copy-Item -LiteralPath (Join-Path $targetFrameworkDir.FullName "$($template.Name).dll") -Destination $pluginDir -Force
    Copy-Item -LiteralPath (Join-Path $templateDir $template.Manifest) -Destination $pluginDir -Force

    Write-Host "Copied sample plugin folder: $pluginDir"
}

Write-Host "Adapter template build complete."
