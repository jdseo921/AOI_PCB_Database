# AOI Monitor Design Contract

This document defines the design constraints for AOI Monitor. It is a product and engineering contract, not a mood board. The goal is to prevent amateur HMI problems before they reach review: clipped text, missing scrollbars, cramped menus, duplicated controls, hidden states, slow navigation, misleading demo claims, and screens that work only on the developer's monitor.

AOI Monitor is an industrial desktop HMI for PCBA automated optical inspection workflows. It must feel calm, legible, accountable, and production-oriented. Future development should preserve this contract even when doing so requires overhauling or rewriting existing UI structure.

This project is standards-aligned, not formally certified. Do not describe the application, design system, or evidence package as ISO, IEC, ISA, safety, cybersecurity, or regulatory certified unless a separate accredited certification process has actually occurred.

## Design Authority

When design decisions conflict, use this order:

1. Operator safety, clarity, and recoverability.
2. Truthful evidence and clear separation of simulated versus real hardware readiness.
3. Layout stability at 1920x1080 and common Windows DPI scales.
4. Fast, cancellable interaction without UI-thread blocking.
5. Consistency with shared HMI styles and existing workflow language.
6. Visual polish.

If an existing screen violates these rules, the correct fix is structural improvement, not another local patch that hides the problem. It is acceptable to rewrite a page, split a page, move diagnostics behind tabs, or remove duplicated UI if that is needed to keep the HMI future-proof.

## Scope

These rules apply to:

- `AOI_Monitor/MainWindow.xaml` and shell navigation.
- Every WPF page in `AOI_Monitor/Views/`.
- Shared controls, styles, templates, dialogs, and popups.
- Runtime dashboards, reports, export screens, and client-demo evidence.
- Setup, calibration, hardware boundary, MES, robot, camera, lighting, and model-validation flows.
- Documentation and screenshots that represent product readiness.

The companion documents are:

- `Docs/Industrial_HMI_and_Software_Quality_Baseline.md`
- `Docs/HMI_Style_Guide.md`
- `Tools/quality-gates/industrial_quality_gates.json`
- `.github/pull_request_template.md`

## Product Design Position

AOI Monitor is not a marketing site, consumer dashboard, or decorative demo. The default screen should be the usable factory workflow, not a hero page or explanation page.

The UI should be:

- Work-focused: inspection, review, setup, export, readiness, and troubleshooting actions are easy to find.
- Dense but organized: factory users need to scan status quickly without decorative filler.
- Honest: simulated, mock, demo, pending, and not-validated states are always labeled.
- Calm under failure: errors become alarms, recovery guidance, reports, and audit records instead of crashes or raw stack traces.
- Stable across time: adding a feature must not require shrinking everything else until text clips.

## Hard Layout Constraints

All operator-facing screens must satisfy these constraints:

- Minimum supported display: 1920x1080.
- DPI assumptions checked where feasible: 100%, 125%, 150%, and 200%.
- Minimum operator-readable text: 14 pt or equivalent.
- Primary action buttons: at least 120x40 px.
- Important text must wrap or resize; it must not clip, overlap, disappear, or rely on hover to be understood.
- Dense pages must use `ScrollViewer`, a virtualized table with scrollbars, or an approved adaptive layout.
- Critical verdicts, alarms, machine states, mode/profile labels, and primary actions must not be hidden below scroll-only areas.
- Long operator names, file paths, model IDs, station IDs, recipe names, defect labels, endpoint names, and translated-like strings must remain readable.
- Pages must tolerate missing images, disconnected hardware, empty datasets, failed exports, and unavailable services.

Absolute positioning is forbidden for production UI unless there is a specific visual-coordinate reason, such as image overlay geometry. Normal layout must use WPF `Grid`, `DockPanel`, `StackPanel`, shared styles, star sizing, min/max dimensions, wrapping, and scrolling.

## Shell And Navigation

The shell must preserve a stable industrial workstation layout:

