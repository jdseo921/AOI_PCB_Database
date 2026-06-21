[CmdletBinding()]
param(
    [long]$MaxImageBytes = 10485760
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

    return @($output | ForEach-Object { "$_" } | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
}

function Normalize-RepoPath {
    param([string]$Path)
    return $Path.Replace('\', '/').TrimStart('./')
}

function Add-Issue {
    param(
        [System.Collections.Generic.List[string]]$Issues,
        [string]$Scope,
        [string]$Path,
        [string]$Reason
    )

    $Issues.Add("[$Scope] $Path - $Reason")
}

Write-Host "AOI Monitor repository hygiene check"
Write-Host "Repository: $repoRoot"
Write-Host "Max tracked/staged/untracked image size: $([Math]::Round($MaxImageBytes / 1MB, 2)) MB"

$trackedFiles = Invoke-GitLines @("ls-files")
$stagedFiles = Invoke-GitLines @("diff", "--cached", "--name-only", "--diff-filter=ACMRT")
$untrackedFiles = Invoke-GitLines @("ls-files", "--others", "--exclude-standard")

$forbiddenPatterns = @(
    @{ Reason = "build output"; Pattern = '(^|/)(bin|obj)/' },
    @{ Reason = "Visual Studio workspace state"; Pattern = '(^|/)\.vs/' },
    @{ Reason = "generated release/package output"; Pattern = '(^|/)(Release|publish|artifacts)/' },
    @{ Reason = "generated template plugin binary"; Pattern = '^Templates/.+\.(dll|exe|pdb|deps\.json|runtimeconfig\.json)$|^Templates/.+/(plugin|plugins|package)/' },
    @{ Reason = "SQLite database or sidecar"; Pattern = '\.(sqlite|sqlite3|db|db3)(-(wal|shm))?$' },
    @{ Reason = "generated export artifact"; Pattern = '(^|/)exports/' },
    @{ Reason = "image vault runtime folder"; Pattern = '(^|/)image_vault/' },
    @{ Reason = "training-set runtime folder"; Pattern = '(^|/)(training_set|training_sets)/' },
    @{ Reason = "model registry runtime storage"; Pattern = '(^|/)model_registry/' },
    @{ Reason = "customer/private dataset folder"; Pattern = '(^|/)(CustomerData|customer_data|customer_images|customer-datasets|customer_datasets|large_datasets)(/|$)|(^|/)datasets/customer/' },
    @{ Reason = "local AOI settings file"; Pattern = '(^|/)(first_run_settings|storage_root_settings|inspection_model_config|camera_source_settings|lighting_settings|mes_integration_settings|local\.settings|appsettings\.Development)\.json$' },
    @{ Reason = "SampleData image/archive payload"; Pattern = '^SampleData/.+\.(png|jpe?g|bmp|tiff?|webp|gif|zip|7z|rar|tar|gz)$' }
)

$issues = [System.Collections.Generic.List[string]]::new()

foreach ($entry in @(
    @{ Scope = "tracked"; Files = $trackedFiles },
    @{ Scope = "staged"; Files = $stagedFiles },
    @{ Scope = "untracked"; Files = $untrackedFiles }
)) {
    foreach ($file in $entry.Files) {
        $path = Normalize-RepoPath $file
        foreach ($rule in $forbiddenPatterns) {
            if ($path -match $rule.Pattern) {
                Add-Issue $issues $entry.Scope $path $rule.Reason
            }
        }
    }
}

$templateProjectFiles = @($trackedFiles + $stagedFiles + $untrackedFiles |
    Sort-Object -Unique |
    ForEach-Object { Normalize-RepoPath $_ } |
    Where-Object { $_ -match '^Templates/.+\.csproj$' })

foreach ($path in $templateProjectFiles) {
    $fullPath = Join-Path $repoRoot $path
    if (!(Test-Path -LiteralPath $fullPath -PathType Leaf)) {
        continue
    }

    $projectXml = Get-Content -LiteralPath $fullPath -Raw
    if ($projectXml -match '<PackageReference\b') {
        Add-Issue $issues "templates" $path "template projects must not add vendor SDK or NuGet package dependencies"
    }
}

$imageExtensions = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($extension in @(".png", ".jpg", ".jpeg", ".bmp", ".tif", ".tiff", ".webp", ".gif")) {
    [void]$imageExtensions.Add($extension)
}

foreach ($file in ($trackedFiles + $stagedFiles + $untrackedFiles | Sort-Object -Unique)) {
    $path = Normalize-RepoPath $file
    $extension = [System.IO.Path]::GetExtension($path)
    if (!$imageExtensions.Contains($extension)) {
        continue
    }

    $fullPath = Join-Path $repoRoot $path
    if (!(Test-Path -LiteralPath $fullPath -PathType Leaf)) {
        continue
    }

    $info = Get-Item -LiteralPath $fullPath
    if ($info.Length -gt $MaxImageBytes) {
        Add-Issue $issues "tracked/staged" $path "large image payload ($($info.Length) bytes > $MaxImageBytes bytes)"
    }
}

$markdownFiles = @($trackedFiles + $stagedFiles + $untrackedFiles |
    Sort-Object -Unique |
    ForEach-Object { Normalize-RepoPath $_ } |
    Where-Object { $_ -match '\.md$' })

foreach ($path in $markdownFiles) {
    $fullPath = Join-Path $repoRoot $path
    if (!(Test-Path -LiteralPath $fullPath -PathType Leaf)) {
        continue
    }

    $text = Get-Content -LiteralPath $fullPath -Raw
    $matches = [regex]::Matches($text, '\[[^\]]+\]\((Docs/[^)#?]+\.md)(?:#[^)]+)?\)')
    foreach ($match in $matches) {
        $target = Normalize-RepoPath $match.Groups[1].Value
        $targetPath = Join-Path $repoRoot $target
        if (!(Test-Path -LiteralPath $targetPath -PathType Leaf)) {
            Add-Issue $issues "docs" $path "linked doc does not exist: $target"
        }
    }
}

$forbiddenDocClaims = @(
    'simulated camera is production-ready',
    'simulated cameras are production-ready',
    'camera simulation is production-ready',
    'simulated robot is production-ready',
    'simulated robots are production-ready',
    'robot simulation is production-ready',
    'simulated mes is production-ready',
    'mock mes is production-ready',
    'mes simulation is production-ready'
)

foreach ($path in $markdownFiles) {
    $fullPath = Join-Path $repoRoot $path
    if (!(Test-Path -LiteralPath $fullPath -PathType Leaf)) {
        continue
    }

    $lower = (Get-Content -LiteralPath $fullPath -Raw).ToLowerInvariant()
    foreach ($claim in $forbiddenDocClaims) {
        if ($lower.Contains($claim)) {
            Add-Issue $issues "docs" $path "overclaims simulated integration evidence: '$claim'"
        }
    }
}

if ($issues.Count -gt 0) {
    Write-Host "Repository hygiene check failed:" -ForegroundColor Red
    foreach ($issue in $issues) {
        Write-Host "  $issue" -ForegroundColor Red
    }

    throw "Forbidden runtime/customer/build artifacts were found in tracked, staged, or untracked files."
}

Write-Host "Repository hygiene check passed. No forbidden tracked, staged, or untracked runtime artifacts found."
