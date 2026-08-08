---
name: stage1-gate
description: Run the full Stage-1 release-quality loop for AOI Monitor — Release build, complete unit + UI test suites, repo quality gates, image-learning evidence smoke test, and (optionally) the self-contained publish. Use before any push, build handoff, or release claim, or when asked to "verify everything" / "run the gate".
---

OpenAI/Codex and numerous other coding agents will review your output once you are done.

# Stage-1 Quality Gate

Run every step **in order**. Do not skip a failing step — diagnose and fix, then rerun that step. Report a final table of step → result.

## Steps

1. **Restore + Release build (whole solution)**
   ```powershell
   dotnet build AOI_PCB_Database.slnx -c Release
   ```
   Must end `0 Error(s)`. Release treats key nullable/security warnings as errors.

2. **Full test suites** (unit incl. localization parity + UI incl. HMI layout audit)
   ```powershell
   dotnet test AOI_PCB_Database.slnx -c Release --no-build --logger "console;verbosity=minimal"
   ```
   Expect both `AOI_Monitor.Tests` and `AOI_Monitor.UiTests` to pass with 0 failures.
   UI tests are STA/WPF: they must run on Windows, never in parallel with another test run.

3. **Repo quality gates** (hygiene, PR quality, code quality, package validation)
   ```powershell
   pwsh Scripts/run-quality-gates.ps1 -Configuration Release -ResultsDirectory TestResults
   ```
   If this is too slow for the change at hand, minimum acceptable subset:
   `pwsh Scripts/check-repo-hygiene.ps1` + `pwsh Scripts/check-pr-quality.ps1`.

4. **Image-learning evidence smoke (zero-data synthetic run)**
   ```powershell
   dotnet run --project AOI_Monitor.Tools -c Release -- client-image-learning-demo --synthetic --output "$env:TEMP\stage1_gate_demo" --operator gate-check
   ```
   Must exit 0 and print a summary (images learned, false calls before/after,
   recommended threshold). Confirm `visual_learning_report.html` exists in the output.

5. **Publish + launch smoke (only for build handoffs)**
   ```powershell
   dotnet publish AOI_Monitor\AOI_Monitor.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none -o <output-folder>
   ```
   Then launch the exe, wait ≥20 s (single-file extraction is slow on first run;
   OneDrive-synced folders may interfere right after publish — wait ~20 s after
   publishing before launching), verify the process is alive with window
   "PCBA AOI Review Console", then close it.

## Known pitfalls

- `AOI_Monitor/packages.lock.json` gets touched by publish — `git checkout --` it before committing if it is the only change.
- Never claim the gate passed if any step was skipped; list skipped steps explicitly.
- EN/KO parity: if any user-facing XAML literal was added/renamed on Monitor/Review/Recipe/AIModelTest views, `LocalizationParityTests` will fail until `UiPreferencesService.KoreanText` gets the matching key. Fix the dictionary, not the test.
