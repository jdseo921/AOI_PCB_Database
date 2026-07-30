# Customer Specification Gap Audit

This audit maps every atomic requirement in the three customer specification documents under
`Docs/customer-specs/` to implementing code, covering tests, covering documentation, and an
honest implementation status. It is the Phase 1 deliverable of the Stage 1 readiness program
and the working companion to `Docs/Requirements_Traceability_Matrix.md` (RTM).

| Field | Value |
|---|---|
| Audit date | 2026-07-30 |
| Source specs | `AOI_PoC_GUI_Concept_and_Functional_Specification.md` · `PCBA_Defect_Classification_Table.md` · `AOI_PoC_Development_Roadmap_and_Commercialization_Plan.md` |
| Repo state audited | branch `main` at commit `92ebaa6` (specs present untracked in `Docs/customer-specs/`) |
| Method | 143 atomic requirements assigned stable IDs; each mapped to code/tests/docs by a dedicated audit pass, then **adversarially verified** by an independent pass that re-opened every cited file and re-tested every status claim. 134 findings confirmed, 10 adjusted (all adjustments applied below), 0 refuted. |
| Governing rules | AGENTS.md truthfulness gates; `Docs/standard/` VOL01 §5 (source traceability), VOL17 §51 (DoD). Simulation/mock/null-adapter/sample-CSV evidence never counts as real hardware or production capability. |

## 1. Status Vocabulary

- **Implemented** — working local Stage-1 functionality exists in code, honestly scoped and cited.
- **Partial** — part of the requirement works in Stage-1 scope; the rest is missing, config-gated with no shipped default, or only simulated. The split is stated in the notes.
- **Missing** — no meaningful implementation of the Stage-1-scoped part.
- **Deferred-to-Stage-2/3/4** — requirement inherently needs later-stage hardware/integration per the customer roadmap (Stage 1 = uploaded-image validation only; Stage 2 = live cameras/lighting; Stage 3 = robot; Stage 4 = MES/ERP). Existing boundaries/simulations are noted as architecture evidence, never as the capability itself.
- **Future** — spec §12 Future Expansion items beyond the four-stage plan (recorded with nearest prerequisite stage).

## 2. Summary Dashboard

143 atomic requirements (plus one boundary-readiness cross-check, TECH-6.2-01b).

| Status | Count | Notes |
|---|---:|---|
| Implemented | 70 (+1 cross-check) | Concentrated in GUI §4 module functions, Stage-1 scope (§5.1), technical data-management (§6.3), model governance (§6.4), roles (§8) |
| Partial | 26 | Mostly per-class defect-taxonomy conformance (severity not modeled), acceptance-criteria evidence gaps, and UI-guideline reinterpretations |
| Missing | 23 | 22 defect-classification classes absent from the taxonomy + the `.pt/.h5` model-artifact deliverable (deliberately substituted, but with no customer-facing sign-off) |
| Deferred-to-Stage-2 | 10 | Cameras, lighting, GPU decision, live feed, on-board validation |
| Deferred-to-Stage-3 | 6 | Robot transport, commands, trigger sync, safety interlock |
| Deferred-to-Stage-4 | 8 | MES/ERP REST/OPC UA, uploads, MES auth, plus Future items with Stage-4 prerequisites |

Headline conclusions:

1. **The Stage-1 functional surface is genuinely strong.** All five spec GUI modules exist as
   working, tested views; the Stage-1 scope items (image upload, offline inference, overlays,
   batch validation, CSV/annotated exports) are Implemented with real code and test citations.
2. **The single largest conformance gap is the defect classification table**: 21 of 33 spec
   defect classes have no taxonomy representation at all, and no taxonomy entry anywhere
   carries the spec's Severity or Detection Method columns. All 10 mandatory-set classes are,
   however, present, required-flagged, and test-asserted.
3. **The most important customer-facing omission is the model-format deviation**: ONNX replaces
   `.pt/.h5` for sound security reasons (SD-01/D-03 in the standard), but no customer-facing
   document states the substitution and no sign-off line exists.
4. **Acceptance criteria §11 are Partial across the board** — the machinery (benchmark p95,
   soak harnesses, export verification) exists and is honest, but executed evidence artifacts
   (a real 8-hour run, customer-dataset validation) are still outstanding, consistent with the
   repo's own Stage-1 exit blockers.
5. **Truthfulness posture is excellent and largely test-enforced** (disclaimers asserted by
   tests, simulated evidence hard-blocked from readiness claims). The audit found only two
   truthfulness-adjacent defects, both stale-label/doc issues (§12 below), not overclaims in
   evidence paths.

## 3. Spec Five-Screen Model vs Repo Focused-Window Model (audit item a)

The spec (§3) defines five main modules. The repo ships a 13-window task-oriented shell
(DESIGN.md). Mapping, as verified in `AOI_Monitor/ViewModels/MainViewModel.cs` and
`MainWindow.xaml.cs` (which aliases both vocabularies onto the same navigation keys):

| Spec module (§3/§4) | Repo window(s) | View file(s) |
|---|---|---|
| 1. Main Inspection Screen | 03 Run Inspection (primary); disposition split into 05 Defect Review; comparison into 04 Golden Compare | `MonitorView`, `ReviewView`, `CompareView` |
| 2. Recipe Editor | 06 Recipe Rules | `RecipeView` |
| 3. AI Model Test Screen | 07 AI / Models (batch validation + AI Training Setup) | `AIModelTestView`, `AiTrainingSetupView` |
| 4. Log & Export Screen | 09 Export & Trace | `ReportsView.*` |
| 5. 3D Profile Viewer | 11 3D Profile | `ProfileView` |
| — (additive) | 01 Home, 02 Board & Images, 08 Yield Analytics, 10 Calibration, 12 Hardware Readiness, 13 System Settings | `HomeView`, `LibraryView`, `SpcView`, `CalibrationView`, `PilotWizardView`/`PlannedStageView`, `SettingsView.*` |

**Recommendation: keep the focused-window model and annotate the spec mapping — do not
reconcile the software to the literal five screens.** Rationale: the decomposition reduces
per-page density (a DESIGN.md hard constraint), every spec function remains reachable, the
shell already aliases spec vocabulary (`ExerciseUiNavigationForStabilityAsync` maps
"Main Inspection"/"Recipe Editor"/"AI Model Test"/"Log & Export"/"3D Profile Viewer" onto the
same routes), and the extra windows are additive. This mapping table should be reproduced in a
customer-facing document with a one-line acknowledgment (see Deviation Register DEV-03).

