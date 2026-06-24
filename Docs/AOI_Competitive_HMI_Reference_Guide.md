# AOI Competitive HMI Reference Guide

## 1. Purpose

This document converts common patterns from commercial AOI, SPI, 3D inspection, smart-factory, and AI-AOI systems into practical design rules for AOI Monitor.

It is a reference guide for this project, not a claim that AOI Monitor is equivalent to Nordson, PARMI, MIRTEC, VizInspect Pro, or any other commercial inspection platform. It does not copy vendor UI, branding, screenshots, logos, or proprietary layouts. It extracts general HMI and workflow principles and translates them into enforceable requirements for the WPF industrial console in this repository.

The guide should be used together with:

- `DESIGN.md`
- `Docs/HMI_Style_Guide.md`
- `Docs/Industrial_HMI_and_Software_Quality_Baseline.md`
- `Docs/Stage_Mapping.md`
- `Docs/Requirements_Traceability_Matrix.md`
- `Docs/HMI_Page_Scroll_Audit.md`

The goal is a factory-credible AOI console: operator-safe, readable, auditable, honest about simulation boundaries, and maintainable as the software grows from Stage 1 image validation toward camera, lighting, robot, and MES integration.

## 2. Reference Systems Reviewed

| Vendor / System | What it is known for | Relevant UI / design ideas | How this project should apply it | What must not be overclaimed |
| --- | --- | --- | --- | --- |
| Nordson SQ5000Pro / SIGHT software | Integrated AOI, SPI, and CMM inspection/measurement workflows; intuitive software; multi-touch control; 3D visualization; faster setup and auto-programming concepts. | Treat inspection as a unified visual workflow. Prioritize direct visual control, easy setup, large image/3D views, and reduced operator interaction. Use data-rich recipe/model libraries where evidence exists. | Keep `Run Inspection`, `Golden Compare`, `Recipe Rules`, `Calibration`, and `3D Profile` visually connected through common sample/recipe/engine context. Make image/overlay viewing direct and large. Engineer setup should reduce repetitive recipe work, but still record recipe revisions and validation evidence. | Do not claim AOI/SPI/CMM parity, SIGHT compatibility, auto-programming parity, or autonomous model update behavior. Stage 1 remains local image validation unless later hardware and model evidence exists. |
| PARMI 3D AOI / smart factory workflow | Full 3D PCB inspection, fast cycle time, PCB warpage/Z-axis measurement, broad defect coverage across color/material/surface variation, debugging and SPC-oriented inspection history. | 3D inspection UI should expose height, shape, warpage, confidence, process correction, and measurement units, not just OK/NG. 3D views should be synchronized with selected defects and process evidence. | `3D Profile` should keep height map, slice/profile graph, selected defect table, units, legends, CSV sample labeling, and real-camera acceptance state clear. Future Stage 2 camera work should show Z-axis/warpage evidence and confidence. | Do not claim real 3D camera, calibrated height, warpage accuracy, fast cycle time, or material-independent detection until real Stage 2 hardware evidence exists. |
| MIRTEC / Intellisys / Smart Factory Solutions | Long-term inspection data collection, big-data/statistical analysis, remote management/control, precision measurement, clear images, advanced image interpretation, management-facing quality insight. | AOI consoles need more than the operator screen. They need quality trend dashboards, root-cause views, management summaries, and remote/central status concepts with clear boundaries. | Keep `Yield Analytics`, `Export & Trace`, factory readiness, central sync, MES queue, and management dashboards separate from operator inspection. Show OK/NG/REVIEW counts, false calls, possible escapes, latency, top defect classes, readiness, and export verification. | Do not claim remote production equipment control, centralized factory data, or production smart-factory integration while the app uses local SQLite, local files, mock REST, or sample data. |
| VizInspect Pro AI-AOI paper | Identifies traditional AOI pain points: vendor programming dependence, weak product-line scalability, limited tolerance to variations, and missing aggregated insights. Proposes intuitive inspection-profile setup without ML/vision expertise and scalable inference/visualization. | Engineer setup should hide unnecessary ML internals while preserving auditability. AI result screens should emphasize dataset health, scalable validation, operator-understandable evidence, and deviation tolerance limits. | `AI / Models` should lead with dataset preflight, ground-truth quality, model readiness, confusion metrics, false-call/escape analysis, preview images, and clear conditional states. The operator screen should not require ML expertise. | Do not claim validated AI, production model accuracy, scalable cloud AI, or customer-proven value unless this repository contains model acceptance evidence and deployment proof. |
| Internal AOI PoC requirements | WPF industrial HMI with Main Inspection, Recipe Editor, AI Model Test, Log & Export, 3D Profile Viewer, staged hardware/MES roadmap, 1920x1080 target, 14 pt text, 120x40 buttons, high contrast, 1-second visualization target, 8-hour stability target, exports, and Operator/Engineer/Admin roles. | The competitive patterns must be translated into the current staged architecture, not bolted on as a visual mock. UI must preserve truthfulness, role safety, audit logs, export evidence, and simulation labels. | Use focused workflow pages: `Run Inspection`, `Recipe Rules`, `AI / Models`, `Export & Trace`, `3D Profile`, `Hardware Readiness`, and `System Settings`. Keep Stage 1/2/3/4 evidence boundaries visible. | Do not imply the PoC is certified, production-ready, live-hardware connected, MES integrated, or equivalent to commercial AOI platforms. |

