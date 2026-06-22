# Frontend Design Review and Rework Plan

This review evaluates the current AOI Monitor frontend against `DESIGN.md`, `Docs/HMI_Style_Guide.md`, and `Docs/Industrial_HMI_and_Software_Quality_Baseline.md`.

The target is a standards-aligned industrial HMI/software-quality baseline, not a certification claim. The goal is to make the UI resilient enough that future features cannot reintroduce clipped text, tiny controls, hidden scroll, duplicated status, slow navigation, or simulated-hardware overclaiming.

## Executive Summary

The application now has a credible industrial HMI foundation: dark high-contrast surfaces, explicit OK/NG/warning/simulated color semantics, persistent mode/profile/user status, active alarm visibility, client-demo readiness gates, HMI layout audit, navigation performance smoke tests, and standards/export dashboards.

The remaining design risk is mainly enforcement and follow-through:

- Several legacy pages still contain local styling and fixed columns that should continue moving toward shared HMI components.
- Reports and Settings have been split into clearer categories, but their tabs must remain audited because they own many evidence and integration workflows.
- The shell now uses focused workflow windows instead of squeezing all features into one overloaded screen; future additions must not reverse that by making any page dense again.
- Some pages remain acceptable at 1920x1080 but should keep improving under long strings, Korean labels, and DPI scaling.

This pass raises the baseline and documents the larger rework path.

## Changes Applied In This Pass

- Default window size was moved to 1920x1080, matching the operator-display target.
- Shell minimum size was raised to reduce accidental cramped layouts.
- Default button, text box, combo box, table, tab, checkbox, status badge, and date picker sizing was increased.
- Page-level explicit font sizes below 14 pt were normalized to 14 pt.
- The shell navigation was remodeled into thirteen focused workflow windows: Home, Board & Images, Run Inspection, Golden Compare, Defect Review, Recipe Rules, AI / Models, Yield Analytics, Export & Trace, Calibration, 3D Profile, Hardware Readiness, and System Settings.
- Persistent status strip, alarm panel, workflow chips, footer, and title bar text were enlarged.
- Setup wizard buttons now meet the primary button sizing rule.
- PR quality checks now fail future XAML additions that explicitly set sub-14 pt font sizes or tiny minimum heights.
- Shell access/user-management controls were collapsed into an explicit Access panel so normal production navigation remains focused while the existing authentication backend and handlers remain intact.
- Log & Export was clarified as Export & Trace and remains the owner for evidence exports, readiness/quality, database/image index, MES traceability, and central evidence sync.
- Settings was remodeled into Basics, QOL, AI, Hardware, Traceability, and Evidence tabs while preserving existing settings services and named controls.
- Installation Notes and Guide remain support pages and are reachable from System Settings instead of becoming competing top-level workflows.

## Current Strengths

- The shell exposes mode, profile, simulation warning, user/role, engine/source state, readiness strip, active alarms, page title, workflow status, and export/support actions.
- The focused workflow-window structure matches the design contract and keeps overloaded functions out of a single crowded screen.
- Major evidence surfaces exist in Export & Trace: export history, Factory Readiness, Standards & Quality Checklist, Management Dashboard, MES queue, Central Sync, Factory Acceptance, and Evidence Completion.
- Simulation/mock/demo states are mostly labeled and use purple styling.
- Shared HMI resources exist and can become the single design system layer.
- HMI layout audit and navigation smoke tests already instantiate major pages and produce machine-readable reports.

## Cross-Cutting Design Findings

### Typography

Finding: explicit page-local font sizes below the 14 pt baseline were widespread.

Action: normalized current page XAML text to 14 pt and added PR checks to prevent new undersized explicit values.

Remaining rework: replace page-local `FontSize` attributes with shared text styles so typography is token-driven instead of manually repeated.

### Buttons and Controls

Finding: default and mini buttons were sized for developer convenience rather than operator confidence.

Action: increased default and compact control baselines. Setup wizard buttons now use 120x40 sizing.

Remaining rework: classify commands as primary, secondary, destructive, evidence/export, or diagnostic. Then use only the corresponding shared styles.

### Scroll and DPI

Finding: dense pages have ScrollViewer coverage, but several medium-density workflow pages still rely on fixed columns and fixed heights.

Action: existing HMI audit remains the release-blocking safety net.