- Top banner: mode, deployment profile, user/role, engine/source state, and any simulation/mock warning.
- Left navigation: focused workflow windows remain reachable with readable labels at 1920x1080. The rail may scroll when the number of windows exceeds the viewport, but it must not clip menu text or hide active state.
- Alarm/readiness strip: visible without requiring page-specific navigation.
- Page content: one primary workflow area with secondary diagnostics kept visually subordinate.

The current focused workflow windows are:

1. `01 Home`
2. `02 Board & Images`
3. `03 Run Inspection`
4. `04 Golden Compare`
5. `05 Defect Review`
6. `06 Recipe Rules`
7. `07 AI / Models`
8. `08 Yield Analytics`
9. `09 Export & Trace`
10. `10 Calibration`
11. `11 3D Profile`
12. `12 Hardware Readiness`
13. `13 System Settings`

Support pages such as Installation Notes and Guide are owned by System Settings and must remain reachable from there without becoming duplicate top-level workflows.

Do not add another focused window without updating the shell, Home module map, role authorization, HMI layout audit, navigation smoke tests, and this design contract. New functionality should either fit one existing owner or justify a new window by reducing cognitive load and clipping risk.

Do not duplicate the same status or control in the shell and a page unless the duplication serves different operator decisions. If two UI areas answer the same question, consolidate them.

## Page Composition

Use this structure for major pages unless a documented exception is justified:

1. Page title and current workflow state.
2. Primary status/verdict area.
3. Primary action band.
4. Main work area.
5. Secondary evidence, logs, diagnostics, or settings.
6. Export/audit actions where relevant.

The first viewport must answer:

- What state is the system in?
- What does the operator need to decide or do next?
- Is this real, simulated, mock, demo, pending, or not validated?
- Are there active alarms or release blockers?
- Can the operator recover from the current problem?

Do not start a workflow page with explanatory prose. Put help text in the guide, tooltips, inline plain-language notes, or documentation.

## Shared Styles

Use shared HMI resources before page-local styling:

- `FactoryPageContainer`
- `FactoryScrollablePage`
- `FactoryCard`
- `HmiKpiCard`
- `HmiTable`
- `HmiOperatorActionButton`
- `ActionBtnGreen`
- `ActionBtnBlue`
- `ActionBtnAmber`
- `ActionBtnRed`

Page-local styles are allowed only when they express page-specific structure that cannot reasonably live in the shared style layer. Repeated local styles should be promoted into shared resources.

Do not introduce a second visual language for one feature. Avoid one-off colors, custom card systems, custom buttons, decorative panels, and unrelated spacing scales.

## Typography And Text

Operator-facing text must be written and laid out for factory use:

- Use plain production terms before technical terms.
- Use short labels for controls and tables.
- Use details, tooltips, dialogs, or expandable panels for long explanations.
- Wrap important button and label text instead of clipping it.
- Use trimming only for secondary text, and provide the full value in a tooltip or details view.
- Do not shrink text below the readable minimum to force a crowded design to fit.
- Avoid unexplained acronyms in operator-facing text.
- Never show raw exception stack traces to operators.

Engineering diagnostics may include technical details, but they must be clearly separated from operator recovery guidance.

## Buttons And Controls

Controls must communicate intent through placement, label, state, and color:

- Primary actions are large, readable, and consistently placed.
- Destructive or stop/fail actions use red and are separated from routine actions.
- Review/pending/conditional actions use amber/yellow.
- Pass/ready/connected/normal states use green only when the state is actually validated.
- Disabled controls explain why they are unavailable through nearby state text or tooltip.
- Icon-only buttons require accessible names or tooltips.
- Commands that start long work must show progress and support cancellation where safe.

Do not use green for simulated success, mock connections, future placeholders, or unvalidated acceptance evidence.

## Color And Status Semantics

Use status colors consistently:

- Green: OK, pass, ready, connected, running normal.
- Red: NG, fail, alarm, stop, critical error.
- Amber/yellow: warning, review, pending, conditional, not tested.
- Gray/blue: disabled, not connected, unavailable, not configured.
- Purple: simulated, mock, demo, or non-production evidence.

Color cannot be the only signal. Pair color with text, icons, severity labels, or patterns. High contrast is mandatory for body text, controls, tables, charts, alarms, and overlays.