Reference URLs:

- Nordson SQ5000Pro / SIGHT software: <https://www.nordson.com/en/products/test-and-inspection-products/aoi-sq5000pro>
- PARMI equipment technology: <https://parmi.co.kr/equipment/>
- MIRTEC / Intellisys / Smart Factory Solutions: <https://www.mirtec.com/>
- VizInspect Pro AI-AOI paper: <https://arxiv.org/abs/2205.13095>

## 3. Common AOI Console Patterns to Adopt

AOI Monitor should adopt the following general patterns, adapted to this project's staged evidence model:

- Large central image or live-feed area.
- Optional large image viewer window for detailed zoom, fit, 100% view, and PNG save without sacrificing the main workflow.
- Right-side or bottom-side defect list with sortable, readable columns.
- Clear `OK`, `NG`, and `REVIEW` status banner.
- Top, Side, and Bottom view switching where the source supports it.
- Defect overlay with bounding boxes, ROI boxes, labels, score/severity, and selected-defect highlighting.
- Plain-language result explanations for operators.
- Recipe and ROI editing separated from normal operator inspection.
- Batch validation, model readiness, threshold analysis, and false-call review separated from normal operation.
- Log, export, traceability, audit, and management reporting separated from inspection operation.
- 3D height map and slice/profile view separated from 2D image view, but linked by selected defect/sample when possible.
- Factory readiness, management dashboard, MES queue, central sync, and quality gates separated from operator control.
- Simulation, mock, CSV sample, not-connected, and not-validated states visibly labeled on every affected workflow.

These patterns imply a strong information architecture rule:

- Operator pages should answer "what is the board status and what do I do next?"
- Engineer pages should answer "how do I tune, validate, and explain the inspection?"
- Admin/management pages should answer "what evidence, traceability, and readiness do we have?"

## 4. Industrial Layout Rules

These rules are strict implementation requirements for WPF page work.

- Root page content must use adaptive `Grid`, `DockPanel`, `WrapPanel`, or shared layout styles.
- Dense pages must use `ScrollViewer`, tab decomposition, or bounded internal table scrolling.
- The global `MainWindow` top banner/navigation strip and bottom evidence footer must stay fixed.
- Do not wrap the entire shell in a `ScrollViewer`.
- No clipped text is allowed in buttons, labels, table headers, verdicts, alarms, banners, or action controls.
- `TextBlock` controls must wrap unless intentionally trimmed.
- Trimmed secondary values must include a tooltip with the full text.
- Long paths, model IDs, recipe names, MES URLs, endpoint summaries, operator names, and export paths must use wrapping or trimming plus tooltip.
- Do not hard-code small label widths. Use star sizing, minimum widths, wrapping, and shared field styles.
- Primary buttons must be at least 120x40 px.
- Operator-facing text must be at least 14 pt equivalent.
- Test important UI at 1920x1080 and 125% Windows DPI.
- Important actions must remain reachable without resizing.
- Tables must not push action buttons off-screen.
- Dense tables should use `HmiDenseTable` or equivalent internal scrolling.
- Do not put unbounded large `DataGrid` controls inside page-level `ScrollViewer` unless the row count is small or bounded.
- Critical verdicts, alarms, mode/profile labels, and primary actions must not be hidden only below scroll.
- Empty, loading, error, simulated, not-connected, and not-validated states must be designed explicitly.

Default page body structure:

1. Page title and current workflow state.
2. Current sample/board/recipe/model context.
3. Primary action band.
4. Main image/table/work area.
5. Secondary evidence, logs, diagnostics, or details.
6. Export/audit actions where relevant.

## 5. Main Inspection Screen Design

