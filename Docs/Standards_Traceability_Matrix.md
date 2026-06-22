# Standards Traceability Matrix

This repository maintains a standards traceability matrix so industrial HMI and software-quality expectations are visible before client demo, pilot, release packaging, or factory-readiness review.

The matrix is standards-aligned project evidence. It is not formal ISO, IEC, ISA, or third-party certification.

## Scope

The matrix maps these expectation sources to concrete evidence:

- Project specification: 1920x1080 minimum resolution, 14 pt operator text, 120x40 px primary actions, high-contrast status colors, 1-second visualization target, 8-hour factory PoC stability, verified CSV/PNG/PDF exports, and staged camera/robot/MES integration.
- ISO 9241-style HMI principles: task suitability, self-descriptiveness, controllability, expectation conformity, learnability, and user-error robustness.
- ISO/IEC 25010-style quality categories: functional suitability, performance, compatibility, usability, reliability, security, maintainability, and portability.
- IEC 62682 / ISA-18.2-style alarm/event expectations: readable, prioritized, timestamped, recoverable, acknowledged, and exportable alarms/warnings.

## Evidence

Evidence may come from:

- automated test results and TRX files,
- `TestResults/hmi_layout_audit.json` and `.html`,
- `TestResults/ui_navigation_performance.json`,
- industrial quality-gate and code-quality reports,
- crash and recoverable-error reports,
- audit logs and workflow events,
- export verification records,
- readiness packages and acceptance reports,
- staged hardware, MES, and central-sync evidence.

Missing evidence is not hidden. Each row records `Satisfied`, `Partial`, `Missing`, or `NotApplicable`, with an `Info`, `Warning`, or `Release Blocker` blocking level.

## Runtime UI

Open `Factory Readiness > Standards & Quality Checklist` in the Reports view to inspect the current matrix. The dashboard shows:

- source standard or project source,
- principle or requirement,
- project requirement ID,
- evidence type and path,
- current status,
- blocking level,
- notes explaining scope, gaps, and simulation boundaries.

The dashboard exports HTML, PDF, and JSON.

## CI And Packaging

`Scripts/run-quality-gates.ps1` exports:

- `TestResults/standards_traceability_matrix.json`,
- `TestResults/standards_traceability_matrix.html`,
- `TestResults/standards_traceability_matrix.pdf`.

Factory readiness packages include the same matrix files and list them in `package_manifest.json` with checksums. `ClientDemoReadinessGateService` includes a standards traceability summary so missing quality evidence is visible before client-facing package generation.

## Certification Boundary

Do not describe this matrix, dashboard, report, or package as certified. Acceptable wording is:

- "standards-aligned evidence",
- "mapped to ISO 9241-style HMI principles",
- "mapped to ISO/IEC 25010-style quality categories",
- "mapped to IEC 62682 / ISA-18.2-style alarm discipline".

Unacceptable wording is:

- "ISO certified",
- "IEC certified",
- "ISA certified",
- "formally certified HMI",
- "certification-compliant" unless an actual third-party certification exists and is cited.
