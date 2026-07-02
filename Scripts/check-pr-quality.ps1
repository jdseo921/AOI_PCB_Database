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

function Test-IsDocumentationClaimPath {
    param([string]$Path)

    return $Path -match '(?i)(^Docs/|^README\.md$|^DESIGN\.md$|^\.github/.*\.md$|\.md$)'
}

function Test-LineStillExists {
    param(
        [string]$Path,
        [string]$Text
    )

    $fullPath = Join-Path $repoRoot $Path
    if (!(Test-Path -LiteralPath $fullPath -PathType Leaf)) {
        return $false
    }

    return (Get-Content -LiteralPath $fullPath -Raw).Contains($Text, [StringComparison]::Ordinal)
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
$negativeInstructionPattern = '(?i)\b(do not|did not|must not|cannot|never|avoid|forbidden|prohibited|not claim|do not claim|do not use|not use|not be interpreted|not interpreted|not a substitute|not production)\b'
$stage2CompletePattern = '(?i)\bStage\s*2\s+(?:is\s+)?complete(?:d)?\b'
$stage2RealHardwareEvidencePattern = '(?i)\breal[- ]hardware[- ]acceptance\b|\breal[- ]camera\b|\breal[- ]lighting\b|\breal[- ]3D\b|\breal 3D\b|\bvendor camera\b|\bcamera, lighting, and 3D acceptance\b'
$productionReadyPattern = '(?i)\bproduction[- ]ready\b'
$clearProductionReadyPattern = '(?i)\b(is|are|now|fully|validated as|declared|marked|certified)\b.{0,40}\bproduction[- ]ready\b|\bproduction[- ]ready\b.{0,40}\b(release|deployment|status|claim)\b'
$factoryReadinessEvidencePattern = '(?i)\bfactory[- ]readiness[- ]evidence\b|\bfactory readiness (package|report|profile|status|evidence)\b|\bGo/No-Go\b|\breal[- ]hardware[- ]acceptance\b|\baccepted real hardware\b|\bexport verification\b'
$mesIntegratedPattern = '(?i)\bMES[- /]*(?:ERP[- /]*)?integrated\b|\bMES integration complete\b|\bintegrated\s+MES\b'
$mockMesContextPattern = '(?i)\bmock\b|\bmock mode\b|\bmock REST\b|\bMES mock\b|\bsimulation\b|\bnot connected\b|\bonly mock\b'
$realMesEvidencePattern = '(?i)\bMES REST Ready\b|\bproduction MES\b|\breal MES\b|\baccepted factory traceability\b|\btraceability signoff\b|\bpassing traceability\b|\bMES acceptance\b'
$noFalsePositiveClaimPattern = '(?i)\b(no|zero|0)\s+false[- ]positives?\b|\b(no|zero|0)\s+false[- ]calls?\b|\bfalse[- ]positive[- ]free\b|\bfalse[- ]call[- ]free\b'
$detectsAllDefectsPattern = '(?i)\bdetects?\s+(all|every)\s+defects?\b|\ball\s+defects?\s+(are\s+)?detected\b|\bevery\s+defect\s+(is\s+)?detected\b'
$ngValidationEvidencePattern = '(?i)\bNG validation\b|\bknown[- ]bad\b|\bpossible[- ]escape\b|\bpossible escapes\b|\bmissed[- ]defect\b|\bmissed defect rate\b'

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
        if (!(Test-LineStillExists $group.Name $lines[$index].text)) {
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

$docLinesByPath = $addedLines |
    Where-Object { Test-IsDocumentationClaimPath $_.path } |
    Group-Object -Property path

foreach ($group in $docLinesByPath) {
    $lines = @($group.Group)
    for ($index = 0; $index -lt $lines.Count; $index++) {
        $line = $lines[$index].text
        if (!(Test-LineStillExists $group.Name $line)) {
            continue
        }

        $start = [Math]::Max(0, $index - 5)
        $end = [Math]::Min($lines.Count - 1, $index + 5)
        $context = ($lines[$start..$end].text -join " ")

        if ($context -match $negativeInstructionPattern) {
            continue
        }

        if ($line -match $stage2CompletePattern -and $context -notmatch $stage2RealHardwareEvidencePattern) {
            Add-Issue "FAIL" "PR-STAGE2-CLAIM-001" $group.Name "Stage 2 completion wording was added without nearby real camera/lighting/3D hardware acceptance evidence."
            continue
        }

        if ($line -match $productionReadyPattern -and $context -notmatch $factoryReadinessEvidencePattern) {
            Add-Issue "FAIL" "PR-PROD-CLAIM-001" $group.Name "Production-ready wording was added without nearby factory readiness evidence."
        }

        if ($line -match $noFalsePositiveClaimPattern) {
            Add-Issue "FAIL" "PR-FP-CLAIM-001" $group.Name "Absolute no-false-positive or no-false-call wording was added. Report measured false-call rates with OK Validation image counts instead."
        }

        if ($line -match $detectsAllDefectsPattern -and $context -notmatch $ngValidationEvidencePattern) {
            Add-Issue "WARN" "PR-DEFECT-CLAIM-001" $group.Name "Absolute defect-detection wording was added without nearby NG Validation and possible-escape evidence."
        }

        if ($line -match $mesIntegratedPattern -and $context -notmatch $realMesEvidencePattern) {
            if ($context -match $mockMesContextPattern) {
                Add-Issue "FAIL" "PR-MES-CLAIM-001" $group.Name "MES integrated wording appears with mock/not-connected context and no real MES acceptance evidence."
            }
            else {
                Add-Issue "WARN" "PR-MES-CLAIM-001" $group.Name "MES integrated wording may need real MES acceptance or traceability signoff evidence."
            }
        }
    }
}

foreach ($file in @($changedFiles | Where-Object { $_ -match '(?i)\.(cs|ps1)$' } | Sort-Object -Unique)) {
    if ($file -eq "Scripts/check-pr-quality.ps1") {
        continue
    }

    $fullPath = Join-Path $repoRoot $file
    if (!(Test-Path -LiteralPath $fullPath -PathType Leaf)) {
        continue
    }

    $sourceLines = @(Get-Content -LiteralPath $fullPath)
    for ($index = 0; $index -lt $sourceLines.Count; $index++) {
        $line = $sourceLines[$index]
        if ($line -match '(?i)\b(OperationCanceledException|TaskCanceledException)\b') {
            continue
        }

        if ($line -match '(?i)\bcatch\s*(\([^)]*\))?\s*\{\s*\}') {
            Add-Issue "FAIL" "PR-CATCH-001" $file "Empty catch block was added."
            continue
        }

        if ($line -match '(?i)\bcatch\s*(\([^)]*\))?\s*$' -and
            $index + 2 -lt $sourceLines.Count -and
            $sourceLines[$index + 1] -match '^\s*\{\s*$' -and
            $sourceLines[$index + 2] -match '^\s*\}\s*$') {
            Add-Issue "FAIL" "PR-CATCH-001" $file "Empty catch block was added."
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
            id = "PR-STAGE2-CLAIM-001"
            description = "Stage 2 completion wording requires nearby real camera, lighting, and 3D hardware acceptance evidence."
            result = if (@($script:issues | Where-Object { $_.ruleId -eq "PR-STAGE2-CLAIM-001" }).Count -gt 0) { "FAIL" } else { "PASS" }
        },
        [pscustomobject]@{
            id = "PR-PROD-CLAIM-001"
            description = "Production-ready wording requires nearby factory readiness evidence."
            result = if (@($script:issues | Where-Object { $_.ruleId -eq "PR-PROD-CLAIM-001" }).Count -gt 0) { "FAIL" } else { "PASS" }
        },
        [pscustomobject]@{
            id = "PR-FP-CLAIM-001"
            description = "Absolute no-false-positive/no-false-call claims are forbidden; use measured OK Validation evidence."
            result = if (@($script:issues | Where-Object { $_.ruleId -eq "PR-FP-CLAIM-001" }).Count -gt 0) { "FAIL" } else { "PASS" }
        },
        [pscustomobject]@{
            id = "PR-DEFECT-CLAIM-001"
            description = "Absolute defect-detection wording needs NG Validation and possible-escape context."
            result = if (@($script:issues | Where-Object { $_.ruleId -eq "PR-DEFECT-CLAIM-001" }).Count -gt 0) { "WARN" } else { "PASS" }
        },
        [pscustomobject]@{
            id = "PR-MES-CLAIM-001"
            description = "MES integrated wording requires real MES acceptance or traceability signoff evidence, especially near mock-mode context."
            result = if (@($script:issues | Where-Object { $_.ruleId -eq "PR-MES-CLAIM-001" -and $_.level -eq "FAIL" }).Count -gt 0) { "FAIL" } elseif (@($script:issues | Where-Object { $_.ruleId -eq "PR-MES-CLAIM-001" }).Count -gt 0) { "WARN" } else { "PASS" }
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
