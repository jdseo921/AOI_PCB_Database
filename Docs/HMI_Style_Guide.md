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
- Use `FactoryCard` or `HmiKpiCard` for grouped status or metrics.
- Use `HmiTable` for operator-visible `DataGrid` controls.
- Use `HmiOperatorActionButton`, `ActionBtnGreen`, `ActionBtnBlue`, `ActionBtnAmber`, or `ActionBtnRed` consistently.
- Place primary actions at the lower-right or in a clearly marked top action band. Keep destructive actions red and separated.

## Banner

The persistent top banner must show:

- Operating mode.
- Deployment profile.
- Active engine/model status.
- Role/user.
- A purple simulation/mock warning whenever demo, simulated, or mock sources are active.
