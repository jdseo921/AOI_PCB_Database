# AOI Monitor HMI Style Guide

This guide defines the factory HMI rules for AOI Monitor screens. Use the shared resources in `AOI_Monitor/Styles/FactoryHmiLayout.xaml` before adding page-specific colors, font sizes, cards, buttons, tables, or status badges.

## Core Rules

- Use high contrast text and the shared HMI dark surfaces.
- Keep operator pages task-focused. Move diagnostics, tuning, and evidence review into Engineer/Admin areas.
- Keep critical actions visible and reachable at 1920x1080 with 125% Windows DPI scaling.
- Put dense content in a `ScrollViewer` or a table with scroll bars.
- Use 14 pt-equivalent readable text where practical. Avoid tiny labels except secondary hints.
- Long paths, model IDs, audit messages, and endpoint summaries must wrap or expose the full value through a tooltip.
- Purple always means simulated, mock, demo, or non-production evidence.

## Status Colors

Use these words and colors consistently:

- `OK`, `GO`, `READY`, `CONNECTED`: green.
- `NG`, `NO-GO`, `ERROR`, `FAIL`: red.
- `REVIEW`, `CONDITIONAL`, `WARNING`, `NOT TESTED`: amber/yellow.
- `NOT CONNECTED`, `NOT VALIDATED`, `DISABLED`: gray/blue.
- `SIMULATED`, `MOCK`, `DEMO`: purple and clearly labeled.

Do not use green for Demo mode, mock services, simulated hardware, or unvalidated acceptance evidence.

## Plain Language

Use operator-readable labels first, then technical terms where needed.

Examples:

- `MES / Traceability`: “MES is the factory system used for traceability, result upload, and production records.”
- `ONNX`: “ONNX is the supported machine-learning model file format.”
- `ROI`: “Region of interest: the board image area checked for a defect.”
- `Threshold`: “The score limit used to decide OK, REVIEW, or NG.”
- `False call`: “An OK board flagged for review or NG.”
- `Possible escape`: “A known NG board that may be missed or under-called.”
- `Acceptance`: “Evidence review against configured criteria; not automatic production release.”

## Forbidden Overclaims

Do not write:

- “Production ready” for simulated, mock, demo, or unvalidated flows.
- “Certified safe” for robot, PLC, or E-stop simulations.
- “MES connected” when the mode is Mock REST, future production placeholder, or not connected.
- “Factory accepted” when only partial evidence or a generated checklist exists.
- “Validated model” unless the model registry and acceptance evidence show an approved state.

Preferred wording:

- “Evidence collected for review.”
- “Simulated source active.”
- “Factory readiness: REVIEW.”
- “Acceptance package exported; approval still required.”
- “Not connected / not validated.”

## Layout

- Use `FactoryPageContainer` for page roots.
- Use `FactoryScrollablePage` for dense settings, reports, dashboards, and acceptance pages.
- Dense workflow page bodies that can exceed the center workspace must use vertical body scrolling inside the active page content area. The global top banner/navigation strip and bottom evidence footer remain fixed in `MainWindow` and must not be wrapped in a window-level `ScrollViewer`.
- Do not rely on 1920x1080 fitting every row of logs, reports, settings, or inspection evidence. If a page is dense, make secondary body content reachable by scrolling while keeping critical verdicts, alarms, mode/profile labels, and primary actions readable and easy to reach.
- Pages that already fit cleanly should remain unchanged; add page-level scrolling only when clipping or dense overflow risk is clear.
- Use `FactoryCard` or `HmiKpiCard` for grouped status or metrics.
- Use `HmiTable` for operator-visible `DataGrid` controls.
- Use `HmiOperatorActionButton`, `ActionBtnGreen`, `ActionBtnBlue`, `ActionBtnAmber`, or `ActionBtnRed` consistently.
- Place primary actions at the lower-right or in a clearly marked top action band. Keep destructive actions red and separated.
- Keep normal workflow navigation and noncritical readiness summaries on Home. Keep the persistent top banner compact so Home can show the workflow menu map without unnecessary scrolling. Workflow pages should fill the workspace between the persistent top banner and bottom evidence footer; only active critical/alarm status and the Home return button stay persistent outside pages.
- Keep image-heavy inspection and comparison workflows embedded in the main shell. Use separate image viewer windows only when an operator asks to enlarge a camera, screening, overlay, or comparison image; viewer windows must include zoom, fit, 100% view, PNG save, active critical/alarm status, and prototype/simulation boundary labels.

## Spacing and Alignment

- Use the shared spacing scale instead of ad-hoc pixel margins so gaps stay even across every screen: `HmiSpaceXS`=4, `HmiSpaceS`=8, `HmiSpaceM`=12, `HmiSpaceL`=16, `HmiSpaceXL`=24 (in `AOI_Monitor/Styles/FactoryHmiLayout.xaml`).
- For margins, use the gap tokens: `HmiButtonGap` (horizontal button spacing), `HmiRowGap` (vertical spacing between stacked rows), `HmiFieldGap` (wrapped filter fields), `HmiPageMargin` (page root).
- Put horizontal button rows in `HmiRightActionBand` (bottom/top-right action rows) or `HmiInlineActionBand` (toolbars), and give each button `Margin="{StaticResource HmiButtonGap}"` for even spacing. Right-aligned action rows must not touch the container edge.
- Prefer `Auto`, `*`, or `MinWidth` over fixed pixel `Width` on layout containers and columns. Fixed pixel widths leave dead space and clip under DPI scaling and localization. The PR quality gate warns (`PR-HMI-WIDTH-001`) on new fixed `Width` >= 80 outside `Styles/`.

## Clipping Prevention Gate

- Every UI/layout change must pass the HMI layout audit before handoff: `dotnet test AOI_Monitor.UiTests\AOI_Monitor.UiTests.csproj --configuration Release --filter FullyQualifiedName~HmiLayoutAuditTests`.
- Treat unapproved text, button, input, and table-header clipping as blocking for UI changes, including warning-severity clipping audit findings.
- Secondary IDs and paths may trim only when they expose the full value through a tooltip. Critical verdicts, alarm counts, role/mode labels, and primary actions must wrap, resize, or move instead of trimming.
- Review `hmi_layout_audit.json` and `hmi_layout_audit.html` after dense page changes. Add screenshot capture/manual screenshot review for new workflows, major shell changes, or any layout issue reported from the GUI; for small text/style fixes, the audit plus affected-file inspection is the default proportional check.
- For page-body scroll changes, verify that mouse wheel/touchpad scrolling moves only the active page body and that the persistent shell top and bottom bars stay visible.

## Banner

The persistent top banner must show these items in a compact instrument strip:

- Operating mode.
- Deployment profile.
- Active engine/model status.
- Role/user.
- A purple simulation/mock warning whenever demo, simulated, or mock sources are active.
- Active critical/alarm counts stay visible in the banner; the full alarm list, filters, acknowledgement, details, and export actions open from a flyout that must not increase banner height.
