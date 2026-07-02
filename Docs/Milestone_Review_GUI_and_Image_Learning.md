# Milestone Review: GUI Readiness and Stage 1 Image-Learning

Review date: 2026-07-02.

> **Status update (same day):** GUI defects G1-G6 from Section 3.1 and the demo-semantics items (amber/green demo chips to purple, font-preset px labeling, over-trimmed Home/alarm chips, synchronous learning-report export) were fixed on this branch — see the follow-up commit for the file list. The learning-pipeline items L1-L8 (Section 4.3) and the P1/P2 plan items remain open and are the next priority.
Scope: full static review of the WPF GUI (all views, shell, styles, code-behind), the image-only PCB learning pipeline and its tests, and traceability against the three client documents (PCBA Defect Classification Table v1.0, AOI PoC Software GUI Concept & Functional Specification, Development Roadmap & Commercialization Plan).
Method: line-by-line code reading with geometry checks for layout claims. This review environment cannot run Windows/WPF builds; `dotnet build/test` and the quality-gate scripts were not executed here. CI on `windows-latest` remains the authoritative build/test evidence.

## 1. Executive Summary

**GUI verdict: close to industrial standard, not yet clean at the shipped defaults.** The underlying system — shared HMI design tokens, honest simulation labeling, async navigation with cancellation and recoverable page errors, virtualized evidence tables, alarm visibility, role gating — is genuinely production-grade thinking and well beyond a typical PoC. However, six confirmed geometry defects (Section 3.2) would be visible in a client demo within minutes, the worst being that the SIM/MOCK truthfulness banner clips out of the shell entirely at the default 1440x900 launch size. All are localized fixes, not redesigns.

**Milestone verdict: the image-learning capability already exists end-to-end and is demoable from the GUI today; the accuracy claim behind it is not yet trustworthy.** `AI / Models > AI Training Setup` genuinely performs: create project, import image groups (folder convention, picker, drag-drop), learn a visual model, calibrate a threshold against OK/NG validation images, inspect samples with anomaly overlays, export HTML/JSON/CSV/PDF evidence, and version/activate learned models. Nothing in that path is mocked. But three confirmed calibration defects (Section 4.3) mean the false-call rate the tool reports is not what the deployed model actually delivers — and one legal input path (no OK Validation images) silently produces a hair-trigger model that flags nearly everything NG. These must be fixed before demonstrating accuracy claims to a client.

**Recommendation: conditional GO.** Fix the P0 list (Section 6.1, roughly 1-2 weeks of focused work), demo with fixtured same-camera imagery, and present the capability truthfully as statistical visual learning (Stage 1) with an ONNX hook for future ML models — then move fully into the milestone's customer-dataset validation phase.

## 2. What Is Genuinely Strong

- **Design-token discipline.** `Styles/FactoryHmiLayout.xaml` defines semantic status styles (including purple simulated/mock), 120x40 primary-button minimums, state cards for empty/error/not-validated, trim-with-tooltip patterns. Base button template wraps text instead of clipping (`App.xaml:62-69`). Tab headers scroll rather than clip (`FactoryHmiLayout.xaml:620-630`). No XAML font size below 14 units anywhere.
- **Navigation engineering.** Page cache, lightweight constructors, `IAsyncNavigationPage` with cancellation, delayed loading overlay with Cancel, recoverable page-error card with Retry/Dismiss (`MainWindow.xaml.cs:164-252, 338-409`).
- **Truthfulness architecture.** Simulation/mock/demo boundaries are labeled in the shell, pages, exports, and audit events; the quality-gate config blocks real-hardware claims from simulated evidence; the HMI layout audit and navigation-performance tests run in CI with an empty approved-exceptions list.
- **Evidence and traceability.** SQLite persistence with 28 versioned migrations, SHA-256 hashing of images and model artifacts, export verification records, audit events on every mutation, a real model registry with lifecycle states and acceptance gating (`Services/ModelRegistryService.cs:26-150`).
- **Spec coverage where it counts.** Defect list has No/Type/Score/Side/X/Y plus ROI/Severity/Board X/Y (`Views/MonitorView.xaml:268-278`); Start/Stop/Next Board/Save Result wired; Recipe ROI draw/move/resize with zoom/pan and revisions saved with timestamp and user; batch validation computes Accuracy/Precision/Recall/False-Call-Rate with correct formulas (`Services/BatchValidationService.cs:109-117`); exports have confirmation dialogs; role gating is enforced on navigation with audited denials.
- **Tests and CI.** ~384 test methods across 45 files, a separate UI-test project (layout audit, navigation performance, smoke), and a Windows CI pipeline that also generates synthetic Stage 1 learning evidence.

