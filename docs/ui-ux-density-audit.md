# Forge UI density and navigation audit

Date: 2026-10-27  
Scope: all Forge navigation groups, 42 primary routes plus Settings, shared shell/layout styles, and all routed page modules under `src/Forge.Host/wwwroot/js/pages`.

## What stands out

Forge has already adopted several useful density patterns: Daily Mission and the employee review board use scrollable lanes; Machine Readiness and plant-map cards disclose their details on demand; the parent-part workspace separates BOM, operations, and tree views into tabs; and inventory, work-center, and workforce-intelligence screens have tabbed workspaces. The main remaining issue is that some board lanes and record details still increase the whole document height, while a few key pages put multiple work areas one after another.

### Highest-value layout improvements

1. **Machine Readiness — fit the status lanes and contain card scrolling.** The board lays lanes out in columns with a 270px minimum and horizontal overflow. It switches to a two-column grid only below 900px, so common laptop widths can still hide lanes sideways. The lane card lists also have no height limit, so a busy status column makes the entire page very tall. Use an adaptive four-column grid on wide screens, two columns at laptop/tablet widths, and one column on phones. Give each lane a viewport-aware maximum height, keep its heading visible, and scroll the machine list inside the lane. Machine details already open on demand, so the compact board can stay focused on machine, location, and signal. References: `src/Forge.Host/wwwroot/js/pages/machine-kanban.js:225`, `:262`; `src/Forge.Host/wwwroot/css/forge.css:1063-1067`, `:1149-1152`.

2. **Workforce Kanban — prevent crowded work centers from stretching the 4×4 board.** The current four-column layout is a strong match for the requested plant view, and the planning rail already caps its own people lists. The work-center tiles and their worker lists can still grow without a height bound, making one busy center expand a row and push the rest of the plant below the fold. Keep the current four-column arrangement, make tile heights consistent within the available viewport, and put overflow inside each worker list. Keep names and official titles visible; reveal qualification, safety, and proposal details when a supervisor selects a person. The map view remains a separate option. References: `src/Forge.Host/wwwroot/js/pages/workforce-kanban.js:303-328`, `:369-391`; `src/Forge.Host/wwwroot/css/forge.css:885-927`, `:1160-1175`.

3. **Production Order detail — use the work order as a compact, tabbed workspace.** The order number, part/revision, quantity, due date, priority, status, and actions are already in the header. Below that, readiness, drop-dead dates, manufacturing flow, operations, BOM, quality, labor, reports, and training are rendered as successive sections. Keep a compact order context bar visible and make the body tabs `Overview`, `Operations`, `Bill of Material`, `Quality`, and `Labor & reports`. The overview can hold readiness and the manufacturing-flow summary; selecting an operation should expose its evidence and print-work-instruction action. This removes long page scrolling without hiding the core order facts. References: `src/Forge.Host/wwwroot/js/pages/orders.js:329-369`, `:372-414`.

4. **Scenarios — separate scenario selection from the editor and result.** The current page places the demo scenario center, new-scenario form, saved scenarios, and any preview result in one vertical flow. Use a compact scenario list beside a selected scenario workspace; open the new-scenario form in a drawer or dedicated tab, and show preview/recovery results in the selected workspace. Keep the WhiskerWorks demo center available under its own tab so it does not push the user's saved work down. References: `src/Forge.Host/wwwroot/js/pages/scenarios.js:158-173`, `:228`; `src/Forge.Host/wwwroot/css/forge.css:1385-1389`.

5. **Audit and QMS record review — use master/detail instead of list-then-record.** Internal Audit currently renders a record table and then appends the selected record's full revisions, findings, actions, investigations, and effectiveness evidence beneath it. This makes every selection require a long scroll back and forth. Keep the selected audit/CAPA in a right-side detail pane on wide screens and a full-screen detail view on small screens; group the selected record's sections into tabs or collapsible evidence groups. Apply the same pattern to QMS Review and QMS Planning where selecting a record currently adds its detail below the list. References: `src/Forge.Host/wwwroot/js/pages/qms-audit.js:267-271`, `:295-302`; `src/Forge.Host/wwwroot/js/pages/qms-review.js`; `src/Forge.Host/wwwroot/js/pages/qms-planning.js`.

6. **Program and Project detail — let people choose the work area.** These pages contain several parallel work areas (deliveries/orders/WBS/milestones/funding for Programs; change requests/material/purchases/returns/tasks/outages for Projects). Put high-level status and next actions in a small summary row, then use section tabs for the work areas rather than making users pass every section on the way to the one they need. Preserve the current tables and records inside each tab. References: `src/Forge.Host/wwwroot/js/pages/programs.js`, `src/Forge.Host/wwwroot/js/pages/projects.js`.

