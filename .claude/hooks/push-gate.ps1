# Claude Code PreToolUse hook (matcher: Bash|PowerShell): block `git push` when fast quality gates fail.
# Reads the tool-call JSON from stdin; no-ops for anything that is not a git push.
$ErrorActionPreference = 'Stop'
try {
    $payload = [Console]::In.ReadToEnd() | ConvertFrom-Json
    $command = $payload.tool_input.command
} catch {
    exit 0  # unparsable input: never block
}
if ([string]::IsNullOrWhiteSpace($command) -or $command -notmatch 'git\s+push') {
    exit 0
}

$repo = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
Set-Location $repo

$failures = @()

$build = dotnet build AOI_PCB_Database.slnx -c Release -v q --nologo 2>&1
if ($LASTEXITCODE -ne 0) {
    $failures += "Release build FAILED:`n" + (($build | Select-String -Pattern 'error ' | Select-Object -First 6) -join "`n")
}

$hygiene = pwsh -NoProfile -File Scripts/check-repo-hygiene.ps1 2>&1
if ($LASTEXITCODE -ne 0) {
    $failures += "Repo hygiene gate FAILED:`n" + (($hygiene | Select-Object -Last 6) -join "`n")
}

# The CI step that has historically broken silently: formatting + analyzer drift.
$quality = pwsh -NoProfile -File Scripts/check-code-quality.ps1 2>&1
if ($LASTEXITCODE -ne 0) {
    $failures += "Code quality gate FAILED (dotnet format / analyzers):`n" + (($quality | Select-Object -Last 6) -join "`n")
}

if ($failures.Count -gt 0) {
    [Console]::Error.WriteLine("PUSH BLOCKED by quality gate:`n" + ($failures -join "`n---`n") + "`nFix the issues, then push again. Full gate: /stage1-gate")
    exit 2  # exit 2 = blocking: Claude sees stderr and must address it
}
exit 0
