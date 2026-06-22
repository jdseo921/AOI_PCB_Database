# Industrial HMI and Software Quality Baseline

This baseline is the permanent quality checklist for AOI Monitor development, CI, release packaging, factory pilots, and client-demo readiness. It is aligned with ISO 9241-style HMI principles, ISO/IEC 25010-style software quality categories, and IEC 62682 / ISA-18.2-style alarm and event discipline.

This document is standards-aligned, not certified. It does not claim formal ISO, IEC, ISA, safety, cybersecurity, or regulatory certification. Formal certification requires an accredited assessment process, controlled scope, and independent evidence review.

## HMI Layout Requirements

- The application shall be designed for a minimum operator display resolution of 1920x1080.
- Primary production tasks shall fit without hiding critical verdict, station, alarm, image, or action controls.
- Layouts shall be task-suitable: inspection, review, reports, setup, and diagnostics screens must present the next operator action clearly.
- Navigation shall be self-descriptive and conform to operator expectations: page names, status labels, commands, and dialogs must use plain production terminology.
- Operators shall remain in control: long work must show progress, blocking operations must be cancellable when safe, and view changes must not discard unsaved operator decisions without confirmation.
- Learning burden shall be low: repeated actions must use consistent placement, names, colors, and keyboard/mouse behavior across pages.
- Operator errors shall be robustly handled: invalid recipes, missing images, failed exports, disconnected hardware, and MES errors must produce readable recovery instructions instead of crashes.

## Typography, Button, and Spacing Requirements

- Minimum readable font size is 14 pt for operator-facing text.
- Primary action buttons shall be at least 120x40 px.
- Icon-only commands shall provide accessible names or tooltips.
- Touch or mouse targets used during production flow shall have adequate spacing to prevent accidental activation.
- Text shall not clip, overlap, or disappear at 100%, 125%, 150%, or 200% Windows DPI scaling.
- Compact dashboards may use dense information layouts, but labels, numbers, and alarm text must remain readable from normal operator distance.

## Color and Status Rules

- Status colors shall use green for OK/pass/running-normal, red for fail/alarm/stop, and yellow or amber for warning/review/pending.
- Color shall not be the only status channel. Use text labels, icons, severity names, or patterns in addition to color.
- High contrast shall be maintained for text, alarms, controls, and charts.
- Disabled, unavailable, simulated, and real-hardware states shall be visually distinct.
- Demo, pilot, simulated, and production evidence shall be labeled so clients cannot confuse simulated results with real factory validation.

## Scroll, Resize, and DPI Rules

- All operator pages shall remain usable at 1920x1080 and under common Windows DPI scaling values.
- Scrollbars are acceptable for secondary information, logs, details, and reports, but critical alarms, verdicts, and emergency-relevant status must not be hidden below the fold.
- Resizing shall not clip alarms, buttons, headers, grid columns, or report export controls.
- Large tables shall support readable column headers and preserve timestamp, status, ID, and action columns.
- Screens shall tolerate missing images, long file paths, long operator names, translated-like strings, and long defect labels.

## Operator Alarm and Event Rules

- Alarms and warnings shall be readable, prioritized, timestamped, recoverable, and not hidden or clipped.
- Alarm severity shall be explicit: alarm, warning, review, info, or equivalent labels.
- Alarm/event records shall include UTC timestamp where persisted and local time where useful for operators.
- Operators shall be given recovery guidance for camera, robot, lighting, MES, export, model, and database failures.
- Active alarms shall not be buried behind modal windows, collapsed panels, or scroll-only areas.
- Acknowledgement, reset, retry, waiver, and signoff actions shall be auditable when they affect production or client evidence.

## Performance Rules

- Visualization from inspection input to operator-visible result shall target 1 second or less.
- Performance evidence shall include timing summaries for image load, preprocessing, inference, overlay rendering, and persistence where available.
- Slow paths shall produce warnings and evidence instead of silent degradation.
- Long-running reports, exports, benchmarks, syncs, and hardware checks shall not freeze the HMI.
- Factory pilot and production profiles shall include stability evidence appropriate to the deployment stage.

## Reliability and Crash Rules

- Client-demo and release packages shall not be produced with known uninvestigated crashes in the current evidence set.
- PoC operation shall demonstrate an 8-hour stable run before factory or client claims that imply extended operation.
- Exceptions in UI actions, exports, hardware adapters, MES sync, and database operations shall be caught, logged, and surfaced with recovery guidance.
- Crash reports, support bundles, and audit records shall be retained as release evidence.
- Fail-safe behavior shall prefer review, stop, retry, or degraded mode over misleading OK/NG output.

## Security and Accountability Rules

- Operator identity, role, station ID, timestamp, and action category shall be captured for production-relevant actions.
- Secrets, API keys, passwords, tokens, and endpoint credentials shall not appear in exports, logs, screenshots, support bundles, or client packages.
- Role-based authorization shall protect administrative setup, model approval, recipe changes, production-mode confirmations, and waivers.
- Audit exports shall be verifiable and include enough detail for accountability without exposing secrets.
- Simulation mode and production mode shall be explicitly indicated in evidence.

## Maintainability Rules

- New code shall follow existing project structure, naming, nullable annotations, and service patterns.
- Quality gates shall be machine-readable and testable.
- CI shall parse the gate configuration and run tests covering gate severity decisions.
- Gate changes shall update this baseline, the checklist, and the JSON gate file together.
- Shared behavior shall be covered by focused tests, especially where release blocking decisions are made.
- Documentation shall distinguish implemented behavior, simulated integration, planned integration, and real hardware evidence.

## Export and Report Evidence Rules

- CSV, PNG, PDF, JSON, HTML, TXT, and package exports used as evidence shall be verified for existence, non-empty content, format signature or parseability where practical, checksum, and required fields.
- Release evidence shall include generated timestamp, operator or automation identity, station or machine identity where applicable, status, and artifact path/checksum.
- Export failures shall be visible to operators and shall block release when the export is required for the selected profile.
- Client packages shall include manifest evidence so file lists can be compared with actual package contents.

## Hardware and MES Evidence Rules

- Camera, lighting, 3D profile, robot, safety, and MES evidence shall identify whether it is simulated, mock, boundary-only, pilot, or real hardware.
- Simulation evidence may support development and Stage 1 PoC claims, but it shall not satisfy a real hardware gate.
- Staged integration evidence shall be accepted only for the matching deployment stage:
  - Stage 1: image validation and export evidence.
  - Stage 2: camera and lighting pilot evidence.
  - Stage 3: robot and safety pilot evidence.
  - Stage 4: MES or central sync pilot evidence.
  - Full factory automation: real hardware, MES/central sync, release package, stability, and accountability evidence.
- MES and central sync evidence shall include payload mapping, queue/retry behavior, endpoint mode, redaction rules, and signoff status.

## Release Rule

A release or client-demo package shall not be represented as production-ready unless the selected deployment profile has passing or explicitly accepted evidence for HMI layout, performance, reliability, security/accountability, export verification, hardware readiness, MES/central sync where applicable, and release packaging.