## 3. GUI Findings

### 3.1 Confirmed layout defects (fix before any client demo)

| # | Defect | Evidence | Effect |
|---|--------|----------|--------|
| G1 | Top-banner center column clips at the default 1440x900 launch size; the SIM/MOCK banner is the last chip and disappears entirely below roughly 1650 px window width. The window starts at 1440x900 and the resolution preset does not resize it (`resizeWindowToPreset` defaults to false). | `MainWindow.xaml:6, 54, 81-92`; `Services/UiPreferencesService.cs:395` | The single most truth-critical indicator is invisible at the size the app actually opens at. |
| G2 | Even when visible, "SIM / MOCK ACTIVE" is ellipsized ("SIM / MOCK ACT...") because of `MaxWidth="125"` at 14 px ExtraBold; "Production Mode" similarly always trims under `MaxWidth="92"`. Tooltip-only recovery violates the design contract for critical states. | `MainWindow.xaml:82, 91` | Critical mode labels never render fully at any resolution. |
| G3 | Run Inspection's Start/Stop/Next Board/Save Result band sits below the fold at 1920x1080. The page root is a vertical ScrollViewer, so its star rows behave as Auto and the Viewbox hosting a fixed 1000x700 overlay grid takes ~0.7x column width in height, pushing the action band past the ~900 px workspace. The alarm DataGrid also loses virtualization inside the ScrollViewer (up to 80 rows rendered). | `Views/MonitorView.xaml:24-31, 156-170`; cap at `MonitorView.xaml.cs:1014-1018` | Primary operator actions require scrolling at the guaranteed resolution — an explicit forbidden pattern in DESIGN.md. |
| G4 | AI / Models "Stage 1 Validation" tab cannot fit 1080: a ScrollViewer placed in an `Auto` grid row measures at full content height (its scrollbar can never engage), starving the header row and the results table below their minimums. | `Views/AIModelTestView.xaml:46, 105` | Visible squash/clip on the page that anchors the Stage 1 accuracy story. |
| G5 | First-run wizard footer row is fixed at 48 px but contains 10 px padding + 40 px-min buttons (60 px): Skip/Back/Next/Finish render squashed/clipped at every window size. | `Views/FirstRunWizardView.xaml:32, 179` | The first screen a new evaluator sees exhibits the classic amateur clipping error. |
| G6 | Golden Compare and Defect Review roots are fixed `MinHeight="665"` grids with no ScrollViewer, while the shell permits 720 px window height (~630 px workspace): bottom rows clip with no scrollbar at the app's own minimum size. Golden Compare's fixed 430 px center column also overlaps side-panel headers below ~1500 px width. | `Views/CompareView.xaml:50, 54`; `Views/ReviewView.xaml:39` | Broken at supported window sizes; fine at 1080 full-screen. |

Also worth fixing in the same pass: `SpcView.xaml:40` uses a bare StackPanel root (no scroll) and its yield trend is a hardcoded decorative polyline labeled only by an amber "Prototype Trend" chip (`Controls/LineChart.xaml:32-40`) — either label it purple as demo data or hide the panel for client demos.

### 3.2 Contract and spec deviations (decisions to make, then enforce)

