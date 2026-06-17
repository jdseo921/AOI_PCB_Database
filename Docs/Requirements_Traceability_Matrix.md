# AOI Monitor Requirements Traceability Matrix

This matrix maps the current AOI Monitor proof of concept against the staged AOI PoC requirements represented in the project documentation. It is intended for client and evaluator review.

Status definitions:

- `Implemented` means the current app has working local functionality.
- `Partially Implemented` means a useful local/prototype/simulation workflow exists, but production capability is incomplete.
- `Planned` means the codebase has a boundary, placeholder, or documented roadmap item, but the production feature is not implemented.
- `Not Applicable` means the item is out of current PoC scope.

Important boundary: this PoC is a local Windows WPF application. It uses local files and SQLite. It does not claim live camera hardware, live robot/handler control, production MES/ERP authentication, a bundled trained production ML model, or production database integration.

## Main Inspection

| Requirement ID | Requirement text | Source section | Current status | App module | Evidence/source file | Notes/gaps |
| --- | --- | --- | --- | --- | --- | --- |
| MI-001 | Provide Main Inspection as the primary operator workflow. | User Manual / Main Inspection Workflow | Implemented | Main Inspection | `AOI_Monitor/Views/MonitorView.xaml`, `AOI_Monitor/Views/MonitorView.xaml.cs` | Operator can start, stop, load next board, review result, and save results. |
| MI-002 | Show large image/live-feed display area. | Main Inspection prompt / User Manual | Implemented | Main Inspection | `AOI_Monitor/Views/MonitorView.xaml` | Uses imported images or simulated folder frames, not live hardware feed. |
| MI-003 | Support Top, Side, and Bottom view selection. | Main Inspection prompt / Stage Mapping | Implemented | Main Inspection | `AOI_Monitor/Views/MonitorView.xaml.cs`, `AOI_Monitor/Services/CameraSources.cs` | View switching works with Folder Camera Simulation. |
| MI-004 | Display defect overlay layer with bounding boxes and labels. | Acceptance checklist / Defect Overlay Display | Implemented | Main Inspection / Golden Compare | `AOI_Monitor/Views/MonitorView.xaml.cs`, `AOI_Monitor/Views/CompareView.xaml.cs` | Overlay uses current `AnalysisResult.Defects` or hotspot evidence. |
| MI-005 | Display defect list columns No, Type, Score, Side, X, Y. | Main Inspection prompt | Implemented | Main Inspection | `AOI_Monitor/Views/MonitorView.xaml` | Also includes approximate board X/Y mm when a 2D calibration profile is selected. |
| MI-006 | Provide Start, Stop, Next Board, Save Result controls. | Main Inspection prompt | Implemented | Main Inspection | `AOI_Monitor/Views/MonitorView.xaml.cs` | Start/Stop control simulated acquisition state. |
| MI-007 | Persist inspection result and defects to SQLite. | Acceptance checklist / Database Persistence | Implemented | Main Inspection / Data | `AOI_Monitor/Data/AoiDatabase.cs`, `AOI_Monitor/Services/WorkflowState.cs` | Saves `InspectionResults` and `Defects`; audit row also written. |
| MI-008 | Show large OK/NG/REVIEW/WARNING result indicator. | Main Inspection prompt | Implemented | Main Inspection | `AOI_Monitor/Views/MonitorView.xaml.cs` | Uses green/red/yellow status bands. |
| MI-009 | Log inspection start, stop, next board, completion, save, and errors. | Main Inspection prompt / Audit prompt | Implemented | Main Inspection / Log & Export | `AOI_Monitor/Views/MonitorView.xaml.cs`, `AOI_Monitor/Services/WorkflowState.cs` | Logged to event grid, review events, and audit trail. |
| MI-010 | Show station, board model, lot ID, operator, engine, and model version. | Main Inspection prompt | Implemented | Main Inspection | `AOI_Monitor/Views/MonitorView.xaml` | Local context only; no MES-sourced work order yet. |
| MI-011 | Measure inspection time and warn above 1 second. | Performance prompt / Acceptance criteria | Implemented | Main Inspection / AI Model Test | `AOI_Monitor/Models/WorkflowModels.cs`, `AOI_Monitor/Views/MonitorView.xaml.cs` | Timing fields saved; overlay timing is approximate WPF render timing. |
| MI-012 | Display approximate board coordinates for defect centers. | Calibration prompt | Partially Implemented | Main Inspection / Calibration | `AOI_Monitor/Views/MonitorView.xaml.cs`, `AOI_Monitor/Services/CalibrationTransformService.cs` | Approximate 2D planning transform only; not robot-ready coordinate mapping. |

