# HMI Page Scroll Audit

This focused audit tracks page-body overflow decisions. Dense page bodies should scroll inside the active page content area; the `MainWindow` top banner/navigation strip and bottom evidence footer stay fixed.

| Page / View | Overflow risk | Scroll added | Reason | Manual check needed |
| --- | --- | --- | --- | --- |
| Main Inspection / `MonitorView` | High | Yes | The Alarm / Event Log can sit below the available center workspace and become clipped. The page body now uses `FactoryScrollablePage` while the shell bars remain fixed. | Verify at 1920x1080 and 125% DPI that mouse wheel scrolling reaches the full Alarm / Event Log and action band. |
| Home / `HomeView` | Low | Already present | The workflow map already uses the shared scroll style for future menu growth. | Spot check after adding or renaming workflow tiles. |
| AI / Models / `AIModelTestView` | Medium | Already present | Metrics, result tables, and acceptance sections already use body/internal scrolling. | Check after long validation runs or new result sections. |
| Export & Trace / `ReportsView` | Medium | Not changed | Filter/details areas and dense tables already use local scroll/table scrolling. A broad page scroll was not added in this targeted pass. | Check long evidence paths, table headers, and report tabs. |
| System Settings / `SettingsView` | Medium | Already present | Settings tabs already use `FactoryScrollablePage`. | Check lower controls in each tab after adding settings. |
| Recipe Rules / `RecipeView` | Medium | Not changed | ROI tables and tolerance detail areas already have bounded table/internal scrolling. | Check after adding rule sections or longer defect labels. |
| Defect Review / `ReviewView` | Medium | Not changed | Queue/image metadata panels use internal scrolling and current first view remains usable. | Check long engine, risk, and disposition text. |
| Board & Images / `LibraryView` | Medium | Not changed | Inventory/schema grids use DataGrid scrolling; no observed page-body clipping in this pass. | Check high row counts and long image paths. |
| Golden Compare / `CompareView` | Low/Medium | Not changed | The current comparison layout fits the center workspace; image enlargement is handled by the large-image viewer. | Check long image IDs and comparison notes. |
| Yield Analytics / `SpcView` | Low/Medium | Not changed | Dashboard and database health table fit in the observed layout. | Check with longer table/index names. |
| Calibration / `CalibrationView` | Low/Medium | Not changed | Current two-column calibration layout fit in inspection and already uses table scrolling for points. | Check long profile names and status messages. |
| 3D Profile / `ProfileView` | Low/Medium | Not changed | Existing profile layout fit in inspection; no observed clipped footer or action area. | Check full sample/acceptance result data. |
| Hardware Readiness / `PilotWizardView` | Medium | Not changed | The step table uses DataGrid scrolling; no observed page-body clipping in this targeted pass. | Check full step evidence paths and messages. |
| Guide / `GuideView` and Install / `InstallView` | Low | Already present | Support pages already have root vertical scrolling. | None for this patch. |
