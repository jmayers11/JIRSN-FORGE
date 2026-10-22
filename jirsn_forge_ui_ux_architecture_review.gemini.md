# JIRSN Forge — Comprehensive UI/UX Architecture Review

## Executive Summary & Architectural Verdict

JIRSN Forge is an ambitious, deeply engineered manufacturing operations platform built as a .NET 10 / C# modular monolith (`Forge.slnx`, 16 projects, 110 PostgreSQL migrations, 653 API routes across 38 endpoint modules in `src/Forge.Host/Api/`) paired with a zero-dependency, vanilla ES module Single-Page Application (`src/Forge.Host/wwwroot/`, 43 JavaScript/MJS modules totaling 12,185 lines of code, 549 lines of CSS in `wwwroot/css/forge.css`, and 17 lines of HTML in `wwwroot/index.html`).

From an operational governance and data-integrity standpoint, Forge achieves capabilities that many legacy tier-1 ERP/MES platforms struggle to enforce natively:
- **Append-only ledger and genealogy traceability** (`wwwroot/js/genealogy.js`, `wwwroot/js/pages/inventory.js`) where every stock balance, split, merge, remnant, and multi-parent byproduct (`V5-S1`) is backed by immutable source transactions.
- **Deterministic finite-capacity scheduling and readiness evaluation** (`wwwroot/js/model.js`, `wwwroot/js/executable.js`, `wwwroot/js/pages/horizon.js`) that distinguishes scheduled shifts from actual physical presence (`ClockedIn` vs. `SupervisorConfirmed`), evaluates machine/tooling/training/material readiness inside command transactions, and refuses to invent numbers when source data is absent.
- **Strict cryptographic and procedural separation of duties** (`wwwroot/js/ui-authority.mjs`, `wwwroot/js/api.js`) enforcing `Idempotency-Key` headers, mandatory audit trail reasons (`confirmWithReason` in `wwwroot/js/ui.js`), two-person approvals on order releases, schedule approvals, NCR Use-As-Is dispositions, and `Badge+PIN` electronic signoffs.
- **Distinctive industrial visual identity** (`wwwroot/css/forge.css`, `wwwroot/img/brand/`, `wwwroot/img/icons/`) anchored by the JIRSN red-tail hawk branding, dark graphite-and-steel surfaces (`#0F1419` to `#1E2227`), high-visibility Forge Orange (`#F97316`) and Red-Tail (`#C2410C`) accents, custom 56px glossy industrial icons, and contextual work-center stage photography.

However, because V3, V4, and V5-S1 features were rapidly layered onto the frontend by multiple implementation waves without a unified component library or design-system governance, **the user experience now suffers from severe architectural and usability friction that would impede real-world factory adoption**:
1. **Unstyled DOM Structures Across Six Pages**: Six modules built during V3.18–V3.23 and V4.6 (`pages/work-inbox.js`, `pages/notifications.js`, `pages/integrity.js`, `pages/shift-continuity.js`, `pages/scenarios.js`, and `pages/qms-metrics.js`) reference CSS layout and card classes (`.section`, `.section-head`, `.stack`, `.card`, `.card-head`, `.row`, `.panel-head`, `.eyebrow`) that **do not exist in `wwwroot/css/forge.css`**. These screens render as unstyled, borderless blocks.
2. **Developer-Centric Raw JSON & Pipe-Delimited Textareas**: Instead of structured form controls, multiple V4 screens force quality engineers, safety engineers, and executives to type raw JSON or brittle pipe-separated (`|`) strings into `<textarea>` elements—most notably `pages/commercial-quality.js` (16-row raw JSON payload editor), `pages/qms-review.js` (5-column and 8-column pipe-delimited meeting matrices), `pages/qms-audit.js` (6-column pipe-delimited evidence strings), `pages/acceptance.js` (7-column pipe-delimited inspection characteristics), `pages/safety-rules.js` (4-column pipe-delimited role slots), and `receiving.js` (4-column pipe-delimited FAI results).
3. **35-Route Flat Sidebar Overload**: `wwwroot/js/app.js` renders all 34 main navigation entries plus `Settings` in a single scrolling left sidebar for every authenticated user, regardless of whether they are a shop-floor kiosk (`ShopFloorTerminal`), a receiving clerk (`MaterialHandler`), or a corporate quality auditor (`InternalAuditor`).
4. **Severe Shop-Floor Scanning & Staging Friction**: In `wwwroot/js/scan-session.mjs` and `wwwroot/js/identification-input.mjs`, staging a single barcode under a V4.1 governed scan policy requires up to **9 discrete interactions and 2 manual text-reason entries**, while exposing internal architectural jargon (*"Begin server-owned staging"*, *"Forge did not confirm this authenticated channel's current staging pointer"*) to machine operators.
5. **WCAG 2.1 AA Contrast Failures on Critical Tokens**: Programmatic luminance analysis of `wwwroot/css/forge.css` confirms that `--faint (#6B7280)`—used across every module for table metadata, hints, and timestamps at `11px`–`12.5px`—achieves only **3.32:1 to 3.83:1** contrast against dark surfaces (failing the 4.5:1 AA floor), while white text (`#FFFFFF`) on `--forge-orange (#F97316)` in `.btn.primary` and `.seg button.on` achieves only **2.80:1**, and white text on `.pill.ok` (`#22C55E`) achieves only **2.28:1**.


## Phase 1 — Application Architecture & Role-Based Workflows

### 1.1 Frontend Architecture Inventory

| Layer | Implementation Details & Primary Files | Architectural Assessment |
|---|---|---|
| **HTML Entry & Shell** | `wwwroot/index.html` (17 lines) + `wwwroot/js/app.js` (300 lines) | Minimal HTML shell mounting `#app`. `app.js` handles development/OIDC PKCE login, lazy global search indexing (`lookups`), notification bell polling (`refreshBell`), and hash routing (`#/...`). |
| **Styling & Brand Tokens** | `wwwroot/css/forge.css` (549 lines) + `wwwroot/img/brand/*` + `wwwroot/img/icons/*` (47 PNGs) + `wwwroot/img/stages/*` (7 JPGs) | Single monolithic stylesheet using CSS custom properties (`:root`). Strong visual identity on V1–V3 pages, but missing class definitions for V3.18–V4.6 pages (`.card`, `.stack`, `.section`, `.row`). |
| **Core Transport & Auth** | `wwwroot/js/api.js` (184 lines) + `wwwroot/js/ui-authority.mjs` (20 lines) + `wwwroot/js/roles.js` (17 lines) | Clean fetch wrapper. Every `command()` attaches a UUID `Idempotency-Key` header, optional `X-Forge-Scan-Session`/`X-Forge-Scan-Version` headers, and an audit `reason`. `canAction()` evaluates direct, scoped, and time-bounded delegated permissions plus currency/amount approval thresholds. |
| **UI Primitives & Forms** | `wwwroot/js/ui.js` (190 lines) + `wwwroot/js/forms.js` (81 lines) + `wwwroot/js/icons.js` (81 lines) | Provides `esc()`, date/number formatters, `toast()`, `drawer()`, `confirmWithReason()`, `manageDialogFocus()`, `evidenceTree()`, and `formModal()`. Lacks a reusable data table, multi-row child-item repeater, autocomplete/combobox, or stepper component. |
| **Shop-Floor & Identification** | `wwwroot/js/terminal-state.mjs` (48 lines) + `wwwroot/js/identification-input.mjs` (113 lines) + `wwwroot/js/scan-session.mjs` (199 lines) + `wwwroot/js/identification-labels.js` (294 lines) | Enforces fail-closed terminal session tokens (`createTerminalSession`), two-step barcode resolution/selection (`createIdentificationStage`), V4.1 server-owned scan sessions (`mountScanSession`), and SVG barcode label lifecycle/reconciliation (`mountLabelsPanel`). |
| **Domain Feature Modules** | 12 shared feature panels (`actuals.js`, `attachments.js`, `executable.js`, `genealogy.js`, `goals.js`, `history.js`, `materialflow.js`, `model.js`, `outside-processing.js`, `people.js`, `receiving.js`, `shelflife.js`, `sourcing.js`, `tools.js`) + 28 routed page modules in `wwwroot/js/pages/*.js` | Every page module fetches multiple API endpoints in parallel via `Promise.all([tryGet(...), ...])` and rebuilds `page.innerHTML` on state changes. |

### 1.2 Role-Based Workflow Matrix: Implemented vs. Incomplete/Planned Functionality

 Forge defines **37 role codes** in `wwwroot/js/roles.js`. Below is the verified mapping of the nine enterprise user personas against the actual codebase in `src/Forge.Host`, strictly distinguishing implemented workflows from incomplete, unimplemented, or backend-blocked features.

