# JIRSN Forge — Sprint 7 Commercial Parity & Market Leadership Specification

## Executive Summary

Across Sprints 1–6 and ADR 0057, **JIRSN Forge** achieved an **A+ (99/100)** internal architecture and UX grade, outperforming legacy enterprise ERPs ([SAP S/4HANA Manufacturing](https://www.google.com/search?q=https%3A%2F%2Fwww.sap.com%2Fproducts%2Fscm%2Fs4hana-manufacturing-solutions.html), [Microsoft Dynamics 365 Supply Chain Management](https://www.google.com/search?q=https%3A%2F%2Fwww.microsoft.com%2Fen-us%2Fdynamics-365%2Fproducts%2Fsupply-chain-management), [Oracle Fusion Cloud Manufacturing](https://www.google.com/search?q=https%3A%2F%2Fdocs.oracle.com%2Fen%2Fcloud%2Fsaas%2Fsupply-chain-and-manufacturing%2F26c%2Ffaips%2Fabout-oracle-fusion-cloud-manufacturing.html)) and modern cloud MRPs ([Plex](https://www.google.com/search?q=https%3A%2F%2Fplex.rockwellautomation.com%2Fen-us%2Fsmart-manufacturing-platform.html), [Tulip](https://www.google.com/search?q=https%3A%2F%2Ftulip.co%2Fplatform%2F), [Odoo MRP](https://www.google.com/search?q=https%3A%2F%2Fwww.odoo.com%2Fapp%2Fmanufacturing), [MRPeasy](https://www.google.com/search?q=https%3A%2F%2Fwww.mrpeasy.com%2Fmanufacturing-erp-software%2F), [Katana](https://www.google.com/search?q=https%3A%2F%2Fkatanamrp.com%2F)) in **deterministic explainability (`Why?` pegging + netting strips)**, **actual-presence finite readiness**, **cryptographic separation of duties**, and **lot-level genealogy**.

However, our competitive benchmark identified **6 specific areas where commercial systems currently beat Forge in day-to-day buyer evaluations**. Crucially, an inspection of the current `Forge.Host` (`v4`) C# endpoints reveals that **4 of these 6 gaps already have complete, production-tested backend endpoints in `src/Forge.Host/Api/`** that simply lack frontend UI wiring, while the remaining **2 gaps require only lightweight, additive C# DTO/endpoint extensions**.

This specification is split into two parts:
* **Part 1 (Tasks 7.1–7.4 — Frontend-Only, Zero Backend Changes, Codex Ready)**: Closes the Master Data Creation, Bulk CSV Onboarding/Export, Interactive Gantt Dispatching, Multi-Level Indented BOM Tree, and Searchable Security Audit Log gaps using existing endpoints in `EngineeringEndpoints.cs`, `KernelEndpoints.cs`, `PlanningEndpoints.cs`, and `MaterialsEndpoints.cs`.
* **Part 2 (Tasks 7.5–7.6 — Full-Stack Additive Extensions, Claude + Codex)**: Closes the Financial Valuation / PO Approval Threshold gap (`V3.15`) and the External Accounting / ERP / Grant-Reporting Export gap without introducing general-ledger bloat.

| Task | Commercial Gap Closed | Target File(s) (`src/Forge.Host/...`) | Existing Backend Endpoint Leveraged | Layer | Effort | Risk |
|---|---|---|---|---|---|---|
| **7.1** | **Item Authoring, Release/Withdraw & Multi-Level Indented BOM Tree** (vs. Odoo / MRPeasy / Arena PLM) | `wwwroot/js/pages/engineering.js`, `wwwroot/css/forge.css` | `POST /api/engineering/items` (`EngineeringEndpoints.cs:65`), `POST .../revisions/{rev}/release` & `/withdraw` (`lines 93–96`) | Frontend only | 45 min | Zero |
| **7.2** | **Universal CSV Import Wizard & 1-Click Table CSV Export** (vs. Katana / MRPeasy / Dynamics 365 Excel Add-in) | `wwwroot/js/forms.js`, `wwwroot/js/ui.js`, `pages/engineering.js`, `pages/mrp.js`, `pages/procurement.js` | `POST /api/engineering/items`, `POST .../bom`, `POST /api/planning/demand`, `POST /api/procurement/suppliers` | Frontend only | 45 min | Zero |
| **7.3** | **Interactive Gantt Operation Inspector, Priority Resequencing & 1-Click What-If Launcher** (vs. PlanetTogether / SAP Fiori Gantt / Plex) | `wwwroot/js/pages/timeline.js`, `wwwroot/js/pages/scenarios.js`, `wwwroot/css/forge.css` | `POST /api/orders/{id}/priority/preview` & `/priority` (`PlanningEndpoints.cs`), `POST /api/planning/scenarios/recovery-options` | Frontend only | 45 min | Zero |
| **7.4** | **Searchable Security Audit Trail Explorer & Role Action Bar** (vs. SAP `AUT10` / Oracle Audit Workbench) | `wwwroot/js/pages/settings.js`, `wwwroot/js/pages/horizon.js` | `GET /api/kernel/audit?entityType=...&entityId=...&take=200` (`KernelEndpoints.cs:162–175`) | Frontend only | 35 min | Zero |
| **7.5** | **PO Line Unit Pricing, Order Value Rollup & Kernel `V3.15` Financial Threshold Enforcement** (vs. NetSuite / SAP / MRPeasy) | `Api/MaterialsEndpoints.cs`, `wwwroot/js/pages/procurement.js`, `wwwroot/js/pages/mrp.js` | Extends `PurchaseLineRequest` / `PurchaseOrderView` & passes `ResourceRef.Amount` into `ApprovePurchaseOrder` | Full-stack (`Forge.Host`) | 60 min | Low |
| **7.6** | **Accounting, EDI & Grant-Reporting Financial/Inventory Export Feed** (vs. Katana / QuickBooks / Xero / NetSuite connectors) | `Api/MaterialsEndpoints.cs`, `wwwroot/js/pages/reports.js` | Combines `/api/inventory/transactions`, `/api/procurement/purchase-orders`, and `/api/shipping/shipments` into signed CSV/JSON feeds | Full-stack (`Forge.Host`) | 45 min | Zero |


## Part 1 — Frontend-Only Commercial Parity Upgrades (Codex Ready, Zero Backend Changes)

### Task 7.1 — Master Item Creation, Revision Release/Withdraw & Multi-Level Indented BOM Explorer (`wwwroot/js/pages/engineering.js` & `wwwroot/css/forge.css`)

**Why Commercial Systems Win Today**:
In `EngineeringEndpoints.cs` (`lines 65–96`), the backend already supports:
* `POST /api/engineering/items` (`ItemRequest(PartNumber, Description, UnitOfMeasure, MakeBuy, LeadTimeDays, LotTracked, Serialized, Reason, Class)`)
* `POST /api/engineering/items/{part}/revisions/{rev}/release` (`ReleaseRevisionRequest(EffectiveFrom, Reason)`)
* `POST /api/engineering/items/{part}/revisions/{rev}/withdraw` (`ReasonRequest(Reason)`)

Yet in `wwwroot/js/pages/engineering.js`:
1. `itemList()` (`lines 60–88`) has **no `+ New item` button**, forcing engineers to use seed scripts or curl to create a new part!
2. `structureDetail()` (`lines 548–576`) lets engineers create a draft revision and edit BOM/routing lines, but has **no `Release revision` or `Withdraw revision` button** in the UI!
3. `structureDetail()` only shows a single-level flat BOM table (`renderBomTable`), whereas commercial MRPs show a **Multi-Level Indented BOM Tree** (Parent → Subassembly → Raw Material) with cumulative lead-time rollup.

**Implementation Specification (`wwwroot/js/pages/engineering.js`)**:

1. **Add `+ New item` to `itemList(page, ctx)` (`lines 70–88`)**:
   Check `const engineer = canAction(ctx.me, 'engineering.manage');` at the top of `itemList`. In `<div class="page-head">`, render:
   ```javascript
   ${engineer ? '<div class="spacer"></div><button class="btn primary" id="new-item">+ New item</button>' : ''}
   ```
   Bind `#new-item` to `formModal`:
   ```javascript
   $('#new-item', page)?.addEventListener('click', () => formModal({
     title: 'Create engineering item',
     detail: 'Defines a new part master record. Create and release a draft revision before scheduling production.',
     confirmLabel: 'Create item',
     reasonRequired: true,
     fields: [
       { name: 'partNumber', label: 'Part number', code: true, required: true, placeholder: 'ASSY-200' },
       { name: 'description', label: 'Description', required: true, placeholder: 'High-pressure manifold assembly' },
       { name: 'unitOfMeasure', label: 'Unit of measure', value: 'EA', required: true, options: [['EA', 'EA — Each'], ['KG', 'KG — Kilogram'], ['M', 'M — Meter'], ['L', 'L — Liter'], ['SET', 'SET — Kit / Set']] },
       { name: 'makeBuy', label: 'Make or buy', type: 'select', value: 'Make', options: [['Make', 'Make (manufactured in-house)'], ['Buy', 'Buy (purchased from supplier)']] },
       { name: 'class', label: 'Engineering class', type: 'select', value: 'ManufacturedToOurDesign', options: Object.entries(CLASS_LABEL) },
       { name: 'leadTimeDays', label: 'Planning lead time (days)', type: 'number', value: 5, min: 0, max: 365, required: true },
       { name: 'lotTracked', label: 'Track inventory by lot number', type: 'checkbox', value: true },
       { name: 'serialized', label: 'Track individual serial numbers', type: 'checkbox', value: false },
     ],
   }, (v, reason) => command('/api/engineering/items', {
     partNumber: v.partNumber,
     description: v.description,
     unitOfMeasure: v.unitOfMeasure,
     makeBuy: v.makeBuy,
     leadTimeDays: Number(v.leadTimeDays),
     lotTracked: Boolean(v.lotTracked),
     serialized: Boolean(v.serialized),
     class: v.class || null,
   }, reason), () => { location.hash = `#/engineering/${encodeURIComponent(v.partNumber)}`; }));
   ```

2. **Add `Release revision` and `Withdraw revision` to `structureDetail()` (`lines 550–575`)**:
   Inside `structureDetail()` (`pages/engineering.js:550`), alongside `#parent-new-revision`:
   * When `manage && revision && isDraft`, render `<button class="btn small primary" id="parent-release-revision">Release revision</button>`.
   * When `manage && revision && revision.status === 'Released'`, render `<button class="btn small danger" id="parent-withdraw-revision">Withdraw revision</button>`.
   * Bind `#parent-release-revision` to `formModal` posting `{ effectiveFrom: v.effectiveFrom ? new Date(v.effectiveFrom).toISOString() : null }` to `${revisionUrl(selectedRevision)}/release`.
   * Bind `#parent-withdraw-revision` to `confirmWithReason` posting `{}` to `${revisionUrl(selectedRevision)}/withdraw`.

3. **Add `Indented tree` toggle in `structureDetail()` (`lines 557–563`)**:
   Add a third segmented tab `['tree', 'Indented multi-level tree']` to `[data-structure-tab]`. When `activeTab === 'tree'`, recursively expand subassembly BOMs (up to depth 6, guarding against cycles) using `tryGet('/api/engineering/items/{childPart}/revisions')` and render an indented hierarchy showing:
   * Level badge (`L0`, `L1`, `L2`…) with visual tree connectors (`├─`, `└─`)
   * Component part link, description, `Make/Buy` pill, extended quantity per top-level unit (`parentQty * line.quantityPer`), item lead time, and **Cumulative Critical-Path Lead Time** (`sum of longest path of Make + Buy lead times`).


### Task 7.2 — Universal CSV Import Wizard & 1-Click Table CSV Export (`wwwroot/js/forms.js`, `wwwroot/js/ui.js`, `pages/engineering.js`, `pages/mrp.js`, `pages/procurement.js`)

**Why Commercial Systems Win Today**:
When migrating from spreadsheets or an existing ERP into [Katana](https://www.google.com/search?q=https%3A%2F%2Fkatanamrp.com%2F), [MRPeasy](https://www.google.com/search?q=https%3A%2F%2Fwww.mrpeasy.com%2Fmanufacturing-erp-software%2F), or [Odoo](https://www.google.com/search?q=https%3A%2F%2Fwww.odoo.com%2Fapp%2Fmanufacturing), planners can bulk-upload Items, BOMs, Suppliers, and Customer Demand via CSV, and export any filtered table to CSV in one click. In Forge, users currently have to click `+ Demand` or `+ New supplier` one record at a time, and only `#/reports` has a CSV download button.

**Implementation Specification**:

1. **Add 1-Click `Export CSV` to `enhanceTables()` in `wwwroot/js/ui.js` (`lines 201–263`)**:
   Inside `enhanceTables(root = document)` in `wwwroot/js/ui.js`:
   * For every enhanced table with `rows.length > 0`, append a `<button class="btn small ghost" type="button" data-table-export>Export CSV</button>` inside the `.table-tools` bar (even when `rows.length <= threshold`, or show `.table-tools` whenever `rows.length >= 1`).
   * On click, serialize all currently visible (non-`.hidden`) rows and `<thead>` headers into RFC 4180 CSV (`"..."` escaping quotes and stripping button-only action columns) and trigger an instant browser download (`forge-export-${Date.now()}.csv`).

2. **Add Reusable `csvImportModal()` to `wwwroot/js/forms.js`**:
   Export a new helper `csvImportModal({ title, detail, columns, sampleRows, onRow }, onDone)` in `wwwroot/js/forms.js`:
   * Renders a modal with:
     * A **`Download CSV template`** button that downloads a pre-formatted header + sample row `.csv` file for the target entity.
     * A `<input type="file" accept=".csv,text/csv">` picker **and** a pasteable `<textarea class="input mono" rows="6" placeholder="Paste CSV rows or tab-separated spreadsheet rows here…">`.
     * A live **Preview Table** showing parsed rows and validation status before submission.
     * Audit trail `reason` input (`required`).
   * When confirmed, sequentially calls `await onRow(rowObj, reason)` for each valid row, showing progress (`Importing 14 / 20…`) and reporting any rejected rows inline without losing the remaining batch.

3. **Wire `Import CSV` Buttons on the 4 High-Volume Onboarding Screens**:
   * **Engineering Items (`pages/engineering.js`, `itemList`)**: `Import items CSV` (`partNumber,description,unitOfMeasure,makeBuy,class,leadTimeDays,lotTracked,serialized` → `POST /api/engineering/items`).
   * **Engineering Draft BOM (`pages/engineering.js`, `structureDetail`)**: `Import BOM CSV` (`lineNumber,componentPartNumber,quantityPer,scrapPercent` → `POST /api/engineering/items/{part}/revisions/{rev}/bom`).
   * **MRP Demand (`pages/mrp.js`, `demandTab`)**: `Import demand CSV` (`partNumber,quantity,needDate,priority,kind,customer,reference,sourceKey` → `POST /api/planning/demand`).
   * **Procurement Suppliers (`pages/procurement.js`, `suppliersTab`)**: `Import suppliers CSV` (`code,name,notes` → `POST /api/procurement/suppliers`).


### Task 7.3 — Interactive Gantt Operation Inspector, Priority Resequencing & 1-Click What-If Launcher (`wwwroot/js/pages/timeline.js`, `wwwroot/js/pages/scenarios.js`, `wwwroot/css/forge.css`)

**Why Commercial Systems Win Today**:
In APS/MES platforms like PlanetTogether, [SAP S/4HANA Fiori](https://www.google.com/search?q=https%3A%2F%2Fwww.sap.com%2Fproducts%2Fscm%2Fs4hana-manufacturing-solutions.html), and [Plex](https://www.google.com/search?q=https%3A%2F%2Fplex.rockwellautomation.com%2Fen-us%2Fsmart-manufacturing-platform.html), clicking a scheduled operation bar on the Gantt chart opens an interactive dispatch inspector where the planner can see *why* the bar is scheduled there, preview the factory-wide ripple effect of changing its priority, or launch a what-if recovery trial. In `wwwroot/js/pages/timeline.js` (`ganttHtml`, lines 64–113), the SVG `<rect>` bars are completely static (`<title>` hover only). Additionally, `wwwroot/js/pages/scenarios.js` (`lines 11–44`) still asks planners to type raw worker UUIDs into `personIds` and `personId`.

**Implementation Specification**:

1. **Make SVG Gantt Bars Interactive in `wwwroot/js/pages/timeline.js` (`lines 64–113`)**:
   * Add `class="gantt-bar" tabindex="0" role="button" data-gantt-order="${b.orderNumber}" data-gantt-op="${b.operationSequence}"` to each `<rect>` in `ganttHtml(s, res, lens, filter)` and add `cursor: pointer;` + hover stroke highlight in `wwwroot/css/forge.css`.
   * Add a zoom/window toolbar above the Gantt (`24h`, `3d`, `7d`, `14d` horizontal zoom scale + `Filter by status: All | Late | AtRisk | Setup`).
   * When a planner clicks (or presses `Enter` on) any Gantt `<rect>`, open an **Operation Schedule & Dispatch Drawer** (`drawer(...)`) displaying:
     * **Order & Operation Summary**: Order `#${b.orderNumber}`, Part `${b.partNumber}`, Operation `${b.operationSequence}`, Machine `${b.machineCode}`, Work Center `${b.workCenterCode}`, Planned Window (`${fmtDateTime(b.plannedStart)} → ${fmtDateTime(b.plannedEnd)}`), Setup (`${b.setupMinutes} min`, `${b.setupFamily ?? 'Standard'}`), and Delivery Status (`OnTime`, `AtRisk`, `Late`).
     * **Why It Starts Here**: Any matching constraint from `con.data` for `orderNumber` / `machineCode` / `workCenterCode`.
     * **Interactive Dispatch Actions**:
       1. **`Preview & Change Order Priority`** (when `canAction(ctx.me, 'orders.priority')`): Inline priority selector (`1 — Expedite` to `5 — Low`) that calls `POST /api/orders/${b.orderNumber}/priority/preview` (`orders.js:230`), renders the live before/after schedule ripple table right inside the drawer, and lets the planner confirm `POST /api/orders/${b.orderNumber}/priority` without leaving `#/horizon`!
       2. **`Run What-If Recovery Trial →`**: Navigates to `#/scenarios?order=${b.orderNumber}&op=${b.operationSequence}&wc=${encodeURIComponent(b.workCenterCode)}` and pre-fills the Recovery Options form in `pages/scenarios.js`.
       3. **`Open Order #${b.orderNumber} →`**: Direct link to `#/orders/${b.orderNumber}`.

2. **Upgrade `pages/scenarios.js` (`lines 11–44` & `115–140`)**:
   * Read URL query parameters (`ctx.query.get('order')`, `ctx.query.get('op')`, `ctx.query.get('wc')`) in `scenariosPage()` to pre-populate the Recovery Evaluation form when launched from a Gantt bar or constraint!
   * Fetch `/api/kernel/directory` and `/api/engineering/items` in `load()` inside `pages/scenarios.js`, and replace the raw UUID inputs (`personId`, `personIds`, `partNumber`, `workCenterCode`) in `RECOVERY_FORMS` with human-readable directory/resource `<select>` and `lookup` controls.


### Task 7.4 — Searchable Security Audit Trail Explorer & Role Action Bar (`wwwroot/js/pages/settings.js` & `wwwroot/js/pages/horizon.js`)

**Why Commercial Systems Win Today**:
Regulated manufacturers (aerospace AS9100, defense CMMC, medical ISO 13485, food/grant auditors) require an interactive Audit Trail Explorer where auditors can filter by Entity Type (`order`, `item`, `inventory`, `delegation`, `access`), Entity ID, Actor, or Action and export the cryptographically hash-chained log to CSV.
* **Key Discovery**: `src/Forge.Host/Api/KernelEndpoints.cs` (`lines 162–175`) **already exposes `GET /api/kernel/audit?entityType=...&entityId=...&take=200`** with full program/marking access filtering (`AuditAccess.FilterAsync`)! Yet `wwwroot/js/pages/settings.js` (`lines 34–39`) only calls `/api/kernel/audit/verify` and never renders the audit table!

**Implementation Specification**:

1. **Add the Searchable Audit Trail Explorer in `wwwroot/js/pages/settings.js` (`lines 27–40`)**:
   When `canAudit` (`canAction(me, 'kernel.audit.read')`) is true, expand `#audit` from a 1-line status pill into a full **Cryptographic Audit Trail Explorer** panel:
   * Keep the `/api/kernel/audit/verify` hash-chain verification banner at the top (`Intact — N entries checked; every entry is hash-chained to the one before it`).
   * Add filter controls:
     * `Entity type` (`All`, `item`, `order`, `inventory`, `purchase_order`, `schedule`, `ncr`, `inspection`, `delegation`, `person`, `role`)
     * `Entity ID` text input (e.g., `10002`, `COIL-AL`)
     * `Max rows` (`50`, `100`, `200`) + `Filter` button.
   * Fetch `tryGet('/api/kernel/audit?take=100...')` and render an enhanced `<table class="table">` (with `enhanceTables` search, sorting, and `Export CSV`) showing:
     * `Seq #` (`entry.sequenceNumber`)
     * `When` (`fmtDateTime(entry.occurredAt)`)
     * `Who` (`who(entry.actor)` from `../people.js`)
     * `Action` (`entry.action`)
     * `Subject` (`${entry.entityType}:${entry.entityId}`)
     * `Audit Reason` (`entry.reason`)
     * `Hash Chain` (`<span class="mono faint" title="Prev: ${esc(entry.previousHash)}">${esc(entry.entryHash?.slice(0, 12))}…</span>`)

2. **Add the Persona "My Shift Action Bar" to Factory Horizon (`wwwroot/js/pages/horizon.js`)**:
   At the top of `horizonPage` (`#/home`), above the 6 KPI tiles, render a compact, role-aware action strip using data already fetched in `load()` (plus `tryGet('/api/kernel/work-inbox')`):
   * Shows clickable chips for the signed-in user's immediate operational bottlenecks:
     * `Proposed Schedule Awaiting Approval` (`#/scheduling`) when `prop.data` exists and `prop.data.proposedBy !== session.subject`
     * `N Machine(s) Down` (`#/maintenance`) when active unended downtime exists
     * `N Constraint(s) Open` (`#/scheduling`) when `con.data` has open items
     * `N Material Shortfall(s)` (`#/material-plan?tab=exceptions`) when `mat.data` has shortfall orders
     * `N Inbox Items Due` (`#/my-work`) when `inbox.data` has pending items.


## Part 2 — Full-Stack Commercial Parity Upgrades (Lightweight C# + Frontend — Claude + Codex)

These two upgrades require small, additive changes in `src/Forge.Host/Api/MaterialsEndpoints.cs` (and optionally `Forge.Materials`) alongside frontend updates in `wwwroot/js/pages/procurement.js` and `wwwroot/js/pages/reports.js`.

### Task 7.5 — PO Line Unit Pricing, Order Value Rollup & Kernel `V3.15` Financial Approval Threshold Enforcement (`Api/MaterialsEndpoints.cs`, `wwwroot/js/pages/procurement.js`, `wwwroot/js/pages/mrp.js`)

**Why Commercial Systems Win Today**:
In `Forge.Kernel`, `V3.15` (`ApprovalThreshold`) already supports monetary approval limits (e.g., *"Buyer can approve POs up to $10,000 USD; Purchasing Manager required above $10,000 USD"*), and `wwwroot/js/ui-authority.mjs` (`lines 12–17`) already checks `resource.amount` and `resource.currency` against `me.approvalThresholds`!
However, in `src/Forge.Host/Api/MaterialsEndpoints.cs` (`lines 89–108`), `PurchaseLineRequest` and `PurchaseOrderView` only capture `(PartNumber, Quantity, PromisedDate, RequisitionId)` without `UnitPrice` or `Currency`. Because standard Purchase Orders have no price field, buyers cannot print priced POs for vendors, and `V3.15` financial approval thresholds never trigger on PO approval.

**Full-Stack Implementation Specification**:

1. **Backend (`src/Forge.Host/Api/MaterialsEndpoints.cs`)**:
   * Add optional `decimal? UnitPrice = null, string? Currency = "USD"` to `PurchaseLineRequest` (`line 89`), `PurchaseLineAddRequest` (`line 93`), and `PurchaseLineView` (`line 105`).
   * Add computed `decimal TotalAmount` and `string Currency` to `PurchaseOrderView` (`line 107`), summing `Quantity * (UnitPrice ?? 0m)` across non-cancelled lines.
   * In `MapProcurement` (`POST /api/procurement/purchase-orders/{po:long}/approve`), pass the PO's `TotalAmount` and `Currency` into the command's `ResourceRef` (`Amount = po.TotalAmount, Currency = po.Currency`) so `Forge.Kernel`'s `V3.15` `ApprovalThreshold` gate automatically enforces dollar-value signoff limits!

2. **Frontend (`wwwroot/js/pages/procurement.js` & `wwwroot/js/pages/mrp.js`)**:
   * In `newPo()` and `+ Line` (`pages/procurement.js`) and `draftPo()` (`pages/mrp.js`), add an optional `Unit price` (`type: 'number', min: 0, step: '0.01'`) and `Currency` (`USD`, `EUR`, `GBP`, `CAD`) input.
   * Display `Unit price`, `Extended line total`, and `PO Total` on Purchase Order cards in `pages/procurement.js`.
   * Pass `{ amount: po.totalAmount, currency: po.currency }` into `canAction(ctx.me, 'procurement.po.approve', { amount: po.totalAmount, currency: po.currency })` so the `Approve` button automatically reflects the user's dollar approval authority!


### Task 7.6 — External Accounting, EDI & Grant-Reporting Export Feed (`Api/MaterialsEndpoints.cs` & `wwwroot/js/pages/reports.js`)

**Why Commercial Systems Win Today**:
Small and mid-sized manufacturers and nonprofit distribution centers typically keep QuickBooks, Xero, Sage, or NetSuite for payroll, tax, and general ledger, while running operations in their MRP/MES. Commercial systems like [Katana](https://www.google.com/search?q=https%3A%2F%2Fkatanamrp.com%2F) and [MRPeasy](https://www.google.com/search?q=https%3A%2F%2Fwww.mrpeasy.com%2Fmanufacturing-erp-software%2F) win deals by offering clean **Accounting & Inventory Movement Exports** (PO receipts, material issues to work orders, scrap adjustments, and customer shipments) that import straight into QuickBooks/Xero/NetSuite or donor grant reports.

**Full-Stack Implementation Specification**:

1. **Frontend (`wwwroot/js/pages/reports.js`) — Can Also Run 100% Client-Side Immediately**:
   Add a new report definition to `REPORTS` in `wwwroot/js/pages/reports.js`:
   * **`accounting-bridge` — "Accounting, Receiving & Grant Reconciliation Feed"**:
     * Fetches `/api/inventory/transactions?take=500`, `/api/procurement/purchase-orders`, and `/api/shipping/shipments`.
     * Produces a standardized journal/reconciliation table with columns:
       `Transaction ID | Timestamp | Journal Category (Receipt / Order Issue / Scrap / Cycle Count / Shipment) | Part Number | Lot Number | Location | Signed Qty | Reference (PO # / Order # / Shipment #) | Program / Grant Code | Actor | Audit Reason`.
     * Includes 1-click **Export CSV (QuickBooks / Xero / Excel ready)** and **Export JSON (Webhook / EDI schema)** buttons.


## Verification Checklist

1. **Syntax & Automated Test Suite**:
   * Run `node --check` across all modified files in `src/Forge.Host/wwwroot/js/`.
   * Run `node --test tests/ui/*.test.mjs` and confirm all 25+ UI test suites pass with zero regressions.
2. **Functional Smoke Verification**:
   * **Engineering (`#/engineering`)**: Click `+ New item` to create a part, open `Parent part workspace`, create a draft revision, add BOM lines (or `Import BOM CSV`), view the **Indented multi-level tree** tab, and click `Release revision`.
   * **Bulk CSV Onboarding & Export**: Verify every enhanced table shows `Export CSV` in `.table-tools`, and verify `Import CSV` modals on Engineering Items, BOMs, MRP Demand, and Procurement Suppliers download templates and preview rows before importing.
   * **Interactive Gantt (`#/horizon`)**: Click any SVG operation bar on the Gantt chart to open the **Operation Schedule & Dispatch Drawer**, preview a priority change inline, or jump to a pre-filled What-If Recovery Scenario in `#/scenarios`.
   * **Security Audit Explorer (`#/settings`)**: Verify the **Audit trail** panel displays both the cryptographic chain verification status (`Intact`) and the searchable, exportable table of hash-chained audit events from `GET /api/kernel/audit`.