## 4. GUI Functional Requirements — §4.1 Main Inspection Screen

| ID | Requirement | Status | Key evidence | Notes |
|---|---|---|---|---|
| GUI-4.1-01 | Live camera feed (Top/Side/Bottom) | Deferred-to-Stage-2 | `MonitorView.xaml(.cs)`, `FolderCameraSource`, `ICameraSource`/`GenericVisionCameraSource`; tests `CameraSourceContractTests` | Full display path + view selector exist; every source is Folder Camera Simulation or null-adapter. UI labels it honestly ("Image / Simulated Live Feed", "No Camera Connected"). Large-image viewer (`ImageViewerWindow`) covers the big-display need. |
| GUI-4.1-02 | Defect overlays w/ bounding boxes + labels | Implemented | `MonitorView.RenderOverlay` (box + `{DefectType} [RoiId] {Confidence:P0}` label), toggleable layer | Overlay color follows board verdict, not per-defect severity. Coverage is render-smoke only — no test asserts overlay geometry. |
| GUI-4.1-03 | Defect list: No, Type, Score, Side, X, Y | Implemented | `MonitorView.xaml` DefectGrid; `RefreshDefectRows` | All six spec columns present plus ROI/ROI Type/Severity/Board-mm extras. **Defect found:** the Severity column binding path does not exist on `DefectRow`, so it always renders blank (see §12 D1). Score/X/Y show as percentages, not pixels (DEV-15). |
| GUI-4.1-04 | Start / Stop / Next Board / Save Result | Implemented | `MonitorView` handlers + F5/F6/F7/Ctrl+S hotkeys; tests `UiNavigationSmokeTests.OperatorCriticalButtonsAreDiscoverableByName` | Start/Stop control simulated acquisition, honestly logged as simulated. |
| GUI-4.1-05 | Alarm log w/ timestamps + messages | Implemented | In-page Alarm/Event grid + persistent severity-graded `AlarmEventService` (ack/resolve/export; well tested) feeding the shell banner | In-page grid is an 80-row in-memory rolling log with no dedicated test. |
| GUI-4.1-06 | Real-time overlay updates | Partial | Synchronous overlay redraw per analysis, instrumented frame-to-overlay latency, session p95, >1 s warning; `InspectionLatencyService` (+tests) | Mechanism complete for Stage-1 sources; continuous live-video overlay cadence is only demonstrable with Stage-2 cameras. |
| GUI-4.1-07 | Green OK / Red NG / Yellow Warning | Implemented | `SetResultStatus` bands, `ToVerdictColor` overlays, mode text | Third state is named `REVIEW` (display also accepts `WARNING`) — DEV-08. |
| GUI-4.1-08 | Auto-save after each board | Partial | `AutoSaveCheck` + full persistence path (`WorkflowState.SetAnalysis(persist:true)`); robot-sim cycle always saves | Mechanism complete but checkbox ships **unchecked** and is not persisted — out-of-the-box behavior is manual save (deliberate review-then-save default). DEV-07. |

## 5. GUI §4.2 Recipe Editor and §4.4 Log & Export

| ID | Requirement | Status | Key evidence | Notes |
|---|---|---|---|---|
| GUI-4.2-01 | ROI drawing/editing on image | Implemented | `RecipeView` draw/move/resize/delete state machine; normalized ROI persistence unit-tested | Role- and lock-gated. Bonus: pick-and-place centroid CSV import auto-generates Presence ROIs. |
| GUI-4.2-02 | ROI types: Presence, Polarity, Solder Bridge, Height, Anomaly | Implemented | ROI type combo populated from Presence/Polarity + active taxonomy canonical classes; types scope per-ROI judgments and threshold rules | All five selectable ("Height" appears as taxonomy class "Height Error"); list is a superset. |
| GUI-4.2-03 | Params: AI Score, Height Min/Max, Volume Min/Max | Implemented | All five fields live-update and persist into recipe JSON; AI Score genuinely gates 2D inspection; Height/Volume enforced in the 3D profile path | Test gap: no unit test round-trips the per-ROI Height/Volume values (only AiScoreThreshold asserted). |
| GUI-4.2-04 | Buttons: Test Run, Save Recipe | Implemented | Test Run runs a real inspection honoring **unsaved** edits via a unit-tested preview-override; Save writes immutable `RecipeRevisions` row | |
| GUI-4.2-05 | ROI colors: yellow active, green saved | Implemented | `RenderRois` + on-screen legend | Adds a third blue "unsaved" state beyond spec. |
| GUI-4.2-06 | Zoom/pan for ROI placement | Implemented | Wheel zoom, buttons, fit-to-view, right-drag pan; zoom-compensated resize tolerance | |
| GUI-4.2-07 | Recipe revisions w/ timestamp + user ID | Implemented | Immutable revision rows (UTC revision stamp, OperatorId, ISO-8601 CreatedAtUtc) + linked RECIPE_SAVE audit event; round-trip tested | Backup/restore of recipe revisions exists in `ConfigurationBackupService` but is untested (verifier struck the wrongly-cited test). |
| GUI-4.4-01 | Log table: Time, Model, Result, Defects | Implemented | Export & Trace Inspection History tab from SQLite | "Model" rendered as Board + separate Engine column; Defects is a single suggested class per row (DEV-15 family). |
| GUI-4.4-02 | Export: CSV, Image Overlay | Implemented | CSV (inspection/review/audit) + annotated overlay PNGs; every export SHA-256-verified into ExportHistory | |
| GUI-4.4-03 | Filter by date, model, operator | Implemented | Parameterized SQL filters + extra Result/Role/Action filters | Minor gap: Export History tab ignores page filters (latest 100 rows). |
| GUI-4.4-04 | Sortable columns | Implemented | `CanUserSortColumns` on all grids with correct `SortMemberPath` (Time sorts by UTC value) | |
| GUI-4.4-05 | Confirmation dialog before export | Implemented | Yes/No "Confirm Export" before both spec export options and all major packages | Nine auxiliary repo-added report exports (readiness/MES/sync reports) write without confirmation — beyond spec scope but worth aligning. |
| GUI-4.4-06 | Auto-archive logs older than 30 days | Implemented | Startup archive-then-purge into recoverable `LogArchive` (default 30 d, Admin-configurable, pre-purge warning); thoroughly tested (`LogRetentionTests`) | **Defect found:** static on-screen label `ArchivePolicyText` still claims copy-only archiving, contradicting actual purge behavior; `IMPLEMENTED_FEATURES.md:292` repeats it (§12 D2/D3). |