## Recipe Editor

| Requirement ID | Requirement text | Source section | Current status | App module | Evidence/source file | Notes/gaps |
| --- | --- | --- | --- | --- | --- | --- |
| RE-001 | Engineer/Admin can edit recipes. | Roles/permissions prompt / User Manual | Implemented | Recipe Editor | `AOI_Monitor/Views/RecipeView.xaml.cs`, `AOI_Monitor/Services/RoleAuthorization.cs` | Operator access is blocked. |
| RE-002 | Load image background and draw/edit ROIs. | User Manual / Recipe Editor Workflow | Implemented | Recipe Editor | `AOI_Monitor/Views/RecipeView.xaml`, `AOI_Monitor/Views/RecipeView.xaml.cs` | Local 2D ROI editor for PoC recipes. |
| RE-003 | Configure ROI type and thresholds. | Recipe Editor Workflow | Implemented | Recipe Editor | `AOI_Monitor/Models/AoiModels.cs`, `AOI_Monitor/Views/RecipeView.xaml.cs` | Includes AI threshold and optional height/volume fields. |
| RE-004 | Save and reload recipe revisions from SQLite. | Automated tests prompt / User Manual | Implemented | Recipe Editor / Data | `AOI_Monitor/Data/AoiDatabase.cs`, `AOI_Monitor.Tests/AoiDatabaseTests.cs` | Production recipe server sync is not implemented. |
| RE-005 | Record recipe revision user and role. | Role/audit prompts | Implemented | Recipe Editor / Audit Trail | `AOI_Monitor/Data/AoiDatabase.cs`, `AOI_Monitor/Services/WorkflowState.cs` | Recipe save writes recipe table and audit event. |
| RE-006 | Lock recipe to prevent accidental edits. | User Manual / Recipe Editor Workflow | Implemented | Shell / Recipe Editor | `AOI_Monitor/MainWindow.xaml.cs`, `AOI_Monitor/Views/ReportsView.xaml.cs` | Local lock only; no central approval workflow. |

## AI Model Test

| Requirement ID | Requirement text | Source section | Current status | App module | Evidence/source file | Notes/gaps |
| --- | --- | --- | --- | --- | --- | --- |
| AI-001 | Engineer/Admin can run AI Model Test. | Roles/permissions prompt | Implemented | AI Model Test | `AOI_Monitor/Services/RoleAuthorization.cs`, `AOI_Monitor/Views/AIModelTestView.xaml.cs` | Operator page access is restricted. |
| AI-002 | Support validation folder selection and batch inspection. | User Manual / AI Model Test Workflow | Implemented | AI Model Test | `AOI_Monitor/Views/AIModelTestView.xaml.cs`, `AOI_Monitor/Services/BatchValidationService.cs` | Uses selected engine; skips bad files where possible. |
| AI-003 | Support richer manifest CSV with image, ground_truth, golden_image, defect_type, side, refdes, lot_id, board_model, notes. | AI Model Test prompt | Implemented | AI Model Test | `AOI_Monitor/Services/BatchValidationService.cs` | Simpler CSV formats remain supported. |
| AI-004 | Show accuracy, precision, recall, false-call rate, TP/TN/FP/FN, and category counts. | AI Model Test prompt | Implemented | AI Model Test | `AOI_Monitor/Views/AIModelTestView.xaml`, `AOI_Monitor/Services/BatchValidationService.cs` | Metrics depend on available ground truth. |
| AI-005 | Generate customer validation report. | AI Model Test prompt / Customer report prompt | Implemented | AI Model Test / Reports | `AOI_Monitor/Services/CustomerValidationReportService.cs`, `AOI_Monitor/Views/AIModelTestView.xaml.cs` | HTML and Markdown; print-to-PDF instructions instead of native PDF library. |
| AI-006 | Include performance summary in validation report. | Performance prompt | Implemented | AI Model Test / Reports | `AOI_Monitor/Services/CustomerValidationReportService.cs` | Reports average/min/max/count over 1 second. |
| AI-007 | Run ONNX Runtime inference when configured. | ONNX prompt | Partially Implemented | Inspection Engine / AI Model Test | `AOI_Monitor/Services/OnnxInspectionEngine.cs`, `AOI_Monitor/Services/ModelOutputParsers.cs` | Generic detection parser only; production model format validation remains customer/model-specific. |
| AI-008 | Model readiness test validates paths, labels, tensor names, and runtime session. | Model configuration prompt | Implemented | Settings / AI Model Test | `AOI_Monitor/Services/ModelConfigurationValidator.cs`, `AOI_Monitor/Views/SettingsView.xaml.cs` | Requires Engineer/Admin; records audit event. |

