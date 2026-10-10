# JIRSN Forge

A manufacturing operations platform that answers whether work can actually happen: with the people,
equipment, materials, qualifications and safety conditions available, at the time it is needed.

Architecture: **JIRSN Forge Software Architecture v0.1** (approved 2026-09-30). The phase plan is
in section 31. Implemented so far: Phases 1–9 — kernel, readiness, engineering and orders, capacity and scheduling, shop floor, materials and MRP, quality and shipping, programs and maintenance, reporting with Horizon history, and Forge Analyst.

## What Phase 1 contains

| Area | Where | Notes |
| --- | --- | --- |
| Engine contract | `src/Forge.Kernel/Engines` | Pure `Evaluate(snapshot, parameters, asOf)`; evidence trees; input/output fingerprints for determinism |
| Canonical JSON | `src/Forge.Kernel/Serialization` | Order-independent hashing for fingerprints and audit |
| Authorization | `src/Forge.Kernel/Security` | Role AND program access AND data markings; every failed check is reported |
| Events | `src/Forge.Kernel/Events` | Envelope with business time vs recorded time, correlation and causation |
| Audit | `src/Forge.Kernel/Audit` | Hash-chained, append-only (database triggers), verifiable end to end |
| Access management | `src/Forge.Kernel/Access` | People, roles, program access; idempotent, audited, separation of duties |
| PostgreSQL | `src/Forge.Kernel.Postgres` | Migrations, unit of work, transactional outbox, relay, event log, subscriptions |
| Host | `src/Forge.Host` | ASP.NET Core API, OIDC (JWT) authentication, health checks |

## What Phase 2 contains

| Context | Where | Owns |
| --- | --- | --- |
| Competency | `src/Forge.Competency` | Qualification types and prerequisites; training; certification (by a certifier who is not the person) with expiry |
| Resources | `src/Forge.Resources` | Site → safety area → work center → machine; activity types; machine requirement profiles; activities allowed at a work center |
| Workforce | `src/Forge.Workforce` | Workers, assignments (incl. trainee), expiring presence evidence, unavailability without reasons |
| Safety rules | `src/Forge.SafetyRules` | Versioned staffing rules: drafted by a safety engineer, put in force only by a different approver |
| Planning | `src/Forge.Planning` | Engines only: personnel composition (bipartite matching), operation readiness, reallocation impact preview |

Readiness in Phase 2 evaluates the machine requirement profile, the qualified operator and safety
staffing. Equipment, material, engineering, tooling and quality are reported as "not evaluated"
until their contexts exist. Semantics chosen for open decisions are recorded in
`docs/adr/0002-phase-2-readiness-decisions.md`.

## What Phase 3 contains

| Context | Where | Owns |
| --- | --- | --- |
| Engineering | `src/Forge.Engineering` | Items, revisions with effectivity, BOMs (loop detection), routings; release validates and freezes a revision |
| Execution | `src/Forge.Execution` | Production orders: Planned → FirmPlanned → Released, Held, Cancelled; release snapshots BOM and routing |
| Planning | `src/Forge.Planning` | Order readiness per operation, across every eligible machine |

Orders tied to a program are listed and readable only by people with access to that program; to
everyone else they look like they do not exist. See `docs/adr/0003-phase-3-engineering-and-orders.md`.

## What Phase 4 contains

| Area | Where | Notes |
| --- | --- | --- |
| Shift calendars | `src/Forge.Resources` | Site time zone (IANA, via NodaTime), recurring shifts, work-center shifts, closures |
| Worker schedules | `src/Forge.Workforce` | A worker's shift; future absences come from recorded unavailability |
| Maintenance | `src/Forge.Maintenance` | Unplanned outages (open until restored) and planned maintenance windows |
| Engines | `src/Forge.Planning/Engines` | Executable capacity over time, qualified-labor supply (Skills MRP), finite scheduler with binding constraints, drop-dead dates, constraint finder |
| Schedules and constraints | `src/Forge.Planning` | Schedule versions (proposed, approved by a second person); constraints with an Open → Investigating → Resolved → Verified lifecycle |

Readiness now evaluates Equipment from recorded downtime. Forecasts use projected presence from
shift schedules and are labelled as projections. See `docs/adr/0004-phase-4-capacity-and-scheduling.md`.

## What Phase 5 contains

| Area | Where | Notes |
| --- | --- | --- |
| Badges and attendance | `src/Forge.Workforce` | Badge digests, PINs (PBKDF2), clock in/out; any scanner that types the badge |
| Shop-floor execution | `src/Forge.Execution` | Direct labor, start/stop, good and scrap reporting, operation and order completion |
| Supervised training | `src/Forge.Execution` | Operator-authorization engine; trainee + trainer sessions; trainer evaluation signed with badge + PIN; never auto-qualifies |
| Attainment | `src/Forge.Planning/Engines` | Schedule attainment against the schedule in force; scheduler schedules only the work left on started operations |
| Shop Floor screen | `src/Forge.Host/wwwroot` | Kiosk page for terminals; order pages show labor, reports and training |

Attendance, direct labor, operation authentication and electronic signoff are separate records.
See `docs/adr/0005-phase-5-shop-floor.md`.

## What Phase 6 contains

| Area | Where | Notes |
| --- | --- | --- |
| Inventory | `src/Forge.Inventory` | Locations, lots, an append-only ledger (receipt, issue, return, move, scrap, hold, release, production receipt, adjustment, count); balances are sums of the ledger |
| Procurement | `src/Forge.Procurement` | Purchase orders drafted by a buyer, approved by someone else; supplier date changes with a reason |
| Demand and item planning | `src/Forge.Planning`, `src/Forge.Engineering` | Customer orders, forecasts and other demand; safety stock, lot policy, order quantity and multiple per item |
| MRP | `src/Forge.Planning/Engines/MrpEngine.cs` | Deterministic netting, lot sizing, lead-time offset, multi-level explosion, pegging and a written "why" for every recommendation |
| Material readiness | `src/Forge.Planning/Engines/MaterialEngine.cs` | Stock and supply allocated to released orders; feeds order readiness, the scheduler and constraints |
| Screens | `src/Forge.Host/wwwroot` | Inventory, Material Plan and Procurement pages; material status on each order |

Two people are needed to approve a purchase order, post an adjustment or count variance, or release
stock from hold or inspection. See `docs/adr/0007-phase-6-materials-and-mrp.md`.

## What Phase 7a contains

| Area | Where | Notes |
| --- | --- | --- |
| Quality | `src/Forge.Quality` | Gauges and calibration, inspection points on routing operations, independent inspections, NCRs with disposition, corrective actions |
| Readiness | `src/Forge.Planning/Engines/OrderReadinessEngine.cs` | Quality dimension: an earlier inspection not passed, or an undecided NCR, blocks the next operation |
| Shipping | `src/Forge.Inventory/InventoryService.Shipping.cs` | Shipments created and packed by one person, shipped by another; stock leaves through `Shipment` ledger entries; shipped quantity reduces demand |
| Screens | `src/Forge.Host/wwwroot` | Quality and Shipping pages; quality panel on each order |

