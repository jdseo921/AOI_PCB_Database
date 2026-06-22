# Branch Protection and Quality Gates

This repository uses automated quality gates to prevent client-facing AOI packages from being produced when basic HMI, build, test, crash, export, or packaging evidence is missing or failing.

These gates are standards-aligned for industrial HMI/software quality practice. They are not formal ISO, IEC, ISA, or other third-party certification.

## Required Branch Protection

Protect `main` with these rules:

- Require pull request review before merge.
- Require status checks to pass before merge.
- Require the `.NET CI / Build, Test, Package` workflow.
- Require branches to be up to date before merge.
- Block force pushes.
- Block deletion of the protected branch.
- Restrict bypass permissions to repository administrators only.

## Required Quality Gate Command

CI and release maintainers must run:

```powershell
pwsh Scripts/run-quality-gates.ps1 -Configuration Release -ResultsDirectory TestResults
```

The script runs:

- `pwsh Scripts/check-repo-hygiene.ps1`
- `dotnet restore AOI_PCB_Database.slnx`
- `dotnet build AOI_PCB_Database.slnx --configuration Release`
- `dotnet test AOI_PCB_Database.slnx --configuration Release`
- WPF HMI layout audit
- UI navigation performance smoke test
- export verification tests
- publish/package validation

## Required Reports

The CI workflow uploads these artifacts on every run:

- `TestResults/hmi_layout_audit.html`
- `TestResults/hmi_layout_audit.json`
- `TestResults/ui_navigation_performance.json`
- `TestResults/industrial_quality_gate_report.json`
- `TestResults/**/*.trx`

When a gate fails, inspect `industrial_quality_gate_report.json` first. It lists each gate step, status, duration, message, and artifact path.

## Client Package Enforcement

Client-facing package generation must use:

```powershell
pwsh Scripts/publish.ps1 -Configuration Release -ClientDemoGate
```

With `-ClientDemoGate`, publish is blocked before package generation if required quality gates fail. This prevents accidental delivery when HMI layout, crash/reliability, alarm, export, test, or packaging evidence is incomplete.

## Client Demo Readiness Gates

The application-level `ClientDemoReadinessGateService` evaluates:

- repository hygiene PASS
- Release build PASS
- Release tests PASS
- UI layout audit PASS
- navigation performance PASS or WARN
- no crash reports in the latest session
- no active Critical alarms
- no open Critical/High pilot issues
- export verification PASS
- Stage 1 validation package exists for the Stage 1 profile
- explicit warning when real hardware is not validated

Critical failures block client package generation. Navigation performance warnings and real-hardware-not-validated warnings are allowed only when clearly visible in the gate report and client-facing evidence.