## 6. GUI §4.3 AI Model Test and §4.5 3D Profile Viewer

| ID | Requirement | Status | Key evidence | Notes |
|---|---|---|---|---|
| GUI-4.3-01 | Batch test folder selection | Implemented | Folder picker + optional manifest CSV + dataset preflight | |
| GUI-4.3-02 | Metrics: Accuracy, Precision, Recall, False Call Rate | Implemented | KPI cards + TP/TN/FP/FN; `BatchValidationService.CalculateMetrics` unit-tested; REVIEW excluded from confusion matrix | False Call Rate captioned in plain language ("Good Boards Marked Defect"). Metrics reflect the **prototype engine** unless a customer ONNX model is configured. |
| GUI-4.3-03 | Results table: Image, GT, AI Result, Score, Pass/Fail | Implemented | All five + timing/defect/side/refdes/lot/board/notes; honest N/A state for unlabeled rows | |
| GUI-4.3-04 | Buttons: Run Test Again, Export CSV, Export Report | Implemented | Re-run = Run Batch Inspection; report = Stage 1 Validation Package (HTML + native text-PDF); role-gated | |
| GUI-4.3-05 | Highlight failed samples in red | Implemented | Row style + red FAIL chip + red ROI in annotated exports | |
| GUI-4.3-06 | Image preview per test case | Implemented | Preview Selected opens annotated preview window | |
| GUI-4.3-07 | Store test results in local DB | Implemented | `RecordBatchTestRun` transactionally persists runs/rows/breakdowns; reloaded on navigation; tested | |
| GUI-4.5-01 | 3D height map (color-coded) | Implemented | Genuine WPF `Viewport3D` mesh (not just 2D map) + top-down heat-map inset; mesh builder unit-tested | Sample CSV only; page permanently labeled "Sample Data Mode / 3D Camera Not Connected". Ready-made `SampleData/profile_height_map_sample.csv` ships. |
| GUI-4.5-02 | Defect details: Type, Height, Volume | Implemented | Details panel; type = statistical heuristic; volume = flood-fill estimate honestly tooltipped "(sample data)" | User_Manual still calls volume a "placeholder" — stale (§12 D7). |
| GUI-4.5-03 | Height slice graph w/ peak markers | Implemented | Slice polyline + labeled peak markers; `FindPeakIndices` unit-tested | |
| GUI-4.5-04 | Accept / Reject Defect buttons | Implemented | Persist SQLite review/audit events carrying the 3D-not-connected caveat; tested | |
| GUI-4.5-05 | Rotate / Zoom / Pan | Implemented | Drag-rotate, wheel-zoom, right-drag pan with clamps + Reset View | User_Manual mentions middle-drag pan which is not wired (§12 D7). |
| GUI-4.5-06 | Dynamic height scale legend | Implemented | Legend min/max update per CSV; gradient matches surface material | |
| GUI-4.5-07 | Sync with defect list selection | Implemented | Bi-directional selection sync with reentrancy guard + scroll-into-view | |

## 7. Stage Requirements — §5.1 Stage 1 (and roadmap Stage-1 scope)

| ID | Requirement | Status | Key evidence | Notes |
|---|---|---|---|---|
| GUI-5.1-01 / ROAD-S1-01 | Image upload (PNG/JPG) | Implemented | Library single/batch/folder import + learning-project importer; SHA-256 dedupe; bad-file skip; tested | |
| GUI-5.1-02 / ROAD-S1-02 | Offline AI inference | Implemented | Three local engines: pixel-difference prototype (default, labeled non-ML), **image-only learned visual model (works end-to-end out of the box)**, ONNX slot (customer-supplied model; missing model → honest REVIEW) | ONNX runtime deviation → DEV-01/DEV-02. |
| GUI-5.1-03 / ROAD-S1-03 | Overlays + confidence scores | Implemented | Overlay rendering + confidence labels + heatmap/overlay export services (tested) | |
| GUI-5.1-04 | Export test results for customer validation | Implemented | CSV + HTML/Markdown report with signature block + one-button evidence package + SHA-256 verification + `stage1-exit` CLI | |
| GUI-5.1-05 | Deliver trained AI model + report | Partial | Report delivery complete. "Trained model" exists only as image-learned statistical artifacts (learned reference, tolerance map) exportable per project; no production neural model is bundled — repo states this honestly | Real closure requires customer dataset + acceptance (Stage-1 exit blocker). |
| GUI-5.1-06 | Deliverable: model in .pt or .h5 | **Missing** | No `.pt/.h5` (security-rejected: SD-01, VOL08 bans pickle-bearing formats) **and no ONNX artifact ships either**; substitution documented only in the internal standard | **The deviation needs a customer-facing statement + sign-off line — none exists in User_Manual, Client_Test_Kit_Guide, Installation_Guide, or validation report template.** DEV-01. |
| GUI-5.1-07 / ROAD-S1-05 | Deliverable: CSV + annotated images | Implemented | Generated per dataset by UI and CLI; correctly not committed | |
| ROAD-S1-04 | Batch test tool for customer datasets | Implemented | Rich manifest CSV, confusion metrics, preflight gates, headless CLI | |
| ROAD-S1-06 | Customer validation of AI accuracy | Partial | Complete toolchain (preflight, acceptance thresholds, sign-off report, readiness gates) | The validation **activity** needs the customer dataset — matches the repo's declared Stage-1 exit blockers. All in-repo accuracy evidence is labeled synthetic/pipeline-proof. |
| ROAD-S1-07 | Deliverables: model v1.0, PoC GUI, validation report | Partial | GUI + report generator real; "AI model v1.0" not shipped (same honest gap as GUI-5.1-05/06) | |

## 8. Deferred Stages — §5.2–§5.4 boundary verification (audit scope: boundaries must exist, be contract-tested, and be truthfully labeled)

All verified as Deferred with **strong, honestly-labeled, contract-tested boundaries**. Honest
labeling here is test-enforced: tests assert disclaimers ("No real robot command was sent",
"not safety certification", "Not production MES"), assert `IsSimulated` propagation, assert
simulated camera evidence yields `NOT VALIDATED` readiness, and assert no vendor SDK packages
(Basler/Hikrobot/Cognex/Fanuc/OpcUa/Plc…) exist in any csproj.

