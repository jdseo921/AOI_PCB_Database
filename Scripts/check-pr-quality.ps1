[CmdletBinding()]
param(
    [string]$BaseRef = "",
    [string]$HeadRef = "HEAD",
    [string]$ReportPath = "TestResults/pr_quality_gate_report.json",
    [switch]$TreatWarningsAsErrors
)

$ErrorActionPreference = "Stop"

$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = (Resolve-Path (Join-Path $scriptRoot "..")).Path
$repoRootForGit = $repoRoot.Replace('\', '/')
$gitBaseArgs = @("-c", "safe.directory=$repoRootForGit", "-C", $repoRoot)

function Invoke-GitLines {
    param([string[]]$Arguments)

    $output = & git @gitBaseArgs @Arguments 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "git $($Arguments -join ' ') failed: $($output -join [Environment]::NewLine)"
    }

    return @($output | ForEach-Object { "$_" })
}

function Invoke-GitLinesOrEmpty {
    param([string[]]$Arguments)

    try {
        return Invoke-GitLines $Arguments
    }
    catch {
        Write-Verbose "git $($Arguments -join ' ') failed while collecting optional PR context: $($_.Exception.Message)"
        return @()
    }
}

function Normalize-RepoPath {
    param([string]$Path)

    $normalized = $Path.Replace('\', '/')
    while ($normalized.StartsWith("./", [StringComparison]::Ordinal)) {
        $normalized = $normalized.Substring(2)
    }

    return $normalized
}

function Test-GitRef {
    param([string]$Ref)

    try {
        [void](Invoke-GitLines @("rev-parse", "--verify", "$Ref^{commit}"))
        return $true
    }
    catch {
        Write-Verbose "Git ref check failed for '$Ref': $($_.Exception.Message)"
        return $false
    }
}

function Resolve-DiffBase {
    param(
        [string]$RequestedBase,
        [string]$RequestedHead
    )

    if (![string]::IsNullOrWhiteSpace($RequestedBase)) {
        if (Test-GitRef $RequestedBase) {
            return $RequestedBase
        }

        throw "BaseRef '$RequestedBase' is not a valid git commit or ref."
    }

    $candidateBases = [System.Collections.Generic.List[string]]::new()
    if (![string]::IsNullOrWhiteSpace($env:GITHUB_BASE_REF)) {
        [void]$candidateBases.Add("origin/$env:GITHUB_BASE_REF")
        [void]$candidateBases.Add($env:GITHUB_BASE_REF)
    }

    foreach ($candidate in @("origin/main", "main", "origin/master", "master")) {
        [void]$candidateBases.Add($candidate)
    }

    foreach ($candidate in $candidateBases | Select-Object -Unique) {
        if (Test-GitRef $candidate) {
            $mergeBase = Invoke-GitLines @("merge-base", $candidate, $RequestedHead) | Select-Object -First 1
            if (![string]::IsNullOrWhiteSpace($mergeBase)) {
                return $mergeBase
            }
        }
    }

    if (Test-GitRef "HEAD~1") {
        return "HEAD~1"
    }

    return ""
}

function Add-Issue {
    param(
        [string]$Level,
        [string]$RuleId,
        [string]$Path,
        [string]$Message
    )

    $script:issues.Add([pscustomobject]@{
        level = $Level
        ruleId = $RuleId
        path = $Path
        message = $Message
    })

    $color = if ($Level -eq "FAIL") { "Red" } else { "Yellow" }
    Write-Host "[$Level][$RuleId] $Path - $Message" -ForegroundColor $color
}

function Get-ChangedFiles {
    param(
        [string]$DiffBase,
        [string]$DiffHead
    )

    $files = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)

    if (![string]::IsNullOrWhiteSpace($DiffBase)) {
        foreach ($file in Invoke-GitLinesOrEmpty @("diff", "--name-only", "--diff-filter=ACMRT", $DiffBase, $DiffHead)) {
            if (![string]::IsNullOrWhiteSpace($file) -and $file -notmatch '^warning:') {
                [void]$files.Add((Normalize-RepoPath $file))
            }
        }
    }

    foreach ($args in @(
        @("diff", "--name-only", "--diff-filter=ACMRT"),
        @("diff", "--cached", "--name-only", "--diff-filter=ACMRT"),
        @("ls-files", "--others", "--exclude-standard")
    )) {
        foreach ($file in Invoke-GitLinesOrEmpty $args) {
            if (![string]::IsNullOrWhiteSpace($file) -and $file -notmatch '^warning:') {
                [void]$files.Add((Normalize-RepoPath $file))
            }
        }
    }

    return @($files | Sort-Object)
}