## Information Architecture

Each feature must have one obvious home:

- Module map and operator entry point: `Home`.
- Imported images, folder sources, board image inventory, golden references: `Board & Images`.
- Inspection execution, live progress, current board verdicts: `Run Inspection`.
- Golden template comparison and difference scoring: `Golden Compare`.
- Defect queue, evidence, classification, disposition: `Defect Review`.
- Recipe, ROI, mask, rule, threshold, tolerance setup: `Recipe Rules`.
- Model validation, Stage 1 evidence, ONNX/test-set review, false-call feedback: `AI / Models`.
- Yield, SPC, Pareto, trend, and process analytics: `Yield Analytics`.
- History, reports, standards, exports, client packages, MES queues, quality gates: `Export & Trace`.
- Calibration setup and Stage 2 preparation: `Calibration`.
- 3D CSV/profile visualization and sample-mode evidence: `3D Profile`.
- Customer pilot evidence, camera/lighting/robot/MES readiness walkthroughs: `Hardware Readiness`.
- Display, language, theme, storage, security, integration settings, support notes, and guide access: `System Settings`.

If a feature seems to belong in multiple places, choose one owner and expose links or summaries elsewhere. Do not create parallel copies of the same workflow.

## Performance And Responsiveness

The UI must stay responsive:

- Page constructors must be lightweight and must not perform heavy file I/O, database scans, network calls, model loading, image decoding, hardware checks, or sleeps.
- Heavy work belongs in async refresh, background services, or explicit commands with progress and cancellation.
- Navigation must give visible feedback quickly and must not leave loading overlays stuck.
- Repeated menu switching must be smoke-tested.
- Inspection visualization should target under 1 second from input to operator-visible result where feasible.
- Long exports, reports, package generation, hardware checks, and model evaluations must not freeze the HMI.

Any feature that cannot meet these rules must show explicit progress, timeout behavior, cancellation or retry options, and operator-readable failure messages.

## Backend Alignment

Frontend redesign must preserve backend behavior and evidence contracts:

- UI pages may reorganize controls, but they must not duplicate inspection, export, authentication, readiness, MES, camera, lighting, robot, or database rules in XAML/code-behind when a service already owns that logic.
- Existing service methods, persisted records, audit events, export verification, quality gates, and readiness outcomes remain the source of truth.
- A UI state that says Ready, Blocked, Simulated, Mock, Not Connected, Not Validated, or Failed must be derived from the same backend state used by reports and gates.
- Visual grouping must not hide release blockers, critical alarms, failed exports, crash reports, open critical issues, or simulated-hardware limitations.
- Renaming a control or moving it between sections requires updating HMI audit definitions, navigation smoke tests, and any backend-facing tests that locate the control by name.
- A design improvement is incomplete if it only changes appearance while leaving backend evidence, exports, logs, or client-demo gates inconsistent.
- If a backend capability is not implemented, the UI must show a boundary state instead of implying production readiness.

## Reliability And Error Design

Failures are part of the design:

- Expected failures must be recoverable without restarting the app.
- Unhandled failures must create crash reports and operator-safe messages.
- Critical alarms must remain visible until acknowledged or resolved.
- Operator messages must say what happened, what state the system is in, and what to do next.
- Logs and support bundles must redact secrets.
- A failed export, failed hardware check, failed model run, or failed readiness gate must never be silently treated as success.

Use alarms, audit events, crash reports, and readiness blockers as first-class UI states, not afterthoughts.

## Evidence, Exports, And Readiness

Evidence screens must be designed for auditability:

- CSV, PNG, PDF, JSON, HTML, and TXT exports used as evidence must be verified.
- Evidence rows must include timestamp, status, source/profile, path/checksum where practical, and enough context to reproduce the decision.
- Client-demo readiness must be blocked when required evidence is missing or failing.
- Runtime Standards & Quality status must be visible in Factory Readiness or Export & Trace.
- Evidence must distinguish Stage 1 prototype evidence from real camera, robot, safety, MES, or factory automation readiness.