| ID | Requirement | Status | Boundary evidence |
|---|---|---|---|
| GUI-5.2-01 | GigE/USB3 cameras | Deferred-to-Stage-2 | `IVisionCameraAdapter` + manifest-validated plugin loader + `CameraSourceContractTests`/`VendorAdapterTemplateTests`; default null adapter says "No real camera readiness is claimed" |
| GUI-5.2-02 | Real-time acquisition | Deferred-to-Stage-2 | Frame-per-inspection pull model + `CameraAcceptanceTestService` timing metrics; simulation stamped "simulation evidence only" |
| GUI-5.2-03 | Camera trigger + lighting sync | Deferred-to-Stage-2 | Lighting-sync-before-acquisition implemented against simulated/null controllers; real TCP-text path exists but unvalidated; acceptance timing tested |
| GUI-5.2-04 | Live feed Top/Side/Bottom in GUI | Partial | View selection/switching/display fully working Stage-1 code over simulation; the "live" half needs Stage-2 hardware |
| GUI-5.2-05 | Customer validation on actual boards | Deferred-to-Stage-2 | Pilot wizard + Stage-2 evidence package that **rejects** simulated camera evidence for real-hardware readiness (tested) |
| GUI-5.3-01..04 | Robot transport, Load/Inspect/Unload, trigger sync, safety/e-stop | Deferred-to-Stage-3 | `IRobotController` (protocol-neutral; no Ethernet/RS-485 driver by design), full state machine + invalid-transition rejection, six-signal interlock model, fail-safe no-bypass default; all contract-tested; every success message ends "No real robot command was sent" |
| GUI-5.4-01..04 | MES REST/OPC UA, data exchange, uploads, MES auth | Deferred-to-Stage-4 | Substantive `MesRestClient` (auth/retry/schema validation/multipart image upload/spool/redaction, 18 tests vs fake handlers); payload schema exceeds spec's four fields; OPC UA is a named null boundary ("No OPC UA package… bundled"); MES-auth mode deliberately inert (Operator-only + no-IdP notice) |
| TECH-6.2-01b | Camera boundary readiness (cross-check) | Implemented | The seam itself (plugin loader, per-view acceptance metrics, Stage2CameraPilot profile, evidence CLI) is complete and honesty-hardened |

## 9. Technical Requirements — §6 (audit item c)

| ID | Requirement | Status | Key evidence | Notes |
|---|---|---|---|---|
| TECH-6.1-01 | Windows 10/11 Industrial Edition | Implemented | `net10.0-windows`/win-x64 WPF | Runs on generic Win 10/11; no "Industrial Edition" check (standard names Win 11 IoT LTSC as reference perf platform only). DEV-14. |
| TECH-6.1-02 | .NET / C# | Implemented | Entire app/tests/tools are C#/.NET 10 + WPF | Python exists only as offline ML tooling (`Scripts/ml`) and standards QA (`Scripts/standard_catalogue.py`). |
| TECH-6.1-03 | GPU acceleration (NVIDIA CUDA) | Deferred-to-Stage-2 | **No GPU path exists anywhere**: CPU-only `Microsoft.ML.OnnxRuntime` package, `InferenceSession` with no SessionOptions, `evaluate_onnx.py` pins `CPUExecutionProvider` | Deliberate + documented: SD-12 supersedes CUDA; OD-02 gates GPU EP adoption on Stage-2 latency evidence; D-01 CPU-EP baseline. DEV-02. |
| TECH-6.2-01 | 2D/3D cameras GigE/USB3 | Deferred-to-Stage-2 | See §8 | |
| TECH-6.2-02 | Lighting via serial/Ethernet | Deferred-to-Stage-2 | TCP-text path genuinely coded; serial path deliberately non-functional (System.IO.Ports not bundled, fails safely with explicit message) | |
| TECH-6.2-03 | Robot + MES via TCP/IP | Deferred-to-Stage-3 (robot) / Stage-4 (MES) | See §8 | MES is REST-over-HTTP, not raw TCP (DEV-12). |
| TECH-6.3-01 | SQLite or PostgreSQL | Implemented | SQLite with 30 versioned idempotent migrations, parameterized SQL throughout, extensive tests | Honest abstraction note: raw ADO.NET inside one static `AoiDatabase` facade (10 partial files); all call sites funnel through it, but a PostgreSQL swap would mean rewriting facade internals — no repository interface, no Npgsql. PostgreSQL remains a spec-allowed **future** option; a labeled `NullCentralProductionDatabaseClient` boundary sketches central sync without claiming it. |
| TECH-6.3-02 | Image storage path configurable | Implemented | Admin storage root (Settings + first-run wizard) relocates DB, image vault, settings, exports; tested | Granularity is the whole storage root, not image-only path (minor divergence). |
| TECH-6.3-03 | Export: CSV, PNG, PDF | Implemented | CSV + PNG throughout; **native** minimal PDF 1.4 writer (`PdfExportService`, Helvetica + Korean Type0 CID font) with signature/SHA-256 verification | PDF is text-only (no embedded images/graphics). RTM row AI-005 still says "print-to-PDF instructions" — stale (§12 D6). |
| TECH-6.4-01 | TensorFlow/PyTorch inference engine | Partial | ONNX Runtime engine fully wired (parsers, label maps, registry) + documented external PyTorch→ONNX training pipeline | Verifier-adjusted to Partial: capability is config-gated with **no shipped model** and no test demonstrates a successful in-app inference. Deviation ONNX-for-TF/PyTorch is deliberate and standard-registered (SD-01). DEV-01/DEV-02. |
| TECH-6.4-02 | Model version control | Implemented | Registry with immutable IDs, SHA-256 verified **at activation** (tamper-refusal tested), lifecycle state machine with RBAC, acceptance linkage, audit events | Exceeds spec. Known gap vs own standard: registry metadata unsigned (D-03 signed-manifest half unimplemented, VOL08:579). |
| TECH-6.4-03 | Configurable confidence threshold | Implemented | Global setting + per-model + per-ROI recipe thresholds + governed threshold profiles (draft/approve/deploy RBAC, most-specific-rule resolution); range validation and role denial tested | Exceeds spec. |

## 10. UI Design Guidelines §7 and Roles §8 (audit item e)