## Log & Export

| Requirement ID | Requirement text | Source section | Current status | App module | Evidence/source file | Notes/gaps |
| --- | --- | --- | --- | --- | --- | --- |
| LE-001 | Display SQLite inspection history, review/disposition events, and export history. | User Manual / Log & Export Workflow | Implemented | Log & Export | `AOI_Monitor/Views/ReportsView.xaml`, `AOI_Monitor/Views/ReportsView.xaml.cs` | Reads local SQLite only. |
| LE-002 | Add Audit Trail view/export. | Audit prompt | Implemented | Log & Export | `AOI_Monitor/Views/ReportsView.xaml.cs`, `AOI_Monitor/Data/AoiDatabase.cs` | Audit CSV includes UTC/local time, user, role, station, action, detail, related IDs/paths. |
| LE-003 | Filter logs by date, user/operator, role, result, and action type. | Audit prompt / Log & Export Workflow | Implemented | Log & Export | `AOI_Monitor/Views/ReportsView.xaml` | Result filter applies to inspection/review rows; action type applies to audit rows. |
| LE-004 | Export inspection history, review log, and audit trail CSV. | User Manual / Audit prompt | Implemented | Log & Export | `AOI_Monitor/Views/ReportsView.xaml.cs` | Exports are recorded in `ExportHistory` and audit trail. |
| LE-005 | Export annotated overlays. | Acceptance checklist | Implemented | Log & Export | `AOI_Monitor/Views/ReportsView.xaml.cs` | Requires accessible sample image paths. |
| LE-006 | Create Stage 1 customer package. | Customer package prompt | Implemented | Log & Export | `AOI_Monitor/Views/ReportsView.xaml.cs` | Includes reports, logs, audit trail, overlays, summaries, README, and warnings. |
| LE-007 | Record export history and link to audit events. | Audit prompt | Implemented | Log & Export / Data | `AOI_Monitor/Data/AoiDatabase.cs` | New export rows include `AuditEventId`. |
| LE-008 | Run database integrity checks and image index rebuild with progress/cancellation. | Async prompt | Implemented | Log & Export | `AOI_Monitor/Views/ReportsView.xaml.cs` | Local utility only; no production DBA monitoring. |
| LE-009 | Create production-style publish/package script. | Publish/package prompt | Implemented | Scripts | `Scripts/publish.ps1`, `README.md` | Generates clean release folder; excludes runtime/customer data. |

## 3D Profile Viewer

| Requirement ID | Requirement text | Source section | Current status | App module | Evidence/source file | Notes/gaps |
| --- | --- | --- | --- | --- | --- | --- |
| 3D-001 | Provide 3D Profile Viewer in Sample Data Mode. | 3D Profile prompt / User Manual | Implemented | 3D Profile Viewer | `AOI_Monitor/Views/ProfileView.xaml`, `AOI_Monitor/Views/ProfileView.xaml.cs` | Clearly labels `Sample Data Mode` and `3D Camera Not Connected`. |
| 3D-002 | Load height-map CSV with x, y, height columns. | 3D Profile prompt | Implemented | 3D Profile Viewer | `AOI_Monitor/Views/ProfileView.xaml.cs` | CSV parser validates required fields. |
| 3D-003 | Display 2D color-coded height map and legend. | 3D Profile prompt | Implemented | 3D Profile Viewer | `AOI_Monitor/Views/ProfileView.xaml.cs` | Sample visualization only. |
| 3D-004 | Show min/max/selected point height and slice/profile line. | 3D Profile prompt | Implemented | 3D Profile Viewer | `AOI_Monitor/Views/ProfileView.xaml.cs` | Uses CSV sample points. |
| 3D-005 | Accept/reject sample-data defects and record review events. | 3D Profile prompt | Implemented | 3D Profile Viewer / Data | `AOI_Monitor/Views/ProfileView.xaml.cs`, `AOI_Monitor/Data/AoiDatabase.cs` | Review event/audit records are local SQLite. |
| 3D-006 | Live 3D camera profile inspection. | Stage 2 planned work | Planned | 3D Profile Viewer / Camera | `Docs/Stage_Mapping.md` | Requires Stage 2 hardware integration. |