Do not hide release blockers in logs only. Operators and reviewers need visible status.

## Simulation And Hardware Truthfulness

Simulation is useful, but it must never be overclaimed:

- Simulated, mock, demo, null, sample-data, boundary-only, and not-validated modes must be labeled in UI and exported evidence.
- Purple status styling is reserved for simulated/mock/demo/non-production evidence.
- Stage 1 prototype evidence must not satisfy gates that require live camera, lighting, robot, safety, MES, or full factory integration evidence.
- Do not write "production ready", "factory accepted", "MES connected", "certified safe", or equivalent claims near simulated or unvalidated paths.
- Real hardware readiness requires real hardware evidence for the selected deployment profile.

If the evidence is partial, say partial. If it is simulated, say simulated. If it is not connected, say not connected.

## Accessibility And Keyboard Use

The HMI must support efficient workstation use:

- Keyboard focus order must follow the visible workflow.
- Important buttons and fields need accessible names.
- Modal dialogs must identify the action required to continue.
- Tooltips can clarify, but they cannot be the only way to understand a critical state.
- Text contrast must remain readable in normal factory lighting.
- Tables must keep key identifiers, status, timestamp, and action columns discoverable.

## Forbidden Design Patterns

These patterns require redesign:

- Clipped button text, tab headers, table headers, alarms, verdicts, or menu labels.
- Missing scrollbars on dense pages.
- Shrinking fonts below 14 pt to make content fit.
- Adding features by squeezing the existing page until it becomes unreadable.
- Duplicating the same workflow or status in multiple places.
- Hiding critical alarms or release blockers below scroll-only content.
- Page constructors that load files, sleep, scan directories, call hardware, or block the UI thread.
- Raw exception stack traces in operator messages.
- Decorative gradients, oversized hero sections, marketing-style cards, or visuals that do not help factory work.
- Green status for simulated or unvalidated success.
- "Production ready" language for simulated, mock, or not-validated hardware paths.
- Hard-coded secrets in UI, config examples, logs, screenshots, or exports.

## Design Change Process

Before changing a screen:

1. Identify the operator task and the deployment profile affected.
2. Decide which focused workflow window owns the feature.
3. Sketch the page hierarchy using the standard page composition.
4. Check whether the feature belongs in an existing tab or requires a structural split.
5. Use shared styles first.
6. Plan empty, loading, error, simulated, not-validated, and success states.
7. Add or update layout/performance tests when the visual structure changes.
8. Update docs and evidence wording if the workflow or readiness claim changes.

If the old design cannot satisfy the constraints, rewrite the old design. Do not protect a weak layout for the sake of minimal diff size.

## Required Verification

Design-affecting pull requests must run or provide CI evidence for:

- `dotnet build AOI_PCB_Database.slnx --configuration Release`
- `dotnet test AOI_PCB_Database.slnx --configuration Release`
- `pwsh Scripts/run-quality-gates.ps1 -Configuration Release`
- HMI layout audit output: `hmi_layout_audit.json` and `hmi_layout_audit.html`
- Navigation performance output: `ui_navigation_performance.json`
- Export verification evidence when reports/packages are affected
- Client-demo gate evidence when packaging or readiness is affected

If a check cannot run locally, the PR must state why and point to equivalent CI evidence.

## Definition Of Done For UI Work

A UI change is done only when:

- All important text remains visible at 1920x1080 and 100/125/150% DPI assumptions.
- Dense content has scrolling or adaptive layout.
- All focused workflow windows remain reachable with readable labels, and Home, shell navigation, route handling, role authorization, HMI audit, and navigation smoke tests remain aligned.
- Primary actions are at least 120x40 px.
- Operator text is at least 14 pt equivalent.
- Long strings, missing data, empty states, and failed dependencies behave cleanly.
- Navigation does not freeze or leave stuck loading states.
- Errors are logged and shown as operator-safe messages.
- Simulation, mock, demo, and not-validated states are labeled.
- Exports and evidence claims remain truthful.
- The relevant automated gates pass.

Design quality is not decoration added after implementation. It is part of the control surface of the application.