| ID | Requirement | Status | Key evidence | Notes |
|---|---|---|---|---|
| UI-7-01 | 1920×1080 minimum | Implemented | Machine-verified floor: HMI layout audit renders every registered view at 1920×1080 × 100/125/150 % DPI and **fails the UI test suite** on unapproved clipping/missing/undersized issues | Window remains resizable below (min 1180×720) with mandated scroll policies — validated design target, not hard constraint. |
| UI-7-02 | Industrial blue/gray + green/red/yellow | Implemented | `FactoryHmiLayout.xaml` tokens; `LightBackgroundInDarkHmi` is a Fail-severity enforced audit rule | Adds blue (info) and **purple (reserved for simulated/mock/demo)** — an honest-labeling convention. Admin-gated opt-in "Industrial Light" theme exists; dark is the enforced default. |
| UI-7-03 | Sans-serif ≥ 14 pt | Partial | Segoe UI everywhere; app-wide `FontSize=14` **DIP** (= 10.5 typographic pt); audit service knows true 14 pt = 18.67 DIP but flags it Warn-only; PR gate blocks new FontSize < 14 DIP | Deliberate "14 pt-equivalent" reinterpretation, consistently hedged in docs. Decision item: obtain sign-off for the DIP interpretation or raise the floor. DEV-05. |
| UI-7-04 | Buttons ≥ 120×40 | Partial | 120×40 tokens + `SmallPrimaryButton` Fail-severity enforced audit rule for **primary operator buttons**; PR gate PR-HMI-SIZE-001 | Spec wording is unqualified; repo scopes to primary actions (secondary/mini buttons 96×34–104×38 by design). DEV-06. |
| UI-7-05 | 12-column responsive grid | Partial | No 12-column system (repo says so plainly — Milestone Review Gap 9, "pending sign-off"); responsiveness delivered via star-sizing/WrapPanel + DPI-sweeping layout audit + fixed-width PR gate | Deliberate WPF-appropriate substitution awaiting spec-owner sign-off. DEV-04. |
| ROLE-8-01 | Operator: run inspection, view results | Implemented | Default-deny `CanAccessPage`; inspection run/save ungated; all privileged predicates deny Operator; denials audited; tested both ways | |
| ROLE-8-02 | Engineer: edit recipes, test AI models | Implemented | `CanEditRecipes`/`CanRunModelTests`/`CanChangeThresholds` ≥ Engineer, enforced in handlers + tests | Superset (calibration as Stage-2 prep, etc.). |
| ROLE-8-03 | Admin: manage users, export logs, settings | Implemented | Real local user store (PBKDF2-SHA256; create/change-password/disable/delete Admin-only, double-enforced UI + service exception, audited); `CanExportLogs`/`CanManageSettings` Admin-only; tested both ways | Demo Mode passwordless role selector is blocked outside Demo Mode; MES auth is a Stage-4 boundary. |

EN/KO localization note (binding rule 5): UI-chrome localization machinery is real
(`UiPreferencesService.ApplyLocalization`, KO settings strings, `LocalizationParityTests` with
an honest ledger of untranslated tokens). **Gap: defect taxonomy classes are English-only** —
only a handful of defect-adjacent UI strings have KO translations; `DefectTaxonomyEntry` has no
localized-name facet (picked up in the taxonomy remediation, §11).

## 11. Defect Taxonomy Audit — classification table + mandatory set (audit item b)

Deep audit of `DefectTaxonomyService`, `DefectDetectionCapability`, and their tests against all
six spec tables and the ten-item Mandatory AOI Defect Set.

**Coverage arithmetic:** of 33 classification-table rows, **10 are first-class taxonomy
entries, 2 survive only as aliases** (Excess Solder→Solder Volume; Short/Solder Short→Solder
Bridge), **21 have no representation at all** — not even as labeling classes. All **10
mandatory-set classes are first-class, `IsRequired=true`, and test-asserted**
(`DefaultTaxonomyIncludesMandatoryDefectClasses`). The default taxonomy adds two non-spec
classes (Height Error, Anomaly) plus OK.

**Cross-cutting findings (all verified):**

1. **Severity and Detection Method are not modeled on any entry.** `DefectTaxonomyEntry` has no
   Severity/DetectionMethod field; the only severity on output derives from judgment status
   (NG→Major). The spec's Critical/Major/Minor column is dropped for every class. The standard
   already registers per-class severity as unimplemented future work (VOL09 AIM-111/AIM-117).
   This is why every present class grades Partial on its DEF-3.x row.
2. **Honest 3-tier detectability catalog exists** (`DefectDetectionCapability`: Anomaly2D /
   RequiresTrainedClassifier / RequiresThreeDHardware) and is test-covered — the honest
   Stage-1-vs-3D statements the spec audit demands are genuinely encoded. **But it is dead code
   at runtime**: no engine, view, or acceptance path consults it. The Stage-1 engines are
   honest by construction anyway, but nothing programmatically stops an ONNX label map from
   claiming 3D-only classes beyond an advisory CONDITIONAL.
3. **One detectability over-claim:** Shield Can Gap is tiered **Anomaly2D** although the spec
   requires **Side-View AOI** — a top-down image generally cannot see a gap under a can edge.
   The least defensible image-only claim in the catalog, and the only mandatory class with no
   dedicated tier test (§12 D4).
4. **Versioning is Partial** (AGENTS.md rule 10): snapshots persist under distinct TaxonomyIds
   (import mints `taxonomy-yyyyMMddHHmmss`, priors are deactivated never deleted, saves are
   audited with operator+role, exports hash-verified) — but there is no semantic version
   number, no migration mapping, and re-saving the *same* TaxonomyId destructively replaces its
   children. AIM-110/115/116/120 register full versioning as future work.
5. **Mandatory-set enforcement is advisory-only**: a model label map missing a required class
   goes CONDITIONAL (not FAIL) at acceptance; the per-recipe "must be in all AOI recipes" gate
   (AIM-119/FF-AIM-TAX-09) is documented but unimplemented.
6. **Normalization gap:** the literal label `Short Circuit` does not match the `Short` alias
   under the normalizer's rules, so a ground-truth CSV using the spec's exact wording produces
   an unknown-label warning (§12 D5).
7. **Localization**: canonical classes/customer labels are English-only (see §10).
8. **Mitigating mechanism:** the role-gated taxonomy **CSV import** is a shipped path by which
   every Missing class could be added as a labeling class without code change — but the shipped
   default taxonomy does not include them, and per-class severity/method still has no field.

### DEF-3.1 Solder-related

