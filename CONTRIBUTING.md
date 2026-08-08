OpenAI/Codex and numerous other coding agents will review your output once you are done.

# Contributing to AOI Monitor

For developers and AI agents: read before any change, any pull request, and any client-demo or release packaging. AOI Monitor is a WPF PCBA AOI review console at Stage 1 prototype maturity with clearly labeled simulation/mock boundaries; every gate and checklist here is standards-aligned, not formal ISO, IEC, ISA, or other third-party certification.

## Prerequisites and Windows dev setup

This WPF app targets `net10.0-windows`; WPF builds only on Windows — native machine only, never cloud/Linux, never WSL (native prompt `PS C:\...>`; WSL `user@machine:~$`). Install the .NET 10 SDK (https://dotnet.microsoft.com/download/dotnet/10.0) and Git for Windows (https://git-scm.com/downloads/win), then clone in native Windows PowerShell: `git clone https://github.com/jdseo921/AOI_PCB_Database.git`, `cd AOI_PCB_Database`.

Claude Code must run locally: desktop app Code tab set to Local, not Remote (Anthropic cloud Linux; cannot build WPF), or the CLI, which always runs where you type `claude`. Install it with `irm https://claude.ai/install.ps1 | iex`, reopen PowerShell so `claude` is on PATH, then run `claude` from the repository folder.

Verify with `ver`, `dotnet --version`, and `dotnet build AOI_PCB_Database.slnx -c Release`: expect Microsoft Windows, 10.x, and a passing build; Linux output or missing `dotnet` means a Remote session.

Self-contained distributable (no SDK needed to run): `dotnet publish AOI_Monitor\AOI_Monitor.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -o publish\AOI_Monitor`, then run `publish\AOI_Monitor\AOI_Monitor.exe` — or `pwsh Scripts/prepare-client-test-package.ps1 -Zip`.

## Local build, test, and quality gates

CI runs the same commands maintainers run locally, from the repository root:

```powershell
dotnet restore AOI_PCB_Database.slnx
dotnet build AOI_PCB_Database.slnx --configuration Release --no-restore
dotnet test AOI_PCB_Database.slnx --configuration Release --no-build
```

Quick application-only build: `dotnet build AOI_Monitor/AOI_Monitor.csproj`.

**Repository hygiene.** Run `pwsh Scripts/check-repo-hygiene.ps1` before committing; it fails on tracked or staged runtime/customer payloads. Do not commit:

- Customer, production, or large demo images, or customer dataset folders.
- Local SQLite databases, WAL, or SHM sidecars.
- `image_vault`, training-set, export, package, or MES payload folders.
- Local settings such as `inspection_model_config.json`, `camera_source_settings.json`, or `storage_root_settings.json`.
- `bin`, `obj`, `.vs`, or generated `Release` output.

Small non-confidential instructions may live in `SampleData/`; keep image payloads outside the repository and import them locally.

**PR gate.** `pwsh Scripts/check-pr-quality.ps1` (run before pushing) writes `TestResults/pr_quality_gate_report.json`; it warns when UI or service changes lack matching evidence and fails on release-readiness overclaims around mock/simulation contexts, silent catches, or likely hard-coded secrets.

**Full quality gates** (required for CI and release): `pwsh Scripts/run-quality-gates.ps1 -Configuration Release -ResultsDirectory TestResults` runs `pwsh Scripts/check-repo-hygiene.ps1`, `dotnet restore AOI_PCB_Database.slnx`, `dotnet build AOI_PCB_Database.slnx --configuration Release`, `dotnet test AOI_PCB_Database.slnx --configuration Release`, the WPF HMI layout audit, the UI navigation performance smoke test, export verification tests, and publish/package validation.

**Publish.** `pwsh Scripts/publish.ps1` (local Windows x64 PoC package), `pwsh Scripts/publish.ps1 -SelfContained`, or `pwsh Scripts/publish.ps1 -ValidationOnly` (no writes to the repository `Release/` folder). CI uses `-ValidationOnly -NoRestore` with a temp output folder and uploads the package only on successful `main` pushes; with `-NoRestore` locally, restore first: `dotnet restore AOI_Monitor/AOI_Monitor.csproj --runtime win-x64`, then `pwsh Scripts/publish.ps1 -ValidationOnly -NoRestore`.

Client-facing packages must use `pwsh Scripts/publish.ps1 -Configuration Release -ClientDemoGate`, which blocks packaging while required gates fail (incomplete HMI layout, crash/reliability, alarm, export, test, or packaging evidence).

## CI and branch protection

`.github/workflows/dotnet-ci.yml` (`push`/`pull_request`, `windows-latest`, .NET SDK `10.0.x`) runs: solution restore, the `AOI_Monitor.Tools` Release build, `Scripts/check-pr-quality.ps1`, `Scripts/check-code-quality.ps1`, the full quality gates, tiny-image Stage 1 dataset/manifest generation under `TestResults/simulation-dry-run`, the smokes below, and artifact uploads (test/result/audit files; simulation and image-learning evidence named with synthetic/non-acceptance wording; the package only on successful `main` pushes). The quality-gate step is the authoritative CI health check; the milestone smokes are supporting workflow evidence, never a substitute for customer data, model acceptance, or real hardware acceptance.

**Branch protection.** `main` requires: pull request review; passing status checks including the `.NET CI / Build, Test, Package` workflow; branches up to date before merge; force pushes and branch deletion blocked; bypass restricted to repository administrators only.

**Required reports.** Every CI run uploads `TestResults/hmi_layout_audit.html`, `TestResults/hmi_layout_audit.json`, `TestResults/ui_navigation_performance.json`, `TestResults/industrial_quality_gate_report.json`, and `TestResults/**/*.trx`. On failure, read `industrial_quality_gate_report.json` first (each gate step, status, duration, message, artifact path).

**Client demo readiness gates.** `ClientDemoReadinessGateService` (application level) requires: repository hygiene, Release build, Release tests, UI layout audit, and export verification PASS; navigation performance PASS or WARN; no crash reports in the latest session; no active Critical alarms; no open Critical/High pilot issues; a Stage 1 validation package for the Stage 1 profile; an explicit warning when real hardware is not validated. Critical failures block client packaging; navigation-performance and real-hardware-not-validated warnings must stay clearly visible in the gate report and client-facing evidence.

**Milestone evidence CLI smoke (simulation-only).** CI exercises the milestone evidence commands with generated tiny PNG files only — intentionally simulation-only. It builds the tools (`dotnet build AOI_Monitor.Tools/AOI_Monitor.Tools.csproj --configuration Release --no-restore`), sets `$env:AOI_MONITOR_STORAGE_ROOT = "TestResults/simulation-dry-run/runtime-storage-not-acceptance"`, then via `dotnet run --project AOI_Monitor.Tools/AOI_Monitor.Tools.csproj --configuration Release --no-build --` runs:

- `stage1-exit --dataset TestResults/simulation-dry-run/stage1-sample-dataset --manifest TestResults/simulation-dry-run/stage1-sample-dataset/customer_validation_manifest.csv --output TestResults/simulation-dry-run/stage1-evidence-package --operator ci-simulation-dry-run --allow-simulation`
- `stage2-camera-pilot --output TestResults/simulation-dry-run/stage2-camera-pilot-package --operator ci-simulation-dry-run --allow-simulation`

The dataset is tiny synthetic PNG bytes plus a synthetic manifest: not customer data, no production ONNX model acceptance gate, no vendor camera SDK load, no real lighting-synchronization or 3D-acquisition validation. `AOI_MONITOR_STORAGE_ROOT` only isolates the CLI's SQLite/runtime state; never upload that storage as acceptance evidence — only the exported dry-run packages are published. The `simulation-dry-run` artifact names (`stage1-simulation-dry-run-evidence-package`, `stage2-camera-pilot-simulation-dry-run-evidence-package`) are deliberate: the packages prove evidence-package generation, export verification, and Stage 2 aggregation plumbing still execute, and must not be interpreted as customer dataset acceptance, production model readiness, real camera readiness, lighting readiness, 3D readiness, or factory acceptance.

**Image-only learning CI smoke (synthetic).** After the authoritative quality gates pass, CI runs (same `dotnet run` form) `client-image-learning-demo --synthetic --output TestResults/image-learning-demo --operator ci-image-learning --false-call-target 0.05`. It builds a synthetic image-only learning project under `TestResults` (image-group import, Learned PCB Visual Model v1 training, false-call calibration, inspection, heatmap/overlay export, `visual_learning_report.html`) and uploads `image-learning-demo-report-synthetic-not-customer-acceptance` and `image-learning-demo-overlays-synthetic-not-customer-acceptance`, each with a README: synthetic only, not customer acceptance, not production model certification. The smoke proves the workflow executes.

## Quality checklist

One checklist merges the former contributor and industrial checklists. It supports ISO 9241-style HMI principles, ISO/IEC 25010-style software-quality categories, and IEC 62682 / ISA-18.2-style alarm discipline, but it is not formal ISO, IEC, or ISA certification. The table is the enforceable baseline for development, CI, release packaging, and client-demo readiness; the bullets add what the table and `AGENTS.md` do not.

| ID | Requirement | Evidence required | Automated check | Manual check | Blocking level |
| --- | --- | --- | --- | --- | --- |
| HMI-001 | Operator screens support minimum 1920x1080 resolution without clipping critical verdicts, alarms, or actions. | Layout stress report or UI regression evidence at 1920x1080. | Yes | Yes | Release Blocker |
| HMI-002 | Operator-facing text is at least 14 pt or equivalent readable WPF size. | HMI style audit, UI screenshots, or layout stress evidence. | Partial | Yes | Release Blocker |
| HMI-003 | Primary action buttons are at least 120x40 px. | XAML/style audit and screenshots for primary pages. | Partial | Yes | Warning |
| HMI-004 | Screens remain usable at 100%, 125%, 150%, and 200% Windows DPI scaling. | DPI/layout stress report with screenshots or notes. | Partial | Yes | Release Blocker |
| HMI-005 | Critical alarms, warnings, verdicts, and station status are not hidden in scroll-only areas. | Operator walkthrough evidence and alarm screenshots. | No | Yes | Release Blocker |
| COLOR-001 | Green, red, and yellow/amber status colors are used consistently for OK, fail/alarm, and warning/review. | HMI style guide and page screenshots. | Partial | Yes | Warning |
| COLOR-002 | Status is not conveyed by color alone. | Screenshots showing text/icon/severity labels. | No | Yes | Warning |
| ALARM-001 | Alarms and warnings are readable, prioritized, timestamped, and recoverable. | Alarm/event log export and UI screenshots. | Partial | Yes | Release Blocker |
| ALARM-002 | Alarm text is not clipped, hidden, or buried behind modal windows. | Layout stress and operator review evidence. | Partial | Yes | Release Blocker |
| PERF-001 | Inspection visualization targets 1 second or less from input to operator-visible result. | Benchmark or latency report with p95 and over-1-second count. | Yes | Yes | Release Blocker |
| PERF-002 | Long-running work does not freeze the HMI. | UI navigation soak/stability report. | Yes | Yes | Warning |
| REL-001 | Current evidence set has no uninvestigated crash report. | Crash report summary and gate output. | Yes | No | Release Blocker |
| REL-002 | PoC/client claims of extended operation require 8-hour stable operation evidence. | Soak report with duration, cycle count, and crash count. | Partial | Yes | Release Blocker |
| REL-003 | Failed hardware, MES, export, and database operations produce recoverable messages. | Error-boundary test evidence and support bundle. | Partial | Yes | Warning |
| SEC-001 | Production-relevant actions record operator, role, station, timestamp, and action category. | Audit export verification and sample audit records. | Yes | Yes | Release Blocker |
| SEC-002 | Secrets are redacted from logs, exports, support bundles, and screenshots. | Secret handling tests and reviewed artifacts. | Yes | Yes | Release Blocker |
| SEC-003 | Administrative setup, model approval, recipe changes, production confirmation, and waivers are role protected. | Authorization tests and operator walkthrough. | Yes | Yes | Release Blocker |
| MAINT-001 | Gate rules are documented, machine-readable, and covered by tests. | Baseline docs, checklist, JSON gate config, and unit tests. | Yes | Yes | Release Blocker |
| MAINT-002 | New code follows existing nullable, service, model, and test conventions. | Code review and build/test evidence. | Yes | Yes | Warning |
| EXPORT-001 | CSV, PNG, PDF, JSON, HTML, TXT, and package exports used as evidence are verified. | Export verification records with checksum and format checks. | Yes | Yes | Release Blocker |
| EXPORT-002 | Client packages include manifest evidence matching actual package files. | Package manifest verification report. | Yes | Yes | Release Blocker |
| HW-001 | Camera evidence states simulated vs real hardware and includes acceptance status. | Camera acceptance run. | Yes | Yes | Release Blocker |
| HW-002 | Lighting evidence states simulated vs real hardware and includes command/frame timing. | Lighting acceptance run. | Yes | Yes | Release Blocker |
| HW-003 | 3D profile evidence states simulated vs real hardware and includes acquisition quality. | Profile 3D acceptance run. | Yes | Yes | Warning |
| HW-004 | Robot and safety evidence includes cycle timing, invalid transition, emergency stop, and safety fault behavior. | Robot acceptance run. | Yes | Yes | Release Blocker |
| HW-005 | Simulation evidence cannot satisfy real hardware readiness gates. | Gate report showing real-hardware evidence only. | Yes | Yes | Release Blocker |
| MES-001 | MES/traceability evidence includes payload mapping, queue/retry behavior, endpoint mode, redaction, and signoff status. | Traceability signoff and MES queue/export evidence. | Yes | Yes | Release Blocker |
| MES-002 | Central sync evidence includes queued, sent, failed, and pending state plus redaction behavior. | Central sync readiness and queue report. | Yes | Yes | Warning |
| PKG-001 | Release package includes build, test, publish validation, gate report, and verified export evidence. | Build/test evidence and release package manifest. | Yes | Yes | Release Blocker |
| PKG-002 | Missing build/test evidence is a warning for early Stage 1 PoC and a release blocker for factory/production profiles. | Industrial gate report for selected profile. | Yes | No | Release Blocker |

The `AGENTS.md` non-negotiables apply to every change. In addition:

- Run the build, tests, and gates above; run the HMI layout audit for any UI, XAML, view model, navigation, alarm panel, or dashboard change, and verify 1920x1080 at 125% DPI.
- Dense pages (settings, factory readiness, model acceptance, dashboards, queues, exports, checklists) need `ScrollViewer`/adaptive layout or a documented approved exception — update `AOI_Monitor.UiTests` or `Tools/quality-gates/hmi_layout_approved_exceptions.json` for intentional layout changes. Operator-critical buttons stay visible and reachable; no fixed-height containers around warning text, model IDs, file paths, alarms, or validation messages unless wrapping or scrolling is guaranteed; alarms stay visible until acknowledged or resolved.
- Expose cancellation and progress for long operations; record workflow, alarm, or crash evidence for recoverable failures.
- Commit no credentials, tokens, keys, connection strings, or private paths; redact secrets and customer-sensitive data from reports, logs, alarms, support bundles, and crash reports.
- Simulated camera, robot, lighting, or MES evidence never satisfies a real hardware readiness gate (HW-005). Document adapter assumptions, required vendor SDKs, deployment profile, station setup, and acceptance-test evidence for hardware/MES changes; client-facing packages must warn clearly when real hardware validation is incomplete.
- Update docs when operator workflow, readiness evidence, package process, hardware/MES behavior, or quality-gate behavior changes; add or update tests for changed service, data-model, export/report, HMI-layout, navigation-performance, alarm, crash-safety, release-gate, authentication, storage, model, or database behavior.

## Binding standard and PR checklist

[AGENTS.md](AGENTS.md) and the engineering standard in `Docs/standard/` (start at [Docs/standard/00_Index.md](Docs/standard/00_Index.md)) bind all work, including AI-agent work. Consult the standard before every change: Change Execution Contract (VOL01 §3), Definition of Done (VOL17 §51), auto-reject list (VOL17 §49), AI-assisted-development controls (VOL17 §48). Report the `AGENTS.md` Definition of Done checks for every meaningful change and complete every section of [.github/pull_request_template.md](.github/pull_request_template.md) on every PR; neither is duplicated here.

## Related documents

- [AGENTS.md](AGENTS.md) — binding project instructions and Definition of Done.
- [Docs/standard/00_Index.md](Docs/standard/00_Index.md) — canonical engineering standard index.
- [.github/pull_request_template.md](.github/pull_request_template.md) — PR checklist, evidence, and risk notes.
- [README.md](README.md), [DESIGN.md](DESIGN.md) — project overview and UI design contract.

Pre-consolidation source text: git history at commit b2c4616.
