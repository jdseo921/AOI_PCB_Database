param(
    [string]$Configuration = "Release",
    [string]$ResultsDirectory = "TestResults",
    [switch]$SkipPackageValidation
)

$ErrorActionPreference = "Stop"
$script:overallStatus = "PASS"
$script:failed = $false
$script:steps = New-Object System.Collections.Generic.List[object]

$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = Resolve-Path (Join-Path $scriptRoot "..")
$solutionPath = Join-Path $repoRoot "AOI_PCB_Database.slnx"
$uiTestsProject = Join-Path $repoRoot "AOI_Monitor.UiTests\AOI_Monitor.UiTests.csproj"
$unitTestsProject = Join-Path $repoRoot "AOI_Monitor.Tests\AOI_Monitor.Tests.csproj"
$resultsRoot = if ([System.IO.Path]::IsPathRooted($ResultsDirectory)) {
    $ResultsDirectory
} else {
    Join-Path $repoRoot $ResultsDirectory
}

New-Item -ItemType Directory -Force -Path $resultsRoot | Out-Null

function Invoke-External {
    param([string[]]$Command)

    Write-Host ">>> $($Command -join ' ')"
    & $Command[0] @($Command[1..($Command.Length - 1)])
    if ($LASTEXITCODE -ne 0) {
        throw "Command failed with exit code ${LASTEXITCODE}: $($Command -join ' ')"
    }
}

function Add-StepResult {
    param(
        [string]$Id,
        [string]$Name,
        [string]$Status,
        [datetime]$StartedAt,
        [string]$Message,
        [string[]]$Artifacts = @()
    )

    $script:steps.Add([ordered]@{
        id = $Id
        name = $Name
        status = $Status
        startedAtUtc = $StartedAt.ToUniversalTime().ToString("O")
        completedAtUtc = (Get-Date).ToUniversalTime().ToString("O")
        durationSeconds = [Math]::Round(((Get-Date) - $StartedAt).TotalSeconds, 2)
        message = $Message
        artifacts = $Artifacts
    }) | Out-Null
}

function Write-GateReport {
    $report = [ordered]@{
        schemaVersion = "industrial-quality-gate-report/v1"
        generatedAtUtc = (Get-Date).ToUniversalTime().ToString("O")
        repository = $repoRoot.Path
        configuration = $Configuration
        overallStatus = $script:overallStatus
        steps = $script:steps
        artifacts = [ordered]@{
            hmiLayoutAuditHtml = Join-Path $resultsRoot "hmi_layout_audit.html"
            hmiLayoutAuditJson = Join-Path $resultsRoot "hmi_layout_audit.json"
            uiNavigationPerformanceJson = Join-Path $resultsRoot "ui_navigation_performance.json"
            codeQualityReportJson = Join-Path $resultsRoot "code_quality_report.json"
            standardsTraceabilityHtml = Join-Path $resultsRoot "standards_traceability_matrix.html"
            standardsTraceabilityJson = Join-Path $resultsRoot "standards_traceability_matrix.json"
            standardsTraceabilityPdf = Join-Path $resultsRoot "standards_traceability_matrix.pdf"
            testResultsTrx = Join-Path $resultsRoot "test-results.trx"
        }
    }

    $path = Join-Path $resultsRoot "industrial_quality_gate_report.json"
    $report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $path -Encoding UTF8
    Write-Host "Industrial quality gate report: $path"
}

function Invoke-GateStep {
    param(
        [string]$Id,
        [string]$Name,
        [scriptblock]$Action,
        [string[]]$Artifacts = @()
    )

    if ($script:failed) {
        return
    }

    $started = Get-Date
    Write-Host ""
    Write-Host "=== [$Id] $Name ==="
    try {
        & $Action
        Add-StepResult -Id $Id -Name $Name -Status "PASS" -StartedAt $started -Message "Completed." -Artifacts $Artifacts
    } catch {
        $script:overallStatus = "FAIL"
        $script:failed = $true
        Add-StepResult -Id $Id -Name $Name -Status "FAIL" -StartedAt $started -Message $_.Exception.Message -Artifacts $Artifacts
        Write-Host "::error title=$Name::$($_.Exception.Message)"
    }
}

Write-Host "AOI Monitor quality gates"
Write-Host "Repository: $repoRoot"
Write-Host "Results:    $resultsRoot"
Write-Host "Config:     $Configuration"