## Stage 1 Image Upload / Offline AI Validation

| Requirement ID | Requirement text | Source section | Current status | App module | Evidence/source file | Notes/gaps |
| --- | --- | --- | --- | --- | --- | --- |
| S1-001 | Import sample images into managed image vault. | Acceptance checklist / Image Import Workflow | Implemented | Image Library | `AOI_Monitor/Views/LibraryView.xaml.cs`, `AOI_Monitor/Data/AoiDatabase.cs` | Supports PNG/JPG/JPEG; hash detects duplicates. |
| S1-002 | Batch import images with progress, cancellation, and bad-file skipping. | Async prompt / Image Import Workflow | Implemented | Image Library | `AOI_Monitor/Views/LibraryView.xaml.cs` | Logs invalid/unsupported files. |
| S1-003 | Pixel Difference Prototype Engine remains functional. | Inspection engine prompt | Implemented | Inspection Engine | `AOI_Monitor/Services/PixelDifferenceInspectionEngine.cs` | Explicitly labeled prototype, not production ML. |
| S1-004 | Offline ONNX Runtime inference path exists. | ONNX prompt | Partially Implemented | Inspection Engine | `AOI_Monitor/Services/OnnxInspectionEngine.cs` | Works with valid local compatible model; no bundled trained production model. |
| S1-005 | Missing/invalid model returns clear REVIEW result, not crash. | ONNX prompt | Implemented | Inspection Engine | `AOI_Monitor/Services/OnnxInspectionEngine.cs` | Friendly evidence lines are returned. |
| S1-006 | Save selected engine, model version, confidence threshold, and model path in inspection history. | ONNX prompt | Implemented | Data / Inspection Engine | `AOI_Monitor/Data/AoiDatabase.cs`, `AOI_Monitor/Models/AoiModels.cs` | Saved in `InspectionResults`. |
| S1-007 | Stage 1 validation report/package is readable outside app. | Customer package/report prompts | Implemented | AI Model Test / Log & Export | `AOI_Monitor/Services/CustomerValidationReportService.cs`, `AOI_Monitor/Views/ReportsView.xaml.cs` | HTML/Markdown reports and package README generated. |

## Stage 2 Camera Integration

| Requirement ID | Requirement text | Source section | Current status | App module | Evidence/source file | Notes/gaps |
| --- | --- | --- | --- | --- | --- | --- |
| S2-001 | Define camera-source abstraction. | Camera abstraction prompt | Implemented | Services | `AOI_Monitor/Services/CameraSources.cs` | Includes `ICameraSource`, frame/status models, factory. |
| S2-002 | Provide null camera source. | Camera abstraction prompt | Implemented | Services | `AOI_Monitor/Services/CameraSources.cs` | Reports not connected; safe default. |
| S2-003 | Provide Folder Camera Simulation for Top/Side/Bottom. | Camera abstraction prompt | Implemented | Main Inspection / Settings | `AOI_Monitor/Services/CameraSources.cs`, `AOI_Monitor/Views/SettingsView.xaml.cs` | Simulation only; no hardware claim. |
| S2-004 | Integrate real GigE/USB3 camera SDKs. | Stage 2 planned work | Planned | Camera | `Docs/Stage_Mapping.md` | No vendor SDK dependency added. |
| S2-005 | Add lighting-controller boundary. | Integration contracts prompt | Implemented | Services / Readiness Panel | `AOI_Monitor/Services/IntegrationContracts.cs` | Null implementation only; no real lighting control. |
| S2-006 | Add 2D calibration profile placeholder. | Calibration prompt | Partially Implemented | Calibration / Main Inspection | `AOI_Monitor/Views/CalibrationView.xaml.cs`, `AOI_Monitor/Data/AoiDatabase.cs` | Approximate scale/offset planning workflow, not production calibration. |
| S2-007 | Live 3D camera hardware integration. | 3D Profile prompt / Stage Mapping | Planned | 3D Profile Viewer | `Docs/Stage_Mapping.md` | Current 3D page is sample CSV mode only. |

## Stage 3 Robot Integration