Use-as-is dispositions, corrective-action verification and shipping each need a second person.
Finished goods enter stock only after every inspection point of the order has passed.
See `docs/adr/0008-phase-7a-quality-and-shipping.md`.

## What Phase 7b contains

| Area | Where | Notes |
| --- | --- | --- |
| Programs and contracts | `src/Forge.Programs` | Programs, contracts, CLINs/SLINs, delivery schedules, requirements, milestones; segregated by program |
| Contract demand | `src/Forge.Planning/Demand.cs` | A planner turns a contract delivery into demand, and brings it back in step when the contract changes |
| Program Horizon | `src/Forge.Planning/ProgramHorizon.cs` | Units required, delivered and planned; orders on schedule, at risk and projected late; primary constraint |
| Preventive maintenance | `src/Forge.Maintenance` | PM plans, preventive and corrective work orders, planned downtime, lock-out when safety-critical PM is overdue |
| Shop-floor holds | `src/Forge.Execution` | An undecided NCR or a missed inspection stops work at the kiosk |
| Screens | `src/Forge.Host/wwwroot` | Programs and Program Horizon, Maintenance work orders and PM plans, program access in Settings |

Maintenance work is completed by a technician and signed off by someone else; program access is granted
by a security administrator. Later follow-ups add contract funding, WBS with order charging, schedule-based
earned value, spare parts issued to work orders and run-hour PM.
See `docs/adr/0009-phase-7b-programs-and-maintenance.md`.

## What Phase 8 contains

| Area | Where | Notes |
| --- | --- | --- |
| Reporting engine | `src/Forge.Planning/Reporting` | Ten standard reports with filters, grouping, totals and a headline ratio; every row lists its source records |
| Saved definitions | `src/Forge.Planning/Reporting/ReportDefinitionService.cs` | Named filters and grouping, private or shared; fixed periods or "last N days" |
| Exports | `src/Forge.Host/Api/ReportExport.cs` | CSV and Excel (data sheet plus summary sheet), print/PDF from the browser |
| What changed? | `src/Forge.Planning/Reporting/HorizonHistory.cs` | Compares the approved schedules in force at two moments, order by order, with the evidence recorded in between |
| Screens | `src/Forge.Host/wwwroot/js/pages/reports.js` | Reports page: catalog, saved reports, results with source links, What changed? |

Reports: plan vs actual output, schedule attainment, constraint history and time to resolve, machine
downtime, delivery performance, contract deliveries, nonconformances, inspection results, maintenance
work and PM compliance, inventory transactions. Each report needs read access to what it reports on,
and program-segregated rows appear only for people with access to the program.
See `docs/adr/0010-phase-8-insight-and-horizon-history.md`.

## What Phase 9 contains

| Area | Where | Notes |
| --- | --- | --- |
| Native language parser | `src/Forge.Planning/Analyst/QueryParser.cs` | Deterministic intents, entities, periods, shifts, grouping and output; says what it understood |
| Forge Analyst | `src/Forge.Planning/Analyst/AnalystService.cs` | Order status, late orders, machine blockers, unavailable machines, qualified people, what changed, affected orders, reports |
| Scenarios | same | What-if machine or person unavailable: two scheduler runs compared, nothing saved |
| Brief me | `src/Forge.Planning/Analyst/BriefService.cs` | Shift brief from records the reader may see |
| Optional AI | `src/Forge.Host/Api/AnthropicNarrator.cs` | Off by default; restates finished answers only |
| Screens | `src/Forge.Host/wwwroot/js/pages/analyst.js` | Ask box, suggestions, understood line, report confirmation, briefs |

Forge Analyst answers with the asker's own access: an answer that needs data they cannot read is
"Not authorized". To enable an AI summary, set `Forge:Analyst:Provider` to `anthropic` and
`Forge:Analyst:ApiKey` (and optionally `Forge:Analyst:Model`) in configuration; Forge works fully without it.
See `docs/adr/0011-phase-9-forge-analyst.md`.

## What V2.1 contains

V2 extends Forge without replacing it (see the V2 architecture impact review and `docs/adr/0012-v2-1-classification-and-suppliers.md`).

| Area | Where | Notes |
| --- | --- | --- |
| Item classes | `src/Forge.Engineering/EngineeringService.Classification.cs` | Make, BuildToPrint, Cots, RawMaterial, StandardHardware, Consumable, Mro, Tooling, Service, OtherControlled; existing bought items start Unclassified |
| Manufacturer parts | same | Proposed by one person, approved by a different person with COTS authority; lifecycle recorded with a reason |
| Supplier master | `src/Forge.Procurement/SupplierService.cs` | Supplier codes, holds, approved sources; existing purchase order text migrated to supplier records |
| Purchasing rules | `src/Forge.Procurement/PurchasingService.cs` | COTS lines need an approved manufacturer part; items with approved sources are bought only from them; held suppliers take no new orders or approvals |
| Report | `item-classification` | Unclassified items, COTS items that cannot be bought, lifecycle review |
| Screens | `engineering.js`, `procurement.js` | Engineering item list and detail; Suppliers tab |

## What V2.2 contains

| Area | Where | Notes |
| --- | --- | --- |
| Controlled documents | `src/Forge.Engineering/DocumentService.cs` | Internal (draft, review, approval, release by others), External (recorded as applicable), Reference (uncontrolled) |
| Rules in the database | `src/Forge.Engineering/Infrastructure/Migrations/0005_documents.sql` | One revision in force; released revisions frozen; author ≠ approver ≠ releaser; files and links never deleted |
| Files | `/api/engineering/document-files` | Stored once by SHA-256, at most 50 MB, always downloaded, never rendered |
| Attachments | `src/Forge.Host/Documents/DocumentTargetResolver.cs` | Orders, purchase orders, work orders, NCRs, suppliers, items, programs, work centers; detaching keeps history |
| Segregation | `DocumentAccess` | Program documents and marked documents are invisible without the program or marking |
| Report | `document-register` | What needs a revision, approval, release or review |
| Screens | `engineering.js`, `attachments.js` | Engineering → Documents; Documentation panel on orders; Documents on POs, suppliers, NCRs, work orders and items |

See `docs/adr/0013-v2-2-controlled-documents.md`.

## What V2.3 contains

| Area | Where | Notes |
| --- | --- | --- |
| Built to | `DocumentService.Applicability.cs` | Item revisions list their documents (whole item or one operation, optionally a pinned revision) |
| Applicability rules | same | Program, customer, due-date range, or one order under a deviation; withdrawn, never deleted |
| Applicability engine | `src/Forge.Planning/Engines/ApplicabilityEngine.cs` | Pure; date effectivity by default; disagreeing rules → requires review |
| Order documentation | `src/Forge.Planning/DocumentationService.cs` | Released orders compared with what applied at release; reissue after the floor has the new revision |
| Readiness | `OrderReadinessEngine` | Documentation dimension where the item class is enforced; otherwise "not evaluated" |
| Stock and quality | Inventory, Quality | Revision on receipts, production receipts and lots; inspection plans per revision |
| Reports | `documentation-status`, `stock-by-revision` | Mismatches, missing and unreleased documents; stock with no recorded revision |

See `docs/adr/0014-v2-3-applicability.md`.

## What V2.4 contains