| Persona | Mapped Forge Roles (`roles.js`) | Implemented Workflows & Frontend Modules | Incomplete, Unimplemented, or Backend-Blocked Functionality |
|---|---|---|---|
| **1. Executives & Plant Managers** | `ProgramManager`, `FacilitiesManager`, `QmsApprover` | **Factory Horizon** (`pages/horizon.js`, `model.js`): Live work-center flow diagram, 6 KPI tiles, 7-day Planned vs. Actual SVG bar chart, Top Issues drawer.<br>**Horizon Lenses & History** (`pages/timeline.js`, `pages/reports.js`): Factory, Program, Contract, Work Center, Machine, Order, and Workforce lenses; `-8h`/`-7d`/`+2w` schedule comparisons and CauseMap.<br>**Programs & Earned Value** (`pages/programs.js`): Contract CLIN/SLINs, funding, WBS progress, schedule-based EV/PV/SPI.<br>**Projects** (`pages/projects.js`): Opex/Capex budgets, change requests.<br>**QMS Reviews & Metrics** (`pages/qms-review.js`, `pages/qms-metrics.js`).<br>**Forge Analyst** (`pages/analyst.js`): Deterministic natural-language Q&A and shift operations brief (`/api/analyst/brief`). | **No financial cost accounting or CPI**: Economics are tracked in quantities and hours only; Programs calculate Schedule Performance Index (SPI) from contract CLIN unit prices, but Actual Cost (AC) and Cost Performance Index (CPI) are not modeled.<br>**Material Constraint KPI hardcoded to `null`**: In `wwwroot/js/model.js` (line 175), `kpis.materialConstraint` is hardcoded to `{ value: null, why: 'Material is not evaluated until Materials/MRP (Phase 6).' }` even though MRP (`pages/mrp.js`) is implemented!<br>**Multi-tenant org isolation**: Out of scope (single organization per deployment). |
| **2. Production Managers & Supervisors** | `Supervisor`, `SafetyEngineer`, `SafetyApprover`, `TrainingCoordinator`, `Certifier` | **Work Centers** (`pages/workcenters.js`, `actuals.js`, `executable.js`): Executable capacity, per-machine readiness evaluation, 7-day stoppages, two-person root-cause confirmation, "Can it run now?" operation readiness, real-time worker presence, and hold register.<br>**Workforce & Admin** (`pages/workforce.js`, `pages/workforce-admin.js`): 7-day qualified labor matrix, worker registration, shift assignment, work assignment + reallocation impact preview (`/api/readiness/reallocation-preview`), presence/departure/unavailability, badge issuance/revocation, training/certification.<br>**Shift Continuity** (`pages/shift-continuity.js`): Pass-on snapshots, checklists, supervisor review, incoming acknowledgment, follow-up actions, shift closeout.<br>**Safety Rules** (`pages/safety-rules.js`) & **My Work Inbox** (`pages/work-inbox.js`). | **No command to change a worker's home work center**: Once registered in `workforce-admin.js` (`POST /api/workforce/workers/{id}`), a worker's `homeWorkCenterCode` cannot be modified via any API or UI command.<br>**Overtime is not a live workforce/shift command**: Overtime exists only as a hypothetical private planning trial in `pages/scenarios.js` (`overtime`), not as an executable shift extension in `workforce-admin.js` or `plantsetup.js`.<br>**Indirect/support labor accounting**: Support activities do not accrue to a separate cost ledger (ADR 0048). |
| **3. Production Planners & Schedulers** | `Planner` | **Scheduling** (`pages/scheduling.js`, `goals.js`): Propose finite-capacity schedule (1–62 day horizon), independent second-planner approval, order delivery projection, constraint investigation/resolution/verification, hierarchical production/delivery goals, and released-order goal risk (`/api/planning/goal-risk`).<br>**Production Orders** (`pages/orders.js`): Order creation, Firm/Release/Hold/Resume/Cancel, 5-minute fingerprinted Priority change preview, readiness, drop-dead dates, contract CLIN commitment.<br>**Scenarios & Recovery** (`pages/scenarios.js`): Private what-if trials and 15 recovery option evaluations.<br>**Material Plan (MRP)** (`pages/mrp.js`): Time-phased netting, pegging ("Why?"), substitutions, exceptions, planning parameters, and demand management. | **V5 Predictive Supply Intelligence & Forecasting**: Excluded/unapproved; demand forecasts must be entered manually as `Forecast` demand lines in `pages/mrp.js`.<br>**V5 Automated Allocation Optimization**: Excluded/unapproved.<br>**Infeasible Recovery Options**: Controlled alternate routing (`alternate-work-center`) and order splitting (`split-production`) recovery cards in `pages/scenarios.js` remain `Infeasible` pending controlled source split/routing plans.<br>**No Interactive Gantt Drag-and-Drop**: The SVG Gantt charts in `pages/timeline.js` are read-only projections. |
| **4. Shop-Floor Operators** | `ShopFloorTerminal`, `ScanOperator` | **Shop Floor Kiosk** (`pages/shopfloor.js`, `terminal-state.mjs`): Badge scan identification, Clock In/Out, PIN setup/change (4–8 digits, 10-min lockout after 5 failures), work queue filtered by work center/assignment/schedule, machine selector, Operation Start, Trainer supervision badge handoff and `Badge+PIN` training evaluation, Good/Scrap quantity reporting, and Stop Work.<br>**Scan Staging** (`scan-session.mjs`, `identification-input.mjs`): Order/operation barcode lookup and V4.1 governed reference staging (Manual, KeyboardWedge, Camera `BarcodeDetector`, ConfiguredAdapter).<br>**Order Material & Byproducts** (`genealogy.js`): Material reconciliation and V5-S1 byproduct parent-lot attribution. | **No Shop-Floor Quality Hold or Defect Button**: Operators on `#/shop-floor` (`pages/shopfloor.js`) can report scrap quantities with a reason code, but **cannot raise an NCR, place a quality hold, view work-instruction PDFs/drawings, or acknowledge shift handoffs directly from the Shop Floor screen**—those actions require leaving `#/shop-floor` and signing in as a personal user on `#/quality`, `#/orders`, or `#/shift-continuity`.<br>**No Offline Queue**: Terminal locks immediately on any HTTP 5xx or network timeout (`terminal-state.mjs`). |
| **5. Quality Inspectors & Engineers** | `Inspector`, `QualityEngineer`, `QualityManager`, `QmsAuthor`, `QmsReviewer`, `QmsApprover`, `AuditPlanner`, `InternalAuditor`, `AuditApprover`, `CustomerCommunicationsApprover` | **Quality** (`pages/quality.js`): Post-operation inspections due, calibrated gauge enforcement, NCR creation/disposition/Use-As-Is approval/closure, basic CAPAs, inspection plans, and gauges.<br>**Product Acceptance** (`pages/acceptance.js`): V4.2 governed criteria revisions, unit-by-characteristic inspection sheet, exact-quantity product release (`quality.product.release`), withdrawal, and history.<br>**Investigations** (`pages/investigations.js`): V4.2 supplier-lot/measurement-finding trace snapshots, completeness checklists, candidate impact assessments, and hold linking.<br>**QMS Suite** (`pages/qms.js`, `pages/qms-planning.js`, `pages/qms-audit.js`, `pages/qms-review.js`, `pages/qms-metrics.js`, `pages/commercial-quality.js`): ISO 9001:2026 governance, risk/opportunity, objectives, controlled changes, retention, audits, governed CAPAs, management reviews, edition mappings, and commercial/provider quality.<br>**Receiving Inspection & FAI** (`receiving.js`). | **V5 Next-Gen MRB Automation**: Excluded/unapproved.<br>**Raw JSON / Pipe-Text Entry in V4 Quality Modules**: `commercial-quality.js` has no form fields for its 9 record types (uses raw JSON textareas); `qms-review.js`, `qms-audit.js`, `acceptance.js`, and `receiving.js` rely on pipe-delimited textareas.<br>**External Normative Clause Verification**: Marked `RequiresLicensedVerification` across QMS records. |
| **6. Warehouse & Inventory Personnel** | `MaterialHandler`, `InventoryController`, `Shipper`, `LabelAuthor`, `LabelApprover`, `LabelPrinter`, `IdentificationAdministrator` | **Inventory** (`pages/inventory.js`): 11 tabs (`Stock`, `Lot selection` FIFO/FEFO preview, `Tasks` allocate/scan-pick/scan-deliver, `Ledger` with "Why bought?", `Approvals` two-person count/adjustment signoff, `Locations` hierarchy, `Counts` programs & due, `Shelf life` `shelflife.js` rules/open-life/retests/extensions/excursions, `Receiving` `receiving.js` receipts/split rejections/RTV reduce-ship-replace-credit/RMAs, `Outside processing` `outside-processing.js`, `MRO levels`).<br>**Labels** (`identification-labels.js`): Code128/QR/DataMatrix SVG issuance, approval, reprint, replacement, void, print-attempt human observation reconciliation, aliases, and templates.<br>**Shipping** (`pages/shipping.js`): Containers (Carton/Crate/Pallet pack & seal), two-person Pack & Ship against customer demand. | **V5 Dynamic Allocation Optimization**: Excluded/unapproved.<br>**Customer Return Governed Restock Lookup**: In `receiving.js` (lines 147–153), completing a governed RMA restock requires manually typing raw integer `decisionId` and `expectedDecisionVersion` from `#/acceptance` because no lookup dropdown is wired.<br>**Consigned Customer Tooling (ADR 0051)**: Lacks structured legal ownership, loan authorization, and customer acceptance fields. |
| **7. Maintenance Technicians & Planners** | `MaintenanceTechnician`, `MaintenancePlanner` | **Maintenance** (`pages/maintenance.js`): 6 tabs (`Machines` status/report down/restore/propose outage, `Work orders` readiness drawer/requirements/request spare parts/schedule/start/complete checklist/independent signoff/issue & return spares, `Outages` schedule impact preview & independent approval, `PM plans` calendar & run-hour intervals with overdue machine lockout, `Assets` lifecycle/commissioning/restrictions/installed component genealogy/obsolescence, `Downtime log`).<br>**Tools & Gauges** (`tools.js` on `#/work-centers`): Tool types, registration, calibration, service cycles, custody moves, and use recording. | **Defect D1 (Unfixed Backend Bug)**: Completing a PM work order with a planned downtime window in the same instant/minute it was started returns HTTP 500 because PostgreSQL `CHECK (ends_at > starts_at)` fails at microsecond resolution (`AI_HANDOFF.md`).<br>**Tools & Gauges Hidden Inside Work Centers**: `tools.js` has no dedicated nav route; technicians must scroll to the bottom of `#/work-centers` to manage tools and calibrations. |
| **8. Purchasing & Procurement Staff** | `Buyer`, `PurchasingManager` | **Procurement** (`pages/procurement.js`, `sourcing.js`): 4 tabs (`Purchase orders` draft/independent approve/add line/change promised date/cancel/close/frozen technical package/"Why was this purchased?", `RFQs / sourcing` `sourcing.js` requisition-scoped RFQs/file-backed quotes/independent selection, `Requisitions` grouped by source kind with one-click PO conversion and alternate lookup, `Suppliers` master status & approved part sources).<br>**Engineering COTS & Obsolescence** (`pages/engineering.js`): Manufacturer part numbers (MPNs), lifecycle status, and obsolescence impact. | **PO Approval Financial Thresholds**: Kernel approval amount/currency thresholds (`V3.15`) only evaluate when a command supplies amount and currency in `ResourceRef`, which standard PO headers (`pages/procurement.js`) do not collect (prices are only on RFQ quotes and contract CLINs).<br>**External ERP/EDI Supplier Connectors**: Excluded/unapproved. |
| **9. System Administrators** | `SecurityAdministrator`, `Auditor`, `ManufacturingEngineer`, `DocumentController`, `ChangeBoard` | **Access Control** (`pages/access.js`): People registration, data markings (`CUI`, `ExportControlled`, `Proprietary`), role grants (with optional expiry), program grants, dynamic role creation, permission/scope management, separation-of-duty toggles, and approval thresholds.<br>**Settings** (`pages/settings.js`): Identity profile, hash-chained audit trail verification (`/api/kernel/audit/verify`), program access, and temporary authority delegations.<br>**Plant Setup** (`pages/plantsetup.js`): Sites, closures, shifts, safety areas, work centers, machines, and activity types.<br>**Engineering** (`pages/engineering.js`): Item classification, controlled documents, applicability rules, engineering changes (ECNs), change propagation, and alternates.<br>**Notifications** (`pages/notifications.js`) & **Data Integrity** (`pages/integrity.js`). | **Audit Log Viewer**: `#/settings` (`pages/settings.js`, lines 31–35) only runs `/api/kernel/audit/verify` to display whether the hash chain is `Intact` or `Broken`—there is **no searchable/filterable Audit Log table** in the UI despite `/api/kernel/audit` existing on the server.<br>**BOM & Routing Editor UI**: `pages/engineering.js` views item revisions, documents, and BOM alternate policies, but has **no UI screen to create a new Item, create a new Item Revision, or author BOM lines and Routing steps** (these are seeded via API in `tools/seed-sample-plant.mjs`). |


## Phase 2 — Comprehensive 12-Dimension UI/UX Audit

### 2.1 Navigation Efficiency and Menu Organization
- **35-Item Flat Sidebar (`wwwroot/js/app.js`, lines 40–84)**: The left `.sidebar` renders 34 routes across 4 static section headers (`Plan` [8 items], `Make` [11 items], `Quality` [9 items], `Admin` [6 items]) plus `Settings` at the bottom. Because `.nav` is never filtered by role or permission, a warehouse clerk or shop-floor supervisor must visually scan 35 links—many of which (`QMS Planning`, `QMS Reviews`, `Access Control`, `Scenarios`) immediately render a `403 No access` empty block when clicked.
- **Split-Brain Quality Navigation**: Quality functionality is fragmented across **9 separate top-level sidebar items**: `Quality` (`#/quality`), `Product Acceptance` (`#/acceptance`), `Investigations` (`#/investigations`), `Quality Management` (`#/qms`), `QMS Planning` (`#/qms-planning`), `Audits & CAPA` (`#/qms-audit`), `QMS Reviews` (`#/qms-review`), `Quality Metrics` (`#/qms-metrics`), and `Customer & Provider Quality` (`#/commercial-quality`). Even worse, CAPAs exist in two places (`#/quality?tab=capas` for basic CAPAs and `#/qms-audit?tab=capas` for governed CAPAs), and Inspections exist in three (`#/quality`, `#/acceptance`, and `#/inventory?tab=receiving`).
- **Dual "Home" vs. "Horizon" Confusion (`wwwroot/js/app.js`, lines 41–42; `pages/horizon.js`, line 17)**: Clicking `Home` (`#/home`) loads `horizonPage` ("Factory Horizon"), while clicking `Horizon` (`#/horizon`) loads `timelinePage` ("Horizon" lenses/Gantt). Yet on `Factory Horizon`, clicking a negative time window (`-7d`, `-24h`, `-8h`) or `+2w` silently redirects the user (`location.hash = '#/horizon?t=...'`) to `timelinePage`, completely replacing the dashboard layout with a Gantt lens view without explanation.
- **Buried High-Frequency Modules**:
  - **Tools & Calibration** (`wwwroot/js/tools.js`) has no sidebar entry and is not under `Maintenance` or `Quality`; it is appended at the very bottom of `Work Centers` (`pages/workcenters.js`, line 85) below capacity, downtime, stoppages, and reason codes.
  - **Receiving** (`wwwroot/js/receiving.js`) has no sidebar entry; it is the 9th horizontal tab inside `Inventory` (`#/inventory?tab=receiving`).
  - **Goals** (`wwwroot/js/goals.js`) has no sidebar entry; it is appended at the bottom of `Scheduling` (`pages/scheduling.js`, line 79).

### 2.2 Dashboard Readability and Information Hierarchy
- **Hardcoded `null` KPI Tile on Factory Horizon (`wwwroot/js/model.js`, line 175)**: The 5th KPI card on `#/home` ("Material Constraint") always displays **"Not yet measured — Material is not evaluated until Materials/MRP (Phase 6)"**, even though Phase 6 (`pages/mrp.js` and `/api/planning/material`) was completed in V2! This wastes prime above-the-fold real estate and undermines trust in the executive dashboard.
- **10-Request Uncached Parallel Waterfall on Every Load & 60s Poll (`wwwroot/js/pages/horizon.js`, lines 29–42)**: `horizonPage` fires 10 concurrent API calls (`/api/resources/`, `/api/planning/schedules/approved`, `/api/planning/schedules/proposed`, `/api/planning/constraints`, `/api/maintenance/downtime`, `/api/planning/capacity`, `/api/orders/`, `/api/readiness/active-work`, `/api/planning/attainment`, `/api/shopfloor/activity`) on initial load, every time a time-window button (`Now`, `+24h`, `+3d`, `+1w`) is clicked, and every 60 seconds via `setInterval`.
- **Single-Page Vertical Panel Stacking on Detail Views**:
  - **Order Detail (`pages/orders.js`, lines 62–188)** stacks up to **11 full-width panels vertically** (`Readiness now`, `Drop-dead dates`, `Operations`, `Materials`, `Quality`, `Direct labor`, `Reports and training`, `Contract commitment`, `As built and genealogy`, `Quantity states`, `Holds`, `Material to the machine`, `What became of the material`, `Applicable documentation`, and `Documentation`). A released order requires scrolling 3,500+ pixels to reach documentation or material reconciliation.
  - **Work Centers (`pages/workcenters.js`, lines 32–90)** stacks 8 full-width panels vertically (`Work Centers`, `Executable capacity`, `Downtime`, `Stoppages`, `Reason codes`, `Tools and gauges`, `Tooling required`, `Can it run now?`, `Presence now`, `Holds`).