| Requirement ID | Requirement text | Source section | Current status | App module | Evidence/source file | Notes/gaps |
| --- | --- | --- | --- | --- | --- | --- |
| S3-001 | Define robot/handler interface contracts. | Integration contracts prompt | Implemented | Services | `AOI_Monitor/Services/IntegrationContracts.cs` | Includes `IRobotController`, commands, null implementation. |
| S3-002 | Provide software-only robot simulation cycle. | Robot simulation prompt | Implemented | Main Inspection | `AOI_Monitor/Services/IntegrationContracts.cs`, `AOI_Monitor/Views/MonitorView.xaml.cs` | Load -> Inspect -> Save -> Unload simulation; no real machine movement. |
| S3-003 | Simulated emergency stop interrupts cycle. | Robot simulation prompt | Implemented | Main Inspection / Services | `AOI_Monitor/Services/IntegrationContracts.cs` | Software e-stop only; not connected to safety circuit. |
| S3-004 | Log robot simulation events with cycle time. | Robot simulation prompt | Implemented | Main Inspection / Audit Trail | `AOI_Monitor/Views/MonitorView.xaml.cs`, `AOI_Monitor/Data/AoiDatabase.cs` | Stored in review/audit logs. |
| S3-005 | Control real robot, handler, conveyor, PLC, or safety hardware. | Stage 3 planned work | Planned | Robot / Handler | `Docs/Integration_Boundaries.md`, `Docs/Stage_Mapping.md` | No vendor SDK, PLC, or safety I/O implementation. |
| S3-006 | Map inspection coordinates to robot coordinates. | Stage 3 planned work | Planned | Calibration / Robot | `Docs/Stage_Mapping.md` | 2D board-mm display is planning evidence only, not robot transform validation. |

## Stage 4 MES/ERP Integration

| Requirement ID | Requirement text | Source section | Current status | App module | Evidence/source file | Notes/gaps |
| --- | --- | --- | --- | --- | --- | --- |
| S4-001 | Define MES/traceability interface contracts. | Integration contracts prompt | Implemented | Services | `AOI_Monitor/Services/IntegrationContracts.cs` | Interfaces and null implementations exist. |
| S4-002 | Provide Mock MES integration mode. | Mock MES prompt | Implemented | Settings / Log & Export | `AOI_Monitor/Services/MockMesClient.cs`, `AOI_Monitor/Services/TraceabilityUploadService.cs` | Mock REST or local JSON payload only. |
| S4-003 | Generate MES-style traceability payload. | Mock MES prompt | Implemented | Log & Export | `AOI_Monitor/Models/MesIntegrationModels.cs`, `AOI_Monitor/Views/ReportsView.xaml.cs` | Includes lot, board, station, operator, result, defects, image path. |
| S4-004 | Record Mock MES upload attempts in SQLite. | Mock MES prompt | Implemented | Data / Log & Export | `AOI_Monitor/Data/AoiDatabase.cs` | Audit row also recorded. |
| S4-005 | MES authentication / SSO. | Roles prompt / Stage Mapping | Planned | Auth / MES | `Docs/Stage_Mapping.md` | Local role selector only. MES auth marked Stage 4 planned. |
| S4-006 | Production MES/ERP result upload/writeback. | Stage 4 planned work | Planned | MES / ERP | `Docs/Integration_Boundaries.md` | Mock mode must not be treated as production MES. |

## Technical Requirements