Main Inspection is the operator's primary working surface. It must remain simple enough for routine production use while preserving evidence and alarm visibility.

Required layout:

- Top banner: station, user/role, board model, lot, active engine/model, operating mode, simulation/mock warning, and active critical/alarm summary.
- Left or top controls: `Start`, `Stop`, `Next Board`, and `Save Result`.
- Center: live image or loaded image.
- Overlay: defect bounding boxes, ROI boxes, selected defect, labels, and status colors.
- Right: defect list with Type, Score, Side, X/Y, ROI, RefDes, severity, and board coordinates when validated or clearly labeled as approximate.
- Bottom: event/alarm log that remains reachable through page-body scrolling when content exceeds the center workspace.
- Status: large `OK`, `NG`, or `REVIEW` indicator.

Rules:

- The operator must not need to understand AI internals to run inspection.
- Use plain-language status text such as `No camera connected`, `Folder simulation active`, `Review required`, or `Save result failed`.
- Show simulated, mock, folder-camera, not-connected, and not-validated states prominently.
- Never hide alarm messages.
- Never show raw exception stacks to operators.
- Start/Stop/Next/Save must remain visually stable and large.
- Defect rows must keep the selected image overlay in sync where possible.
- If a defect coordinate is approximate, label it as approximate.
- If a camera frame is simulated or loaded from a folder, never label it as real camera evidence.
- The event log may scroll, but critical active alarms must remain reachable from the persistent shell.

Main Inspection should not contain:

- Recipe editing fields.
- Model training controls.
- Export package controls.
- Admin-only readiness signoff.
- Hidden real-hardware claims.

## 6. Recipe Editor Design

Recipe editing is an Engineer/Admin workflow. It must not appear as a normal operator action.

Rules:

- Restrict access to Engineer/Admin roles.
- Use a large image area with zoom, pan, fit, and selected-ROI focus.
- Keep ROI list and parameter panel visible or reachable without clipping.
- Use explicit ROI status colors:
  - active = yellow
  - saved = green
  - unsaved/editing = blue
  - invalid = red
- Pair color with text; color alone is not enough.
- Threshold fields must explain units and decision meaning.
- AI score thresholds, height limits, volume limits, and tolerance values must state units or scale.
- Save recipe must record user, role, timestamp, revision, and reason/notes where available.
- Unsaved changes must be visible before navigation.
- Test run results must not be confused with production validation.
- Any generated or recommended threshold remains candidate evidence until validated and approved.
- Operators must be blocked from recipe modification and the denial should be operator-safe and auditable.

Recipe Editor should support:

- Image/board context.
- ROI creation/edit/delete.
- ROI type and defect class mapping.
- Threshold profile linkage.
- Revision history.
- Test run summary.
- Exportable evidence of changes.

## 7. AI Model Test / Dataset Validation Design

AI Model Test must be separate from the operator screen. It is for Engineer/Admin validation, dataset quality checks, model readiness, threshold evaluation, and evidence packaging.

Rules:

- Show dataset preflight first.
- Make missing labels, weak ground truth, missing golden references, bad files, and class imbalance visible.
- Show accuracy, precision, recall, false-call rate, possible-escape count, review burden, TP/TN/FP/FN, and category counts.
- Highlight failed samples red.
- Mark unknown, insufficient, or weak-label samples as `CONDITIONAL` or `REVIEW`, not `OK`.
- Include preview of sample image, golden image, overlay, selected defect, and result.
- Show timing and 1-second visualization target warnings.
- Separate model runtime readiness from dataset acceptance.
- Avoid overclaiming accuracy if the dataset is weak, small, biased, unlabeled, or not representative.
- Do not imply a model is validated unless model acceptance evidence exists.
- Do not expose raw customer image paths in reports unless explicitly allowed.
- Threshold recommendations must produce drafts or controlled deployments, not silent production changes.

Required first-view status:

- Dataset path or manifest.
- Engine/model source.
- Preflight status.
- Ground-truth coverage.
- Validation status: `Ready`, `Review`, `Conditional`, or `Blocked`.
- Next action.

## 8. 3D Profile Viewer Design

The 3D Profile Viewer must be visually and semantically separate from 2D image inspection, while still linking selected sample/defect context when available.

Rules:

- Show a height map with a readable color legend.
- Show a slice/profile graph.
- Synchronize selected defect row, selected height region, and graph marker when possible.
- Height and volume values must show units.
- CSV sample mode must be clearly labeled.
- `3D Camera Not Connected` or equivalent must be visible until a real camera adapter and acceptance evidence exist.
- Real 3D camera mode must require Stage 2 acceptance evidence.
- Sample CSV visualizations must not be labeled as live 3D inspection.
- Do not claim calibrated coplanarity, height, volume, or warpage accuracy unless the calibration profile and hardware evidence support it.
- 3D controls must not crowd or replace normal Main Inspection controls.