1. **Font size: systemic pt/px conflation.** The client spec and DESIGN.md say 14 pt minimum; 14 pt = 18.67 WPF units, but every token is 14 units (= 10.5 pt) (`Styles/FactoryHmiLayout.xaml:33-35`, `App.xaml:35`). The repo's own audit enforces true 14 pt only for "critical text" as a warning (`Services/HmiLayoutAuditService.cs:80, 459-462`). Worse, Settings advertises "Minimum 14 pt / Standard 15 pt / Large 17 pt" while applying 14/15/17 px, and the implicit TextBlock style overrides the inherited size so the preset is largely inert (`Views/SettingsView.xaml:209-211`, `Services/UiPreferencesService.cs:438-443`, `App.xaml:32-38`). Decide once: either re-baseline tokens to 18.67 px (large visual change), or obtain client sign-off that 14 px at 1080 is the intent and align the audit, the Settings labels, and the spec text. Fix the inert preset either way.
2. **Launch geometry vs 1920x1080 minimum.** The spec and the repo's own gate say 1920x1080 minimum; the window opens at 1440x900 with MinWidth/MinHeight 1180x720 (`MainWindow.xaml:6`). Launch maximized (or default `resizeWindowToPreset` to true), and make sub-1920 sizes explicitly best-effort engineering sizes that still must not clip critical chrome (see G1/G6).
3. **Demo evidence color semantics.** "Demo Board/Data/Metadata" chips use amber (= warning) instead of the existing purple demo styles (`Views/CompareView.xaml:88-90, 229-231, 288-290`; `Views/ReviewView.xaml:62-64, 100-102, 162-164, 296-298`; `Views/LibraryView.xaml:54-56`). Green is used on demo "Verified OK"/verified-ROI fills (`Views/CompareView.xaml:320-321, 334`) against the "no green for simulated success" rule. Swap to `ChipPurple`/`HmiEvidenceDemo`.
4. **Accessibility.** `AutomationProperties` appears zero times in the project; inputs in Settings/Recipe/Calibration have no programmatic labels, and GT/AI/FOV/RefDes headers are tooltip-only. This also blocks future UIA-based test automation.
5. **Ghost-control graveyard.** `ShellHiddenStateHost` keeps a collapsed set of live-wired controls that still receive localization, role-enabling, and status updates (`MainWindow.xaml:289-308`) — hidden duplication maintained for tests. Consolidate; point tests at services instead.
6. **Theme consistency risk.** A light theme exists (`Services/UiPreferencesService.cs:478-490`), but views hardcode dark-theme hex foregrounds/backgrounds extensively (for example `Views/AiTrainingSetupView.xaml` throughout). Switching to light mode will produce unreadable text in those areas. Either finish brush indirection or remove/label the light theme as not yet supported.

### 3.3 Sustainability and scalability

- **Acceptable Stage 1 debt, plan the refactor:** MVVM exists on only two views; the rest are code-behind-heavy (ReportsView 3,607 lines; SettingsView 3,282; AIModelTestView 1,386; MonitorView 1,375). Business logic is well extracted into services (which is why 384 unit tests exist), but UI-level logic is untestable and the views keep growing. `Data/AoiDatabase.cs` is an 8,649-line static class with ~90 tables of raw SQL — mitigated by WAL/FK pragmas and versioned transactional migrations, but a refactor risk for Stage 2.
- **Export & Trace carries 14 tabs** (~2,300 px of headers; 4-5 off-screen even at 1920) — split dashboard/readiness/FAT/stability/MES workloads per the information-architecture rule.
- **Camera-readiness seam is credible:** `ICameraSource`/`CameraFrame`/adapter plugin loading and acceptance-test services form the right Stage 2 boundary. Two shape issues to schedule: `IInspectionEngine.Analyze` is file-path-based (every live frame must hit disk) and the learned engine reloads its model package per call; the ONNX engine constructs a new `InferenceSession` per image (`Services/OnnxInspectionEngine.cs:61`). Fine board-at-a-time; wrong shape for streaming acquisition.

## 4. Stage 1 Image-Learning Milestone

### 4.1 What the "learning" actually is

Classical statistical template learning, not a neural network: per-pixel mean reference + per-pixel standard-deviation tolerance map from aligned OK images; inference scores `|x - mean| / tolerance` with 3x3 smoothing, connected-component regions, and a threshold calibrated by sweeping against OK/NG validation scores (`Services/ImageOnlyPcbLearningService.cs`). Registration is integer translation only (max +/-20 px at model resolution, default 768x768, nearest-neighbor resize, grayscale, global brightness gain). This is a defensible, explainable Stage 1 choice — deterministic, low-data, auditable — and the ONNX engine + model registry provide a real hook for actual ML models later. It should be described to clients as "the software learns the normal appearance of your board and calibrates its own tolerance" — not as deep learning.

### 4.2 The workflow is real and demoable

Verified end-to-end in code: project creation, convention-folder import, per-role import with drag-drop, Learn (writes `learned_reference.png`, `tolerance_map.png`, `anomaly_threshold_map.png`, `learning_summary.json`, `alignment_summary.csv`, `threshold_sweep.csv`), false-call before/after comparison with HTML/JSON/CSV reports, sample inspection with anomaly overlays, client report export (HTML + PDF), visual evidence export, model versioning/activation, and the `learn-from-images` / `client-image-learning-demo` CLI equivalents used by CI. Role gating and audit events cover every step. Nothing is mocked.

### 4.3 Confirmed defects — fix before demonstrating accuracy claims