| ID | Defect (spec severity · method) | Status | Representation & honest Stage-1 detectability |
|---|---|---|---|
| DEF-3.1-01 | Solder Bridge (Critical · AOI/Visual) | Partial | First-class, MES `SB`, required; tier Anomaly2D (honest); engine suggests only hedged "Possible Solder Bridge". Severity/method not modeled. |
| DEF-3.1-02 | Insufficient Solder (Major · AOI/3D) | Partial | First-class, MES `IS`; tier RequiresTrainedClassifier (test-asserted: image-only engines must NOT claim it). 3D half → Stage 2. |
| DEF-3.1-03 | Excess Solder (Major · AOI) | Partial | Alias of 3D-only Solder Volume — **under-claims Stage-1**: a gross blob is 2D-visible; loses the distinct labeling class. Un-merge recommended. |
| DEF-3.1-04 | Cold Joint (Major · Visual) | Partial | First-class, required; tier RequiresTrainedClassifier (none ships). Spec's own Visual-vs-mandatory-AOI conflict is registered (VOL09 §31.10), not hidden. |
| DEF-3.1-05 | Poor Wetting (Major · AOI) | Missing | No entry/alias/capability row. |
| DEF-3.1-06 | Solder Crack (Major · Visual) | Missing | No representation. |
| DEF-3.1-07 | Solder Ball (Minor · AOI) | Missing | No representation despite 2D-AOI detectability per spec. |
| DEF-3.1-08 | Fillet Shape Defect (Minor · AOI) | Missing | Mentioned only as prose in a capability note. |

### DEF-3.2 Component-related

| ID | Defect | Status | Representation |
|---|---|---|---|
| DEF-3.2-01 | Missing Component (Critical · AOI) | Partial | Strongest class: first-class, required, label-map omission → CONDITIONAL (tested); tier Anomaly2D. Severity not modeled. |
| DEF-3.2-02 | Misalignment (Major · AOI) | Partial | First-class (added in documented remediation), tier Anomaly2D. |
| DEF-3.2-03 | Tombstone (Major · AOI) | Partial | First-class, required, Anomaly2D. |
| DEF-3.2-04 | Polarity Error (Critical · AOI/Visual) | Partial | First-class, required; tier RequiresTrainedClassifier (honest — marks too subtle for generic anomaly). |
| DEF-3.2-05 | Rotation Error (Major · AOI) | Missing | Only a recipe tolerance parameter and robustness-study perturbation — no class. |
| DEF-3.2-06 | Bent Lead (Major · AOI/Visual) | Missing | No representation. |
| DEF-3.2-07 | Damaged Component (Major · Visual) | Missing | Nearest is the "Anomaly" catch-all; no class. |

### DEF-3.3 Solder paste printing (SPI/X-ray domain)

DEF-3.3-01..05 (Paste Misalignment, Insufficient, Excess, Slump, Void): **all Missing.**
SPI and X-ray are separate machine types outside **every** roadmap stage (Stage 2 adds AOI
cameras, not SPI) — detection is out of product scope entirely; but even the Stage-1
labeling/reporting classes are absent from the taxonomy.

### DEF-3.4 PCB / pad / surface

DEF-3.4-01..05 (Pad Lift **[Critical]**, Contamination, Scratch, Silkscreen Error, Copper
Exposure): **all Missing.** The RecipeView "Surface Defect" ROI type is a region-marking
concept, not a classification entry.

### DEF-3.5 Electrical / circuit

| ID | Defect | Status | Representation |
|---|---|---|---|
| DEF-3.5-01 | Open Circuit (Critical · ICT/AOI) | Missing | ICT beyond all stages; AOI-visible broken-trace aspect unrepresented ("Open Solder" alias maps to Insufficient Solder — different concept). |
| DEF-3.5-02 | Short Circuit (Critical · AOI) | Partial | Deliberately folded into Solder Bridge for the visible-bridge case — formally documented (SD-16). Literal "Short Circuit" label fails to normalize (§12 D5). |
| DEF-3.5-03 | Trace Damage (Major · Visual) | Missing | No representation. |
| DEF-3.5-04 | Via Defect (Major · X-ray) | Missing | X-ray beyond all stages; no labeling class either. |

### DEF-3.6 Connector / mechanical

| ID | Defect | Status | Representation |
|---|---|---|---|
| DEF-3.6-01 | Bent Pin (Major · AOI/Visual) | Missing | Connector entries cover pin height only, not deformation. |
| DEF-3.6-02 | Pin Height Error (Major · 3D AOI) | Partial | First-class as "Connector Pin Height" (spec row name preserved as alias), MES `CPH`, required; tier RequiresThreeDHardware (tested). Detection → Stage 2. |
| DEF-3.6-03 | Partial Insertion (Critical · AOI/Visual) | Missing | No representation despite Critical severity. |
| DEF-3.6-04 | Shield Can Gap (Major · Side-View AOI) | Partial | First-class, required, MES `SCG` — but tiered Anomaly2D with no side-view dependency encoded: **the one catalog over-claim** (§12 D4). Side-view acquisition is Stage-2. |

### DEF-4 Mandatory AOI Defect Set (all ten)

DEF-4-01..10 — Missing Component, Misalignment, Polarity Error, Solder Bridge, Tombstone,
Cold Joint, Shield Can Gap, Connector Pin Height, 3D Coplanarity, Solder Volume: **all
Implemented** as first-class `IsRequired=true` entries with test-asserted presence and honest
capability tiers (3D Coplanarity / Solder Volume / Connector Pin Height are
RequiresThreeDHardware — truthfully labeled 3D-dependent; the spec defect that "3D Coplanarity"
matches no classification-table row is registered in the standard). Enforcement depth caveat:
advisory CONDITIONAL only (cross-cutting finding 5). The shipped `BuiltInLabelMap` covers only
7 classes and would itself grade CONDITIONAL against the default taxonomy.

## 12. Defects and Documentation-Accuracy Issues Found (actionable)