### 2.3 Visual Consistency and Design Language
- **Missing CSS Classes in `wwwroot/css/forge.css` Causing Broken Layouts**:
  A static cross-check of all `class="..."` attributes in `wwwroot/js/**/*.js` against `wwwroot/css/forge.css` reveals that six page modules use class names that were never added to `forge.css`:

| Undefined CSS Class | Referenced In Frontend Modules | Visual Consequence in Browser |
|---|---|---|
| `.card`, `.card-head` | `pages/work-inbox.js`, `pages/notifications.js`, `pages/integrity.js`, `pages/shift-continuity.js`, `pages/scenarios.js` | Renders as transparent, borderless `<article>` blocks with zero padding or card separation (unlike `.panel` or `.card-row`). |
| `.section`, `.section-head` | `pages/work-inbox.js`, `pages/notifications.js`, `pages/integrity.js`, `pages/reports.js` | Renders as unstyled `<section>` blocks without panel backgrounds, borders, or flex header alignment. |
| `.stack` | `pages/work-inbox.js`, `pages/notifications.js`, `pages/integrity.js`, `pages/shift-continuity.js`, `pages/scenarios.js`, `pages/inventory.js` | No vertical `display: flex; flex-direction: column; gap: ...` is applied, causing sibling cards and form sections to touch vertically. |
| `.row` | `pages/shift-continuity.js` (lines 48, 65, 99) | Checklist rows and handoff header rows lack `display: flex`, causing `.spacer` spans to collapse and buttons to stack awkwardly. |
| `.panel-head`, `.eyebrow` | `pages/qms-metrics.js` (lines 36, 42) | KPI header and eyebrow text render as plain unstyled `<p>` and `<div>` elements. |
| `.inline` | `pages/inventory.js` (line 361) | Serial/lot lookup form in the Inventory header lacks inline flex layout. |
| `.notice.info` | `pages/workforce-admin.js` (line 88) | Falls back to generic grey `.notice` border instead of `--info (#3B82F6)`. |
| `tr.on` | `pages/investigations.js` (line 138) | Selected trace investigation row in the table has no highlight style. |

- **Inconsistent Modal & Confirmation Patterns**:
  - Most pages use `formModal()` (`wwwroot/js/forms.js`) or `confirmWithReason()` (`wwwroot/js/ui.js`), which unfortunately **does not call `manageDialogFocus()`** (`ui.js`, lines 134–169), meaning standard `confirmWithReason` and `formModal` dialogs lack keyboard `Tab` focus trapping and `Escape`-to-close!
  - Meanwhile, `pages/qms.js`, `pages/qms-planning.js`, `pages/qms-audit.js`, `identification-labels.js`, `pages/shopfloor.js`, and `pages/inventory.js` each implement their own bespoke modal builders that *do* call `manageDialogFocus()`.
  - `pages/qms-review.js` (line 45) and `pages/commercial-quality.js` (line 39) implement a third custom `modal()` helper without `manageDialogFocus()`.
  - `pages/work-inbox.js` (line 27) bypasses Forge modals entirely and calls native browser `window.prompt('Reason for marking ...:')`!
  - `pages/shift-continuity.js` (lines 80–90, 102–108) hardcodes audit reasons in JavaScript strings (e.g., `'Reviewed shift handoff'`, `'Configure work-center shift checklist'`) or reuses the handoff context note as the audit reason instead of prompting the user cleanly.

### 2.4 Color Usage, Contrast, and Accessibility (WCAG 2.1 Audit)
Programmatic relative luminance calculations across `wwwroot/css/forge.css` reveal specific contrast failures alongside strong primary text contrast:

| Foreground Token / Element | Background Token / Surface | Measured Contrast Ratio | WCAG 2.1 AA Normal (4.5:1) | WCAG 2.1 AA Large/UI (3.0:1) | Affected UI Elements (`wwwroot/css/forge.css`) |
|---|---|---|---|---|---|
| `--text (#E5E7EB)` | `--bg (#0F1419)` | **14.95:1** | PASS | PASS | Primary body copy |
| `--text (#E5E7EB)` | `--panel (#171C22)` | **13.84:1** | PASS | PASS | Panel & table cell primary text |
| `#C9CDD3` | `--sidebar (#12161B)` | **11.38:1** | PASS | PASS | Sidebar `.nav a` links |
| `#1F1300` | `.pill.warn (#FBBF24)` | **10.93:1** | PASS | PASS | Warning status pills (dark text on amber) |
| `#9BA9B5` | `--sidebar (#12161B)` | **7.56:1** | PASS | PASS | Sidebar `.nav-section` headers |
| `--muted (#9CA3AF)` | `--panel (#171C22)` | **6.75:1** | PASS | PASS | Table headers (`th`), `.muted` descriptions, field labels |
| `--forge-orange (#F97316)` | `--panel (#171C22)` | **6.11:1** | PASS | PASS | Inline links (`a`), `.c-warn` text, `.icon-btn` |
| `--faint (#6B7280)` | `--bg (#0F1419)` | **3.83:1** | **FAIL** | PASS | Clock subtext, footer metadata |
| `--faint (#6B7280)` | `--panel (#171C22)` | **3.54:1** | **FAIL** | PASS | `.faint` hints (`12px`), table sub-lines, KPI `.why` (`11px`) |
| `--faint (#6B7280)` | `--panel-2 (#1C2229)` | **3.32:1** | **FAIL** | PASS | `.faint` text inside `.wc-card` and `.stage` |
| `#FFFFFF` | `.icon-btn .count (#EF4444)` | **3.76:1** | **FAIL** | PASS | Notification bell unread badge (`10px` bold) |
| `#FFFFFF` | `.pill.bad` top (`#EF4444`) | **3.76:1** | **FAIL** | PASS | Top half of red error/stoppage pills (`11.5px`) |
| `#FFFFFF` | `--forge-orange (#F97316)` | **2.80:1** | **FAIL** | **FAIL** | `.btn.primary`, `.seg button.on`, `.badge.orange`, `.nav a.active` |
| `#FFFFFF` | `.pill.ok` top (`#22C55E`) | **2.28:1** | **FAIL** | **FAIL** | Top half of green `Ready` / `Released` / `Pass` pills (`11.5px`) |

- **Additional Accessibility Gaps**:
  - **Missing Focus Trap & `Escape` Handler in `confirmWithReason` (`wwwroot/js/ui.js`, lines 134–169)**: Unlike `drawer()` (which binds `Escape`) and `manageDialogFocus()`, `confirmWithReason()`—which powers `formModal()` across 20+ modules—does not bind `Escape` or trap `Tab` focus.
  - **Unlabeled Dynamic Form Controls**: In `genealogy.js` (line 119), the Consumed/Scrap/Loss/Remaining inputs inside the transformation table (`const input = (i, role) => '<input class="input num" ...>'`) have no `aria-label` or `<label>` (unlike the `data-parent` input added in V5-S1, which *does* have an `aria-label`).
  - **Color-Only Status Differentiation in Charts**: In `pages/horizon.js` (line 161), the Planned vs. Actual bar chart uses `#6B7280` vs. `#F97316` without bar value labels or pattern differentiation, and in `pages/timeline.js` (line 69), Gantt bars distinguish `AtRisk` (`#F97316`) from `Late` (`#EF4444`) purely by fill color.

### 2.5 Form Complexity and Unnecessary User Input
This is the single most severe UX issue in JIRSN Forge. Across V3 and V4, backend domain contracts were exposed almost 1:1 in the UI without human-centered form abstractions:

1. **Raw JSON Textarea in Customer & Provider Quality (`wwwroot/js/pages/commercial-quality.js`, lines 7–16, 48–64)**:
   - Creating any of the 9 commercial quality record types (`CustomerRequirements`, `DesignApplicability`, `ProviderEvaluation`, `PropertyAgreement`, `PropertyIncident`, `Complaint`, `CustomerFeedback`, `PostDelivery`, `Communication`) presents a 16-row `<textarea data-content>` pre-filled with a raw JSON object template that the user must hand-edit without syntax highlighting or schema validation (`JSON.parse($('[data-content]', box).value)`).
   - Recording an observation (`observe()`, lines 69–82) requires typing a raw JSON array (`<textarea class="input" data-evidence rows="5">[]</textarea>`) for `evidence`.
2. **Pipe-Delimited (`|`) DSL Textareas in Six Modules**:
   - **QMS Management Reviews (`pages/qms-review.js`, lines 84–99)**: `recordMeeting()` requires typing 5 pipe-separated fields per line for `assessments` (`agenda code | Adequate, Inadequate, Unavailable | actual assessment | limits | decision`) and **8 pipe-separated fields per line** for `decisions` (`code | decision | basis | intended result | resources | owner person ID or blank | due UTC or blank | completion criteria`). If any line has 7 or 9 pipes, `parse()` throws `Each decision line needs exactly 8 fields separated by |.`.
   - **Product Acceptance Criteria (`pages/acceptance.js`, lines 14–36)**: Requires typing `number | name | Variable or Attribute | nominal | lower | upper | unit` one per line in a textarea, plus `DOCUMENT-NUMBER REVISION` one per line. Ironically, `pages/quality.js` (`editPlan`, lines 239–256) already solved this with structured per-check inputs!
   - **Audits & CAPA (`pages/qms-audit.js`, lines 20–24, 62–76)**: Requires typing `code | revision` and `context | owner type | owner ID | exact revision | eventId | auditId` in textareas.
   - **Safety Rules (`pages/safety-rules.js`, lines 12–20)**: Requires typing `name | count | qualification codes | trainee yes/no` in a textarea.
   - **Receiving FAI (`wwwroot/js/receiving.js`, lines 18–26, 166)**: Requires typing `characteristic | requirement | measured | pass or fail` one per line.
   - **Inventory Lot Split & Merge (`pages/inventory.js`, lines 127, 200, 211)**: Requires typing `C-1001-A 900, C-1001-B 70` (space-and-comma-delimited lot/quantity pairs).
3. **Raw UUID / Internal Version / Record Key Entry**:
   - **Customer Return Governed Restock (`receiving.js`, lines 147–153)**: Asks the warehouse clerk to manually type `decisionId` (`Product release decision`) and `expectedDecisionVersion` (`Release decision version`, integer) instead of offering a dropdown of passed product releases for that return cohort.
   - **My Work Assignment (`pages/work-inbox.js`, line 22)**, **Scenarios (`pages/scenarios.js`, lines 49, 86)**, **QMS Audits (`pages/qms-audit.js`, lines 60–76)**, and **Shift Continuity (`pages/shift-continuity.js`, line 101)**: Require typing a user's raw 36-character UUID (`personId`) or internal `kind:key` reference (e.g., `work-order:57` in `pages/mrp.js`, line 222, and `pages/procurement.js`, line 183) instead of selecting from a searchable dropdown.
4. **Recursive `<dl>` Object Dumping (`detailHtml` / `detailsHtml`)**:
   - In `pages/qms-planning.js` (line 138), `pages/qms-audit.js` (line 95), `pages/qms-review.js` (line 11), and `pages/commercial-quality.js` (line 19), record detail views render raw nested C# DTOs by recursively iterating `Object.entries(value)` into nested `<dl><dt>...</dt><dd>...</dd></dl>` lists—exposing internal camelCase property names (`manifestFingerprint`, `expectedCaseVersion`, `auditorValidation`) and raw UUIDs directly to users.
   - Even worse, in `pages/acceptance.js` (line 206), looking up a Return Cohort renders `<pre class="mono" style="white-space:pre-wrap">${esc(JSON.stringify(d, null, 2))}</pre>`—a literal JSON dump!

### 2.6 Data Tables, Filtering, Sorting, and Search
- **Zero Column Sorting Across All 60+ Tables**: Not a single `<table class="table">` in the entire application supports clicking `<th>` headers to sort by Due Date, Priority, Quantity, Status, or Age.
- **No Client-Side Table Filtering or Search on High-Volume Screens**:
  - **Production Orders (`pages/orders.js`)** has no status filter (`Planned`, `Released`, `Held`, `Completed`), no work-center filter, and no search box (only a URL query `?part=...`).
  - **Inventory Stock (`pages/inventory.js`)** lists all parts in a 12-column table with no search bar, no filter for "Below Safety Stock" or "On Hold", and no location filter.
  - **Inventory Ledger (`pages/inventory.js`, line 405)** fetches `?take=200` with no date range, part, transaction kind, or order filter on the tab.
- **Inconsistent Pagination**: V4 QMS pages (`qms-planning.js`, `qms-audit.js`, `qms-review.js`, `commercial-quality.js`, `identification-labels.js`) use cursor pagination (`?take=30&after=...`) with "Next visible page" buttons (and no "Previous page" button, only "First page"), whereas V1–V3 pages (`orders.js`, `inventory.js`, `procurement.js`, `quality.js`) render all records into the DOM at once with no pagination.
- **Global Search Stale Cache (`wwwroot/js/app.js`, lines 124–155)**: `runSearch()` caches `lookups` (`orders`, `items`, `resources`, `people`) on its first invocation and **never invalidates or refreshes `lookups` for the rest of the browser session**, meaning newly created orders, parts, machines, or employees never appear in global search until a full page reload (`F5`).