Remaining rework: add explicit adaptive layout sections to Main Inspection, Recipe Editor, Calibration, Profile Viewer, and Pilot Wizard so long strings and 150/200% DPI remain comfortable without relying on last-minute scrolling.

### Information Architecture

Finding: Settings and Reports historically contained many unrelated workflows and were functionally rich but visually dense.

Action: documented ownership rules in `DESIGN.md`; remodeled Settings into Basics, QOL, AI, Hardware, Traceability, and Evidence tabs; separated analytics, export/traceability, hardware readiness, and inspection work into focused windows.

Remaining rework: continue moving repeated page-local controls into shared components and keep tab-specific audit coverage for all dense Settings and Export & Trace subviews.

### Status and Readiness

Finding: the app correctly surfaces readiness, but some status appears in multiple areas.

Action: shell status text is now more legible.

Remaining rework: define a single source-of-truth status summary component for mode/profile/source/readiness and reuse it instead of duplicating local summaries.

### Error and Failure States

Finding: global exception handling, crash reports, recoverable page errors, alarms, and support bundles exist.

Remaining rework: every page should have explicit empty/loading/error/simulated/not-validated states designed in XAML, not only code-behind fallback messages.

### Simulation Truthfulness

Finding: simulation language and purple status styling are present in the key places.

Remaining rework: every adapter boundary, sample-data workflow, and generated evidence panel should show one of these exact states: Real Hardware Evidence, Pilot Evidence, Boundary Only, Simulated, Mock, Demo, Not Connected, or Not Validated.

## Page-by-Page Review

### Shell / MainWindow

Strengths:

- Persistent banner and readiness strip are appropriate for an industrial HMI.
- Thirteen focused workflow windows are reachable through the left rail and Home module map.
- Active alarms remain visible outside page content.

Risks:

- Authentication and local user controls still compete with the top menu bar.
- The shell contains both navigation and administrative controls, which can distract production operators.
- Some workflow chips may become noisy when long file names are loaded.

Recommended rework:

- Continue moving user management toward System Settings. The current interim state collapses those controls into an Access panel so the shell no longer displays every administrative control by default.
- Replace file/generic toolbar wording with task-oriented commands.
- Add a shell screenshot regression test or image-based layout smoke test if feasible.

### Main Inspection

Strengths:

- Primary workflow is recognizable: image, simulated handler, defect list, and disposition actions.
- Simulation warnings are explicit.

Risks:

- Robot simulation controls consume a large part of the primary inspection screen.
- The page mixes inspection operation, handler simulation, image viewing, defect review, and acceptance evidence.

Recommended rework:

- Keep inspection image, verdict, defect list, and disposition as the primary screen.
- Move robot simulation/acceptance controls into a secondary tab or collapsible engineering panel.
- Add a top-level current-board state strip with sample, recipe, engine, verdict, and next action.

### Recipe Editor

Strengths:

- ROI editor is task-specific and supports clear setup actions.

Risks:

- Fixed-width top columns and right-side ROI panel are brittle under longer recipe/program/operator names.
- ROI details and grid controls can become cramped.

Recommended rework:

- Use an adaptive two-row header instead of fixed-width columns.
- Convert ROI Setup into a scrollable inspector panel with clear grouped fields.
- Keep image canvas dominant and stable.

### AI Model Test

Strengths:

- Dense content already uses ScrollViewer.
- Stage 1 evidence and result tables are visible.

Risks:

- Multiple tables and evidence controls compete for attention.
- Some controls belong to evidence packaging rather than test execution.

Recommended rework:

- Split into Run Test, Results, Acceptance Evidence, and Export tabs.
- Keep the current model/source/test-set state persistent at top.

### Export & Trace / Reports

Strengths:

- This is the correct home for evidence, exports, quality status, readiness, and reports.
- Standards & Quality dashboard is present and exportable.

Risks:

- Thirteen tabs in one control make discovery harder.
- Filter area uses fixed columns that are likely to crowd under DPI scaling or localization.
- Management, readiness, quality gates, MES queue, and export history are all different mental models.

Recommended rework:

- Keep evidence, quality gates, readiness, operations history, integration queues, and management views audited as separate subviews.
- Replace any remaining fixed filter grids with wrapping filter chips or two-row adaptive fields.
- Keep export actions in a consistent lower-right or top action band.

### System Settings

Strengths:

- It exposes necessary integration boundaries and explicitly labels mock/simulation.
- It now separates Basics, QOL, AI, Hardware, Traceability, and Evidence settings.
- Apply/Cancel/Reset remain available regardless of selected category.
- Installation Notes and Guide are reachable from Evidence / Maintenance.