| Requirement ID | Requirement text | Source section | Current status | App module | Evidence/source file | Notes/gaps |
| --- | --- | --- | --- | --- | --- | --- |
| TR-001 | Windows WPF desktop application. | Installation Guide / README | Implemented | App Shell | `AOI_Monitor/AOI_Monitor.csproj`, `AOI_Monitor/MainWindow.xaml` | Targets `net10.0-windows`; Windows desktop support required. |
| TR-002 | Local SQLite persistence. | User Manual / Database Persistence | Implemented | Data | `AOI_Monitor/Data/AoiDatabase.cs` | Local PoC database, not centralized production DB. |
| TR-003 | Avoid committing customer/runtime data. | README / Publish prompt | Implemented | Repo / Scripts | `.gitignore`, `Scripts/publish.ps1` | Release script excludes databases, vaults, exports, image payloads. |
| TR-004 | Async progress and cancellation for long operations. | Async prompt | Implemented | Image Library / AI Model Test / Log & Export | `AOI_Monitor/Views/LibraryView.xaml.cs`, `AOI_Monitor/Views/AIModelTestView.xaml.cs`, `AOI_Monitor/Views/ReportsView.xaml.cs` | Coverage varies by workflow; UI remains responsive for major batches/exports. |
| TR-005 | Automated non-UI tests. | Tests prompt | Implemented | Tests | `AOI_Monitor.Tests/AoiDatabaseTests.cs`, `AOI_Monitor.Tests/IntegrationContractsTests.cs` | Uses temp folders and generated tiny images. |
| TR-006 | Production-style publish/package script. | Publish/package prompt | Implemented | Scripts | `Scripts/publish.ps1`, `README.md` | Default framework-dependent win-x64; `-SelfContained` available. |
| TR-007 | Local audit trail with user accountability. | Audit prompt | Implemented | Data / Log & Export | `AOI_Monitor/Data/AoiDatabase.cs`, `AOI_Monitor/Views/ReportsView.xaml.cs` | Local audit only; production SIEM/MES audit integration planned. |
| TR-008 | Documentation deliverables. | Documentation prompt | Implemented | Docs | `Docs/Installation_Guide.md`, `Docs/User_Manual.md`, `Docs/Stage_Mapping.md`, this file | Documents distinguish implemented/planned boundaries. |
| TR-009 | 8-hour stability/soak-test mode. | Soak-test prompt | Partially Implemented | Log & Export | `AOI_Monitor/Views/ReportsView.xaml.cs` | Local simulated soak test exists; true 8-hour factory soak still requires execution evidence. |
| TR-010 | 1-second visualization target tracking. | Performance prompt | Implemented | Main Inspection / AI Model Test / Reports | `AOI_Monitor/Models/WorkflowModels.cs`, `AOI_Monitor/Services/CustomerValidationReportService.cs` | Measures local processing timings; hardware timing remains future work. |

## Roles / Permissions

| Requirement ID | Requirement text | Source section | Current status | App module | Evidence/source file | Notes/gaps |
| --- | --- | --- | --- | --- | --- | --- |
| RP-001 | Operator can run inspection, view results, save result, view guide. | Roles prompt | Implemented | Shell / Main Inspection | `AOI_Monitor/Services/RoleAuthorization.cs`, `AOI_Monitor/MainWindow.xaml.cs` | Local role selector only. |
| RP-002 | Engineer has Operator permissions plus recipes, AI Model Test, thresholds. | Roles prompt | Implemented | Shell / Recipe / AI Model Test / Settings | `AOI_Monitor/Services/RoleAuthorization.cs` | Engineer can access calibration as Stage 2 prep. |
| RP-003 | Admin has Engineer permissions plus exports, settings, paths, maintenance. | Roles prompt | Implemented | Shell / Log & Export / Settings | `AOI_Monitor/Services/RoleAuthorization.cs`, `AOI_Monitor/Views/ReportsView.xaml.cs` | Local Admin role only, not enterprise IAM. |
| RP-004 | Restricted actions show permission denied and are logged. | Roles prompt | Implemented | Shell / WorkflowState | `AOI_Monitor/Services/WorkflowState.cs`, `AOI_Monitor/MainWindow.xaml.cs` | Access denied is written to review/audit logs. |
| RP-005 | Record user ID and role in major audit records. | Roles/audit prompts | Implemented | Data / Audit Trail | `AOI_Monitor/Data/AoiDatabase.cs` | Audit events store `UserId` and `UserRole`; legacy tables also keep operator strings. |
| RP-006 | MES-based login. | Roles prompt / Stage 4 | Planned | Auth / MES | `Docs/Stage_Mapping.md` | Explicitly Stage 4 planned. |

## Acceptance Criteria