### 2.7 Mobile, Tablet, and Desktop Responsiveness
- **Desktop (`>1360px`) and Laptop (`900px–1360px`)**: `.shell` uses a `236px 1fr` grid; `.kpis` steps down cleanly from 6 columns to 3 columns at `1360px` and 2 columns at `900px`.
- **Tablet & Mobile (`≤900px`) Gaps**:
  - **11-Button Segmented Tab Strip on Inventory (`pages/inventory.js`, line 365)**: The `#tabs` container uses `.seg` (`display: inline-flex; overflow: hidden;`) with **11 tabs** (`Stock`, `Lot selection`, `Tasks`, `Ledger`, `Approvals`, `Locations`, `Counts`, `Shelf life`, `Receiving`, `Outside processing`, `MRO levels`). Because `.seg` has no `flex-wrap: wrap` or `overflow-x: auto`, this 11-button bar overflows horizontally and clips on tablets and narrow desktop windows!
  - **14-Button Action Toolbar on Inventory (`pages/inventory.js`, lines 354–362)**: Renders 14 action buttons (`Receive`, `Issue to order`, `Return`, `Move`, `From production`, `Split`, `Merge`, `Request material`, `Hold`, `Release`, `Scrap`, `Count`, `Adjust`, `+ Location`) in a single `.toolbar` above the KPIs, pushing actual inventory data below the fold on 1024×768 shop-floor tablets.
  - **Fixed-Width SVG Gantt Charts (`pages/timeline.js`, lines 66, 134)**: Hardcodes `width="1100"` and `style="min-width:1100px"`, forcing horizontal scrolling without sticky left machine/operation labels.
  - **Touch Target Sizing (`wwwroot/css/forge.css`, lines 65–78, 286–287)**: Standard buttons (`.btn`: `padding: 7px 12px`, ~32px height), `.btn.small` (`padding: 5px 10px`, ~26px height), and `.btn.tiny` (`padding: 1px 8px`, ~20px height) fall well below the **44×44px WCAG 2.5.5 / 48×48px industrial glove-touch standard**.

### 2.8 Error Handling, Confirmations, and User Feedback
- **Strengths**: `tryGet()` (`wwwroot/js/api.js`, line 108) and `errorBlock()` (`wwwroot/js/ui.js`, line 46) ensure that a `403 Forbidden` or failed sub-panel explains *why* (including specific policy/scope denial reasons) rather than crashing the page or displaying misleading zeros. `toast()` sets `role="alert"` / `aria-live="assertive"` on errors.
- **Weaknesses**:
  - **Destructive Modal Closure on 5xx Errors**: In `identification-labels.js` (line 63), `qms.js` (line 175), `qms-planning.js` (line 178), and `qms-audit.js` (line 138), if a command returns HTTP 5xx (such as **Defect D1** or a transient DB timeout), the dialog **immediately calls `close()` and destroys everything the user typed**, showing only a toast! A user who spent 10 minutes filling out a 20-field QMS definition or audit report loses all their work on a transient server error.
  - **Ephemeral Toasts for Critical Multi-Line Results**: In `pages/maintenance.js` (line 277), `requestParts` displays generated requisition numbers in a 3.5-second auto-dismissing toast (`toast(done.lines.map(...).join(' · '))`), giving the technician no time to copy or click the requisition IDs.

### 2.9 Workflow Efficiency and Unnecessary Navigation
- **Cross-Screen Scavenger Hunts for Dependent IDs**:
  - To restock a governed Customer Return in `Inventory → Receiving` (`receiving.js`, line 147), the user must first click `Prepare inspection`, navigate to `Quality → Product Acceptance` (`#/acceptance`), inspect the return cohort, switch to the `Release` tab to create a Product Release decision, **memorize or write down the integer `decisionId` and `version`**, navigate back to `Inventory → Receiving`, and type those two numbers into the `Disposition return` modal.
  - To link a Hold to a Trace Investigation candidate in `Investigations` (`pages/investigations.js`, line 229), if no hold exists yet, the modal tells the user: *"No active hold matches this candidate yet. Place one on Shop Floor or Production Orders first."* There is no inline `+ Place Hold & Link` action!
  - To add a revision to an Engineering Change (`pages/engineering.js`, line 326), the user must first create the draft revision in `Engineering → Documents`, then navigate to `Engineering → Changes`, open the ECN, and add the line.

### 2.10 Operator Usability Under Real Manufacturing Conditions
- Evaluated in depth in **Phase 3** below. Key issues include the 60-second idle auto-lock wiping active form inputs (`pages/shopfloor.js`, line 19), `type="password"` masking non-secret order/operation barcodes if `scanBox` is reused, lack of touch numpads for quantity entry, and the 9-step V4.1 scan-staging ceremony (`scan-session.mjs`).

### 2.11 Role-Based Information Visibility
- **Action Gating Works Well; Navigation & Layout Adapts Poorly**: `canAction(me, permission, resource)` (`wwwroot/js/ui-authority.mjs`) accurately hides mutation buttons when a user lacks permission or when separation of duties (`!mine(...)`) prohibits self-approval.
- **However, Read Surfaces Are Monolithic**: Every role sees the exact same 35-item sidebar, the exact same `Home` page (`Factory Horizon`), and the exact same 11-tab `Inventory` page. An Inspector signing in does not land on `Inspections Due`, a Buyer does not land on `Procurement / Requisitions`, and a `ShopFloorTerminal` account still renders the full 236px left navigation sidebar, global search bar, notification bell, and settings link alongside the kiosk screen!

### 2.12 Performance Implications of Current & Proposed UI Patterns
- **Full-Page `innerHTML` Replacements & Uncached Reference Lookups**:
  - Almost every action (`reload()`) refetches 4–12 endpoints and replaces `page.innerHTML`, destroying scroll position and re-downloading static reference lists (`/api/engineering/items`, `/api/resources/`, `/api/kernel/directory`, `/api/procurement/suppliers`).
  - On `Inventory` (`pages/inventory.js`, lines 38–45), every single tab switch or modal action fires **15 API requests** (12 in `Promise.all`, plus `loadShelfLife()` [3 requests], `loadReceiving()` [4 requests], and `/api/inventory/outside-processing` [1 request] = **20 HTTP GETs per reload!**), even when the user is only viewing the `Locations` or `MRO levels` tab!
- **Performance Impact of Proposed Changes**:
  - **Lazy Tab Data Loading**: Fetching tab-specific data (`loadShelfLife`, `loadReceiving`, `outside-processing`, `ledger`, `counts`) only when that tab is active reduces `inventoryPage` initial network requests from **20 GETs to 5 GETs** (a 75% reduction in API load and PostgreSQL connection contention).
  - **Session-Scoped Reference Cache with Event Invalidation**: Caching `/api/resources/`, `/api/engineering/items`, and `/api/kernel/directory` with a 5-minute TTL (and invalidating on write commands) eliminates redundant payloads across `app.js`, `orders.js`, `inventory.js`, `mrp.js`, and `procurement.js`.


## Phase 3 — Shop-Floor Interface Architecture & Operator Experience

### 3.1 Diagnosis of Current Shop-Floor Kiosk (`pages/shopfloor.js` & `scan-session.mjs`)

1. **Full Chrome Distraction on Kiosk Accounts**: When signed in as `kiosk` (`ShopFloorTerminal` role), `app.js` still renders the 236px left sidebar with 35 enterprise routes and the top global search bar. An operator on a 1024×768 or 1280×800 ruggedized VESA mount loses 20% of horizontal screen width to links they have zero permission to use.
2. **60-Second Silent Idle Lockout (`pages/shopfloor.js`, lines 18–19, 53–65)**:
   - Default `idleMs` is `60,000 ms` (60 seconds). While `click`, `keydown`, and `input` reset the timer, an operator physically loading a coil onto a slitter, measuring a part with a micrometer, or walking 15 feet to read a pallet tag will exceed 60 seconds.
   - Because there is **no visual countdown warning** in the last 15 seconds, the screen abruptly wipes `terminal.person`, closes any open dialog, and invalidates active scan staging (`clearScanWork()`) without warning.
3. **Excessive Ceremony in V4.1 Governed Scan Staging (`wwwroot/js/scan-session.mjs`, lines 48–178)**:
   - Under an adopted V4.1 execution policy, an operator cannot simply scan a material lot barcode and press `Start`. They must:
     1. Click `Stage references` on the queue row.
     2. Type a free-text reason into `#session-start-reason` (*"Why this work is being staged"*) and click `Begin server-owned staging`.
     3. Scan the barcode into `#session-reference-code`, click `Resolve code` (or press Enter), read the preview, and click `Use this subject`.
     4. Select `Declared capture method` (`Manual`, `KeyboardWedge`, `Camera`, `ConfiguredAdapter`) from a dropdown (`#session-input-mode`).
     5. Type a *second* free-text reason into `#stage-reason` (*"Reason for recording this reference"*).
     6. Click `Record reviewed reference`.
     7. Repeat steps 3–6 for every required reference kind (`Order`, `Operation`, `Material`, `Location`, `Tool`, `ControlledInstruction`), then finally click `Start`!
   - Requiring an operator wearing nitrile or cut-resistant gloves to type two free-text audit reasons and choose `"KeyboardWedge"` from a `<select>` dropdown for every barcode scan is untenable in a real manufacturing plant.
4. **Missing Essential Operator Workflows on `#/shop-floor`**:
   - **No Work Instructions / Drawings Viewer**: Even though Forge enforces document training (`V3.9`) and exact instruction revisions (`V4.1`), `pages/shopfloor.js` does not show the operation's attached drawings or work instructions!
   - **No Inline Material Lot Traceability / Issue / Byproduct Entry**: Operators cannot view which lots are staged at their machine or record consumed/remnant/byproduct quantities (`genealogy.js`) from `#/shop-floor`.
   - **No Inline Quality Hold / Defect Flagging**: Operators can report scrap quantities (`#scrap` + `#reason`), but cannot place an operation/lot on Quality Hold or raise an NCR when an out-of-tolerance condition occurs.
   - **No Shift Handoff Acknowledgment**: Incoming operators clocking in at `#/shop-floor` are not shown unacknowledged pass-on notes from `#/shift-continuity`.

### 3.2 Shop-Floor Redesign Recommendations & Safeguards

1. **Dedicated Full-Screen Kiosk Shell Mode (`app.js`, `forge.css`)**:
   - When `session.me.roles` contains only `ShopFloorTerminal` (or when a `Kiosk Mode` toggle is active in `#/shop-floor`), collapse the 236px `.sidebar` and global `.topbar`, dedicating 100% of the viewport to the operator terminal with a high-contrast top status bar showing **Site Clock**, **Active Work Center / Machine**, **Operator Name + Presence Pill**, **Idle Countdown Ring (`45s` — tap anywhere to extend)**, and **Switch Operator / Lock (`64×64px` button)**.
2. **Streamlined Wedge-First Barcode Scanning (`scan-session.mjs`, `identification-input.mjs`)**:
   - **Auto-detect `KeyboardWedge` vs. `Manual`**: Measure inter-keystroke latency in `identification-input.mjs` (hardware wedge scanners emit characters at `<15ms` intervals followed by `Enter`). Pre-select `KeyboardWedge` automatically when wedge timing is detected, while keeping the backend's honest rule that capture mode is a declared metadata field.
   - **Default Audit Reasons via One-Tap Presets**: Replace free-text reason typing in `scan-session.mjs` (`#session-start-reason` and `#stage-reason`) with pre-filled default reasons (`"Standard operation staging"` / `"Verified barcode at point of use"`) and one-tap override chips (`"Re-stage after roll change"`, `"Supervisor directed"`), eliminating mandatory keyboard typing while still sending a non-empty `reason` string to `command()`.
   - **Auto-Resolve & Auto-Stage on `Enter`**: When a barcode is scanned into the active staging box and resolves unambiguously to a required reference kind for the current operation, display a large green confirmation banner (`✓ Material COIL-AL Lot L-204 matched`) and allow a single Enter/tap (or second confirmation scan) to record the stage.
3. **Glove-Friendly Touch Controls & On-Screen Numpad (`pages/shopfloor.js`, `forge.css`)**:
   - Enforce a **56px minimum height** (`min-height: 56px; font-size: 18px`) for all shop-floor buttons, machine selectors, and inputs.
   - Provide an on-screen **Touch Numpad + Quick Increment Buttons (`+1`, `+5`, `+10`, `Remaining: 45`)** for `Good` and `Scrap` quantity reporting and `PIN` entry so operators on touch-panel PCs without physical keyboards never rely on tiny OS virtual keyboards.
