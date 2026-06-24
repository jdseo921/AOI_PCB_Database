param(
    [string]$DatasetRoot = (Join-Path $PSScriptRoot "..\SampleData\DemoSet_Quick"),
    [switch]$SkipGenerate
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$generator = Join-Path $repoRoot "SampleData\demo_dataset_generator.ps1"
$datasetRootFull = [System.IO.Path]::GetFullPath($DatasetRoot)
$imagesDir = Join-Path $datasetRootFull "images"
$goldenDir = Join-Path $datasetRootFull "golden"
$manifestPath = Join-Path $datasetRootFull "customer_validation_manifest.csv"
$cameraTop = Join-Path $datasetRootFull "folder_camera\top"
$cameraSide = Join-Path $datasetRootFull "folder_camera\side"
$cameraBottom = Join-Path $datasetRootFull "folder_camera\bottom"

function Assert-PathExists([string]$path, [string]$label) {
    if (-not (Test-Path -LiteralPath $path)) {
        throw "$label was not found: $path"
    }

    Write-Host "[OK] ${label}: $path"
}

function Assert-ManifestPathsResolve([string]$manifest, [string]$root) {
    $rows = Import-Csv -LiteralPath $manifest
    if ($rows.Count -eq 0) {
        throw "Manifest has no rows: $manifest"
    }

    $requiredColumns = @(
        "image",
        "ground_truth",
        "golden_image",
        "defect_type",
        "side",
        "refdes",
        "roi_id",
        "roi_type",
        "lot_id",
        "board_model",
        "notes"
    )
    foreach ($column in $requiredColumns) {
        if (-not ($rows[0].PSObject.Properties.Name -contains $column)) {
            throw "Manifest is missing required column '$column'."
        }
    }

    $missing = [System.Collections.Generic.List[string]]::new()
    foreach ($row in $rows) {
        foreach ($field in @("image", "golden_image")) {
            $value = [string]$row.$field
            if ([string]::IsNullOrWhiteSpace($value)) {
                $missing.Add("$field is blank for manifest row $($row.image)") | Out-Null
                continue
            }

            $candidate = if ([System.IO.Path]::IsPathRooted($value)) {
                $value
            } else {
                Join-Path $root $value
            }

            if (-not (Test-Path -LiteralPath $candidate)) {
                $missing.Add("$field path does not resolve: $value") | Out-Null
            }
        }
    }

    if ($missing.Count -gt 0) {
        $top = $missing | Select-Object -First 10
        throw "Manifest path resolution failed:`n$($top -join [Environment]::NewLine)"
    }

    $okCount = ($rows | Where-Object { $_.ground_truth -eq "OK" }).Count
    $ngRows = @($rows | Where-Object { $_.ground_truth -eq "NG" })
    $defectClasses = @($ngRows | Group-Object defect_type)
    Write-Host "[OK] Manifest rows: $($rows.Count), OK: $okCount, NG: $($ngRows.Count), NG defect classes: $($defectClasses.Count)"
}

if (-not $SkipGenerate) {
    Assert-PathExists $generator "Demo dataset generator"
    Write-Host "Generating synthetic/demo sample dataset..."
    & pwsh -NoProfile -ExecutionPolicy Bypass -File $generator -OutputRoot $datasetRootFull
}

Assert-PathExists $imagesDir "Demo images folder"
Assert-PathExists $goldenDir "Golden reference folder"
Assert-PathExists $manifestPath "Customer validation manifest"
Assert-PathExists $cameraTop "Folder Camera Simulation top folder"
Assert-PathExists $cameraSide "Folder Camera Simulation side folder"
Assert-PathExists $cameraBottom "Folder Camera Simulation bottom folder"
Assert-ManifestPathsResolve $manifestPath $datasetRootFull

Write-Host ""
Write-Host "Next manual GUI steps:"
Write-Host "1. Open AOI Monitor."
Write-Host "2. AI / Models: select '$imagesDir'."
Write-Host "3. AI / Models: select '$manifestPath'."
Write-Host "4. Click Run Dataset Preflight, then Run Batch Inspection."
Write-Host "5. Verify rows, OK/NG/REVIEW, false calls, possible escapes, selected-row preview, CSV export, annotated export, and Stage 1 validation package export."
Write-Host "6. Export & Trace: run Performance Benchmark against '$imagesDir'."
Write-Host "7. Export & Trace > Stage 1 Readiness: refresh and export the readiness report."
Write-Host "8. Open validation_summary.html, customer_validation_report.html, benchmark_report.html, stage1_readiness_report.html, benchmark_results.csv, and limitations.txt."
Write-Host ""
Write-Host "Reminder: this is synthetic/demo Stage 1 evidence using the Pixel Difference Prototype Engine and Folder Camera Simulation only."