| Area | Where | Notes |
| --- | --- | --- |
| Change records | `src/Forge.Engineering/ChangeService.cs` | ECN numbers; lines list document and item revisions moved; one open change per revision |
| Review policy | same, `ReviewPolicies` | Criteria → required reviews; no match → Technical; reviewers never the originator |
| Change board | same | Approval needs all reviews; approver is not the originator or an author |
| Release | same | Separate person unless a policy combines approval and release; every revision in force in one transaction |
| Impact preview | `src/Forge.Planning/Engines/ChangeImpactEngine.cs` | Orders by state, open purchases, stock by revision, where-used, inspection plans, documents |
| UI and report | Engineering → Changes, `engineering-changes` | Analyst: "show open engineering changes" |

See `docs/adr/0015-v2-4-engineering-change-control.md`.

## What V2.5 contains

| Area | Where | Notes |
| --- | --- | --- |
| Propagation | `src/Forge.Host/Propagation/` | Subscriber to `EngineeringChangeReleased`; one step per record; runs as `system:propagation` |
| Orders | `ProductionOrderService.ApplyChangeRevisionAsync` | Future and released-not-started orders move to the new revision; WIP is never edited |
| Actions | `src/Forge.Engineering/ChangeActionService.cs` | WIP dispositions (quality), purchase reviews (buyer), training acknowledgments (training coordinator) |
| Verification | `PropagationVerification` | Expected vs done/waiting/failed per kind; "propagation incomplete" until they match; retries |
| Technical packages | `procurement.line_package` | Recorded at PO approval, never changed |
| UI and report | Engineering → Changes (propagation panel), Engineering → Change actions, PO line → Package; `change-actions` | |

See `docs/adr/0016-v2-5-propagation-and-verification.md`.

## What V2.6 contains

| Area | Where | Notes |
| --- | --- | --- |
| Alternate relationships | `src/Forge.Engineering/AlternateService.cs` | Proposed by one person, approved/rejected/expired by another with authority; conditions and conversion |
| Usability | `Alternates` (Engineering contracts) | Pure; proposed never usable; unknown conditions never usable; approved vs requires approval never mixed |
| BOM line policy | `engineering.bom_line_policy` | Never / Suggest (default) / Automatic, set with approval authority and a reason |
| MRP substitution | `MrpEngine` | Approved equivalents on automatic lines, up to the shortage; substitution pegs explain it |
| Issue | `InventoryService` | `alternateFor` issues/returns an approved alternate in place of the needed part |
| UI and report | Engineering → Alternates, item where-used, Material Plan → Substitutions / Alternates; `alternates` | |

See `docs/adr/0017-v2-6-alternates.md`.

## What V2.7 contains

| Area | Where | Notes |
| --- | --- | --- |
| Demand kinds | `planning.demand` | Maintenance, MRO replenishment, facility/capital project, quality, tooling, engineering; these name their source as `kind:key` |
| Requisitions | `src/Forge.Procurement/RequisitionService.cs` | One queue from every source; raised manually or from an MRP Buy recommendation |
| Source on PO lines | `procurement.purchase_order_line` | Copied from the requisition, never changed; cancelling reopens the requisition |
| Why purchased | `src/Forge.Planning/PurchaseTraceService.cs` | PO line, receipt or issue → source, requisition, demand, receipts and where the stock went |
| UI | Purchasing → Requisitions, PO line → Why?, Material Plan → Requisition, Inventory ledger → Why bought? | |

See `docs/adr/0018-v2-7-unified-demand-and-requisitions.md`.

## What V2.8 contains

| Area | Where | Notes |
| --- | --- | --- |
| Assets | `src/Forge.Maintenance/AssetService.cs` | Production assets point at their machine; buildings, utilities and test equipment too |
| Lifecycle | `maintenance.asset_event` | Every change is a history row; nothing is edited or deleted; decommissioning needs a second person |
| Capacity | `AssetOutOfService` downtime | A restricted or decommissioned production asset takes its machine out of capacity |
| Components | `maintenance.asset_component` | Recorded as delivered, then installed and removed only by work order |
| MRO levels | `engineering.item_mro`, `MrpEngine` | At or below the reorder point MRP buys up to the maximum; critical spares below minimum are reported |
| Obsolescence | `src/Forge.Engineering/ObsolescenceService.cs`, `ObsolescenceImpactEngine` | Records with evidence; replacement approved by someone else; impact without scores or invented replacements |
| UI | Maintenance → Assets, Inventory → MRO levels, Engineering → Obsolescence | |

See `docs/adr/0019-v2-8-assets-mro-obsolescence.md`.

## What V2.9 contains

| Area | Where | Notes |
| --- | --- | --- |
| Work types | `WorkOrderKind` | Preventive, corrective, emergency, predictive, calibration, inspection, facility; on a machine, an asset or both |
| Requirements | `maintenance.work_order_part`, `SetWorkOrderRequirements` | Asset, procedure revision, qualifications, parts with positions; fixed once work starts |
| Readiness | `src/Forge.Planning/Engines/MaintenanceReadinessEngine.cs` | Asset, material (with approved MRO alternates), procedure, technician; blocked work is not scheduled |
| Parts for blocked work | `POST /work-orders/{id}/request-parts` | Requisitions sourced to the work order (V2.7) |
| Outages | `maintenance.outage`, `OutagePreviewEngine` | Proposed by one person; approved by another after the schedule preview; the impact is recorded |
| Installation | `src/Forge.Host/Installation/InstallationSubscriber.cs` | Issues to a positioned requirement record the component on the asset |
| UI and reports | Maintenance → Readiness / Outages; `maintenance-readiness`, `outages` | |

See `docs/adr/0021-v2-9-maintenance-readiness-and-outages.md`.

## What V2.10 contains

| Area | Where | Notes |
| --- | --- | --- |
| Projects context | `src/Forge.Projects` (schema `projects`) | Facility or capital; operating or capital funding; budget, dates, financial reference |
| Approval and change | `ProjectService` | Draft edited directly; approved by a different person (Facilities Manager); later changes need a reason and a second person |
| Material check | `src/Forge.Planning/Engines/ProjectMaterialEngine.cs` | Issued, from stock, on order, requested, short — per line |
| Shortages | `POST /api/projects/{code}/request-material` | Project demand (MRP) and requisitions sourced `project:CODE/LINE` |
| Issues and returns | `InventoryService.Projects.cs` | Against a line of an approved project; never over the line, never more back than issued |
| Outages | `ProposeProjectOutage` | Decided like any outage, after the schedule preview |
| UI and report | Projects page; `project-material` | |

See `docs/adr/0022-v2-10-facility-and-capital-projects.md`.

## What V2.11 contains

| Area | Where | Notes |
| --- | --- | --- |
| Serial numbers | `InventoryService` | Serialized parts move one serial at a time; serials assigned at production receipt, never reused |
| As-built record | `execution.as_built`, `IAsBuiltRecord` | Written at every issue/return to an order: revision, lot/serial, alternate and approval, supplier, PO; append-only |
| Qualifications on labor | `execution.labor.qualifications` | Recorded when an operation is started, never changed |
| Genealogy | `src/Forge.Planning/GenealogyService.cs` | Serial, order and asset genealogy from records only |
| UI and report | Order page (As built), Inventory (Genealogy lookup), Maintenance asset (Genealogy); `as-built` | |