function Get-AddedLinesForFile {
    param(
        [string]$Path,
        [string]$DiffBase,
        [string]$DiffHead
    )

    $diffLines = [System.Collections.Generic.List[string]]::new()
    if (![string]::IsNullOrWhiteSpace($DiffBase)) {
        foreach ($line in Invoke-GitLinesOrEmpty @("diff", "--unified=5", "--no-ext-diff", $DiffBase, $DiffHead, "--", $Path)) {
            [void]$diffLines.Add($line)
        }
    }

    foreach ($args in @(
        @("diff", "--unified=5", "--no-ext-diff", "--", $Path),
        @("diff", "--cached", "--unified=5", "--no-ext-diff", "--", $Path)
    )) {
        foreach ($line in Invoke-GitLinesOrEmpty $args) {
            [void]$diffLines.Add($line)
        }
    }

    $added = [System.Collections.Generic.List[object]]::new()
    foreach ($line in $diffLines) {
        if ($line -match '^warning:') {
            continue
        }

        if ($line.StartsWith("+") -and !$line.StartsWith("+++")) {
            [void]$added.Add([pscustomobject]@{
                path = $Path
                text = $line.Substring(1)
            })
        }
    }

    $fullPath = Join-Path $repoRoot $Path
    $isUntracked = (Invoke-GitLinesOrEmpty @("ls-files", "--others", "--exclude-standard", "--", $Path)).Count -gt 0
    if ($added.Count -eq 0 -and $isUntracked -and (Test-Path -LiteralPath $fullPath -PathType Leaf)) {
        foreach ($line in Get-Content -LiteralPath $fullPath) {
            [void]$added.Add([pscustomobject]@{
                path = $Path
                text = $line
            })
        }
    }

    return @($added)
}

function Test-HasUiEvidence {
    param([string[]]$ChangedFiles)

    return @($ChangedFiles | Where-Object {
        $_ -match '^(AOI_Monitor\.UiTests/|Docs/|DESIGN\.md$|Tools/quality-gates/hmi_layout_approved_exceptions\.json$|\.github/pull_request_template\.md$)'
    }).Count -gt 0
}

function Test-HasServiceTestEvidence {
    param([string[]]$ChangedFiles)

    return @($ChangedFiles | Where-Object {
        $_ -match '^(AOI_Monitor\.Tests/|AOI_Monitor\.UiTests/)' -and $_ -match '\.cs$'
    }).Count -gt 0
}

Write-Host "AOI Monitor PR quality gate"
Write-Host "Repository: $repoRoot"

$diffBase = Resolve-DiffBase $BaseRef $HeadRef
$changedFiles = @(Get-ChangedFiles $diffBase $HeadRef)
$script:issues = [System.Collections.Generic.List[object]]::new()

if ([string]::IsNullOrWhiteSpace($diffBase)) {
    Write-Host "Diff base: working tree only"
}
else {
    Write-Host "Diff base: $diffBase"
}

Write-Host "Changed files: $($changedFiles.Count)"

$designContractPath = Join-Path $repoRoot "DESIGN.md"
if (!(Test-Path -LiteralPath $designContractPath -PathType Leaf)) {
    Add-Issue "FAIL" "PR-DESIGN-001" "DESIGN.md" "Root design contract is required."
}
else {
    $designContractText = Get-Content -LiteralPath $designContractPath -Raw
    $requiredDesignClauses = @(
        "1920x1080",
        "14 pt",
        "120x40",
        "ScrollViewer",
        "UI-thread",
        "simulated",
        "not formally certified"
    )

    foreach ($clause in $requiredDesignClauses) {
        if ($designContractText -notmatch [regex]::Escape($clause)) {
            Add-Issue "FAIL" "PR-DESIGN-001" "DESIGN.md" "Design contract is missing required clause '$clause'."
        }
    }
}

$uiChanged = @($changedFiles | Where-Object {
    $_ -match '(?i)(\.xaml$|\.xaml\.cs$|^AOI_Monitor/(Views|Controls|Styles)/|^AOI_Monitor/MainWindow\.xaml(\.cs)?$|^AOI_Monitor/App\.xaml$)'
})

if ($uiChanged.Count -gt 0 -and !(Test-HasUiEvidence $changedFiles)) {
    Add-Issue "WARN" "PR-HMI-001" ($uiChanged -join ", ") "UI/XAML changed without a UI test, approved layout exception, or documentation update."
}

$serviceChanged = @($changedFiles | Where-Object {
    $_ -match '(?i)^AOI_Monitor/(Services|Data|Models)/.+\.cs$'
})

if ($serviceChanged.Count -gt 0 -and !(Test-HasServiceTestEvidence $changedFiles)) {
    Add-Issue "WARN" "PR-SVC-001" ($serviceChanged -join ", ") "Services, data, or models changed without a corresponding test project update."
}