7. **Shift Continuity — keep today's handoff separate from the archive.** Capturing a handoff and reviewing prior handoffs/checklists/closeouts are different tasks. Keep the active work-center checklist and pass-on form together in the primary pane; move recent handoffs into a selectable list with the selected handoff in a detail pane. References: `src/Forge.Host/wwwroot/js/pages/shift-continuity.js`.

## Shared layout guidance

- **Avoid stacking multiple sticky bars.** The top bar is sticky, order context can also be sticky, and shared workspace tab bars stick below them. On long detail pages these can consume a large portion of a short screen. Use one compact sticky context band at a time; let the page title and longer description scroll away.
- **Keep detail scrolling inside the active work area.** Boards should constrain lane height and scroll their cards internally. Master/detail pages should keep a short list and selected detail visible together. Keep full-document scrolling for print views, long forms, and record histories where reading order matters.
- **Make filters secondary until needed.** Keep primary search, shift, and status controls visible; put infrequent filters and explanatory legends behind a clear disclosure. Machine Readiness already does this well.
- **Use density presets only where they help.** A normal view should fit the key summary and active work area; a compact view may reduce card detail for supervisors. Avoid shrinking controls or type below comfortable touch/read sizes.
- **Preserve vertical workflows that have a real sequence.** Shop Floor reporting, inspection result entry, approval forms, and audit decisions should stay in clear top-to-bottom order. Their improvement is to remove unrelated neighboring panels, not flatten the workflow into a dashboard.
- **Responsive breakpoint.** Shared `grid-2`/`grid-3` layouts collapse to one column at 900px. Keep side-by-side summary/detail panes through the larger tablet widths when each pane remains readable, and stack only when the actual content width requires it. References: `src/Forge.Host/wwwroot/css/forge.css:547-566`.

## Page-by-page coverage

The route inventory below covers every primary navigation area. “Keep” means the current page structure is generally appropriate; it may still benefit from the shared spacing and responsive guidance above.

| Navigation area | Routes reviewed | UI direction |
|---|---|---|
| Daily | `daily-mission` | Keep the bounded, scrollable lanes. On typical laptop widths, consider a lane-focus selector or adaptive 3/4-column view; five minimum-width lanes can still require horizontal scrolling. `daily-mission.js:99`; `forge.css:313-321`. |
| Supervisor | `supervisor`, `workforce-kanban`, `machine-kanban`, `performance` | Apply findings 1–2. Keep Training & Performance's employee roster/profile split and tabs; avoid turning its selected employee page into a series of unrelated panels. |
| Plan | `home`, `horizon`, `programs`, `projects`, `engineering`, `scheduling`, `scenarios`, `material-plan` | Apply findings 4 and 6. `home` and `supervisor` are aliases of Plant Overview; `horizon` is the timeline. Keep the parent-part BOM/Operations/tree tabs. Long Gantt, BOM, and material tables should retain their own horizontal scrolling. |
| Make | `orders`, `work-centers`, `tools`, `shop-floor`, `plant-setup`, `safety-rules`, `shift-continuity`, `inventory`, `receiving`, `procurement`, `maintenance`, `shipping` | Apply findings 3 and 7. `tools` is a focused Work Centers view. Inventory/Receiving and Work Centers already use tabs. Keep Shop Floor as a task-focused, touch-friendly sequence; use tabs or selected-record panes for long maintenance/shipping details. |
| Quality | `quality`, `acceptance`, `investigations`, `qms`, `qms-planning`, `qms-audit`, `qms-review`, `qms-metrics`, `commercial-quality` | Apply finding 5 to audit/review/planning record details. Keep inspection and approval sequences ordered; use compact queues and selected-record details for investigations and commercial-quality records. |
| Admin | `my-work`, `notifications`, `workforce`, `costing`, `integrity`, `reports`, `analyst`, `access`, `settings` | Keep policy forms and audit/report tables readable. Consider tabs for multiple independent queues on My Work and Notifications; use a compact list/detail pattern for Analyst and Integrity findings. `settings` is a separate route outside the grouped navigation. |

## Review limits and implementation order

This was a source-level audit of all route modules and shared CSS. The local Forge browser opened at the sign-in screen, so I did not inspect authenticated rendered states or measure screen heights across different resolutions. The findings above are supported by the page markup and responsive CSS; final sizing should be checked in the live UI after implementation.

Suggested order: Machine Readiness lane sizing; Workforce Kanban tile/list sizing; Production Order tabs; Scenarios split workspace; QMS master/detail; Program/Project tabs; then responsive cleanup across the remaining pages. No application source code was changed for this audit.

## Implementation status (Claude, 2026-10-27)

Findings 1–7 and the Daily Mission row are implemented; see `AI_HANDOFF.md` ("UI density pass implemented") for what changed on each page. The "consider" items for My Work, Notifications, Analyst and Integrity are not done. Rendered sizes still need checking in the live UI at laptop, tablet and phone widths.