See `docs/adr/0023-v2-11-serial-genealogy.md`.

## What V2.12 contains

| Area | Where | Notes |
| --- | --- | --- |
| "What changed?" for one subject | `src/Forge.Planning/HistoryService.cs` | Asset, machine, project, document, engineering change or part; recorded events only, newest first, each linked to its record |
| Future events | `HistoryService.CollectFutureAsync` | Dates stored on records: revisions taking effect, reviews, changes, alternates, obsolescence dates, outages, planned downtime, work-order due dates, project tasks |
| UI | Horizon → Coming up (and markers on machine rows); "What changed?" on assets, projects, items, documents and changes | |
| Analyst and report | "What changed on SL-02?"; `dated-events` ("What is coming up?") | |

See `docs/adr/0024-v2-12-horizon-and-what-changed.md`.

## What V3.1 contains

| Area | Where | Notes |
| --- | --- | --- |
| Quality status | `inventory.transaction` status | `Released`, `Inspection`, `Hold` (was `Available`); migration proves every balance unchanged |
| Locations | `inventory.location` | Hierarchy (area to position), kinds incl. staging, machine side, WIP, outside processor, in transit; nettable; point of use; custody |
| Stock positions | `StockPosition`, `/api/inventory/positions`, `stock-positions` report | On hand, released, available for planning, at point of use and custody kept apart |
| Planning | MRP, material, maintenance, project and MRO netting | Only released stock in nettable locations |

See `docs/adr/0025-v3-1-stock-dimensions-and-locations.md`.

## What V3.2 contains

| Area | Where | Notes |
| --- | --- | --- |
| Identity kind | `inventory.lot.identity_kind` | Lot, batch, roll, serial, reel, sheet or piece; serialized parts are always serials; a move never changes identity |
| Split | `POST /api/inventory/splits` | New instances whose quantities add up exactly to what left the source; revision and supplier lot carried; serials never split |
| Merge | `POST /api/inventory/merges`, `inventory.merge_policy` | Off for every part until inventory approval authority allows it; may require one revision and one supplier lot |
| Lineage | `inventory.instance_link`, `/api/inventory/lots/{part}/{lot}/lineage` | Every split and merge recorded for good: sources, results, first instances |

See `docs/adr/0026-v3-2-material-identity-and-quantity.md`.

## What V3.3 contains

| Area | Where | Notes |
| --- | --- | --- |
| Transformation records | `inventory.transformation`, `POST /api/inventory/transformations` | Consumed, scrap, loss, remaining, remnant (new instance of the issued part) and byproduct per order; never changed |
| Reconciliation | `MaterialReconciliation`, order page, `material-reconciliation` report | Issued = consumed + returned + scrap + loss + remnant + remaining ± yield tolerance; unexplained is flagged, never scrapped |
| Yield tolerance | `inventory.yield_tolerance` | Percent of what is issued, per part; set with a reason by inventory approval authority |
| Genealogy graph | `GenealogyGraph`, `GET /api/genealogy/trace`, `genealogy-trace` report | Thirteen relations read from records; back, forward or both; moves are never links |

See `docs/adr/0027-v3-3-transformation-and-genealogy-graph.md`.

## What V3.4 contains

| Area | Where | Notes |
| --- | --- | --- |
| Material tasks | `inventory.material_task`, `/api/inventory/tasks` | Stage, kit, put-away and replenish: requested, allocated (holds stock), picked (location and lot scanned), delivered (destination scanned, the only ledger move) |
| Material flow | `MaterialFlowLine`, order page, `material-flow` report | Required, requested, allocated, picked, delivered, issued, consumed; in the warehouse is a flow constraint, not a shortage |
| Cycle-count programs | `inventory.cycle_count_program`, `/api/inventory/counts-due`, `counts-due` report | A location tree, a part or both, every N days; due until counted |

See `docs/adr/0028-v3-4-movement-and-point-of-use.md`.

## What V3.5 contains

| Area | Where | Notes |
| --- | --- | --- |
| Reason codes | `execution.reason_code`, Work Centers page | The plant's words for downtime (each in one stoppage category), scrap and rework; retired, never deleted |
| Stoppages | `execution.stoppage`, `stoppages` report | Planned, unplanned, setup, changeover, maintenance, waiting, starved, blocked; one reason each; setups and changeovers against the routing's setup minutes |
| Root causes | `execution.root_cause` | Proposed with evidence; proven only when a different person confirms; never the same thing as a reason code |
| Scrap reason codes | `execution.production_report.scrap_reason_code`, shop-floor terminal | Scrap can name a scrap reason code |
| Rework and repair | `quality.rework`, NCR disposition `Repair` | Take good output back from an operation without changing its reports; repair only on an approved NCR; closing records good and scrap |
| Quantity states | `OrderQuantityStates`, order page, `quantity-states` report | Started, produced, accepted, received, shipped so far; in process, rework, repair, pending inspection, released, hold, staged now; scrapped |

See `docs/adr/0029-v3-5-production-actuals-and-quantity-states.md`.

## Plant setup

Every plant is different: one slits and winds, another bonds and laminates, another machines and paints.
**Plant Setup** in the web UI lets a manufacturing engineer build and edit the plant without code:
sites, safety areas, work centers (with a picture), machines, the kinds of work (activity types) each
machine or work center supports and who may run them, and shifts.

- Codes are permanent business keys; names, pictures and placement can be edited. Every edit carries a reason.
- Nothing is deleted. Retired records stay in history, drop out of planning, and can be reactivated.
- Forge refuses to retire, move or remove anything still used by an unfinished order, a live or draft
  routing, a worker's home work center or a current work assignment, and lists what is in the way.

API: `POST /api/resources/{sites|safety-areas|work-centers|machines|activity-types}/{code}/edit`,
`.../{code}/retire` and `.../{code}/reactivate`, `POST /api/resources/machines/{m}/activities/{a}/remove`,
`/work-centers/{wc}/activities/{a}/remove` and `/work-centers/{wc}/shifts/{s}/remove`.
`GET /api/resources/?includeRetired=true` includes retired records. See `docs/adr/0006-plant-setup.md`.

Build rules enforced at compile time: nullable, warnings as errors, and a ban on `DateTime.Now`,
`Guid.NewGuid` and `Random` in production code (use `TimeProvider` and `ForgeIds`).
Architecture dependency rules are tests in `tests/Forge.Architecture.Tests`.

## Prerequisites

- .NET SDK 10
- PostgreSQL 16 or later for the integration tests, either:
  - a local install: copy `forge.local.example.cmd` to `forge.local.cmd` and set the `postgres`
    password (the file is not committed; each test creates and migrates its own database), or
  - Docker Desktop running: tests start a throwaway PostgreSQL 16 container

## Build and test

```
dotnet build Forge.slnx
dotnet test Forge.slnx
```

On Windows, double-click `verify.cmd`; results go to `.forge-logs\verify.log`.

## Run locally

On Windows with a local PostgreSQL (`forge.local.cmd` set up as above), double-click
`run-forge.cmd`. It creates a `forge` database if needed, starts Forge and opens
http://localhost:5080/ in your browser. Keep the window open while you use Forge.