$addedLines = [System.Collections.Generic.List[object]]::new()
foreach ($file in $changedFiles) {
    $fullPath = Join-Path $repoRoot $file
    if (!(Test-Path -LiteralPath $fullPath -PathType Leaf)) {
        continue
    }

    foreach ($line in Get-AddedLinesForFile $file $diffBase $HeadRef) {
        [void]$addedLines.Add($line)
    }
}

$productionClaimPattern = '(?i)\bproduction[- ]ready\b|\breal[- ]hardware[- ]ready\b|\bfactory[- ]ready\b|\bclient[- ]ready\b'
$simulationPattern = '(?i)\bmock\b|\bsimulat(?:ed|ion|or)\b|\bstub\b|\bfake\b|\bnot[- ]validated\b'
$negativeInstructionPattern = '(?i)\b(do not|must not|cannot|never|avoid|forbidden|prohibited|not claim|do not claim|do not use)\b'

$linesByPath = $addedLines | Group-Object -Property path
foreach ($group in $linesByPath) {
    if ($group.Name -eq "Scripts/check-pr-quality.ps1") {
        continue
    }

    $lines = @($group.Group)
    for ($index = 0; $index -lt $lines.Count; $index++) {
        if ($lines[$index].text -notmatch $productionClaimPattern) {
            continue
        }

        $start = [Math]::Max(0, $index - 5)
        $end = [Math]::Min($lines.Count - 1, $index + 5)
        $context = ($lines[$start..$end].text -join " ")
        if ($context -match $negativeInstructionPattern) {
            continue
        }

        if ($context -match $simulationPattern) {
            Add-Issue "FAIL" "PR-CLAIM-001" $group.Name "Release readiness wording appears near mock/simulation/not-validated context."
        }
    }
}

foreach ($group in $linesByPath) {
    if ($group.Name -notmatch '(?i)\.(cs|ps1)$') {
        continue
    }

    $lines = @($group.Group)
    for ($index = 0; $index -lt $lines.Count; $index++) {
        $line = $lines[$index].text
        if ($line -match '(?i)\b(OperationCanceledException|TaskCanceledException)\b') {
            continue
        }

        if ($line -match '(?i)\bcatch\s*(\([^)]*\))?\s*\{\s*\}') {
            Add-Issue "FAIL" "PR-CATCH-001" $group.Name "Empty catch block was added."
            continue
        }

        if ($line -match '(?i)\bcatch\s*(\([^)]*\))?\s*$' -and
            $index + 2 -lt $lines.Count -and
            $lines[$index + 1].text -match '^\s*\{\s*$' -and
            $lines[$index + 2].text -match '^\s*\}\s*$') {
            Add-Issue "FAIL" "PR-CATCH-001" $group.Name "Empty catch block was added."
        }
    }
}

foreach ($group in $linesByPath) {
    if ($group.Name -notmatch '(?i)\.xaml$') {
        continue
    }

    foreach ($line in @($group.Group)) {
        if ($line.text -match 'FontSize\s*=\s*"(?<size>[0-9]+(?:\.[0-9]+)?)"' -or
            $line.text -match 'Property\s*=\s*"FontSize"\s+Value\s*=\s*"(?<size>[0-9]+(?:\.[0-9]+)?)"') {
            $size = [double]::Parse($Matches["size"], [Globalization.CultureInfo]::InvariantCulture)
            if ($size -lt 14) {
                Add-Issue "FAIL" "PR-HMI-FONT-001" $group.Name "XAML adds explicit FontSize $size below the 14 pt operator baseline."
            }
        }

        if ($line.text -match 'MinHeight\s*=\s*"(?<height>[0-9]+(?:\.[0-9]+)?)"' -or
            $line.text -match 'Property\s*=\s*"MinHeight"\s+Value\s*=\s*"(?<height>[0-9]+(?:\.[0-9]+)?)"') {
            $height = [double]::Parse($Matches["height"], [Globalization.CultureInfo]::InvariantCulture)
            if ($height -gt 0 -and $height -lt 34) {
                Add-Issue "FAIL" "PR-HMI-SIZE-001" $group.Name "XAML adds MinHeight $height below the compact HMI control baseline."
            }
        }
    }
}