| # | Defect | Evidence | Effect |
|---|--------|----------|--------|
| L1 | **Zero-OK-validation training silently deploys threshold 0.5.** With no OK/NG validation images every sweep row trivially meets both targets, so the lowest candidate (0.5) is selected and persisted; the summary claims "default learned threshold was retained", and status is "OK" so no warning fires. Training without OK Validation is a legal GUI path (gate is 1 golden or 5 OK-learning images). | `ImageOnlyPcbLearningService.cs:219-223, 248-301`; gate at `ImageLearningProjectService.cs:88-99`; unasserted in `ImageOnlyPcbLearningServiceTests.cs:162-182` | A hair-trigger model flags essentially everything NG — the exact opposite of the near-zero-false-positive goal, on the most likely first-use path. Fix: retain the default threshold, set status REVIEW, and gate Learn (or warn hard) when OK Validation is empty. |
| L2 | **Calibration and runtime score definitions differ.** During calibration the region gate uses `DefaultLearnedThreshold` (LearnedThreshold is still 0); at inspection it uses the learned threshold. The image score is discontinuous in that gate (max-region-score vs 99.5th percentile), so an image scored 1.2 at calibration can score 4.0 at deployment. | `ImageOnlyPcbLearningService.cs:100-103, 321-327` | The calibrated false-call rate does not hold at inspection time. Fix: compute validation scores with the same gating rule that will be deployed (e.g., two-pass calibration or score definition independent of the gate). |
| L3 | **Tolerance map degrades between calibration and runtime.** Calibration uses the in-memory float tolerance; runtime reloads it from an 8-bit PNG scaled x16 — quantized to 1/16 steps and hard-capped at 15.94, clipping high-variance pixels (misaligned edges, specular pads). | `ImageOnlyPcbLearningService.cs:682-686, 799-802` | Runtime scores inflate above calibrated scores exactly where variance is high — unexpected false calls. Fix: persist tolerance losslessly (16-bit PNG or binary) or calibrate against the reloaded artifact. |
| L4 | **One corrupt-but-recognized image aborts the whole training run.** WPF's `BitmapDecoder` throws `FileFormatException` (a `FormatException`) for truncated files; the skip filters only catch IO/UnauthorizedAccess/InvalidData/NotSupported. | `ImageOnlyPcbLearningService.cs:479`; also `ImageLearningProjectService.cs:257`, `AiTrainingSetupView.xaml.cs:401` | A single bad customer file kills the demo instead of being skipped with a warning. |
| L5 | **No sample-size statistics behind the false-call claim.** The sweep picks the lowest threshold meeting the target; with fewer than ~20 OK validation images the 5% target forces an empirical false-call count of 0, so the threshold lands at max(OK score)+0.001 with no safety margin. By order statistics, the next good board exceeds that with probability ~1/(N+1) (~17% at N=5) while the report prints "0.0%". | `ImageOnlyPcbLearningService.cs:256-259, 287-290` | The headline metric is statistically hollow on small sets. Fix: minimum OK-validation count before quoting a rate, confidence bounds (e.g., Clopper-Pearson upper bound), and/or a margin above max OK score; report the measured count together with the OK Validation image count (for example "0 of 25 OK validation images flagged") rather than a bare percentage. |
| L6 | **"Export Client Learning Report" runs the full pipeline synchronously on the UI thread** (`RunUiAction`, not `RunLongActionAsync`), freezing the HMI during the export the client will be watching. | `AiTrainingSetupView.xaml.cs:198-203` | Visible freeze in the demo's finale; also violates the no-UI-thread-blocking rule. |
| L7 | **Mixed units in the learned engine's result contract:** `DifferenceScore` is raw mean-abs-diff percent while Review/NG thresholds are anomaly-score units. | `Services/LearnedPcbVisualInspectionEngine.cs:59-61` | Any consumer comparing them (the established semantic of those fields) gets nonsense. Surface the anomaly score instead. |
| L8 | **Out-of-frame pixels are filled with the reference itself after translation,** zeroing differences in the shifted border band (up to 20 px). | `ImageOnlyPcbLearningService.cs:655-672` | Structural escape path (and false-call suppressor) at board edges. Mark the band as untested/REVIEW instead. |

Secondary observations: the GUI's "Calibrate False Calls" button re-runs full learning and creates a new model version per click (`AiTrainingSetupView.xaml.cs:166-180`); the "false calls before learning" baseline runs the pixel-diff engine at its most trigger-happy setting (`MaximizeDefectRecall`), which flatters the after-learning number; the comparison service computes a "recommended threshold" that is displayed but never deployed, so it can disagree with the model's actual threshold; nearest-neighbor downsampling aliases fine traces (area-average would stabilize scores); the model package (PNG + DB reads) is reloaded on every inspection call.

