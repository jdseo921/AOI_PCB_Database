# Industrial Quality Checklist

This checklist is the enforceable baseline for AOI Monitor development, CI, release packaging, and client-demo readiness. It is standards-aligned, not formal ISO/IEC/ISA certification.

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