To get something to look at, double-click `seed-sample-plant.cmd` once (Forge must be running).
It builds a clearly labeled **Sample Plant** through the ordinary API: seven work centers, six
machines, a two-person slitting rule, certified workers, a released part, six released orders, a
machine outage, planned maintenance, a PTO, and a schedule proposed by one planner and approved
by another. Then sign in as `planner` (Michael Carter). Other sign-ins: `planner2`, `eng`,
`approver`, `tech`, `dev-admin`.

With Docker instead:

```
docker compose up -d
dotnet run --project src/Forge.Host
```

Development mode uses header authentication (`X-Forge-Dev-Subject: dev-admin`) and bootstraps
`dev-admin` as the first security administrator. The host refuses to start in that mode outside
the Development environment. Every command needs an `Idempotency-Key` header and a `reason`.

```
curl -H "X-Forge-Dev-Subject: dev-admin" http://localhost:5080/api/kernel/me
```

## Web UI

The UI is plain HTML, CSS and JavaScript modules in `src/Forge.Host/wwwroot` (no build step, no
packages), in the JIRSN Forge identity: Forge Orange `#F97316`, Red Tail `#C2410C`, Graphite
`#1E2227`, Steel `#4B5563`, Light `#E5E7EB`, Dark `#0F1419`.

- **Home — Factory Horizon**: work centers in routing order with status, scheduled load and the
  primary constraint; KPI tiles; work center status; planned output; top issues. Window: -24h,
  Now (8 h), +24h, +3d, +1w. Click any card or row for the evidence behind it.
- **Horizon**: the schedule as a timeline per machine, with downtime and why each operation starts when it does.
- **Engineering**: items by class, classification and manufacturer parts; controlled documents and their revisions.
- **Programs**, **Production Orders**, **Scheduling** (propose, approve, work constraints), **Material Plan**,
  **Work Centers** (shifts, executable capacity), **Shop Floor**, **Plant Setup**, **Workforce**
  (qualified labor hours), **Inventory**, **Procurement** (purchase orders, suppliers and approved sources), **Quality**, **Maintenance**, **Shipping**,
  **Settings**.
- **Reports**: standard reports, saved reports, exports and What changed?
- **Forge Analyst**: plain-language questions and Brief me, answered from Forge's records.

Every figure comes from the API. A metric Forge cannot measure is shown as *Not yet measured* with
the reason, never as a number.
Phones get the same pages with a collapsible menu and a Today's Focus / My Assignments panel.

Sign-in: in Development the sign-in page takes an Employee ID (no password). With
`Forge:Authentication:Mode = Oidc`, set `Authority` and `ClientId`; the page signs in with
authorization code + PKCE and calls the API with the access token.

## Kernel API

| Method | Path | Permission |
| --- | --- | --- |
| GET | `/api/kernel/me` | registered, active |
| GET | `/api/kernel/people` | `kernel.people.read` |
| POST | `/api/kernel/people` | `kernel.access.manage` |
| POST | `/api/kernel/people/{id}/roles` | `kernel.access.manage` |
| POST | `/api/kernel/people/{id}/roles/{role}/revoke` | `kernel.access.manage` |
| POST | `/api/kernel/people/{id}/programs` | `kernel.access.manage` |
| POST | `/api/kernel/people/{id}/programs/{programId}/revoke` | `kernel.access.manage` |
| GET | `/api/kernel/audit` | `kernel.audit.read` |
| GET | `/api/kernel/audit/verify` | `kernel.audit.read` |
| GET | `/health/live`, `/health/ready` | none |

## Phase 2 API

| Method | Path | Permission |
| --- | --- | --- |
| GET | `/api/resources/` | `resources.read` |
| POST | `/api/resources/sites`, `/safety-areas`, `/work-centers`, `/machines`, `/activity-types` | `resources.manage` |
| POST | `/api/resources/machines/{machine}/activities`, `/work-centers/{wc}/activities` | `resources.manage` |
| GET | `/api/competency/qualification-types`, `/api/competency/people/{id}` | `competency.read` |
| POST | `/api/competency/qualification-types` (+ `/{code}/prerequisites`), `/people/{id}/training` | `competency.manage` |
| POST | `/api/competency/people/{id}/certifications`, `/suspensions`, `/revocations` | `competency.certify` |
| POST | `/api/workforce/workers/{id}` | `workforce.manage` |
| POST | `/api/workforce/workers/{id}/assignment`, `/assignment/end`, `/presence`, `/departure`, `/unavailability` | `workforce.supervise` |
| GET | `/api/safety-rules/` | `safety.rules.read` |
| POST | `/api/safety-rules/drafts`, `/{code}/draft/discard` | `safety.rules.draft` |
| POST | `/api/safety-rules/{code}/versions/{v}/publish`, `/{code}/retire` | `safety.rules.approve` |
| GET | `/api/readiness/machines/{machine}?activity=`, `/api/readiness/active-work?safetyArea=` | `planning.readiness.read` |
| POST | `/api/readiness/reallocation-preview/{personId}` (read-only) | `planning.readiness.read` |

## Phase 3 API

| Method | Path | Permission |
| --- | --- | --- |
| GET | `/api/engineering/items`, `/items/{part}/revisions`, `/items/{part}/revisions/{rev}` | `engineering.read` |
| POST | `/api/engineering/items`, `/items/{part}/revisions`, `.../bom`, `.../bom/{line}/remove`, `.../routing`, `.../routing/{seq}/remove` | `engineering.manage` |
| POST | `/api/engineering/items/{part}/revisions/{rev}/release`, `/withdraw` | `engineering.release` |
| GET | `/api/orders/`, `/api/orders/{number}` | `execution.orders.read` + program access |
| POST | `/api/orders/`, `/{number}/firm`, `/hold`, `/resume`, `/cancel` | `execution.orders.manage` |
| POST | `/api/orders/{number}/release` | `execution.orders.release` |
| GET | `/api/orders/{number}/readiness` | `planning.readiness.read` + program access |

## Phase 5 API

Shop-floor calls come from a terminal (role `ShopFloorTerminal`) or a supervisor; the badge in the
body identifies the person. Badge numbers and PINs are never stored or logged.

| Method | Path | Permission |
| --- | --- | --- |
| POST | `/api/workforce/people/{id}/badge`, `/badge/revoke` | `workforce.badges.manage` |
| POST | `/api/shopfloor/identify` `{badge}` | `execution.shopfloor.read` |
| POST | `/api/shopfloor/clock-in`, `/clock-out`, `/pin` | `workforce.attendance.record` (clock-out also stops labor) |
| POST | `/api/shopfloor/start` `{badge, orderNumber, sequence, machineCode?, trainerBadge?}` | `execution.shopfloor.record` |
| POST | `/api/shopfloor/stop`, `/report` | `execution.shopfloor.record` |
| POST | `/api/shopfloor/training/{id}/evaluate` `{trainerBadge, pin, evaluation, notes}` | `execution.shopfloor.record` |
| GET | `/api/shopfloor/activity?from=&to=` | `execution.shopfloor.read` (program-filtered) |
| GET | `/api/orders/{number}/shopfloor` | `execution.orders.read` + program access |
| GET | `/api/planning/attainment?from=&to=` | `planning.schedule.read` (program-filtered) |

