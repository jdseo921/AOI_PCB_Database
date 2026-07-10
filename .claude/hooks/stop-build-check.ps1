# Claude Code Stop hook: block "done" on a broken build, cheaply.
# Exits 0 instantly when no C#/XAML/csproj source differs from the last verified state;
# otherwise runs a quiet Release build and exits 2 (blocking) if it fails.
$ErrorActionPreference = 'Stop'
$repo = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
Set-Location $repo

$marker = Join-Path $PSScriptRoot '.last-verified-build'

# Fingerprint = HEAD hash + hash of the working-tree diff of source files.
$head = git rev-parse HEAD 2>$null
$dirty = git status --porcelain -- '*.cs' '*.xaml' '*.csproj' '*.slnx' 'Directory.Build.props' 2>$null | Out-String
$fingerprint = "$head|$([Convert]::ToBase64String([System.Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($dirty))))"

if ((Test-Path $marker) -and ((Get-Content $marker -Raw -ErrorAction SilentlyContinue) -eq $fingerprint)) {
    exit 0  # nothing changed since last verified build
}
if ([string]::IsNullOrWhiteSpace($dirty) -and -not (Test-Path $marker)) {
    # Clean tree, first run: trust CI/HEAD state rather than paying a build on every session start.
    Set-Content -Path $marker -Value $fingerprint -NoNewline
    exit 0
}

$build = dotnet build AOI_PCB_Database.slnx -c Release --no-restore -v q --nologo 2>&1
if ($LASTEXITCODE -ne 0) {
    # Retry once with restore in case the failure is a missing/changed package graph.
    $build = dotnet build AOI_PCB_Database.slnx -c Release -v q --nologo 2>&1
}
if ($LASTEXITCODE -ne 0) {
    $errors = ($build | Select-String -Pattern 'error ' | Select-Object -First 8) -join "`n"
    [Console]::Error.WriteLine("BLOCKED: the Release build is broken. Fix before finishing.`n$errors")
    exit 2  # exit 2 = blocking: Claude sees stderr and must address it
}

Set-Content -Path $marker -Value $fingerprint -NoNewline
exit 0