Invoke-GateStep "HYGIENE-001" "Repository hygiene" {
    Invoke-External @("pwsh", (Join-Path $scriptRoot "check-repo-hygiene.ps1"))
}

Invoke-GateStep "RESTORE-001" "Restore solution" {
    Invoke-External @("dotnet", "restore", $solutionPath)
}

Invoke-GateStep "BUILD-001" "Build Release solution" {
    Invoke-External @("dotnet", "build", $solutionPath, "--configuration", $Configuration)
}

Invoke-GateStep "TEST-001" "Run Release tests" {
    Invoke-External @(
        "dotnet", "test", $solutionPath,
        "--configuration", $Configuration,
        "--logger", "trx;LogFileName=test-results.trx",
        "--results-directory", $resultsRoot)
} @((Join-Path $resultsRoot "test-results.trx"))

Invoke-GateStep "CODE-001" "Code quality and dangerous pattern scan" {
    Invoke-External @(
        "pwsh", (Join-Path $scriptRoot "check-code-quality.ps1"),
        "-Configuration", $Configuration,
        "-ReportPath", (Join-Path $resultsRoot "code_quality_report.json"),
        "-SkipBuild")
} @((Join-Path $resultsRoot "code_quality_report.json"))

Invoke-GateStep "HMI-001" "UI layout audit" {
    Invoke-External @(
        "dotnet", "test", $uiTestsProject,
        "--configuration", $Configuration,
        "--no-build",
        "--filter", "FullyQualifiedName~HmiLayoutAuditTests",
        "--logger", "trx;LogFileName=hmi-layout-audit.trx",
        "--results-directory", $resultsRoot)
} @((Join-Path $resultsRoot "hmi_layout_audit.html"), (Join-Path $resultsRoot "hmi_layout_audit.json"))

Invoke-GateStep "PERF-001" "Navigation performance smoke" {
    Invoke-External @(
        "dotnet", "test", $uiTestsProject,
        "--configuration", $Configuration,
        "--no-build",
        "--filter", "FullyQualifiedName~UiNavigationPerformanceTests",
        "--logger", "trx;LogFileName=ui-navigation-performance.trx",
        "--results-directory", $resultsRoot)
} @((Join-Path $resultsRoot "ui_navigation_performance.json"))

Invoke-GateStep "EXPORT-001" "Export verification tests" {
    Invoke-External @(
        "dotnet", "test", $unitTestsProject,
        "--configuration", $Configuration,
        "--no-build",
        "--filter", "FullyQualifiedName~ExportVerification",
        "--logger", "trx;LogFileName=export-verification-results.trx",
        "--results-directory", $resultsRoot)
} @((Join-Path $resultsRoot "export-verification-results.trx"))

Invoke-GateStep "STD-001" "Standards traceability report" {
    $env:AOI_STANDARDS_TRACEABILITY_OUTPUT = $resultsRoot
    try {
        Invoke-External @(
            "dotnet", "test", $unitTestsProject,
            "--configuration", $Configuration,
            "--no-build",
            "--filter", "FullyQualifiedName~StandardsTraceabilityServiceTests.ExportStandardsTraceabilityForCi",
            "--logger", "trx;LogFileName=standards-traceability.trx",
            "--results-directory", $resultsRoot)
    } finally {
        Remove-Item Env:\AOI_STANDARDS_TRACEABILITY_OUTPUT -ErrorAction SilentlyContinue
    }
} @((Join-Path $resultsRoot "standards_traceability_matrix.html"), (Join-Path $resultsRoot "standards_traceability_matrix.json"), (Join-Path $resultsRoot "standards_traceability_matrix.pdf"))

if (-not $SkipPackageValidation) {
    Invoke-GateStep "PKG-001" "Package validation" {
        $publishRoot = Join-Path $resultsRoot "package_validation"
        Invoke-External @(
            "pwsh", (Join-Path $scriptRoot "publish.ps1"),
            "-Configuration", $Configuration,
            "-OutputRoot", $publishRoot,
            "-ValidationOnly",
            "-NoRestore")
    }
} else {
    Add-StepResult -Id "PKG-001" -Name "Package validation" -Status "SKIPPED" -StartedAt (Get-Date) -Message "Skipped by caller to avoid recursive publish validation."
}

Write-GateReport

if ($script:failed) {
    Write-Host "Quality gates FAILED. See TestResults/industrial_quality_gate_report.json."
    exit 1
}

Write-Host "Quality gates PASS."