## Phase 4 API

| Method | Path | Permission |
| --- | --- | --- |
| POST | `/api/resources/shifts`, `/work-centers/{wc}/shifts`, `/sites/{site}/closures` | `resources.manage` |
| POST | `/api/workforce/workers/{id}/shift` | `workforce.manage` |
| GET | `/api/maintenance/downtime?from=&to=` | `maintenance.read` |
| POST | `/api/maintenance/machines/{machine}/down`, `/restore` | `maintenance.downtime.report` |
| POST | `/api/maintenance/machines/{machine}/planned` | `maintenance.plan` |
| GET | `/api/planning/capacity?from=&to=&workCenter=` | `planning.readiness.read` |
| POST | `/api/planning/schedules` (propose), `/schedules/{id}/approve` | `planning.schedule.propose` / `.approve` |
| GET | `/api/planning/schedules/proposed`, `/approved`, `/{id}` | `planning.schedule.read` (program-filtered) |
| GET | `/api/planning/constraints?includeClosed=` | `planning.schedule.read` (program-filtered) |
| POST | `/api/planning/constraints/{id}/investigate`, `/resolve`, `/verify` | `planning.constraints.manage` |
| GET | `/api/orders/{number}/drop-dead-dates?shipDays=` | `planning.readiness.read` + program access |

## Phase 8 API

| Method | Path | Permission |
| --- | --- | --- |
| GET | `/api/reports/catalog` | `planning.reports.save` (lists only reports you may run) |
| GET | `/api/reports/{reportId}?from=&to=&lastDays=&programId=&workCenter=&machine=&partNumber=&status=&search=&groupBy=` | the report's read permission (program-filtered) |
| GET | `/api/reports/{reportId}/export?format=csv\|xlsx&…` | as above |
| GET | `/api/reports/definitions` | `planning.reports.save` |
| POST | `/api/reports/definitions` `{definitionId?, name, reportId, parameters, shared}` | the report's read permission; owner only for changes |
| POST | `/api/reports/definitions/{id}/delete` | `planning.reports.save`; owner only |
| GET | `/api/planning/history/compare?at1=&at2=&includeUnchanged=` | `planning.schedule.read` (program-filtered; audit evidence with `kernel.audit.read`) |

## Phase 9 API

| Method | Path | Permission |
| --- | --- | --- |
| GET | `/api/analyst/status` | `planning.analyst.use` |
| POST | `/api/analyst/interpret` `{question}` | `planning.analyst.use` |
| POST | `/api/analyst/ask` `{question, narrate?}` | `planning.analyst.use`, plus the read permission each answer needs |
| GET | `/api/analyst/brief?shift=` | `planning.analyst.use`; sections by read permission |

## V2.1 API

| Method | Path | Permission |
| --- | --- | --- |
| POST | `/api/engineering/items/{part}/classification` `{class, reason}` | `engineering.manage` |
| GET | `/api/engineering/manufacturer-parts?part=` | `engineering.read` |
| POST | `/api/engineering/items/{part}/manufacturer-parts` `{manufacturer, mpn, manufacturerRevision?, lifecycle?, datasheet?, note?}` | `engineering.cots.propose` |
| POST | `/api/engineering/items/{part}/manufacturer-parts/approve` `{manufacturer, mpn}` | `engineering.cots.manage`, not the proposer |
| POST | `/api/engineering/items/{part}/manufacturer-parts/withdraw` `{manufacturer, mpn, reason}` | `engineering.cots.manage` |
| POST | `/api/engineering/items/{part}/manufacturer-parts/lifecycle` `{manufacturer, mpn, lifecycle, reason}` | `engineering.cots.propose` |
| GET / POST | `/api/procurement/suppliers` `{code, name, notes?}` | `procurement.read` / `procurement.manage` |
| POST | `/api/procurement/suppliers/{code}` `{name, notes?}` | `procurement.manage` |
| POST | `/api/procurement/suppliers/{code}/status` `{status, reason}` | `procurement.approve` |
| GET | `/api/procurement/approved-sources?part=` | `procurement.read` |
| POST | `/api/procurement/items/{part}/approved-sources` `{supplierCode, note?, reason}` | `procurement.approve` |
| POST | `/api/procurement/items/{part}/approved-sources/{code}/remove` `{reason}` | `procurement.approve` |

## V2.2 API

| Method | Path | Permission |
| --- | --- | --- |
| POST | `/api/engineering/document-files` (raw body, `X-File-Name`) | `engineering.documents.attach` |
| GET | `/api/engineering/documents`, `/api/engineering/documents/{number}` | `engineering.read`, program and markings |
| GET | `/api/engineering/documents/{number}/revisions/{rev}/file` | `engineering.read`, program and markings |
| POST | `/api/engineering/documents` `{number, title, type, authority, externalAuthority?, externalNumber?, programId?, markings?}` | `engineering.documents.manage` |
| POST | `/api/engineering/documents/{number}` `{title, markings, reason}` | `engineering.documents.manage` |
| POST | `/api/engineering/documents/{number}/revisions` `{revision, fileSha256?, fileName?, changeNote?, reviewBy?}` | `engineering.documents.manage` |
| POST | `/api/engineering/documents/{number}/revisions/{rev}` (edit draft), `/submit` | `engineering.documents.manage` |
| POST | `/api/engineering/documents/{number}/revisions/{rev}/approve`, `/return` `{reason}` | `engineering.documents.approve`, not the author |
| POST | `/api/engineering/documents/{number}/revisions/{rev}/release` `{effectiveFrom?, reason}`, `/obsolete` `{reason}` | `engineering.documents.release`, not the author |
| GET | `/api/engineering/attachments/{type}/{id}?history=` | `engineering.read` and the record's read permission |
| POST | `/api/engineering/attachments/{type}/{id}` `{documentNumber, revision?, purpose, note?}` | `engineering.documents.attach` and the record's read permission |
| POST | `/api/engineering/attachments/{type}/{id}/file` `{fileSha256, fileName, title?, type?, markings?, note?}` | `engineering.documents.attach` |
| POST | `/api/engineering/attachment-links/{linkId}/detach` `{reason}` | `engineering.documents.attach` |

Record types: `ProductionOrder`, `PurchaseOrder`, `WorkOrder`, `Ncr`, `Supplier`, `Item`, `ItemRevision` (`PART:REV`), `Program`, `WorkCenter`.

## V2.3 API

| Method | Path | Permission |
| --- | --- | --- |
| GET / POST | `/api/engineering/items/{part}/revisions/{rev}/documents` `{documentNumber, documentRevision?, operationSequence?, note?, reason?}` | `engineering.read` / `engineering.documents.manage` (reason on released revisions) |
| POST | `/api/engineering/document-requirements/{id}/remove` `{reason}` | `engineering.documents.manage` |
| GET / POST | `/api/engineering/documents/{number}/rules` `{revision, condition, programId?, customer?, dueFrom?, dueTo?, orderNumber?, reference?, reason}` | `engineering.read` / `engineering.documents.release` |
| POST | `/api/engineering/applicability-rules/{id}/withdraw` `{reason}` | `engineering.documents.release` |
| GET / POST | `/api/engineering/documentation-policy`, `/api/engineering/documentation-policy/{itemClass}` `{enforced, reason}` | `engineering.read` / `engineering.documents.release` |
| POST | `/api/engineering/orders/{n}/reissue-documentation` `{reason}` | `execution.orders.release` |
| GET | `/api/planning/applicability?order=` or `?po=` | `execution.orders.read` or `procurement.read`, and `engineering.read` |

