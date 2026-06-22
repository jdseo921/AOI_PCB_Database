[CmdletBinding()]
param(
    [string]$Solution = "AOI_PCB_Database.slnx",
    [string]$Configuration = "Release",
    [string]$ReportPath = "TestResults/code_quality_report.json",
    [string[]]$SourceRoots = @("AOI_Monitor", "Templates", "Scripts"),
    [switch]$SkipFormat,
    [switch]$SkipBuild
)

$ErrorActionPreference = "Stop"

$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = (Resolve-Path (Join-Path $scriptRoot "..")).Path
$issues = [System.Collections.Generic.List[object]]::new()
$steps = [System.Collections.Generic.List[object]]::new()

function Resolve-RepoPath {
    param([string]$Path)

    if ([System.IO.Path]::IsPathRooted($Path)) {
        return $Path
    }

    return Join-Path $repoRoot $Path
}

function Convert-ToDisplayPath {
    param([string]$Path)

    $full = [System.IO.Path]::GetFullPath($Path)
    if ($full.StartsWith($repoRoot, [StringComparison]::OrdinalIgnoreCase)) {
        return $full.Substring($repoRoot.Length).TrimStart('\', '/').Replace('\', '/')
    }

    return $full
}

function Add-Issue {
    param(
        [string]$RuleId,
        [string]$Path,
        [int]$Line,
        [string]$Message
    )

    $issue = [pscustomobject]@{
        ruleId = $RuleId
        path = $Path
        line = $Line
        message = $Message
    }
    $issues.Add($issue)
    Write-Host "[FAIL][$RuleId] ${Path}:${Line} - $Message" -ForegroundColor Red
}

function Add-Step {
    param(
        [string]$Name,
        [string]$Status,
        [string]$Details
    )

    $steps.Add([pscustomobject]@{
        name = $Name
        status = $Status
        details = $Details
    })
}

function Invoke-QualityCommand {
    param(
        [string]$Name,
        [string]$Executable,
        [string[]]$Arguments
    )

    Write-Host "$Name"
    Write-Host "  $Executable $($Arguments -join ' ')"
    & $Executable @Arguments
    $exitCode = $LASTEXITCODE
    if ($exitCode -ne 0) {
        Add-Step $Name "FAIL" "$Executable exited with code $exitCode."
        Add-Issue "CQ-CMD-001" $Executable 0 "$Name failed with exit code $exitCode."
        return
    }

    Add-Step $Name "PASS" "$Executable completed successfully."
}

function Get-SourceFiles {
    $files = [System.Collections.Generic.List[System.IO.FileInfo]]::new()
    foreach ($root in $SourceRoots) {
        $resolved = Resolve-RepoPath $root
        if (Test-Path -LiteralPath $resolved -PathType Leaf) {
            $item = Get-Item -LiteralPath $resolved
            if ($item.Extension -in @(".cs", ".ps1")) {
                $files.Add($item)
            }
            continue
        }

        if (!(Test-Path -LiteralPath $resolved -PathType Container)) {
            continue
        }

        foreach ($file in Get-ChildItem -LiteralPath $resolved -Recurse -File -Include *.cs,*.ps1) {
            if ($file.FullName -match '\\(bin|obj)\\') {
                continue
            }

            $files.Add($file)
        }
    }

    return @($files | Sort-Object FullName -Unique)
}

function Get-LineNumber {
    param(
        [string]$Text,
        [int]$Index
    )

    if ($Index -le 0) {
        return 1
    }

    return ($Text.Substring(0, $Index).ToCharArray() | Where-Object { $_ -eq "`n" }).Count + 1
}

function Test-IsUiCodePath {
    param([string]$DisplayPath)

    return $DisplayPath -match '(^|/)AOI_Monitor/(Views|Controls)/' -or
        $DisplayPath -match '(^|/)AOI_Monitor/(MainWindow|App)\.xaml\.cs$'
}

function Test-IsTestPath {
    param([string]$DisplayPath)

    return $DisplayPath -match '(^|/)(AOI_Monitor\.Tests|AOI_Monitor\.UiTests)/'
}

function Test-IsEventHandlerSignature {
    param(
        [string]$MethodName,
        [string]$Parameters
    )

    if ($Parameters -match '\bobject\s+\w+' -and $Parameters -match '\bEventArgs\b') {
        return $true
    }

    return $MethodName.StartsWith("On", [StringComparison]::Ordinal) -and
        $Parameters -match '\b(sender|e)\b'
}

function Invoke-PatternChecks {
    $sourceFiles = @(Get-SourceFiles)
    Write-Host "Pattern scan source files: $($sourceFiles.Count)"

    foreach ($file in $sourceFiles) {
        $displayPath = Convert-ToDisplayPath $file.FullName
        $text = Get-Content -LiteralPath $file.FullName -Raw
        $lines = Get-Content -LiteralPath $file.FullName

        foreach ($match in [regex]::Matches($text, 'catch\s*(?:\([^)]*\))?\s*\{\s*\}', [System.Text.RegularExpressions.RegexOptions]::Singleline)) {
            if ($match.Value -match '(?i)(OperationCanceledException|TaskCanceledException)') {
                continue
            }

            Add-Issue "CQ-CATCH-001" $displayPath (Get-LineNumber $text $match.Index) "Empty catch block is forbidden. Log, record an alarm/report, or rethrow."
        }

        foreach ($match in [regex]::Matches($text, 'async\s+void\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*\((?<parameters>[^)]*)\)')) {
            $methodName = $match.Groups["name"].Value
            $parameters = $match.Groups["parameters"].Value
            if (!(Test-IsEventHandlerSignature $methodName $parameters)) {
                Add-Issue "CQ-ASYNC-001" $displayPath (Get-LineNumber $text $match.Index) "async void is allowed only for event handlers. Return Task for helper methods."
            }
        }

        if (Test-IsUiCodePath $displayPath) {
            for ($index = 0; $index -lt $lines.Count; $index++) {
                $line = $lines[$index]
                if ($line -match '\bThread\.Sleep\s*\(') {
                    Add-Issue "CQ-UI-001" $displayPath ($index + 1) "Thread.Sleep is forbidden in UI code. Use async delay or background work."
                }

                if ($line -match '\bFile\.ReadAllBytes\s*\(') {
                    Add-Issue "CQ-UI-002" $displayPath ($index + 1) "File.ReadAllBytes is forbidden in UI code. Move heavy file IO to a service/background path."
                }
            }
        }

        foreach ($match in [regex]::Matches($text, 'MessageBox\.Show\s*\((?<body>.*?);', [System.Text.RegularExpressions.RegexOptions]::Singleline)) {
            $body = $match.Groups["body"].Value
            if ($body -match '(?i)(\.StackTrace\b|\bStackTrace\b|(?:ex|exception)\.ToString\s*\()') {
                Add-Issue "CQ-MSG-001" $displayPath (Get-LineNumber $text $match.Index) "MessageBox.Show must not display raw exception stacks or exception.ToString()."
            }
        }

        if (!(Test-IsTestPath $displayPath) -and $displayPath -notmatch '(^|/)Scripts/check-code-quality\.ps1$') {
            $secretPattern = '(?i)\b(password|passwd|api[_-]?key|access[_-]?key|client[_-]?secret|secret|token|connectionstring)\b\s*[:=]\s*["'']([^"'']{8,})["'']'
            $allowedSecretEvidencePattern = '(?i)\b(example|sample|placeholder|dummy|fake|test|redacted|masked|xxxx|your_|<[^>]+>|UNKNOWN)\b'
            for ($index = 0; $index -lt $lines.Count; $index++) {
                $line = $lines[$index]
                if ($line -match $secretPattern -and $line -notmatch $allowedSecretEvidencePattern) {
                    Add-Issue "CQ-SEC-001" $displayPath ($index + 1) "Possible hard-coded credential or secret literal."
                }
            }
        }
    }
}

Write-Host "AOI Monitor code quality gate"
Write-Host "Repository: $repoRoot"

if (!$SkipFormat) {
    Invoke-QualityCommand "dotnet format --verify-no-changes" "dotnet" @(
        "format",
        (Resolve-RepoPath $Solution),
        "--verify-no-changes",
        "--no-restore",
        "--verbosity",
        "minimal",
        "--severity",
        "error")
}
else {
    Add-Step "dotnet format --verify-no-changes" "SKIPPED" "Skipped by parameter."
}

if (!$SkipBuild) {
    Invoke-QualityCommand "dotnet build Release with analyzers" "dotnet" @(
        "build",
        (Resolve-RepoPath $Solution),
        "--configuration",
        $Configuration,
        "--no-restore",
        "-p:RunAnalyzersDuringBuild=true",
        "-p:EnableNETAnalyzers=true")
}
else {
    Add-Step "dotnet build Release with analyzers" "SKIPPED" "Skipped by parameter."
}

Invoke-PatternChecks
if ($issues.Count -eq 0) {
    Add-Step "dangerous pattern scan" "PASS" "No forbidden code-quality patterns found."
}
else {
    Add-Step "dangerous pattern scan" "FAIL" "$($issues.Count) issue(s) found."
}

$status = if ($issues.Count -gt 0 -or @($steps | Where-Object { $_.status -eq "FAIL" }).Count -gt 0) { "FAIL" } else { "PASS" }
$report = [pscustomobject]@{
    schemaVersion = "aoi-code-quality/v1"
    generatedAtUtc = [DateTimeOffset]::UtcNow.ToString("O")
    status = $status
    configuration = $Configuration
    sourceRoots = $SourceRoots
    steps = @($steps)
    issues = @($issues)
}

$resolvedReportPath = Resolve-RepoPath $ReportPath
$reportDirectory = Split-Path -Parent $resolvedReportPath
if (![string]::IsNullOrWhiteSpace($reportDirectory)) {
    New-Item -ItemType Directory -Force -Path $reportDirectory | Out-Null
}

$report | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $resolvedReportPath -Encoding UTF8
Write-Host "Code quality report: $resolvedReportPath"

if ($status -eq "FAIL") {
    throw "Code quality gate failed with $($issues.Count) issue(s)."
}

Write-Host "Code quality gate passed."