Risks:

- It still owns many high-impact services and must remain guarded by role checks and readiness evidence.
- Long endpoint paths, model IDs, and translated settings labels can still pressure two-column layouts.

Recommended rework:

- Keep the current tab categories intact and add new categories only when they reduce density.
- Add a readiness impact summary at top: which settings affect client-demo gates.
- Move local user management from the shell Access panel into a Users & Auth settings section once the shared authentication control can be reused without duplicating backend logic.

### Pilot Wizard

Strengths:

- The purpose is clear: customer evidence and staged readiness.

Risks:

- Wizard-style workflows should show current step, remaining blockers, and next action more strongly.

Recommended rework:

- Convert to a true stepper with profile, evidence, checks, export, and signoff stages.
- Use release-blocking gate status as the primary visual model.

### 3D Profile Viewer

Strengths:

- Profile evidence is separated from image inspection.

Risks:

- Sample CSV mode and real profile hardware readiness need stronger visual separation.

Recommended rework:

- Add a persistent source badge: Sample CSV, Simulated, Pilot Hardware, or Real Hardware Evidence.
- Keep acceptance/export controls separate from viewing controls.

### Calibration

Strengths:

- Calibration is isolated from production inspection.

Risks:

- Calibration can be mistaken for live camera/robot calibration if wording is not consistently constrained.

Recommended rework:

- Add a persistent "Stage 2 preparation only" status band.
- Separate image-to-board planning from any future real hardware calibration workflow.

### Review / Compare / Library

Strengths:

- These pages are appropriately evidence/review oriented.

Risks:

- Compare has canvas-fixed mock PCB visuals that are acceptable for demonstration but not scalable as production evidence.
- Review and Library risk chips need to remain consistent with the shared color semantics.

Recommended rework:

- Convert mock PCB visuals to generated evidence from actual image/ROI data where possible.
- Keep risk/verdict chips standardized through shared status components.

### First Run / Install / Guide / Planned Stage

Strengths:

- These screens help prevent hidden setup assumptions.

Risks:

- Planned/disabled states must never look like implemented production features.

Recommended rework:

- Use the same purple/amber readiness language used elsewhere.
- Ensure setup wizard output feeds the readiness dashboard and gate evidence.
- Keep Installation Notes and Guide reachable from System Settings.

## Rework Roadmap

### Phase 1: Enforce the Floor

- Keep all explicit operator-facing text at 14 pt or larger.
- Keep primary actions at 120x40 or larger.
- Reject new tiny XAML through PR gates.
- Keep HMI audit, navigation smoke, export verification, crash containment, and client-demo gates release-blocking.

### Phase 2: Shell Simplification

- Move local user management from shell toolbar to System Settings.
- Keep only user/role/session summary and Access/Login command in the shell.
- Make active alarms and readiness the dominant persistent shell information.
- Keep the focused workflow-window map in sync across Home, shell navigation, route handling, role authorization, HMI audit, and navigation smoke tests.

### Phase 3: Page Decomposition

- Preserve the Settings tab split and Export & Trace subviews.
- Move engineering diagnostics away from primary operator workflows.
- Add page-level empty/loading/error/simulated/not-validated state components.

### Phase 4: Componentization

- Create shared WPF components for status badge, evidence gate card, readiness row, export action band, page header, and error panel.
- Remove repeated page-local colors, fonts, button sizes, and card styles.
- Extend HMI audit to cover shell, dialogs, and every secondary page.

### Phase 5: Visual Regression Evidence

- Add screenshot or rendered-layout snapshots for 1920x1080 at 100/125/150% DPI where feasible.
- Store audit artifacts in CI for review.
- Add diffable HTML summaries for page visual health.

## Design Acceptance Bar

A frontend change should not be considered ready unless:

- Text does not clip at 1920x1080 and common DPI scales.
- Dense pages scroll or adapt.
- All focused workflow windows remain reachable and the Home module map matches the shell route map.
- Shell status remains readable.
- Primary actions are obvious and large enough.
- Simulation and real-hardware evidence are visually distinct.
- Page navigation is responsive.
- Failures are visible, logged, and recoverable.
- HMI audit and navigation performance reports pass.

This plan intentionally prioritizes future-proof industrial usability over preserving cramped legacy layouts.