Receipts take an optional `revision`; inspection plans take an optional `revision` (and `?revision=` to remove one).

## V2.4 API

| Method | Path | Permission |
| --- | --- | --- |
| GET | `/api/engineering/changes`, `/api/engineering/changes/{n}` (with required reviews and reviewer roles), `/api/engineering/changes/{n}/impact` | `engineering.read` |
| POST | `/api/engineering/changes` `{title, class, type, priority?, changeReason, description?, programId?, customer?, requestedEffective?}`; `/api/engineering/changes/{n}` (edit draft) | `engineering.changes.manage` |
| POST | `/api/engineering/changes/{n}/lines` `{kind: DocumentRevision\|ItemRevision, subject, toRevision, note?}`; `/lines/{line}/remove` `{reason}` | `engineering.changes.manage` |
| POST | `/api/engineering/changes/{n}/submit`, `/cancel` `{reason}` | `engineering.changes.manage` |
| POST | `/api/engineering/changes/{n}/reviews` `{kind, decision, notes?}` | `engineering.changes.review` and a reviewer role for the kind |
| POST | `/api/engineering/changes/{n}/approve`, `/reject` `{reason}` | `engineering.changes.approve` |
| POST | `/api/engineering/changes/{n}/release` `{effectiveFrom?, reason}` | `engineering.release` |
| GET / POST | `/api/engineering/review-policies[?history=true]` `{name, changeClass?, changeType?, itemClass?, programId?, customer?, requiredReviews, combineApprovalRelease, reason}`; `/review-policies/{id}/withdraw` `{reason}` | `engineering.read` / `engineering.changes.approve` |


## V2.5 API

| Method | Path | Permission |
| --- | --- | --- |
| GET | `/api/engineering/changes/{n}/propagation` (record, verification, actions), `/api/engineering/change-propagations` (incomplete) | `engineering.read` |
| POST | `/api/engineering/changes/{n}/propagation/retry` | `engineering.release` |
| GET | `/api/engineering/change-actions[?open=false&kind=]` | `engineering.read` |
| POST | `/api/engineering/change-actions/{id}/resolve` `{resolution, notes?}` | by kind: `quality.disposition`, `procurement.manage`, `competency.manage` |
| GET | `/api/procurement/purchase-orders/{po}/lines/{line}/package` | `procurement.read` |


## V2.6 API

| Method | Path | Permission |
| --- | --- | --- |
| GET | `/api/engineering/alternates[?part=&status=]`, `/alternates/{id}`, `/alternates/where-used/{part}`, `/alternates/candidates/{part}?program=&customer=&parent=&asset=&machine=&operation=&supplier=&date=&maintenance=` | `engineering.read` |
| POST | `/api/engineering/alternates` `{primaryPart, alternatePart, kind, factor?, programId?, customer?, parentPart?, assetCode?, machineCode?, operationSequence?, supplierCode?, validFrom?, validTo?, qualityNotes?, evidence?, changeNumber?}` | `engineering.alternates.propose` |
| POST | `/api/engineering/alternates/{id}/approve`, `/reject` `{reason}`, `/expire` `{reason}` | `engineering.alternates.approve` (`maintenance.alternates.approve` for maintenance-only) |
| POST | `/api/engineering/items/{part}/revisions/{rev}/bom/{line}/policy` `{policy, reason}` | `engineering.alternates.approve` |
| POST | `/api/inventory/issues`, `/returns` now take `alternateFor` | `inventory.transact` |

## V2.7 API

| Method | Path | Permission |
| --- | --- | --- |
| GET | `/api/procurement/requisitions[?open=false]` | `procurement.requisition.raise` |
| POST | `/api/procurement/requisitions` `{partNumber, quantity, needDate, sourceKind, sourceKey, sourceLabel, requiredRevision?, supplierCode?, programId?}` | `procurement.requisition.raise` |
| POST | `/api/procurement/requisitions/{id}/cancel` `{reason}` | requester, or `procurement.manage` |
| POST | `/api/planning/mrp/requisitions` `{plannedOrder, supplierCode?, requiredRevision?}` | `procurement.requisition.raise` |
| POST | `/api/procurement/purchase-orders`, `/{po}/lines` lines take `requisitionId` | `procurement.manage` |
| POST | `/api/planning/demand` takes `sourceKey` | `planning.demand.manage` |
| GET | `/api/procurement/purchase-orders/{po}/lines/{line}/why`, `/api/inventory/transactions/{id}/why?part=` | `procurement.read`, `inventory.read` or `procurement.requisition.raise` |

## V2.8 API

| Method | Path | Permission |
| --- | --- | --- |
| GET | `/api/maintenance/assets`, `/assets/{code}` (asset, history, components, obsolescence) | `maintenance.read` |
| POST | `/api/maintenance/assets` `{assetCode, name, type, machineCode?, workCenterCode?, location?, manufacturer?, model?, serialNumber?, criticality, expectedLifeYears?, replacementCost?}` | `maintenance.assets.manage` |
| POST | `/api/maintenance/assets/{code}/change`, `/status` `{status: Active\|Restricted\|Obsolete, commissionedOn?, reason}`, `/decommission` `{reason}` | `maintenance.assets.manage` |
| POST | `/api/maintenance/assets/{code}/decommission/decide` `{approve, reason}` | `maintenance.approve` (not the requester) |
| POST | `/api/maintenance/assets/{code}/components` `{partNumber, revision?, position, quantity, workOrderId?}`, `/api/maintenance/components/{id}/remove` `{workOrderId?, reason}` | `maintenance.work` with a work order; `maintenance.assets.manage` while Planned |
| GET | `/api/inventory/mro`, `/mro/{part}/history` | `inventory.read` |
| POST | `/api/inventory/mro/{part}` `{minQuantity, reorderPoint, maxQuantity, criticalSpare, shelfLifeDays?, reason}`, `/mro/{part}/clear` `{reason}` | `inventory.mro.manage` |
| GET | `/api/engineering/obsolescence[?all=true]`, `/obsolescence/{id}/impact` | `engineering.read` |
| POST | `/api/engineering/obsolescence` `{partNumber, status, lastTimeBuy?, endOfLife?, endOfSupport?, evidence, replacementPart?}`, `/{id}/replacement` `{replacementPart}` | `engineering.obsolescence.record` |
| POST | `/api/engineering/obsolescence/{id}/replacement/approve`, `/reject` `{reason}` | `engineering.alternates.approve` (not the proposer) |

## V2.9 API