| Requirement ID | Requirement text | Source section | Current status | App module | Evidence/source file | Notes/gaps |
| --- | --- | --- | --- | --- | --- | --- |
| AC-001 | App builds and runs without ONNX model. | Stage 1 acceptance / ONNX prompt | Implemented | App / Inspection Engine | `AOI_Monitor/Services/InspectionEngineFactory.cs`, `AOI_Monitor/Services/PixelDifferenceInspectionEngine.cs` | Pixel-difference engine is default fallback. |
| AC-002 | Pixel Difference Prototype Engine workflow remains functional. | ONNX prompt | Implemented | Inspection Engine / Golden Compare | `AOI_Monitor/Services/PixelDifferenceInspectionEngine.cs` | Prototype engine, not production ML. |
| AC-003 | Missing model file produces warning, not crash. | ONNX prompt | Implemented | Inspection Engine / Settings | `AOI_Monitor/Services/OnnxInspectionEngine.cs`, `AOI_Monitor/Services/ModelConfigurationValidator.cs` | Returns `REVIEW` or readiness warning. |
| AC-004 | Inspection history records engine and model version. | Engine configuration prompt | Implemented | Data / Log & Export | `AOI_Monitor/Data/AoiDatabase.cs` | Also records model path and confidence threshold. |
| AC-005 | Existing batch test works with new manifest format. | AI Model Test prompt | Implemented | AI Model Test | `AOI_Monitor/Services/BatchValidationService.cs` | Bad rows skipped/logged where possible. |
| AC-006 | Customer report is readable outside the app. | Customer report prompt | Implemented | AI Model Test / Log & Export | `AOI_Monitor/Services/CustomerValidationReportService.cs` | HTML/Markdown artifacts. |
| AC-007 | Operator can complete full inspection cycle from Main Inspection. | Main Inspection prompt | Implemented | Main Inspection | `AOI_Monitor/Views/MonitorView.xaml.cs` | Uses simulated/imported images. |
| AC-008 | Folder-simulated camera frames work and view switching works. | Camera prompt | Implemented | Main Inspection / Camera Source | `AOI_Monitor/Services/CameraSources.cs` | No real hardware implied. |
| AC-009 | Operator cannot edit recipes or run restricted validation tests. | Roles prompt | Implemented | Shell / Role Authorization | `AOI_Monitor/Services/RoleAuthorization.cs` | Local role policy only. |
| AC-010 | Height-map CSV loads, displays, and accept/reject creates review event. | 3D Profile prompt | Implemented | 3D Profile Viewer | `AOI_Monitor/Views/ProfileView.xaml.cs` | Sample Data Mode only. |
| AC-011 | One button creates complete customer-review folder. | Customer package prompt | Implemented | Log & Export | `AOI_Monitor/Views/ReportsView.xaml.cs` | Missing optional evidence creates warnings instead of failure. |
| AC-012 | Long-running workflows show progress/cancel and skip bad files. | Async prompt | Implemented | Image Library / AI Model Test / Log & Export | `AOI_Monitor/Views/*.xaml.cs` | Scope covers major batch/export workflows. |
| AC-013 | `dotnet test` passes from repository root. | Tests prompt | Implemented | Tests | `AOI_Monitor.Tests` | Current suite covers core non-UI services. |
| AC-014 | Docs are client/evaluator readable and planned features marked. | Documentation prompt | Implemented | Docs | `Docs/*.md`, `README.md` | This matrix adds direct requirement mapping. |
| AC-015 | Hardware/MES/robot contracts build with null/mock implementations. | Integration contracts prompt | Implemented | Services | `AOI_Monitor/Services/IntegrationContracts.cs` | No vendor SDK dependencies. |
| AC-016 | Static/demo data is labeled or replaced with SQLite-backed records. | Static data prompt | Partially Implemented | Library / Reports / SPC | `AOI_Monitor/Views/LibraryView.xaml.cs`, `AOI_Monitor/Views/SpcView.xaml.cs` | Remaining demo/prototype rows are labeled; SPC remains prototype trend data. |
| AC-017 | Simulated robot cycle works and e-stop interrupts. | Robot simulation prompt | Implemented | Main Inspection / Services | `AOI_Monitor.Tests/IntegrationContractsTests.cs` | Software-only simulation. |
| AC-018 | Calibration points can be saved/reloaded and defect coordinates mapped. | Calibration prompt | Partially Implemented | Calibration / Main Inspection | `AOI_Monitor.Tests/AoiDatabaseTests.cs`, `AOI_Monitor/Views/CalibrationView.xaml.cs` | Approximate 2D transform only; no production calibration claim. |
| AC-019 | Audit CSV contains QC accountability fields. | Audit prompt | Implemented | Log & Export / Data | `AOI_Monitor/Views/ReportsView.xaml.cs`, `AOI_Monitor/Data/AoiDatabase.cs` | Local SQLite audit; production audit integration planned. |
| AC-020 | Publish script creates shareable release excluding private data. | Publish/package prompt | Implemented | Scripts | `Scripts/publish.ps1`, `README.md` | Verified release folder contained app/docs and no runtime/customer data. |