4. **Safeguards for Critical Shop-Floor Transactions**:
   - **Over-Reporting & High-Scrap Guardrail**: In `pages/shopfloor.js` (`#report` submit handler, line 183), if `good + scrap > remainingOrderQuantity` or if `scrap > 0` without a selected scrap reason code, block submission inline and require explicit confirmation (`"You are reporting 50 Scrap (25% of order). Confirm scrap quantity and reason."`).
   - **Material Consumption & Byproduct Cap Guardrail**: Embed a simplified "Material at Machine" drawer on `#/shop-floor` showing issued lots, remaining balances, and one-tap `Record Remnant / Byproduct` with live validation that parent-lot attribution never exceeds consumed quantity (`V5-S1`).
   - **One-Tap "Stop & Flag Quality Issue" Button**: Add a prominent amber/red `Flag Quality Issue / Hold` button on the active operation panel that pauses labor, lets the operator pick a defect code, and places an immediate operation/lot hold or NCR draft for Quality review.
   - **Shift Pass-On Gate at Clock-In**: When an operator clocks in or scans their badge at a work center with an unacknowledged `Reviewed` shift handoff (`#/shift-continuity`), display a concise 3-bullet banner at the top of `#/shop-floor` with a single `Acknowledge Shift Pass-On` button.


## Phase 4 — Enterprise Modernization & Competitive Benchmark

### 4.1 Benchmarking JIRSN Forge Against Tier-1 ERP & Modern MES Platforms

| Dimension | SAP Fiori (S/4HANA Manufacturing) | Microsoft Dynamics 365 Supply Chain | Oracle Fusion Cloud Manufacturing | Contemporary Cloud MES (Tulip / Plex) | JIRSN Forge (Current V5-S1 State) | Modernization Opportunity for Forge |
|---|---|---|---|---|---|---|
| **Navigation & Role Launchpad** | Role-based Launchpad tiles + Spaces/Pages tailored per persona | Role workspaces (e.g., Production Floor Management, Warehouse, Quality) | Springboard icon grid + Infolets per job role | App-per-station / Role-specific station player | Single 35-item static sidebar shown to all 37 roles (`app.js`) | Introduce **Role-Filtered Sidebar + Persona Home Workspaces** while keeping instant `Ctrl+K` command/search access. |
| **Object Detail Architecture** | Object Page Floorplan with sticky header KPIs + horizontal anchor tabs | FastTabs (collapsible accordion sections) + FactBoxes on right rail | Tabbed sub-views + contextual side panel | Step-by-step guided canvas | 10–14 panels stacked vertically in a single long scroll (`orders.js`, `workcenters.js`) | Adopt a **Sticky Object Header + Tabbed Sub-Views (`Overview`, `Execution & Readiness`, `Material & Genealogy`, `Quality & Holds`, `Docs & History`)**. |
| **Complex Data Entry** | SmartTable inline grid editing + Value Help (`F4`) lookup dialogs | Editable grids + segmented lookup comboboxes | Drawer forms with structured child-row repeaters | Touch widgets, visual selectors, barcode triggers | Pipe-delimited (`\|`) textareas (`acceptance.js`, `qms-review.js`) & raw JSON textareas (`commercial-quality.js`) | Replace all pipe-text and raw JSON textareas with a **Reusable `<forge-repeater>` Table Builder** and **Entity Combobox**. |
| **Shop-Floor Execution HMI** | DMC Pod (Production Operator Dashboard) — modular full-screen touch panels | Production Floor Execution interface — touch numpad, large radial progress, bundled tabs | Work Execution workstation — scan-driven single-screen dispatch | Zero-typing touch buttons, embedded PDF/CAD viewer, inline camera/gauge capture | Clean badge scan (`shopfloor.js`), but cluttered by desktop sidebar, 9-step scan staging (`scan-session.mjs`), and missing drawings/handoffs | Build a **Full-Screen Kiosk Mode** with auto-wedge detection, embedded drawing/WI viewer drawer, touch numpad, and 1-tap quality hold. |
| **Explainability & Audit Integrity** | Scattered across Change Documents (`CDHDR`/`CDPOS`) and separate BI logs | Requires drill-through across multiple inquiry forms | Separate OTBI reporting layer | Strong station logs, weaker ERP pegging | **Industry-Leading**: Native `evidenceTree`, deterministic `Why?` drawers, `CauseMap`, and append-only ledger/genealogy | **Preserve & Elevate**: Keep Forge's no-black-box explainability (`Why?`, `What changed?`, `Can it run now?`) as its core competitive advantage! |

### 4.2 Preserving the JIRSN Red-Tail Hawk Identity & Successful Visual Elements

Forge already possesses a distinctive, memorable aesthetic that avoids the sterile "generic Bootstrap/Tailwind SaaS" look. The following elements in `wwwroot/css/forge.css` and `wwwroot/img/` are highly successful and **must be preserved**:
- **JIRSN Red-Tail Hawk Brand Assets**: The login lockup (`/img/brand/login-lockup.jpg`), hero banner (`/img/brand/hero-banner.jpg`), primary sidebar lockup (`/img/brand/forge-logo-primary.png`), subtle bottom-left wing watermark (`.sidebar::after` using `/img/brand/wing.jpg` with CSS gradient masking), and textured panel surfaces (`/img/brand/card-bg.jpg`).
- **Industrial Typography Pairing**: `Bahnschrift` / `DIN Alternate` / `Eurostile` (`--font-display`) for bold geometric headers and KPI numerals, paired with `Segoe UI` / `Inter` (`--font`) for dense operational tables and `Consolas` / `Cascadia Mono` (`.mono`) with `tabular-nums` for part numbers, lots, and quantities.
- **Factory Horizon Visual Flow (`pages/horizon.js`)**: The routing-ordered work-center stage cards (`.stage`) with contextual industrial photography (`/img/stages/slitting.jpg`, `fabrication.jpg`, `paint.jpg`, etc.), load bars, and one-click evidence drawers are visually compelling and immediately communicative.
- **Plain-Language Epistemological Honesty**: Forge's microcopy philosophy—stating *"Not yet measured"* instead of `0%`, distinguishing *"Correlated constraint"* from *"Recorded causal link"* in `CauseMap`, and providing a 1-click `"Why?"` arithmetic drawer on every MRP recommendation—is a major differentiator over SAP and Dynamics 365.


## Phase 5 — Actionable Recommendations, Scorecards, Wireframes & Roadmap

### 5.1 Interface Scorecards (35 Routed Screens + Shared Sub-Modules)

Each interface is scored on a **1–5 scale** across five criteria (**1 = Critical Friction / Broken**, **3 = Functional but High Friction**, **5 = Best-in-Class Industrial UX**), weighted for an overall score out of **100**.

| Interface / Screen | File Path (`wwwroot/js/...`) | Clarity & Hierarchy | Workflow Speed | Form & Input UX | Visual & CSS Polish | Accessibility & Touch | Overall Score (/100) | Primary Audit Finding |
|---|---|---|---|---|---|---|---|---|
| **Factory Horizon (Home)** | `pages/horizon.js`, `model.js` | 4.5 | 4.0 | 4.5 | 4.5 | 3.5 | **84** | Strong visual flow; fix hardcoded `null` Material KPI (`model.js:175`) and jarring `-8h`/`+2w` redirect to `timelinePage`. |
| **Horizon Lenses & Gantt** | `pages/timeline.js` | 4.0 | 4.0 | 4.0 | 4.0 | 3.0 | **76** | Good lens switching; SVG Gantt is fixed `1100px` wide with no sticky labels or zoom controls. |
| **Programs** | `pages/programs.js` | 4.5 | 4.0 | 4.0 | 4.5 | 3.5 | **82** | Clean WBS, CLIN, and Earned Value (SPI) presentation; clarify that CPI/actual costs are not modeled. |
| **Projects** | `pages/projects.js` | 4.0 | 4.0 | 4.0 | 4.0 | 3.5 | **78** | Solid two-person change control and shortage requisitioning; long vertical scroll on detail view. |
| **Production Orders** | `pages/orders.js`, `actuals.js`, `materialflow.js` | 3.5 | 3.5 | 4.0 | 4.0 | 3.5 | **74** | List lacks search/status filters; detail view stacks 11+ panels vertically (needs tabs); good priority preview modal. |
| **Engineering (Items, Docs, ECNs, Alternates)** | `pages/engineering.js`, `attachments.js` | 4.0 | 3.5 | 3.5 | 4.0 | 3.5 | **74** | Strong ECN impact & propagation tracking; missing UI to create new Items, Item Revisions, BOM lines, or Routings. |
| **Scheduling & Goals** | `pages/scheduling.js`, `goals.js` | 4.0 | 4.0 | 4.0 | 4.0 | 3.5 | **78** | Clear two-planner proposal/approval comparison; Goals (`goals.js`) is buried at the bottom of Scheduling. |
| **Scenarios & Recovery** | `pages/scenarios.js` | 3.0 | 3.0 | 2.5 | 2.0 | 3.0 | **54** | Uses undefined `.card`, `.card-head`, `.stack` CSS classes; requires raw UUID `personId` entry in main form. |
| **Material Plan (MRP)** | `pages/mrp.js` | 4.5 | 4.5 | 4.0 | 4.5 | 3.5 | **84** | Exemplary "Why?" pegging drawer and 1-click PO/Order/Requisition creation; replace `kind:key` text input in `+ Demand`. |
| **Work Centers & Tools** | `pages/workcenters.js`, `tools.js`, `executable.js` | 3.0 | 3.5 | 3.5 | 4.0 | 3.5 | **70** | Overloaded with 10 stacked panels; `Tools and gauges` (`tools.js`) is buried at the bottom instead of having its own route/tab. |
| **Shop Floor Terminal** | `pages/shopfloor.js`, `scan-session.mjs`, `identification-input.mjs` | 3.0 | 2.0 | 2.0 | 3.5 | 2.5 | **52** | Sidebar not hidden in kiosk mode; 60s silent lock; 9-step scan staging with 2 manual text reasons; no drawings or quality hold button. |
| **Plant Setup** | `pages/plantsetup.js` | 4.5 | 4.0 | 4.5 | 4.5 | 4.0 | **86** | Best-designed configuration screen in Forge: visual tree, stage photo picker, chip-based shifts/activities, and clear dependency guards. |
| **Workforce & Admin** | `pages/workforce.js`, `pages/workforce-admin.js` | 3.5 | 3.5 | 3.5 | 3.5 | 3.5 | **70** | Good 7-day labor matrix and reallocation preview; 11 admin buttons only appear after picking a person from a flat dropdown; `.notice.info` undefined. |
| **Safety Rules** | `pages/safety-rules.js` | 3.5 | 3.0 | 2.0 | 4.0 | 3.5 | **64** | Forces safety engineers to type pipe-delimited role slots (`name \| count \| quals \| trainee yes/no`) in a textarea. |
| **Shift Continuity** | `pages/shift-continuity.js` | 3.0 | 3.0 | 2.5 | 2.0 | 3.0 | **54** | Uses undefined `.row`, `.stack`, `.card` CSS classes; requires typing raw `assignee` person IDs; hardcodes audit reasons. |
| **Notifications** | `pages/notifications.js` | 3.0 | 3.5 | 2.5 | 2.0 | 3.0 | **56** | Uses undefined `.section`, `.section-head`, `.stack`, `.card`, `.card-head` CSS classes; 21-field inline rule creation form is always expanded. |
| **My Work Inbox** | `pages/work-inbox.js` | 3.5 | 3.5 | 2.0 | 2.0 | 3.0 | **56** | Uses undefined `.section`, `.card`, `.stack` CSS classes; uses raw `window.prompt()` (line 27); requires raw UUID in `Assign work`. |
| **Data Integrity** | `pages/integrity.js` | 4.0 | 4.0 | 4.0 | 2.0 | 3.5 | **70** | Clear read-only exception cards, but uses undefined `.section`, `.section-head`, `.stack`, `.card`, `.card-head` CSS classes. |
| **Inventory (Stock, Tasks, Counts, Shelf Life, Labels)** | `pages/inventory.js`, `shelflife.js`, `identification-labels.js`, `genealogy.js` | 3.5 | 3.0 | 3.0 | 3.5 | 3.0 | **64** | 14 action buttons + 11 horizontal `.seg` tabs overflow on tablets; fires 20 GETs on every reload; split/merge uses text syntax; byproduct modal is dense. |
| **Receiving & Returns** | `receiving.js` (inside `Inventory`) | 3.5 | 3.0 | 2.0 | 3.5 | 3.0 | **60** | Buried as 9th tab in Inventory; FAI results require pipe-delimited textarea; RMA governed restock requires typing raw `decisionId` & `expectedDecisionVersion`. |
| **Procurement & Sourcing** | `pages/procurement.js`, `sourcing.js` | 4.0 | 4.0 | 3.5 | 4.0 | 3.5 | **76** | Strong "Why bought?" and frozen technical package drawers; New PO form hardcodes exactly 3 line items; Requisition requires `kind:key` text. |
| **Quality (Inspections, NCR, CAPA)** | `pages/quality.js` | 4.0 | 4.0 | 4.0 | 4.0 | 3.5 | **78** | Clean structured inspection modal and NCR/CAPA flow; overlaps confusingly with `#/acceptance` and `#/qms-audit`. |
| **Product Acceptance** | `pages/acceptance.js` | 3.5 | 3.0 | 2.0 | 3.5 | 3.0 | **60** | Criteria form requires 7-field pipe-delimited textarea; Release tab requires manual inspection ID/version typing; Return Cohort lookup dumps raw `JSON.stringify`. |
| **Investigations** | `pages/investigations.js` | 4.0 | 3.5 | 3.5 | 3.5 | 3.5 | **72** | Excellent completeness transparency (`completenessNotes`); `tr.on` undefined; linking a hold requires leaving the page to create the hold first. |
| **Quality Management (QMS)** | `pages/qms.js` | 3.0 | 3.0 | 2.5 | 3.5 | 3.0 | **60** | Deeply nested dynamic modal forms for Profiles/Policies/Adoptions; densecamelCase object rendering in detail views. |
| **QMS Planning & Retention** | `pages/qms-planning.js` | 3.0 | 2.5 | 2.0 | 3.0 | 3.0 | **54** | Requires typing 6-field owner evidence tuples (`context`, `type`, `key`, `revision`, `eventId`, `auditId`); renders raw recursive `<dl>` dumps. |
| **Audits & CAPA (Governed)** | `pages/qms-audit.js` | 3.0 | 2.5 | 2.0 | 3.0 | 3.0 | **54** | Requires pipe-delimited `code \| revision` and 6-field `context \| type \| id...` strings; recursive `<dl>` dumps. |
| **QMS Reviews & Editions** | `pages/qms-review.js` | 2.5 | 2.0 | 1.5 | 3.0 | 3.0 | **48** | `recordMeeting` requires typing 5-field and 8-field pipe-delimited lines in textareas; custom modal lacks focus management. |
| **Quality Metrics** | `pages/qms-metrics.js` | 3.5 | 3.5 | 3.5 | 2.5 | 3.5 | **66** | Uses undefined `.panel-head` and `.eyebrow` classes; requires manual `From`/`To` datetime picking before showing any KPIs (no default period). |
| **Customer & Provider Quality** | `pages/commercial-quality.js` | 2.0 | 1.5 | 1.0 | 3.0 | 2.5 | **40** | **Lowest Score**: Forces users to edit a 16-row raw JSON payload (`JSON.parse`) to create any of the 9 record types, and raw JSON arrays for observations. |
| **Maintenance** | `pages/maintenance.js` | 4.0 | 4.0 | 3.5 | 4.0 | 3.5 | **76** | Comprehensive readiness & outage schedule preview; `requirements` modal renders 22 flat inputs; blocked by Backend Defect D1 on same-instant PM completion. |
| **Shipping** | `pages/shipping.js` | 4.0 | 4.0 | 3.5 | 4.0 | 3.5 | **76** | Clear two-person Pack & Ship and container sealing; New Shipment modal hardcodes 3 stock line slots. |
| **Reports** | `pages/reports.js` | 4.5 | 4.5 | 4.0 | 4.0 | 3.5 | **82** | Excellent source-record trace links, grouping, CSV/Excel export, print stylesheet, and "What changed?" CauseMap. |
| **Forge Analyst** | `pages/analyst.js` | 4.5 | 4.5 | 4.5 | 4.5 | 4.0 | **88** | **Highest Score**: Transparent confidence pills ("I understood:"), clickable suggestion chips, instant shift brief, and traceable facts. |
| **Access Control & Settings** | `pages/access.js`, `pages/settings.js` | 4.0 | 3.5 | 3.5 | 4.0 | 3.5 | **74** | Good role/permission/SoD visibility; Program Access is duplicated in both `#/access` and `#/settings`; `#/settings` lacks a searchable Audit Log viewer. |