$secretPattern = '(?i)\b(password|passwd|api[_-]?key|access[_-]?key|client[_-]?secret|secret|token|connectionstring)\b\s*[:=]\s*["'']([^"'']{8,})["'']'
$allowedSecretEvidencePattern = '(?i)\b(example|sample|placeholder|dummy|fake|test|redacted|masked|xxxx|your_|<[^>]+>)\b'
foreach ($line in $addedLines) {
    if ($line.path -eq "Scripts/check-pr-quality.ps1") {
        continue
    }

    if ($line.path -match '(?i)\.Tests?/' -or $line.path -match '(?i)^AOI_Monitor\.UiTests/') {
        continue
    }

    if ($line.text -match $secretPattern -and $line.text -notmatch $allowedSecretEvidencePattern) {
        Add-Issue "FAIL" "PR-SEC-001" $line.path "Possible hard-coded credential or secret literal was added."
    }
}

$failureCount = @($script:issues | Where-Object { $_.level -eq "FAIL" }).Count
$warningCount = @($script:issues | Where-Object { $_.level -eq "WARN" }).Count
$status = if ($failureCount -gt 0) {
    "FAIL"
}
elseif ($TreatWarningsAsErrors -and $warningCount -gt 0) {
    "FAIL"
}
elseif ($warningCount -gt 0) {
    "WARN"
}
else {
    "PASS"
}

$report = [pscustomobject]@{
    generatedAtUtc = [DateTimeOffset]::UtcNow.ToString("O")
    status = $status
    treatWarningsAsErrors = [bool]$TreatWarningsAsErrors
    baseRef = $diffBase
    headRef = $HeadRef
    changedFileCount = $changedFiles.Count
    changedFiles = $changedFiles
    checks = @(
        [pscustomobject]@{
            id = "PR-HMI-001"
            description = "UI/XAML changes require UI test, layout exception, or documentation evidence."
            result = if ($uiChanged.Count -gt 0 -and !(Test-HasUiEvidence $changedFiles)) { "WARN" } else { "PASS" }
        },
        [pscustomobject]@{
            id = "PR-SVC-001"
            description = "Service/data/model changes require test evidence."
            result = if ($serviceChanged.Count -gt 0 -and !(Test-HasServiceTestEvidence $changedFiles)) { "WARN" } else { "PASS" }
        },
        [pscustomobject]@{
            id = "PR-DESIGN-001"
            description = "Root design contract must exist and retain the core HMI constraints."
            result = if (@($script:issues | Where-Object { $_.ruleId -eq "PR-DESIGN-001" }).Count -gt 0) { "FAIL" } else { "PASS" }
        },
        [pscustomobject]@{
            id = "PR-CLAIM-001"
            description = "Release readiness wording must not be added near mock, simulated, or not-validated context."
            result = if (@($script:issues | Where-Object { $_.ruleId -eq "PR-CLAIM-001" }).Count -gt 0) { "FAIL" } else { "PASS" }
        },
        [pscustomobject]@{
            id = "PR-CATCH-001"
            description = "Empty catch blocks are forbidden."
            result = if (@($script:issues | Where-Object { $_.ruleId -like "PR-CATCH-*" }).Count -gt 0) { "FAIL" } else { "PASS" }
        },
        [pscustomobject]@{
            id = "PR-HMI-FONT-001"
            description = "New XAML must not add explicit FontSize values below the 14 pt operator baseline."
            result = if (@($script:issues | Where-Object { $_.ruleId -eq "PR-HMI-FONT-001" }).Count -gt 0) { "FAIL" } else { "PASS" }
        },
        [pscustomobject]@{
            id = "PR-HMI-SIZE-001"
            description = "New XAML must not add tiny explicit MinHeight values for HMI controls."
            result = if (@($script:issues | Where-Object { $_.ruleId -eq "PR-HMI-SIZE-001" }).Count -gt 0) { "FAIL" } else { "PASS" }
        },
        [pscustomobject]@{
            id = "PR-SEC-001"
            description = "Hard-coded credentials and secret literals are forbidden."
            result = if (@($script:issues | Where-Object { $_.ruleId -eq "PR-SEC-001" }).Count -gt 0) { "FAIL" } else { "PASS" }
        }
    )
    issues = @($script:issues)
}

$resolvedReportPath = if ([System.IO.Path]::IsPathRooted($ReportPath)) {
    $ReportPath
}
else {
    Join-Path $repoRoot $ReportPath
}

$reportDirectory = Split-Path -Parent $resolvedReportPath
if (![string]::IsNullOrWhiteSpace($reportDirectory)) {
    New-Item -ItemType Directory -Force -Path $reportDirectory | Out-Null
}

$report | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $resolvedReportPath -Encoding UTF8
Write-Host "PR quality gate report: $resolvedReportPath"

if ($status -eq "FAIL") {
    throw "PR quality gate failed with $failureCount failure(s) and $warningCount warning(s)."
}

if ($status -eq "WARN") {
    Write-Host "PR quality gate completed with $warningCount warning(s)." -ForegroundColor Yellow
}
else {
    Write-Host "PR quality gate passed."
}