| # | Severity | Finding | Where |
|---|---|---|---|
| D1 | UI defect | Defect list "Severity" column always blank — binding path doesn't exist on `DefectRow` (`DefectResult.Severity` never mapped; one-line fix + test) | `MonitorView.xaml` / `RefreshDefectRows` |
| D2 | Truthfulness (label) | On-screen Auto-Archive Policy label claims "source rows remain in place, so the archive is reversible" — actual behavior is archive-then-purge. Accurate text exists only in exported reports | `ReportsView.xaml:570-572` (`ArchivePolicyText`) |
| D3 | Doc accuracy | `IMPLEMENTED_FEATURES.md` is stale: "six top-level modules" shell (lists seven) with pre-rework page names; line 292 repeats the copy-only-archive claim | `IMPLEMENTED_FEATURES.md` |
| D4 | Truthfulness (capability) | Shield Can Gap tiered Anomaly2D despite spec's Side-View AOI dependency — over-claims image-only detectability; only mandatory class without a dedicated tier test | `DefectDetectionCapability.cs` |
| D5 | Functional gap | Literal ground-truth label "Short Circuit" does not normalize (only "Short"/"Solder Short" alias) → unknown-label warning on spec-exact CSVs | `DefectTaxonomyService` aliases |
| D6 | Doc accuracy | RTM AI-005 note "print-to-PDF instructions instead of native PDF library" stale vs `PdfExportService`; `Database_Schema.md` says baseline 28 vs code's 30 migrations and omits the four taxonomy tables | `Requirements_Traceability_Matrix.md`, `Database_Schema.md` |
| D7 | Doc accuracy | User_Manual: 3D volume called "placeholder" (now a real estimator); "middle-drag" pan not wired; retention settings attributed to "Engineer or Admin" but gated Admin-only | `Docs/User_Manual.md` |
| D8 | Change control | `Docs/customer-specs/` — the requirement baseline this audit maps against — is **untracked in git** | git status |
| D9 | Architecture hygiene | `DefectDetectionCapability` is runtime-dead (only tests/docs reference it); wire it into label-map validation/evidence text or record the intent | `DefectDetectionCapability.cs` |
| D10 | Repo hygiene | Stale duplicate worktree at `.claude/worktrees/vigorous-jones-098ec2/` mirrors the repo | `.claude/worktrees/` |
| D11 | Minor gap | Export History tab ignores page filters (returns latest 100 rows) | `AoiDatabase.GetExportHistory` |
| D12 | Test gaps | No test round-trips per-ROI Height/Volume params; `ConfigurationBackupService` recipe-revision backup untested; no successful-inference ONNX test (failure paths only); MonitorView overlay/verdict-color rendering untested | tests |

## 13. Acceptance Criteria — §11 evidence state (audit item f)

| ID | Criterion | Status | Evidence today | Missing |
|---|---|---|---|---|
| ACC-11-01 | GUI matches mockups and functional flow | Partial | Functional flow: all five modules working; RTM maps spec functions row-by-row; shell aliases spec vocabulary; HMI audits + navigation tests real | **No mockup images exist in the repo** — "matches mockups" is unverifiable as delivered; needs either customer mockups or sign-off on the §3 mapping table (DEV-03) |
| ACC-11-02 | Defect visualization within 1 s/image | Partial | Per-image stage timings; persisted SQLite latency traces; >1 s warnings; repeatable benchmark writing **persisted p95 evidence** to `StorageRoot/exports/performance_benchmarks/` (`benchmark_report.json/html/pdf`, `benchmark_results.csv`, `latest_benchmark_summary.json`), consumed by Stage-1/factory readiness gates; p95/over-1s math unit-tested; standard rescopes criterion to P95 budget (SD-07, DEV-10) | A preserved benchmark run on the intended demo/customer dataset for the release candidate; live-camera timing is Stage-2 (benchmarks stamp non-hardware runs SIMULATION_ONLY — tested) |
| ACC-11-03 | Stable 8-hour continuous operation | Partial | **Three 8-hour-capable soak harnesses exist**: the in-app inspection soak (`EightHourFactoryPoC` profile), the UI-navigation soak driving the real shell, and — added 2026-07-30 for this criterion — the headless `batch-soak` CLI (`BatchSoakTestService` + `AOI_Monitor.Tools/BatchSoakCommand`) looping the real batch-inspection pipeline with per-pass timing, managed memory (post-GC trend with slope-based fail), handle counts, SQLite file growth, error/alarm capture, stuck-iteration watchdog, unhandled-exception fail semantics, and HTML+JSON+CSV evidence with run ID/software version/engine config and truthful uploaded-image scope labels (tested: `BatchSoakTestServiceTests`; procedure: `Docs/Stage1_Soak_Test_Procedure.md`) | **An executed 8-hour run artifact** (none exists yet; tests run short profiles by design). Structural note: `SoakTestService` hard-codes `FolderCameraSource` (bypasses `CameraSourceFactory.ActiveSource`), so factory-evidence status is unreachable until Stage 2 wiring — fine for Stage-1 PoC evidence, which is what §11 asks |
| ACC-11-04 | Exported reports verified for accuracy | Partial | Pervasive persisted verification: per-file + aggregate SHA-256, PNG/PDF signatures, per-type required CSV headers and JSON fields, package-manifest reconciliation, ~89 `RecordVerifiedExport` call sites, readiness gates go No-Go on errors (7 dedicated tests) | Verification is **structural integrity, not content accuracy** — nothing cross-checks exported values/row counts against the database. Standard deliberately rescoped this (SD-06 "report-integrity verification", DEV-09); a content-accuracy cross-check would close the literal criterion |
| ACC-11-05 | Camera/robot/MES integration | Deferred-to-Stage-2/3/4 | Contract-tested, honesty-gated boundaries (§8); readiness gates refuse simulated evidence | Real integrations per roadmap stages; standard supersedes the lumped criterion with per-stage acceptance (SD-17) |

## 14. Data Flow §9, Deliverables §10, Future §12