### 5.2 Prioritized List of Improvements (Ranked by Impact, Effort, and Risk)

| Rank | Recommendation | Affected Files (`src/Forge.Host/wwwroot/...`) | Impact | Effort | Risk | Rationale & Specific Fix |
|---|---|---|---|---|---|---|
| **P0-1** | **Define Missing CSS Classes (`.card`, `.card-head`, `.section`, `.section-head`, `.stack`, `.row`, `.panel-head`, `.eyebrow`, `.inline`, `.notice.info`, `tr.on`)** | `css/forge.css` | **Critical** | **Low** (1 hr) | **Zero** (CSS only) | Instantly repairs broken layouts across 8 modules (`work-inbox.js`, `notifications.js`, `integrity.js`, `shift-continuity.js`, `scenarios.js`, `qms-metrics.js`, `inventory.js`, `investigations.js`) without touching a single line of JS or breaking any of the 216 UI tests. |
| **P0-2** | **Fix WCAG 2.1 AA Contrast Failures on `--faint`, `.btn.primary`, `.seg button.on`, and `.pill.ok`** | `css/forge.css` | **High** | **Low** (1 hr) | **Zero** (CSS only) | Lighten `--faint` from `#6B7280` (3.54:1) to `#8B949E` (**5.24:1** on `--panel`). Darken `.btn.primary`, `.seg button.on`, and `.badge.orange` gradient base or use `#0F1419` dark bold text on `#F97316` (**8.21:1** contrast, matching `.pill.warn`), and darken `.pill.ok` top stop from `#22C55E` (2.28:1) to `#16A34A` / `#15803D` (**5.02:1+**). |
| **P0-3** | **Add `manageDialogFocus()` and Non-Destructive Error Handling to Shared `confirmWithReason()` & `formModal()`** | `js/ui.js` (lines 134–169), `js/pages/qms.js`, `js/pages/qms-planning.js`, `js/pages/qms-audit.js`, `js/identification-labels.js` | **High** | **Low** (2 hrs) | **Low** | Wire `manageDialogFocus(m, () => close(null))` inside `confirmWithReason()` so every modal in Forge gets `Escape`-to-close and `Tab` focus trapping. Remove `close()` inside `catch` blocks on 5xx errors in QMS/label modals so users never lose form input when a server error occurs; replace `window.prompt()` in `pages/work-inbox.js` (line 27) with `confirmWithReason()`. |
| **P1-1** | **Replace Raw JSON & Pipe-Delimited (`\|`) Textareas with Structured Form Repeaters** | `js/forms.js`, `js/pages/commercial-quality.js`, `js/pages/qms-review.js`, `js/pages/acceptance.js`, `js/pages/safety-rules.js`, `js/receiving.js`, `js/pages/qms-audit.js`, `js/pages/inventory.js` | **Critical** | **Medium** (2–3 days) | **Low** (Keep existing parser functions exported for unit test compatibility) | Add a `type: 'repeater'` field type to `forms.js` (with `+ Add row` / `× Remove` buttons) that serializes back to the exact pipe-delimited string or JSON structure expected by `parseCharacteristics()`, `parseSlots()`, `faiResults()`, `recordMeeting()`, and `commercial-quality.js`. This preserves 100% compatibility with existing `tests/ui/*.test.mjs` while eliminating raw JSON and pipe syntax for humans! |
| **P1-2** | **Streamline Shop-Floor Kiosk & V4.1 Scan Staging UX** | `js/pages/shopfloor.js`, `js/scan-session.mjs`, `js/identification-input.mjs`, `css/forge.css` | **Critical** | **Medium** (2 days) | **Low** | Auto-collapse `.sidebar` and `.topbar` in Kiosk mode; add a 15-second visual countdown warning before the 60s idle lock with tap-to-extend; auto-detect `KeyboardWedge` input mode; pre-fill default audit reasons in `scan-session.mjs` with 1-tap override chips; add a Touch Numpad, Work Instruction Document drawer, and 1-tap `Flag Quality Issue / Hold` button on `#/shop-floor`. |
| **P1-3** | **Role-Filtered Navigation, Collapsible Nav Groups, and Consolidated Quality/Inventory IA** | `js/app.js`, `css/forge.css`, `js/pages/workcenters.js`, `js/pages/inventory.js` | **High** | **Medium** (1.5 days) | **Low** | Filter sidebar routes by the user's read/action permissions (with a `"Show all modules"` toggle); make nav sections collapsible; promote `Receiving` (`#/inventory?tab=receiving`) and `Tools & Calibration` (`#/work-centers#tools`) to direct sidebar links; consolidate the 9 Quality routes into 3 clear nav entries (`Quality & Acceptance`, `Investigations & CAPA`, `QMS Governance`) with sub-navigation tabs. |
| **P1-4** | **Replace Raw UUID / `decisionId` / `kind:key` Inputs with Searchable Entity Comboboxes** | `js/forms.js`, `js/receiving.js`, `js/pages/acceptance.js`, `js/pages/work-inbox.js`, `js/pages/scenarios.js`, `js/pages/shift-continuity.js`, `js/pages/mrp.js`, `js/pages/procurement.js` | **High** | **Medium** (1.5 days) | **Low** | In `receiving.js` (`data-rma-dispose`), fetch `/api/quality/acceptance/product-releases` and populate a dropdown of passed releases (auto-filling `decisionId`, `expectedDecisionVersion`, and `quantity`). In `work-inbox.js`, `scenarios.js`, and `shift-continuity.js`, populate person selectors from `loadDirectory()`. |
| **P2-1** | **Convert Vertically Stacked Detail Pages (`orders.js`, `workcenters.js`) to Tabbed Object Pages** | `js/pages/orders.js`, `js/pages/workcenters.js`, `css/forge.css` | **High** | **Medium** (1.5 days) | **Low** | Group Order Detail's 11+ stacked panels into 5 horizontal tabs (`Overview & Readiness`, `Operations & Labor`, `Material & Genealogy`, `Quality & Holds`, `Documents`) below a sticky Order Summary header. Group Work Centers into 4 tabs (`Capacity & Readiness`, `Stoppages & Holds`, `Tools & Gauges`, `Presence`). |
| **P2-2** | **Lazy-Load Inventory Tabs & Cache Reference Lookups** | `js/pages/inventory.js`, `js/api.js`, `js/app.js` | **High** | **Low** (1 day) | **Low** | In `inventory.js`, load only `summary` and `requests` (for KPIs) plus the active tab's endpoint on render, cutting initial load from 20 GETs to 4 GETs. In `app.js`, invalidate global search `lookups` after any `command()` call or after 60 seconds. |
| **P2-3** | **Fix Hardcoded `null` Material Constraint KPI & Default Date Window on Quality Metrics** | `js/model.js` (line 175), `js/pages/horizon.js`, `js/pages/qms-metrics.js` | **Medium** | **Low** (0.5 day) | **Zero** | Wire `/api/planning/material` into `horizonPage` and `buildHorizon()` so the 5th KPI tile displays actual material shortfall count/hours instead of the stale Phase 6 placeholder. Pre-populate `From` (30 days ago) and `To` (now) in `qms-metrics.js` so metrics load immediately on visit. |
| **P2-4** | **Add Reusable Sortable/Filterable Table Helper (`<forge-table>`)** | `js/ui.js`, `js/pages/orders.js`, `js/pages/inventory.js`, `js/pages/procurement.js`, `js/pages/quality.js` | **Medium** | **Medium** (1.5 days) | **Low** | Add a lightweight `enableTableSortAndFilter(tableEl)` helper in `ui.js` that adds click-to-sort on `<thead><th>` headers (`aria-sort`) and a quick client-side filter bar above tables with `>10` rows. |
| **P3-1** | **Replace Recursive `<dl>` DTO Dumps & `<pre>` JSON Dumps with Formatted Cards** | `js/pages/acceptance.js` (line 206), `js/pages/qms-planning.js`, `js/pages/qms-audit.js`, `js/pages/qms-review.js`, `js/pages/commercial-quality.js` | **Medium** | **Medium** (1.5 days) | **Zero** | Replace `JSON.stringify(d, null, 2)` on Return Cohort lookup in `acceptance.js` with a structured `<dl class="kv">` card, and format QMS evidence/source lists using human-readable badges and clickable deep links (`traceLink`). |


### 5.3 Proposed Navigation & Dashboard Redesigns

#### 5.3.1 Role-Adaptive Navigation Architecture (`wwwroot/js/app.js`)
Instead of showing 35 flat links to every user, organize the sidebar into **5 collapsible task-oriented groups (18 top-level items total)** by consolidating sub-pages into tabbed workspaces, and filter default visibility by role while providing a `"Show all modules"` toggle at the bottom:

| Proposed Sidebar Group | Consolidated Top-Level Links (18 Total vs. 35 Today) | Sub-Tabs Consolidated Inside Each Workspace | Default Expanded Roles |
|---|---|---|---|
| **1. Overview & Inbox** | • **Factory Horizon** (`#/home`)<br>• **My Work & Alerts** (`#/my-work` with tabs: `Approvals & Assignments`, `Notifications`, `Shift Pass-On`)<br>• **Forge Analyst** (`#/analyst`) | Combines `My Work`, `Notifications`, and `Shift Continuity` into a unified daily action hub; keeps `Bell` and `Shift` shortcuts in topbar. | All Roles (except dedicated `ShopFloorTerminal` kiosk) |
| **2. Plan & Schedule** | • **Horizon & Timeline** (`#/horizon` — Lenses, Gantt & `What changed?`)<br>• **Scheduling & Goals** (`#/scheduling` — Proposals, Constraints, Goals & Scenarios)<br>• **Material Plan (MRP)** (`#/material-plan` — Recommendations, Substitutions, Exceptions, Demand)<br>• **Programs & Projects** (`#/programs` & `#/projects` tabs) | Moves `Scenarios` into a tab on `Scheduling`; links `What changed?` directly inside `Horizon & Timeline`. | `Planner`, `ProgramManager`, `FacilitiesManager`, `Supervisor` |
| **3. Shop Floor & Make** | • **Shop Floor Terminal** (`#/shop-floor` — Full-Screen Kiosk Mode)<br>• **Production Orders** (`#/orders`)<br>• **Work Centers & Presence** (`#/work-centers`) | Keeps `Work Centers` focused on capacity, readiness, stoppages, and presence; moves `Tools` to Maintenance/Resources. | `ShopFloorTerminal`, `ScanOperator`, `Supervisor`, `Planner` |
| **4. Warehouse & Supply** | • **Inventory & Stock** (`#/inventory` — Stock, Lot Selection, Tasks, Counts, Shelf Life, Ledger, Approvals)<br>• **Receiving & Returns** (`#/receiving` — Receipts, FAI, Rejections, RTV, Customer Returns)<br>• **Shipping & Containers** (`#/shipping` — Shipments, Containers, Outside Processing)<br>• **Procurement & Sourcing** (`#/procurement` — POs, RFQs, Requisitions, Suppliers) | Elevates **Receiving** to a first-class sidebar route (`#/receiving`); groups `Outside Processing` with `Shipping & Containers`; reduces `Inventory` from 11 tabs to 7 tabs (fitting cleanly on all viewports). | `MaterialHandler`, `InventoryController`, `Shipper`, `Buyer`, `PurchasingManager` |
| **5. Quality & Engineering** | • **Quality & Acceptance** (`#/quality` — Inspections Due, Product Acceptance, NCRs, Gauges)<br>• **Investigations & CAPA** (`#/investigations` — Trace Investigations, Measurement Findings, CAPAs, Audits)<br>• **QMS Governance** (`#/qms` — Profile/Policies, QMS Planning/Risks/Objectives, Reviews, Metrics, Commercial Quality)<br>• **Engineering** (`#/engineering` — Items, Documents, Changes, Alternates, Obsolescence)<br>• **Maintenance & Tooling** (`#/maintenance` — Machines, Work Orders, Outages, PM Plans, Assets, Tools & Gauges) | Consolidates the 9 fragmented Quality/QMS sidebar items into **3 logical workspaces** (`Quality & Acceptance`, `Investigations & CAPA`, `QMS Governance`), and moves `Tools & Gauges` (`tools.js`) into `Maintenance & Tooling`. | `Inspector`, `QualityEngineer`, `QualityManager`, `ManufacturingEngineer`, `MaintenanceTechnician`, `QmsAuthor` |
| **Bottom Utility Bar** | • **Plant & Safety Setup** (`#/plant-setup` — Plant Tree, Shifts, Safety Rules, Locations)<br>• **Admin & Security** (`#/access` — Access Control, Delegations, Data Integrity, Reports, Settings) | Consolidates `Plant Setup` and `Safety Rules`; merges `Access Control`, `Data Integrity`, and `Settings` into a tabbed Admin workspace. | `SecurityAdministrator`, `ManufacturingEngineer`, `SafetyEngineer`, `Auditor` |

#### 5.3.2 Role-Specific Landing Dashboard Enhancements (`pages/horizon.js`)
- **Actionable Role Banner Above Factory Flow**: Add a compact 1-row "My Shift Action Bar" at the top of `Factory Horizon` (`#/home`) that surfaces the signed-in user's immediate queue:
  - **Supervisors**: `2 Unreviewed Shift Handoffs` · `1 Machine Down (SL-02)` · `3 Open Stoppage Root Causes to Confirm`
  - **Planners**: `Proposed Schedule #14 Awaiting Approval` · `4 MRP Past-Due Releases` · `2 Orders At Risk`
  - **Quality**: `3 Inspections Due` · `2 Open NCRs` · `1 Overdue Gauge Calibration`
  - **Warehouse**: `4 Open Pick/Deliver Tasks` · `2 Receipts Waiting for Inspection` · `1 Pending Count Variance`
- **Live Material Constraint KPI (`model.js:175`)**: Replace the hardcoded `null` Material Constraint KPI with the count of released orders in `Shortfall` or `Expected` state from `/api/planning/material`, clicking through to `#/material-plan?tab=exceptions`.


### 5.4 Reusable UI Components & Design Tokens Specification

#### 5.4.1 Updated CSS Design Tokens & Missing Classes (`wwwroot/css/forge.css`)
Add the following tokens and missing layout rules to `wwwroot/css/forge.css` to achieve WCAG 2.1 AA compliance and fix the 6 unstyled pages:

```css
:root {
  /* Updated Accessible Text Hierarchy (WCAG 2.1 AA >= 4.5:1 on #0F1419, #171C22, #1C2229) */
  --text: #E5E7EB;          /* 13.84:1 on --panel */
  --muted: #9CA3AF;         /*  6.75:1 on --panel */
  --faint: #8B949E;         /*  5.24:1 on --panel (replaces #6B7280 which failed at 3.54:1) */

  /* Semantic Status Surfaces & High-Contrast Pills */
  --ok-bg: #15803D;         /*  5.02:1 with #FFFFFF */
  --warn-bg: #F59E0B;       /* 10.15:1 with #1F1300 */
  --bad-bg: #B91C1C;        /*  6.47:1 with #FFFFFF */
  --info-bg: #1D4ED8;       /*  6.70:1 with #FFFFFF */

  /* Touch & Spacing Scale */
  --touch-min: 44px;
  --touch-kiosk: 56px;
}

/* Fix white-on-orange contrast on primary buttons and active segmented controls */
.btn.primary {
  background: linear-gradient(180deg, #EA580C, #C2410C);
  border-color: #9A3412;
  color: #FFFFFF; /* 5.18:1 WCAG AA PASS */
}
.seg button.on, .badge.orange {
  background: #C2410C;
  color: #FFFFFF; /* 5.18:1 WCAG AA PASS */
}
.pill.ok {
  background: linear-gradient(180deg, #16A34A, #15803D);
  border-color: #166534;
}
.pill.bad {
  background: linear-gradient(180deg, #DC2626, #B91C1C);
  border-color: #7F1D1D;
}

/* Define missing layout classes used in work-inbox, notifications, integrity, shift-continuity, scenarios, qms-metrics */
.section, .card {
  background: linear-gradient(rgba(23,28,34,.9), rgba(23,28,34,.94)), url(/img/brand/card-bg.jpg) center / cover, var(--panel);
  border: 1px solid var(--line);
  border-radius: var(--radius);
  padding: 14px 16px;
}
.card { background: var(--panel-2); padding: 12px 14px; }
.section-head, .card-head, .panel-head, .row {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 10px;
  flex-wrap: wrap;
  margin-bottom: 10px;
}
.stack { display: flex; flex-direction: column; gap: 12px; }
.inline { display: inline-flex; align-items: center; gap: 8px; flex-wrap: wrap; }
.eyebrow { font: 700 11px var(--font-display); letter-spacing: .14em; text-transform: uppercase; color: var(--forge-orange); margin: 0 0 4px; }
.notice.info { border-left-color: var(--info); color: #BFDBFE; }
.table tr.on td { background: rgba(249, 115, 22, 0.14); border-top: 1px solid var(--forge-orange); border-bottom: 1px solid var(--forge-orange); }
.seg { flex-wrap: wrap; } /* Prevents 11-tab horizontal clipping on tablets */
```

#### 5.4.2 Five High-Leverage Reusable JS Components (`wwwroot/js/ui.js` & `wwwroot/js/forms.js`)
1. **`repeaterField` (`wwwroot/js/forms.js`)**: A structured multi-row table input for modals (`columns: [{ name, label, type, options }]`) with `+ Add Row` and `×` buttons. Includes a `serialize` callback so existing pipe-delimited (`acceptance.js`, `safety-rules.js`, `receiving.js`, `qms-review.js`, `qms-audit.js`) and JSON (`commercial-quality.js`) payloads are generated automatically without user syntax errors.
2. **`entityLookupField` (`wwwroot/js/forms.js`)**: An accessible `<input list="...">` / searchable combobox backed by cached directory/resource lookups that displays human-readable labels (`" Maria Chen (EMP-104)"` or `"Release #42 — 920 EA (v1)"`) while storing the underlying UUID, `decisionId`, or `kind:key` required by the API.
3. **`enhanceTable` (`wwwroot/js/ui.js`)**: Attaches client-side column sorting (`numeric`, `date`, `text`), a quick search/filter input, and sticky headers to any `<table class="table">`.
4. **`tabbedPanels` (`wwwroot/js/ui.js`)**: Converts long sequences of `<section class="panel">` on detail pages (`orders.js`, `workcenters.js`) into clean horizontal tabs with badge counts (e.g., `Holds (1)`, `Documents (4)`) while keeping URL query state (`?section=genealogy`).
5. **`kioskNumpad` (`wwwroot/js/pages/shopfloor.js`)**: A touch-friendly 56px modal/popover numeric keypad (`0–9`, `.`, `Backspace`, `Clear`, `+1`, `+5`, `+10`, `Max Remaining`) for shop-floor quantity and PIN inputs.


### 5.5 Desktop and Shop-Floor Tablet Wireframe Concepts

#### 5.5.1 Desktop Wireframe: Redesigned Order Detail Object Page (`#/orders/10002`, `pages/orders.js`)
Replaces the 11-panel 3,500px vertical scroll with a **Sticky Object Header**, **4-Card Readiness & Progress Strip**, and **5 Horizontal Sub-Tabs**:

```
+------------------------------------------------------------------------------------------------------------------+
| ← Production Orders                                                                    [What changed?] [Print]   |
| ORDER #10002  ·  COIL-SLIT-48 rev B  ×  1,000.000 EA                                   [Released]  [Priority 2]  |
| Due: Oct 24, 2026 · Program: FALCON (Contract FA8650 CLIN 0001) · Created by J. Planner · Released by M. Lead   |
|                                                                   [Hold Order] [Change Priority] [Cancel Order]  |
+------------------------------------------------------------------------------------------------------------------+
| [ READINESS: EXECUTABLE ]    [ SCHEDULED DELIVERY ]       [ QUANTITY PROGRESS ]        [ ACTIVE HOLDS / NCRs ]   |
|  Op 10 Ready on SLIT-01       On Time · Oct 22, 14:30      420 Good / 12 Scrap          0 Active Holds           |
|  All 6 checks passed          Drop-dead start: Oct 21      580 Remaining to make        1 Passed Inspection      |
+------------------------------------------------------------------------------------------------------------------+
| ( Overview & Operations )  ( Material & Flow [2] )  ( Genealogy & Reconciliation )  ( Quality [1] )  ( Docs [3] )|
+------------------------------------------------------------------------------------------------------------------+
| OPERATIONS & READINESS                                                                                           |
| +----+----------------+-------------+----------+---------+---------------+---------+----------------------+      |
| | Op | Work Center ↕  | Activity    | Machine  | Planned | Good / Target | Status  | Readiness Evidence   |      |
| +----+----------------+-------------+----------+---------+---------------+---------+----------------------+      |
| | 10 | Slitting Line  | RUN_SLITTER | SLIT-01  | 4.5 h   | 420 / 1,000   | Working | ✓ Crew ✓ Tool ✓ Matl |      |
| | 20 | Inspection     | QC_FINAL    | CMM-01   | 1.0 h   |   0 / 1,000   | Pending | ! Waits on Op 10     |      |
| +----+----------------+-------------+----------+---------+---------------+---------+----------------------+      |
|                                                                                                                  |
| DIRECT LABOR & REPORTS ON THIS ORDER                                                                             |
| +----+----------------+-----------+----------+------------------+------------------+----------------------+      |
| | Op | Person         | Role      | Machine  | Started          | Ended / Status   | Reported Output      |      |
| +----+----------------+-----------+----------+------------------+------------------+----------------------+      |
| | 10 | R. Morales     | Operator  | SLIT-01  | Oct 18, 07:12 AM | [Working now]    | 420 Good · 12 Scrap  |      |
+------------------------------------------------------------------------------------------------------------------+
```

#### 5.5.2 Shop-Floor Tablet Wireframe: Full-Screen Operator Kiosk (`#/shop-floor`, `1024×768` Touch HMI)
Collapses the 35-item desktop sidebar, provides 56px+ glove-touch controls, embeds work instructions and material staging inline, and shows a live idle timer ring:

```
+==================================================================================================================+
| [JIRSN FORGE]  WORK CENTER: SLIT-WC (Slitting)  |  OPERATOR: R. Morales (Clocked in 3h 14m)  | [⏱ 54s] [SWITCH] |
+==================================================================================================================+
| ⚠️ SHIFT PASS-ON (Day Shift → Swing): "SLIT-01 upper knife changed at 05:30; check edge burr on first 10m."      |
|                                                                                      [ ✓ ACKNOWLEDGE PASS-ON ]   |
+------------------------------------------------------------------------------------------------------------------+
| ACTIVE OPERATION: ORDER #10002 · OP 10 (Slit Master Coil)                     MACHINE: [ SLIT-01 ▼ ]  [RUNNING]  |
| Part: COIL-SLIT-48 rev B  ·  Target: 1,000 EA  ·  Good So Far: 420 EA  ·  Scrap: 12 EA  ·  Remaining: 580 EA     |
| [=========================>                                  ] 42% Complete    Due: Oct 24  ·  Tool: KNIFE-04 ✓  |
+--------------------------------------------------------+---------------------------------------------------------+
| 1. SCAN / STAGE MATERIAL & REFERENCES                  | 2. REPORT PRODUCTION OUTPUT                             |
| +----------------------------------------------------+ | +-------------------------+ +-------------------------+ |
| | [ ▌Scan Lot, Tool, or Location Barcode...        ] | | | GOOD QUANTITY           | | SCRAP QUANTITY          | |
| |   Auto-Wedge Ready · Press Enter or Scan           | | | [        120         ]  | | [         0          ]  | |
| +----------------------------------------------------+ | | [+1] [+10] [+50] [580]  | | [+1] [+5]  [Clear]      | |
| Staged & Verified for Op 10:                           | +-------------------------+ +-------------------------+ |
|  [✓] Order/Op: #10002 / Op 10 (Verified)               | Scrap Reason (required if Scrap > 0):                   |
|  [✓] Material: COIL-RAW Lot L-8821 (980 kg at SLIT-1)  | [ Edge wave ] [ Surface defect ] [ Setup ] [ Burr ▼ ]   |
|  [✓] Instruction: WI-SLIT-04 rev C                     |                                                         |
|                                                        | [ ☐ Mark Operation 10 Complete ]                        |
| [ 📄 View Drawing / WI-SLIT-04 ] [ 📦 Record Remnant ] | [        ✓ SUBMIT PRODUCTION REPORT (56px)            ] |
+--------------------------------------------------------+---------------------------------------------------------+
| [ ⏸ PAUSE / STOP WORK ]     [ 🛑 MACHINE STOPPED / DOWNTIME ]     [ ⚠️ FLAG QUALITY HOLD / NCR ]    [ 🔐 PIN ]  |
+==================================================================================================================+
```


### 5.6 Implementation Roadmap Suitable for Codex

All recommendations below are scoped strictly to `src/Forge.Host/wwwroot/` and `tests/ui/` so Codex can execute and verify each slice using `node --test tests/ui/*.test.mjs` without altering C# domain contracts, PostgreSQL schemas, or migration counts.

#### Sprint 1 — Zero-Risk CSS Repair, WCAG Contrast & Modal Hardening (1–2 Days)
1. **Task 1.1 (`wwwroot/css/forge.css`)**: Add missing CSS rules for `.card`, `.card-head`, `.section`, `.section-head`, `.stack`, `.row`, `.panel-head`, `.eyebrow`, `.inline`, `.notice.info`, `tr.on`, and `.seg { flex-wrap: wrap; }`.
2. **Task 1.2 (`wwwroot/css/forge.css`)**: Update `:root` `--faint` to `#8B949E`, `.btn.primary` / `.seg button.on` / `.badge.orange` to `#C2410C` gradient with `#FFFFFF` text (or `#F97316` with `#0F1419` text), and `.pill.ok` / `.pill.bad` gradients to pass WCAG 2.1 AA (`≥4.5:1`).
3. **Task 1.3 (`wwwroot/js/ui.js`, `wwwroot/js/pages/work-inbox.js`)**: Wire `manageDialogFocus()` into `confirmWithReason()` so all `formModal()` dialogs trap focus and close on `Escape`. Replace `window.prompt()` in `pages/work-inbox.js` (line 27) with `confirmWithReason()`.
4. **Task 1.4 (`wwwroot/js/pages/qms.js`, `qms-planning.js`, `qms-audit.js`, `identification-labels.js`)**: Remove `close()` from `catch` blocks on 5xx errors in modal submit handlers; display the error inside `[data-error]` while disabling the submit button until refreshed, preserving user input.
5. **Verification Gate**: Run `node --check` on modified JS files and run `tests/ui/accessibility.test.mjs` and all `tests/ui/*.test.mjs`.

#### Sprint 2 — Structured Form Repeaters & Entity Lookups (Eliminating Raw JSON & Pipe Syntax) (3–4 Days)
1. **Task 2.1 (`wwwroot/js/forms.js`)**: Add `type: 'repeater'` and `type: 'lookup'` support to `fieldHtml()` and `readFields()`.
2. **Task 2.2 (`wwwroot/js/pages/commercial-quality.js`)**: Replace the 16-row raw JSON `<textarea data-content>` and `<textarea data-evidence>` with typed `formModal` definitions for each of the 9 `KINDS`, serializing the form values into the exact JSON payload expected by `/api/quality/commercial/records`.
3. **Task 2.3 (`wwwroot/js/pages/qms-review.js`, `qms-audit.js`, `acceptance.js`, `safety-rules.js`, `receiving.js`, `inventory.js`)**: Replace pipe-delimited textareas (`assessments`, `decisions`, `characteristics`, `documents`, `slots`, `results`, `splits/merges`) with `repeater` rows while keeping `parseCharacteristics()`, `parseDocuments()`, `parseSlots()`, and `faiResults()` intact and exported so existing UI unit tests in `tests/ui/acceptance.test.mjs`, `admin-pages.test.mjs`, and `screen-actions.test.mjs` continue to pass.
4. **Task 2.4 (`wwwroot/js/receiving.js`)**: In `bindReceivingTab` (`[data-rma-dispose]`), fetch `/api/quality/acceptance/product-releases` and add a `<select>` of passed product releases for the RMA's part/lot, auto-populating `decisionId`, `expectedDecisionVersion`, and `quantity`.
5. **Verification Gate**: Run all 25 `tests/ui/*.test.mjs` suites and add repeater serialization unit tests.

#### Sprint 3 — Shop-Floor Kiosk HMI & V4.1 Scan Session Streamlining (2–3 Days)
1. **Task 3.1 (`wwwroot/js/app.js`, `wwwroot/css/forge.css`, `wwwroot/js/pages/shopfloor.js`)**: Add `.kiosk-mode` class to `.shell` when on `#/shop-floor` (auto-enabled for `ShopFloorTerminal` role, toggleable for `Supervisor`), hiding `.sidebar` and `.topbar` and applying `min-height: 56px` touch targets.
2. **Task 3.2 (`wwwroot/js/pages/shopfloor.js`)**: Add a visible 60-second countdown indicator (`[data-idle-countdown]`) in the kiosk header that turns amber at `<15s` with a `Stay Signed In` tap target; add an on-screen touch numpad for `#good`, `#scrap`, and PIN inputs.
3. **Task 3.3 (`wwwroot/js/scan-session.mjs`, `wwwroot/js/identification-input.mjs`)**: Auto-detect `KeyboardWedge` keystroke cadence (`<15ms`/char), pre-fill default audit reasons (`#session-start-reason` and `#stage-reason`) with quick-select chips, and replace technical error strings with plain operator language.
4. **Task 3.4 (`wwwroot/js/pages/shopfloor.js`)**: Add inline buttons on the active operation panel for `View Work Instructions` (opening `attachmentsDrawer` for the order/item), `Material & Remnants` (opening the `genealogy.js` reconciliation modal), `Flag Quality Hold / NCR` (calling `/api/quality/ncrs` or `/api/holds`), and an unacknowledged `Shift Pass-On` banner at clock-in.
5. **Verification Gate**: Run `tests/ui/terminal-state.test.mjs`, `tests/ui/scan-session.test.mjs`, `tests/ui/identification.test.mjs`, and `tests/ui/accessibility.test.mjs`.

#### Sprint 4 — Information Architecture, Tabbed Object Pages & Performance Optimization (2–3 Days)
1. **Task 4.1 (`wwwroot/js/app.js`)**: Reorganize `NAV_GROUPS` into collapsible sections, filter unpermitted routes by default with a `"Show all modules"` toggle, and invalidate global search `lookups` on command execution.
2. **Task 4.2 (`wwwroot/js/pages/orders.js`, `wwwroot/js/pages/workcenters.js`)**: Refactor Order Detail and Work Centers from 10+ vertically stacked panels into sticky summary headers + 4–5 horizontal sub-tabs.
3. **Task 4.3 (`wwwroot/js/pages/inventory.js`)**: Split `Receiving` into its own top-level route (`#/receiving`) or sub-workspace, reducing `#/inventory` from 11 tabs to 7 tabs, and lazy-load tab data (`loadShelfLife`, `loadReceiving`, `outsideProcessing`, `ledger`, `counts`) only when the active tab requires it.
4. **Task 4.4 (`wwwroot/js/model.js`, `wwwroot/js/pages/horizon.js`, `wwwroot/js/pages/qms-metrics.js`, `wwwroot/js/ui.js`)**: Wire `/api/planning/material` into the 5th KPI tile on `Factory Horizon`; pre-fill a default 30-day window on `Quality Metrics`; add `enhanceTable()` sorting/filtering to `orders.js`, `inventory.js`, `procurement.js`, and `quality.js`.
5. **Verification Gate**: Full `verify.cmd` run (944+ checks) and smoke verification across all roles.


### 5.7 Changes That Must Wait Until Backend Development Is Complete

The following UI/UX enhancements depend on backend commands, schema migrations, or owner-gated V5 slices that **do not exist or are currently blocked on the server** (`AI_HANDOFF.md`), and must wait until backend work is authorized and completed:

| Deferred UI Change | Blocking Backend Dependency / Defect | Current Backend State & Reference |
|---|---|---|
| **1. Same-Instant PM Completion & Quick Maintenance Sign-Off UI** | **Defect D1 (Backend Bug in `Forge.Maintenance`)**: Completing a PM work order with a planned downtime window in the same instant/minute it was started fails PostgreSQL's `CHECK (ends_at > starts_at)` constraint and returns HTTP 500. | Recorded as open Defect D1 in `AI_HANDOFF.md` (requires production C# approval to fix timestamp clamping/resolution). |
| **2. "Change Home Work Center" Action in Workforce Admin (`pages/workforce-admin.js`)** | **Missing Backend Command in `Forge.Workforce`**: `POST /api/workforce/workers/{id}` registers a worker's initial `homeWorkCenterCode`, but no command or endpoint exists to transfer a worker to a new home work center. | Confirmed in `AI_HANDOFF.md`: *"Findings: no command changes a worker's home work center."* |
| **3. Live Overtime Scheduling & Shift Extension UI (`pages/workforce-admin.js`, `pages/plantsetup.js`)** | **Overtime Not Modeled in `Forge.Workforce` / `Forge.Resources`**: Overtime only exists as a transient in-memory copy inside `Forge.Planning` private scenarios (`pages/scenarios.js`), not as an actual shift/workforce command. | Confirmed in `AI_HANDOFF.md`: *"overtime is not a concept."* |
| **4. Searchable / Filterable Security Audit Log Table (`pages/settings.js`, `pages/access.js`)** | **Missing Paginated Audit Query Endpoint in `KernelEndpoints.cs`**: `/api/kernel/audit/verify` verifies hash-chain integrity, but no paginated, source-filtered GET endpoint exposes general `kernel.audit_log` rows for UI browsing. | `src/Forge.Host/Api/KernelEndpoints.cs` only exposes `/api/kernel/audit/verify` and per-delegation history. |
| **5. Item Creation, Revision Authoring, and BOM/Routing Builder UI (`pages/engineering.js`)** | **Backend Engineering Setup vs. Controlled Change Boundaries**: While `/api/engineering/items` and `/api/engineering/items/{part}/revisions` accept POSTs (used by `tools/seed-sample-plant.mjs`), `EngineeringEndpoints.cs` has no draft edit/delete endpoints for BOM lines or routing steps once created. | Requires backend draft-revision editing endpoints before a full interactive BOM/Routing tree editor can be safely exposed. |
| **6. Financial Cost Ledger, Actual Cost (AC), CPI, and PO Line Pricing Thresholds (`pages/programs.js`, `pages/procurement.js`)** | **No Cost Ledger in Domain Model**: `PurchaseOrderLine` (`ProcurementEndpoints.cs`) does not store unit price or currency (unlike `RfqQuote` and `ContractClin`), so `V3.15` approval amount thresholds cannot evaluate on standard POs, and `Programs` cannot compute CPI. | Confirmed in `AI_HANDOFF.md`: *"economics are reported in quantities and time only"* and `AI_SESSION_LOG.md` (`V3.15` boundary note). |
| **7. Structured Consigned Customer Tooling Workflow (`tools.js`, `pages/commercial-quality.js`)** | **ADR 0051 Domain Boundary**: `Forge.Resources` lacks structured fields for legal tool ownership, loan authorization, direct receipt into customer custody, and customer acceptance. | Documented in `AI_HANDOFF.md` and ADR 0051. |
| **8. V5 Predictive Supply Intelligence, Automated Allocation Optimization & Next-Gen MRB Dashboards** | **Owner-Excluded V5 Slices**: The Product Owner explicitly excluded predictive forecasting, allocation optimization, new MRB automation, and external ERP/EDI connectors from V5-S1 (`AI_HANDOFF.md`). | Hold all UI work on these four areas until separate V5 slice approvals are granted. |