### 4.4 Robustness envelope (set demo expectations accordingly)

Handled well: a few pixels of translation jitter, uniform brightness change, learned per-pixel variation. Not handled: rotation (2 degrees displaces corners ~19 px at 768 grid — beyond and unlike the translation budget), scale/perspective (phone photos), directional lighting/shadow changes, color-only defects (grayscale pipeline), defects smaller than the min-area at model resolution (16 px at 768x768; the CLI demo runs far coarser). Handheld phone photos of boards will mass-false-call. Demo and pilot with fixtured, same-camera, same-framing imagery (tripod rig or scanner), and state the fixturing requirement in the client material — the workflow doc currently does not warn that rotation/scale are unhandled.

### 4.5 Milestone verdict

- **Capability ("program takes user images and self-trains"): met.** The guided GUI workflow exists, drives real computation, and produces reviewable evidence.
- **Trust ("properly discern defective vs good with a minimal false-call rate"): not yet.** L1-L3 mean the deployed threshold does not deliver the calibrated rate even on the calibration distribution; L5 means small-sample claims overstate. These are days-scale fixes, not redesigns.
- **Stage 1 exit per the roadmap: correctly gated.** `Docs/Milestone_Status_Stage1_Exit_Stage2_Camera_Pilot.md` already (rightly) requires customer-dataset evidence before exit; after the P0 fixes, run the customer/evaluator image groups through AI Training Setup or `learn-from-images` and review the generated package.

## 5. Spec Traceability Highlights (client documents vs code)

Implemented and verified: image upload (PNG/JPG) with hash dedup; offline inference; defect overlays with boxes/labels and confidence; defect table columns; Start/Stop/Next Board/Save Result; alarm log with timestamps; ROI editor with zoom/pan and revision history; batch metrics (correct formulas) with CSV/report export and red-tinted failures; export confirmation dialogs; filterable, sortable logs; configurable storage root; SQLite persistence; model version control; configurable confidence thresholds; Operator/Engineer/Admin roles enforced on navigation and actions; 8-hour soak-test machinery.

Gaps against the spec, ranked by client visibility:

1. **3D Profile Viewer is not 3D** — 2D bitmap height map; no rotate/zoom/pan, no peak markers on the slice, no defect-list synchronization; "Volume" is a placeholder (`Views/ProfileView.xaml.cs:315-397, 462-475`). Either implement a real height-map interaction set or retitle the module (e.g., "Height Map Viewer (Sample Data)") to protect credibility.
2. **Mandatory AOI defect set incomplete** — default taxonomy has 8 classes; Misalignment, Cold Joint, Shield Can Gap, and Solder Volume are absent; Connector Pin Height / 3D Coplanarity survive only as aliases (`Services/DefectTaxonomyService.cs:226-269`). The taxonomy CSV import exists — ship a default taxonomy covering the classification table's mandatory set (detection can remain future work, but the classes must exist for labeling/reporting).
3. **No trained model deliverable yet** — spec expects a delivered AI model artifact; current deliverable is the learned statistical model + optional customer ONNX. Position the learned visual model as the Stage 1 "AI model v1.0" deliverable explicitly, with the ONNX registry as the upgrade path.
4. **PDF exports are ASCII-only** and drop non-ASCII characters (`Services/PdfExportService.cs:176-177`) — Korean text disappears from PDFs in a Korea-first product. HTML reports are fine; fix PDF text encoding or generate PDFs from the HTML.
5. **30-day auto-archive copies rather than moves** (`Data/AoiDatabase.cs:7631-7689`) — the log tables grow unbounded; acceptable now, but rename in UI ("archive index") or implement purge.
6. **Role deltas vs spec:** Log & Export page is Admin-only (Engineers cannot even view logs; spec reserves only export for Admin) (`Services/RoleAuthorization.cs:38`); AI Model Test batch runs are open to Operators while the Requirements Traceability Matrix claims they are restricted (`Services/RoleAuthorization.cs:35`, `Docs/Requirements_Traceability_Matrix.md:46`) — fix the RTM or the gate.
7. **REVIEW verdicts count as NG in batch metrics** (`Services/BatchValidationService.cs:215`) — inflates the apparent false-call rate of the prototype engine; either add a three-outcome breakdown or footnote the mapping in reports.
8. **Recipe "Processing & Tolerance Rules" panel is decorative** — X/Y tolerance, rotation tolerance, IPC class, lighting profile, false-call policy controls are never read or persisted (`Views/RecipeView.xaml:197-229`); a probing client will find this in one click. Wire them or mark them "Planned (Stage 2)". Test Run also exercises the last *saved* revision, not unsaved ROI edits (`Views/RecipeView.xaml.cs:341-362`).
9. **12-column responsive grid** from the UI guideline does not exist (ad-hoc star grids are arguably fine — get sign-off rather than build it).