Recommended 3D page structure:

1. Source and evidence banner: CSV Sample, Simulated, Pilot Hardware, or Real Hardware Evidence.
2. Height map with legend.
3. Slice/profile graph.
4. Defect/measurement table.
5. Selected measurement details.
6. Acceptance/export actions for Engineer/Admin.

## 9. Log, Export, Traceability, and Management Design

Export and traceability workflows are not normal inspection controls. They belong in `Export & Trace`, `Yield Analytics`, `Hardware Readiness`, or Admin settings depending on ownership.

Rules:

- Logs must be filterable by date, model, operator, role, result, event type, and action category.
- Export actions must show verification status and checksum where practical.
- Export history must show timestamp, operator, status, artifact path, verification status, and failure reason.
- MES/central sync must show pending, failed, sent, abandoned, and retry counts.
- Mock MES must be labeled mock/local interface evidence only.
- Management dashboard must summarize:
  - OK/NG/REVIEW counts.
  - Yield trend.
  - False-call count/rate.
  - Possible-escape count/rate.
  - Review burden.
  - Top defect classes.
  - Top locations/RefDes when available.
  - Latency and over-1-second counts.
  - Readiness status.
  - Export verification status.
- Do not expose secrets, API keys, passwords, bearer tokens, or raw customer image paths unless explicitly allowed.
- Failed exports must be visible and auditable.
- Long-running exports must show progress and support cancellation where safe.
- Reports must distinguish Stage 1 evidence from Stage 2/3/4/full factory evidence.

Management pages may be dense, but they must scroll or decompose into tabs/subviews. Do not squeeze management metrics into Main Inspection.

## 10. Status Color and Language Rules

Use the following status vocabulary consistently:

| Meaning | Preferred words | Color |
| --- | --- | --- |
| Passing, ready, connected, running normally | `OK`, `GO`, `READY`, `CONNECTED`, `PASS` | Green |
| Failed, rejected, stopped, critical error | `NG`, `NO-GO`, `ERROR`, `FAIL`, `STOPPED`, `CRITICAL` | Red |
| Needs review or conditional acceptance | `REVIEW`, `CONDITIONAL`, `WARNING`, `PENDING`, `NOT TESTED` | Amber/yellow |
| Unavailable or not configured | `NOT CONNECTED`, `NOT VALIDATED`, `DISABLED`, `UNAVAILABLE` | Gray or blue-gray |
| Non-production evidence | `SIMULATED`, `MOCK`, `DEMO`, `CSV SAMPLE`, `FOLDER SIMULATION` | Purple or clearly labeled |
| Candidate production state | `PRODUCTION CANDIDATE`, `DEPLOYED` | Only when acceptance evidence exists |

Color must never be the only signal. Pair every color with text, severity name, icon, label, or table value.

Forbidden wording:

- Do not say `production-ready` for simulated camera, robot, MES, lighting, or 3D profile evidence.
- Do not say `validated AI` without model acceptance evidence.
- Do not say `real camera` for folder simulation.
- Do not say `MES integrated` for mock JSON export or mock REST.
- Do not say `certified safe` for robot, PLC, or emergency-stop simulation.
- Do not say `factory accepted` for generated evidence without signoff.
- Do not say `deployed model` for a local test model unless the lifecycle evidence supports it.

Preferred wording:

- `Stage 1 evidence only`.
- `Folder Camera Simulation active`.
- `Mock MES payload generated`.
- `3D CSV Sample Mode`.
- `Requires real hardware acceptance`.
- `Model runtime test passed; production acceptance still required`.
- `Factory readiness: REVIEW`.
- `Export verified; approval still required`.

## 11. Frontend / WPF Implementation Rules

Concrete WPF guidance:

- Prefer `Grid` with `Auto` and `*` rows/columns.
- Use `DockPanel` for fixed header/action/footer bands inside pages.
- Use `WrapPanel` for toolbars that may wrap at 125% DPI.
- Use `ScrollViewer` for dense body content.
- Use `DataGrid` internal scrolling for large tabular data.
- Avoid fixed page heights.
- Avoid fixed small widths for labels and values.
- Avoid `Canvas` for normal layout; reserve it for image overlays, defect boxes, and coordinate visualization.
- Use shared HMI styles before page-local styles.
- Promote repeated local styles into shared resources.
- Use `FactoryScrollablePage` for dense page body scrolling.
- Use `FactoryTrimmedTextWithTooltip` or equivalent for secondary long strings.
- Use wrapped text for critical messages.
- Use `HmiTable` or `HmiDenseTable` for operator-visible tables.
- Freeze `BitmapSource` objects where possible after decoding to reduce UI-thread pressure.
- Use thumbnails or bounded image caches for lists.
- Do not load large images synchronously in constructors.
- Do not run database scans in constructors.
- Do not perform model loading, inference, exports, hardware checks, sleeps, or long loops on the UI thread.
- Use async refresh after navigation.
- Use cancellation tokens for long operations.
- Show progress and cancellation affordances for batch import, validation, export, package generation, hardware checks, and soak tests.
- Keep UI state derived from services/models where those already exist.
- Do not move inspection, database, MES, robot, camera, lighting, or model rules into XAML/code-behind when a service boundary owns them.
- Use operator-safe error messages and log details separately.
- Keep mock/simulation labels visible in both UI and exported evidence.

Recommended dense page pattern:

```xml
<ScrollViewer Style="{StaticResource FactoryScrollablePage}">
  <Grid Style="{StaticResource FactoryPageContainer}">
    <!-- Page content using Auto/* rows and bounded tables -->
  </Grid>
</ScrollViewer>
```

Do not use this pattern around `MainWindow` itself. The shell bars must remain fixed.

## 12. Regression Gates

UI work should use proportional validation, but the following gates define the target quality bar:

- HMI layout audit.
- Navigation performance smoke test.
- No constructor crashes.
- Crash report gate.
- Export verification.
- Secret redaction check.
- Simulation-overclaim text check.
- UI smoke test at 1920x1080.
- UI review at 125% DPI.
- ScrollViewer check for dense pages.
- Long-string stress check for model IDs, paths, endpoint URLs, recipes, station IDs, operator names, and defect labels.
- Empty-data state check.
- Error/retry state check.
- Role access check for Engineer/Admin actions.
- Evidence wording check for Stage 1/2/3/4 boundaries.

Suggested commands when scope justifies them:

```powershell
dotnet build AOI_PCB_Database.slnx --configuration Release
dotnet test AOI_PCB_Database.slnx --configuration Release
pwsh Scripts/run-quality-gates.ps1 -Configuration Release
```

For small layout or documentation changes, a targeted build, affected-file inspection, or documented manual check may be appropriate. Do not claim a gate passed unless it was actually run.

## 13. Definition of Done for UI Changes

Use this checklist for every meaningful UI change:

- No clipped text.
- Dense content scrolls or is decomposed into tabs/subviews.
- Buttons are visible and large enough.
- Primary actions are at least 120x40 px.
- Operator-facing text is at least 14 pt equivalent.
- Page works at 1920x1080 and 125% DPI.
- Navigation does not freeze.
- Long operation has progress and cancellation where safe.
- Errors are recoverable, logged, and operator-safe.
- Simulation/mock/sample states are visible.
- Role-gated actions are protected.
- Critical alarms remain available.
- Long strings wrap or trim with tooltips.
- Empty, loading, failed, and not-connected states are readable.
- Export/report behavior is verified if touched.
- Evidence wording does not overclaim readiness.
- Tests, targeted checks, or manual verification notes are added.

## 14. Codex Instructions for Future UI Work

Reusable instruction block:

```text
When modifying WPF UI, first inspect Docs/AOI_Competitive_HMI_Reference_Guide.md,
DESIGN.md, Docs/HMI_Style_Guide.md, and Docs/Industrial_HMI_and_Software_Quality_Baseline.md.
Use shared HMI styles. Do not create fixed-size dense pages. Always wrap or scroll text.
Do not block navigation. Do not overclaim simulated hardware. Keep operator screens simple
and engineer/admin screens detailed.
```

Additional instructions:

- Keep Main Inspection operator-focused.
- Keep Recipe Rules, AI / Models, Export & Trace, 3D Profile, Hardware Readiness, and System Settings as focused workflows.
- Do not add a new top-level workflow without updating Home navigation, route handling, role authorization, HMI audit coverage, navigation smoke tests, and design documentation.
- Prefer improving an existing workflow owner over duplicating controls in multiple places.
- Treat clipping, missing scrollbars, misleading status wording, and hidden critical actions as blocking HMI defects.
- Use simulation labels every time evidence is not real hardware evidence.
- Keep vendor references as inspiration only; never copy proprietary layouts, screenshots, logos, or product claims.
