# Contributor Quality Checklist

This repository treats industrial HMI behavior and software-quality evidence as release artifacts, not polish. Use this checklist before opening a pull request and again before requesting client-demo or release packaging.

This checklist is standards-aligned and project-specific. It supports ISO 9241-style HMI principles, ISO/IEC 25010-style software-quality categories, and IEC 62682 / ISA-18.2-style alarm discipline, but it is not formal ISO, IEC, or ISA certification.

## Before Opening A PR

- Run `dotnet build AOI_PCB_Database.slnx --configuration Release`.
- Run `dotnet test AOI_PCB_Database.slnx --configuration Release`.
- Run the HMI layout audit when UI, XAML, view model, navigation, alarm panel, or dashboard behavior changed.
- Verify operator-facing UI at 1920x1080 and 125% DPI.
- Confirm dense pages scroll or have a documented approved exception.
- Confirm operator-critical buttons remain visible and reachable.
- Confirm page constructors do not perform database scans, file exports, report generation, model loading, or large image work.
- Confirm expected failures are handled through recoverable messages, alarm events, or crash reports as appropriate.
- Confirm no raw stack traces are shown to operators.
- Confirm no credentials, tokens, keys, connection strings, or private paths are committed.
- Confirm simulation, mock, stub, or not-validated evidence is labeled as such.
- Update documentation when the operator workflow, readiness evidence, package process, hardware/MES behavior, or quality-gate behavior changes.
- Add or update tests for changed services, data models, export/report behavior, HMI layout, navigation performance, alarms, crash safety, or release gates.

## UI And HMI Changes

- Preserve the project baseline: 1920x1080 minimum, 14pt minimum operator-facing text, primary operator buttons at least 120x40 px, high contrast, and green/red/yellow status semantics.
- Avoid fixed-height containers around warning text, model IDs, file paths, alarms, and validation messages unless wrapping or scrolling is guaranteed.
- Use `ScrollViewer` or adaptive layout for dense pages such as settings, factory readiness, model acceptance, dashboards, queues, exports, and checklists.
- Keep warnings and critical alarms readable, prioritized, timestamped, and visible until acknowledged or resolved.
- Update `AOI_Monitor.UiTests` or `Tools/quality-gates/hmi_layout_approved_exceptions.json` when layout behavior intentionally changes.

## Services, Data, And Reliability Changes

- Keep long-running work off the UI thread.
- Expose cancellation and progress for long operations.
- Record workflow, alarm, or crash evidence for recoverable failures.
- Do not add empty or silent `catch` blocks.
- Redact secrets and customer-sensitive data before writing reports, logs, alarms, support bundles, or crash reports.
- Add tests for new quality-gate, export, crash-safety, authentication, storage, model, or database behavior.

## Hardware And MES Changes

- Keep simulation and real hardware evidence separate.
- Do not use simulated camera, robot, lighting, or MES evidence to satisfy a real hardware readiness gate.
- Document adapter assumptions, required vendor SDKs, deployment profile, station setup, and acceptance-test evidence.
- Ensure client-facing packages warn clearly when real hardware validation has not been completed.

## PR Gate Script

Run the script locally before pushing when possible:

```powershell
pwsh Scripts/check-pr-quality.ps1
```

The script writes `TestResults/pr_quality_gate_report.json`. It warns when UI or service changes lack matching evidence, and it fails for clear hazards such as release-readiness overclaims around mock/simulation contexts, silent catches, or likely hard-coded secrets.