## 6. Recommended Plan

### 6.1 P0 — before the next client demo (order matters)

1. Learning trust fixes: L1 (no-validation guard), L2 (consistent score definition), L3 (lossless tolerance persistence), L4 (corrupt-file skip), L6 (async report export). Add regression tests asserting the persisted `LearnedThreshold` in the no-validation case and calibration/runtime score equality on a fixed fixture.
2. Metric wording: quote the measured false-call count against the OK Validation image count with a minimum-N gate (L5) instead of a bare percentage; keep the threshold-sweep CSV as backup evidence.
3. Shell banner: make the center band wrap or prioritize (SIM/MOCK first), remove the fatal MaxWidths (G1/G2), and launch maximized or resize to the 1920x1080 preset by default.
4. Page geometry: MonitorView action band above the fold (G3 — move the action band into a fixed row outside the scroll region or constrain the Viewbox height), AIModelTest Auto-row ScrollViewer (G4), wizard footer height (G5), Compare/Review scroll at small heights (G6).
5. Demo semantics: amber demo chips to purple; remove green from demo "Verified OK" (Section 3.2 item 3); label or hide the hardcoded SPC trend.

### 6.2 P1 — before claiming Stage 1 exit

- Statistical calibration upgrade: minimum OK-validation count, confidence bounds, optional margin policy; document the fixturing requirement (rotation/scale unhandled) in the workflow doc and client guide.
- Customer dataset run per the existing Stage 1 exit blockers, with the generated validation package reviewed and signed off.
- Font-size decision (pt vs px) executed once in the token layer + audit + Settings labels; fix the inert font preset.
- Taxonomy completion for the mandatory defect set; PDF Unicode support; RTM corrections (AI-001; module count in IMPLEMENTED_FEATURES.md); Log & Export role softening (Engineer view).
- Border-band handling (L8) and result-contract units (L7).

### 6.3 P2 — Stage 2 preparation (schedule, don't block on)

- In-memory frame path for `IInspectionEngine` + learned-model/ONNX session caching (live camera throughput).
- Rotation/scale-tolerant registration (coarse angle search or fiducial-based) to widen the robustness envelope.
- MVVM/DI refactor of the largest views; split `AoiDatabase`; split Export & Trace tabs.
- Real height-map interactions for the 3D module or retitle it; `AutomationProperties` pass for accessibility and UIA test automation.

### 6.4 Suggested client demo script (non-technical audience)

1. Prepare 20-40 OK images + all available NG images of one board model, captured on a fixed rig (same camera, distance, lighting). Split OK images: half Learning, half Validation; keep 5-10 mixed images as "Inspection" samples.
2. In `AI / Models > AI Training Setup`: create a project named after the board, drag the folders onto the role cards, press "Learn Normal PCB Appearance". Show the learned reference and tolerance map images appearing — this is the "the software learned your board" moment.
3. Show the False-Call Behavior panel: "on N good boards it never raised a false alarm; on the known-bad boards it flagged M of M" (after P0 fixes these numbers are trustworthy).
4. Press "Inspect Samples" and open the anomaly overlays — defective boards show red boxes, good boards do not.
5. Press "Export Client Learning Report" and hand over the HTML report (print or PDF after the Unicode fix). The one-command CLI (`client-image-learning-demo`) produces the same package for leave-behind evidence.
6. Close with the roadmap slide: Stage 1 (this demo, image-based) -> Stage 2 (live cameras, same GUI) -> Stages 3-4 (robot, MES), matching the roadmap document.

## 7. Checks Not Run Here

`dotnet build/test`, `Scripts/run-quality-gates.ps1`, the HMI layout audit, and navigation-performance runs require Windows and were not executed in this review environment. All layout findings above are geometry-verified against the XAML; all pipeline findings are code-verified with file:line references. CI on `windows-latest` remains the authoritative execution evidence and was reviewed for scope (build, tests, quality gates, synthetic learning evidence).