| Method | Path | Permission |
| --- | --- | --- |
| POST | `/api/maintenance/work-orders` `{machineCode?, assetCode?, title, description?, dueDate, kind}` | `maintenance.downtime.report` |
| GET, POST | `/api/maintenance/work-orders/{id}/requirements` `{assetCode?, procedureDocument?, procedureRevision?, qualifications[], parts[{partNumber, quantity, position?}]}` | `maintenance.read`, `maintenance.plan` |
| GET | `/api/maintenance/readiness`, `/work-orders/{id}/readiness` | `maintenance.read` |
| POST | `/api/maintenance/work-orders/{id}/request-parts` `{reason}` | `procurement.requisition.raise` |
| POST | `/api/maintenance/work-orders/{id}/schedule` `{startsAt, endsAt}` (proposes an outage on a running machine) | `maintenance.plan` |
| GET | `/api/maintenance/outages[?includeDecided=true]`, `/outages/{id}` (with preview), `/outages/preview?machine=&from=&to=` | `maintenance.read` |
| POST | `/api/maintenance/outages/{id}/approve`, `/reject` `{reason}` | `maintenance.outage.approve` (not the proposer) |
| POST | `/api/maintenance/outages/{id}/withdraw` `{reason}` | proposer, or `maintenance.outage.approve` |
| POST | `/api/maintenance/work-orders/{id}/parts` takes `alternateFor` | `inventory.spares.issue` |

## V2.10 API

| Method | Path | Permission |
| --- | --- | --- |
| GET | `/api/projects/`, `/api/projects/{code}` (lines, tasks, changes, material check, movements, purchases, requisitions, outages) | `projects.read` |
| POST | `/api/projects/` `{code, title, type, funding, budget?, startDate, endDate, financialReference?, owner?, description?}` | `projects.manage` |
| POST | `/api/projects/{code}` (header), `/{code}/lines` `{partNumber, quantity, needDate, description?}`, `/{code}/lines/{n}` `{quantity, needDate}`, `/{code}/lines/{n}/cancel` — a change request with `reason` once approved | `projects.manage` |
| POST | `/api/projects/{code}/approve`, `/api/projects/changes/{id}/approve`, `/reject` `{reason}` | `projects.approve` (not the creator / requester) |
| POST | `/api/projects/{code}/complete` `{reason}` | `projects.manage` |
| POST | `/api/projects/{code}/cancel` `{reason}` | `projects.approve` |
| POST | `/api/projects/{code}/tasks` `{title, dueDate, milestone}`, `/api/projects/tasks/{id}/complete` `{completedOn}` | `projects.manage` |
| POST | `/api/projects/{code}/request-material` `{reason?}` | `projects.read`, `planning.demand.manage`, `procurement.requisition.raise` |
| POST | `/api/projects/{code}/issues`, `/returns` `{lineNumber, partNumber, quantity, locationCode, lotNumber?}` | `inventory.transact` |
| POST | `/api/projects/{code}/outages` `{machineCode, startsAt, endsAt, description}` | `projects.manage` |

## V2.11 API

| Method | Path | Permission |
| --- | --- | --- |
| POST | `/api/inventory/production-receipts` takes `serials[]` for serialized parts | `inventory.transact` |
| GET | `/api/execution/serials/{serial}/genealogy[?part=]`, `/api/orders/{n}/genealogy` | `execution.orders.read` (program access applies) |
| GET | `/api/maintenance/assets/{code}/genealogy` | `maintenance.read` |

## V2.12 API

| Method | Path | Permission |
| --- | --- | --- |
| GET | `/api/history/{subject}[?kind=asset\|machine\|project\|document\|change\|item&days=]` | read permission of the subject's context (program access applies) |
| GET | `/api/horizon/events[?days=90]` | any; each kind of event only with its context's read permission |
| GET | `/api/reports/dated-events` | `planning.analyst.use` |

## V3.1 API

| Method | Path | Permission |
| --- | --- | --- |
| POST | `/api/inventory/locations` takes `parentCode`, `level`, `nettable`, `workCenterCode`, `machineCode`, `custodianSupplier` | `inventory.manage` |
| POST | `/api/inventory/locations/{code}` (change, reason required) | `inventory.manage` |
| GET | `/api/inventory/positions[?part=&location=&lot=]` | `inventory.read` |

## V3.2 API

| Method | Path | Permission |
| --- | --- | --- |
| POST | `/api/inventory/receipts` takes `identityKind` | `inventory.transact` |
| POST | `/api/inventory/splits` (reason required) | `inventory.transact` |
| POST | `/api/inventory/merges` (reason required, policy must allow) | `inventory.transact` |
| GET | `/api/inventory/merge-policy/{part}` | `inventory.read` |
| POST | `/api/inventory/merge-policy/{part}` (reason required) | `inventory.approve` |
| GET | `/api/inventory/lots/{part}/{lot}/lineage` | `inventory.read` |

## V3.3 API

| Method | Path | Permission |
| --- | --- | --- |
| POST | `/api/inventory/transformations` (reason required for scrap or loss) | `inventory.transact` |
| GET | `/api/inventory/orders/{order}/reconciliation` | `inventory.read` |
| GET | `/api/inventory/yield-tolerances` | `inventory.read` |
| POST | `/api/inventory/yield-tolerance/{part}` (reason required) | `inventory.approve` |
| GET | `/api/genealogy/trace?part=&lot=` or `?order=` or `?shipment=`, `&direction=Back\|Forward\|Both` | `inventory.read` |

## V3.4 API

| Method | Path | Permission |
| --- | --- | --- |
| GET | `/api/inventory/tasks[?open=&order=]` | `inventory.read` |
| POST | `/api/inventory/tasks` (stage, kit, put-away, replenish; `fromLocation` allocates at once) | `inventory.transact` |
| POST | `/api/inventory/tasks/{id}/allocate`, `/pick` (scans), `/deliver` (scan), `/cancel` (reason required) | `inventory.transact` |
| GET | `/api/inventory/orders/{order}/material-flow` | `inventory.read` |
| GET | `/api/inventory/count-programs`, `/api/inventory/counts-due` | `inventory.read` |
| POST | `/api/inventory/count-programs`, `/api/inventory/count-programs/{code}/retire` (reason required) | `inventory.manage` |

## V3.5 API

| Method | Path | Permission |
| --- | --- | --- |
| GET | `/api/execution/reason-codes` | `execution.shopfloor.read` |
| POST | `/api/execution/reason-codes`, `/api/execution/reason-codes/{code}` (change or retire, reason required) | `execution.reasons.manage` |
| GET | `/api/execution/stoppages[?open=&machine=&days=]` | `execution.shopfloor.read` |
| POST | `/api/execution/stoppages` (`startedAt` up to 7 days back), `/stoppages/{id}/end`, `/stoppages/{id}/root-causes` | `maintenance.downtime.report` |
| POST | `/api/execution/root-causes/{id}/confirm` (reason required, not the proposer) | `execution.rootcause.confirm` |
| POST | `/api/shopfloor/report` takes `scrapReasonCode` | `execution.shopfloor.record` |
| GET | `/api/quality/rework[?order=&open=]` | `execution.shopfloor.read` |
| POST | `/api/quality/rework` (reason required), `/api/quality/rework/{id}/close` | `execution.shopfloor.record` |
| GET | `/api/orders/{order}/quantity-states` | `execution.orders.read` |