| ID | Item | Status | Notes |
|---|---|---|---|
| FLOW-9-01 | Load → capture → detect → display | Partial | Detect→display end-to-end real on uploaded images; load = labeled robot simulation (S3), capture = folder simulation (S2) |
| FLOW-9-02 | Recipe → test → deploy parameters | Implemented | Role-gated revisioned recipes → batch test → evidence-gated threshold-profile deployment / model activation. "Deploy" = this station (no central recipe server — correctly later-stage) |
| FLOW-9-03 | Logs/reports exported for QC | Implemented | Complete for Stage-1 scope with verification + audit linkage |
| FLOW-9-04 | MES/ERP traceability + analytics | Deferred-to-Stage-4 | Honest mock/REST/spool boundaries; SPC labeled prototype data |
| DEL-10-01 | GUI source + assets | Implemented | Repo + publish scripts; a generated release package exists under `Release/` |
| DEL-10-02 | Database schema | Implemented | `Database_Schema.md` current table list; version note stale (D6) |
| DEL-10-03 | AI model integration module | Implemented | Complete as a module (registry/validation/acceptance); no model ships — claimed only when a configured model passes tests |
| DEL-10-04 | Hardware interface drivers | Deferred-to-Stage-2/3 | Correct: boundaries + templates + contract tests instead of drivers |
| DEL-10-05 | User manual + installation guide | Implemented | Substantive, honestly bounded; supplemented by Client Test Kit Guide |
| FUT-12-01 | Inline SMT adaptation | Future (S3 prereq) | Deliberately no inline/SMEMA/Hermes boundary — matches spec's standalone-cell overview; standard defers pending scope ADR |
| FUT-12-02 | Multi-station dashboard | Future (S4 prereq) | Single-station dashboard + StationId-carrying central-sync payloads are genuine groundwork |
| FUT-12-03 | Predictive analytics | Future (S4 prereq) | All analytics descriptive/retrospective, honestly labeled |
| FUT-12-04 | Cloud aggregation | Future (S4 prereq) | Central-sync seam FileDrop-only; REST/PostgreSQL modes state no production client bundled |

## 15. Deviation Register (customer-sign-off items, audit item d)

Deviations are deliberate and internally documented; **none currently has a customer-facing
acknowledgment**. Recommended vehicle: a one-page customer deviation statement with sign-off
lines (remediation M1).

| # | Spec says | Repo does | Standard ref | Sign-off needed |
|---|---|---|---|---|
| DEV-01 | Stage-1 deliverable "AI model (.pt or .h5)" | Single-file **ONNX** only (pickle-bearing formats security-banned); no model artifact ships until customer training completes | SD-01, D-03, VOL08 | **Yes — the one-line sign-off the spec deviation check demands** |
| DEV-02 | TensorFlow/PyTorch engine + NVIDIA CUDA | ONNX Runtime, CPU EP baseline; PyTorch only in offline training pipeline; GPU EP a tracked open decision gated on Stage-2 latency evidence | SD-01/SD-12, D-01, OD-02 | Yes |
| DEV-03 | Five main GUI modules | 13 focused workflow windows; spec vocabulary aliased; all functions reachable (mapping table §3) | VOL01 §5.3 | Yes (mapping annotation) |
| DEV-04 | 12-column responsive grid | WPF star-sizing/WrapPanel + machine-enforced layout audit at 3 DPI scales | Milestone Review Gap 9 | Yes (already flagged by repo as awaiting sign-off) |
| DEV-05 | Font ≥ 14 pt | 14 **DIP** baseline ("14 pt-equivalent", = 10.5 typographic pt); true-14pt threshold known but Warn-only | VOL12 | Yes — or raise the floor (decision) |
| DEV-06 | Buttons ≥ 120×40 (unqualified) | Enforced for primary operator actions; secondary/mini buttons smaller by design | DESIGN.md | Yes (minor) |
| DEV-07 | Auto-save after each board | Operator opt-in toggle, default off (review-then-save default) | — | Yes (minor) — or flip default |
| DEV-08 | Yellow state named "Warning" | Third verdict is "REVIEW" (display accepts both) | — | Note only |
| DEV-09 | "Reports verified for accuracy" | Report-**integrity** verification (checksums/signatures/schemas/manifests); content accuracy separate | SD-06 | Yes — or implement content cross-check (recommended) |
| DEV-10 | "Within 1 second per image" | P95 frame-to-overlay latency budget (default 1000 ms) + zero-tolerance over-threshold count | SD-07 | Note only (stricter) |
| DEV-11 | "8-hour continuous testing" | 8-hour PoC soak as minimum; production soak tracked separately; 30-min UI soak accepted for client-demo gate only | SD-08 | Note only |
| DEV-12 | MES via REST **or** OPC UA; "TCP/IP" | REST-over-HTTP invested; OPC UA a named null boundary | OD-VOL11-4 | Note only (spec-allowed choice) |
| DEV-13 | Distinct Short Circuit / Excess Solder classes | Merged into Solder Bridge / Solder Volume | SD-16 | Yes for Excess Solder (un-merge recommended — it under-claims Stage-1) |
| DEV-14 | Windows 10/11 **Industrial Edition** | Generic Win 10/11; Win 11 IoT LTSC as reference perf platform | REF-HW | Note only |
| DEV-15 | Defect list X/Y (pixels implied); log "Model" column | Percent-normalized coordinates; Board + Engine columns | — | Note only |

## 16. RTM Reconciliation (audit item g)

The RTM was spot-verified during mapping: its MI/RE/AI/LE/3D/S1..S4/TR/RP/AC rows matched the
code everywhere the auditors opened them, with these reconciliation items:

1. **Stale note:** AI-005 "print-to-PDF instructions instead of native PDF library" — a native
   text-PDF writer now exists (`PdfExportService`, tested). Update the row.
2. **No DEF coverage:** the RTM has no rows for the defect classification table or the
   mandatory set — the largest conformance gap is invisible in it. Add a DEF section (or
   reference this audit's §11 tables).
3. **No customer §11 criteria rows:** RTM "Acceptance Criteria" (AC-001..020) tracks internal
   build-prompt items, not the five customer criteria; the closest per-criterion mapping lives
   in the standard's reconciliation register (VOL01 gui-spec:176–180). Add the five ACC-11 rows
   with evidence pointers.
4. **Consistent rows confirmed:** TR-009 (soak "Partially Implemented … execution evidence
   required"), S1-004/AI-007 (ONNX "Partially Implemented"), S2-005/S3-005/S4-005 (Planned) all
   agree with this audit's findings.
5. **Cross-reference key:** GUI-4.1-xx↔MI, GUI-4.2-xx↔RE, GUI-4.3-xx↔AI, GUI-4.4-xx↔LE,
   GUI-4.5-xx↔3D, GUI-5.1/ROAD-S1↔S1, GUI-5.2↔S2, GUI-5.3↔S3, GUI-5.4↔S4, TECH↔TR, ROLE↔RP.
   Recommended: add an "Audit ID" column to the RTM on its next update.

## 17. Audit Change Control

- This audit's statuses must be updated at the end of every remediation milestone (per the
  Phase 2 plan), alongside the RTM.
- The source specs in `Docs/customer-specs/` must be committed to git (D8) so this audit's
  baseline is under change control; any future spec revision requires re-running the affected
  sections of this audit.
- Requirement IDs in this document are stable; do not renumber. New atomic requirements from
  spec revisions append with the next free number in their section.
