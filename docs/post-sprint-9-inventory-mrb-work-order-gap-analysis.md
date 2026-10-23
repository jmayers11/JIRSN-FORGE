# Post-Sprint-9 Inventory, MRB, and Work-Order Upgrade Audit

**Audit date:** 2026-10-23
**Status:** Phase 0 repository audit complete; feature implementation not started.
**Scope:** `Forge_Post_Sprint_9_Inventory_MRB_Work_Order_Upgrades.md`

## Request and execution boundary

The user's request was to review and continue with the attached updates. The attached file is the product specification. Its Phase 0 asks for an inventory of the current implementation and a gap report before feature coding, and its execution boundary schedules these upgrades after Sprint 9 is merged, tested, and baselined. The current `AI_HANDOFF.md` records Sprint 9 as partial: the WhiskerWorks data seed is unapplied, commercial/production seed records and scenarios are missing, and the complete Sprint 9 acceptance suite is not verified. Accordingly, this audit completes Phase 0 and does not claim that the Sprint 9 prerequisite is met.

The working tree already contains extensive uncommitted Sprint 8/9 work. It was preserved. This audit made no application-code, database, or seed changes.

## Existing foundations

| Area | What Forge already has | Evidence and limits |
|---|---|---|
| Part engineering | Items, revisions, revision release, editable BOM lines and routing operations, engineering-change propagation, and parent-part views. | `src/Forge.Host/Api/EngineeringEndpoints.cs`, `src/Forge.Engineering/EngineeringService.cs`, and `src/Forge.Engineering/Infrastructure/Migrations/0001_engineering.sql` through `0016_setup_family.sql`. These define product structure and released manufacturing definitions; they are not a work-order amendment system. |
| Production orders | Planned orders; release copies the effective revision's BOM and routing into order-specific operation/material rows. Order details show operations and BOM, and operation instructions can be printed. Orders have readiness, hold, material-flow, genealogy, actuals, and quality views. | `src/Forge.Execution/ProductionOrderService.cs`, `src/Forge.Execution/Infrastructure/Migrations/0001_execution.sql`, `src/Forge.Host/Api/EngineeringEndpoints.cs`, and `src/Forge.Host/wwwroot/js/pages/orders.js`. |
| Inventory | Append-only transaction ledger with balances derived from ledger entries; stock positions retain lot, location, quality status, custody, nettable status, point-of-use, and shelf-life usability. Receiving, inspection, transfers, issues/returns, adjustments/count approvals, material tasks, containers, shipping, outside processing, split/merge, genealogy, shelf life, and maintenance spares exist. | `src/Forge.Inventory/Infrastructure/Migrations/0001_inventory.sql`, migrations `0008`–`0021`, `src/Forge.Inventory/Contracts/InventoryContracts.cs`, `src/Forge.Host/Api/MaterialsEndpoints.cs`, and `src/Forge.Host/wwwroot/js/pages/inventory.js`. The inventory UI is organized around broad functional tabs rather than the requested part-centered workspace. |
| Holds and audit | The kernel has configurable holds on orders, operations, parts, lots, work centers, machines, suppliers, purchase orders, shipments, and programs. Commands use a shared transaction for domain state, outbox, audit, and idempotency. Hold release authority is policy-driven. | `src/Forge.Kernel/Holds/Holds.cs`, `src/Forge.Kernel/Holds/HoldService.cs`, and `src/Forge.Kernel/Commands/CommandPipeline.cs`. This is a strong base for containment, but hold placement does not itself account for partial quantities in the inventory ledger. |
| Quality and MRB | Inspection plans/results, NCRs linked to part/order/operation/lot, Use As Is and Repair second-person approval, Rework/Scrap/Return-to-Supplier dispositions, CAPA, investigations with candidate assessments and hold links, commercial quality cases, acceptance and shipment controls. | `src/Forge.Quality/QualityService.cs`, `src/Forge.Quality/Investigations/`, `src/Forge.Quality/Commercial/`, `src/Forge.Host/Api/QualityEndpoints.cs`, and quality migrations. NCRs have a compact lifecycle and investigation/hold/CAPA workflows remain separate records. |
| Maintenance | Assets, planned and corrective maintenance orders, parts and qualification requirements, downtime, approved outages, sign-off separation, maintenance readiness and spare inventory. | `src/Forge.Maintenance/MaintenanceService.WorkOrders.cs`, `src/Forge.Maintenance/Infrastructure/Migrations/0001_maintenance.sql` onward, and `src/Forge.Host/Api/AssetEndpoints.cs`. This covers machine maintenance workflows but is not yet connected to a unified equipment nonconformance case and product containment lifecycle. |
| Tests | Existing integration and planning coverage includes stock dimensions, serial integrity, hold governance/propagation, commercial quality, recovery readiness, parent-part engineering, and maintenance readiness. | Examples: `tests/Forge.Integration.Tests/StockDimensionsMigrationTests.cs`, `InventorySerialIntegrityTests.cs`, `GovernanceHoldTests.cs`, `CommercialQualityTests.cs`, `RecoveryInventoryReadinessTests.cs`, `ParentPartEngineeringTests.cs`; `tests/Forge.Planning.Tests/HoldPolicyTests.cs` and `MaintenanceReadinessTests.cs`. |

## Gaps against the brief

### 1. Inventory workspace and stock reservation

Forge already derives on-hand balances from an append-only ledger and has rich stock dimensions. Planning availability is defined as released, nettable, and shelf-life usable stock. The current inventory contract explicitly says physical stock reservation has not been implemented; the repository has no `inventory.reservation` table or reservation aggregate. Material-task allocation and planning allocations are not an equivalent, durable stock reservation that atomically reduces available-to-promise and blocks competing issue, pick, kit, or shipment commands.

There is no unified part detail view combining engineering revision, stock/lot positions, transactions, demand, orders, quality/holds, documents, genealogy, and role-gated costs. Existing inventory, engineering, quality, and order pages expose many of these facts separately. No single inventory action flow currently carries part revision, site, lot/serial, quantity, source NCR, and approved rework context through draft work-order creation.

**Implication:** define physical reservation semantics and concurrency behavior before presenting ATP or adding a wizard that promises material availability. Preserve the ledger as the source of on-hand quantity; model reservation/hold availability alongside it rather than counting reservations as new stock movements.

### 2. Unified nonconformance and MRB lifecycle

The legacy NCR record contains a part, optional order/operation/lot, quantity, defect text, disposition, and a few actor/time fields. Its lifecycle is `Open -> AwaitingApproval -> Dispositioned -> Closed`. It does not itself capture a general source type, site/location, suspect versus confirmed quantities, equipment/tool scope, affected shipments, containment actions, evidence records, review team, disposition execution, verification results, or reopen/cancel history. It supports whole-record dispositions rather than a complete partial-quantity conservation workflow.

Forge has richer investigation assessments, kernel hold records, CAPA, commercial cases, and immutable audit events, but these are distinct aggregates and screens. The current NCR disposition path does not orchestrate lot-level partial hold/release, rework order creation, direct approved scrap ledger movement, supplier return, inspection verification, genealogy, and case closure as one governed workflow. Equipment issues can create maintenance orders and restrictions, but the quality containment link to potentially affected lots and authorized return-to-service signoffs needs a deliberate bridge.

**Implication:** reuse the existing quality case, investigation, kernel hold, inventory disposition, and maintenance capabilities through explicit links and a state machine. Avoid creating duplicate tables for facts those aggregates already own. No disposition should make material usable until its quantity, authority, and required verification are accounted for.

### 3. Dynamic production and rework work orders

Released production operations and material requirements are immutable snapshots of the engineering revision at release. Current change propagation can move a planned or released-but-not-started order to a new effective revision; the service rejects changes once execution has started. That protects history but does not provide the requested controlled amendment proposal, before/after diff, affected quantity/serial scope, impact preview, approval, order-version increment, or change notification for a released/in-process order.

The current production-order API creates, firms, releases, holds, resumes, cancels, and evaluates readiness. It has no order-scoped add/remove/reorder operation or component endpoints and no work-order amendment aggregate. Draft orders obtain their BOM/routing at release rather than supporting a reviewed, editable order-specific route/material plan. Quality rework records are tied to an existing order operation and reconcile good/scrap quantity, but they do not create a separate linked rework order with its own BOM, route, and source disposition.

Maintenance has a separate work-order type with parts and qualification requirements; production and maintenance order models should remain type-aware rather than be forced into a single execution lifecycle.

**Implication:** add a versioned amendment workflow for released production orders and a source-linked rework-order flow. Keep the current order snapshot immutable until an approved amendment applies, preserve completed operation evidence, and use expected-version checks to reject stale proposals.

### 4. Cross-module authorization and scope

Forge already has fine-grained permissions, contextual resource authorization, program visibility checks, reason-required audited commands, and separation-of-duties rules. Inventory locations carry `site_code`, and several quality/order reads derive access from the source record. The audit did not find one uniform tenant/site scope contract across the inventory, quality, execution, and maintenance query/mutation surfaces; many domain records use program or resource context, and inventory transactions reference a location rather than carrying a tenant/site key themselves.

**Implication:** before enabling multi-site seed/application or cross-module case commands, document and test how site scope is resolved from every source and target record. Add narrow role permissions for inventory, MRB review/disposition, engineering amendments, maintenance restriction/release, and cost access; enforce the same checks in the API/service layer.

### 5. Scenario evidence and acceptance coverage

Existing tests provide useful domain and integration fixtures, but the repository does not yet have the requested deterministic set of 14 WhiskerWorks scenarios for partial incoming hold, rework, sort/rework/scrap quantity conservation, equipment containment, supplier return, concession, hold bypass, task qualification, released-order amendment, serial scrap, retry idempotency, disconnected Nexus, and closure guards. Sprint 9's own handoff also says its WhiskerWorks seed has not been applied and its full scenario set is incomplete.

**Implication:** build scenario fixtures against isolated test PostgreSQL and assert ledger balance, reservation availability, hold blocks, order versions, event/audit links, and actor separation—not just screen state.

## Recommended implementation sequence

1. **Complete and baseline Sprint 9.** Finish its missing acceptance items and tests, validate in an isolated database, and record the approved baseline. The current handoff explicitly says the existing preview contains `PLANT1` and must not be used to apply the WhiskerWorks seed.
2. **Inventory read model and part workspace.** Compose engineering, inventory, planning, quality, document, order, and genealogy facts under consistent authorization. Start with a read-only part detail and transaction/lot views; add role-gated actions only when their server-side invariants are ready.
3. **Reservation and availability invariants.** Specify reservation states, locking/version strategy, expiration/cancellation, partial quantities, and consumption links. Enforce available-to-promise and hold eligibility at every issue, pick, kit, production consumption, and shipment command. Add concurrency and retry tests before the UI relies on availability.
4. **Containment and MRB orchestration.** Define state transitions and partial disposition accounting; link existing NCR/investigation records, quantity-scoped holds, inventory transactions, rework/maintenance orders, approvals, attachments, inspections, verification, and closure evidence. Add negative permission and hold-bypass tests.
5. **Dynamic order amendments and rework orders.** Add draft order editing where appropriate and proposal/approve/apply amendments for released orders, including immutable snapshots, optimistic versioning, impact preview, quantity scope, cost/material/capacity/qualification checks, notifications, and audit. Add explicit links from source NCR/disposition and source lot/serial.
6. **Cross-module WhiskerWorks scenarios.** Implement the brief's scenarios on isolated deterministic fixtures; verify exact quantity conservation, lineage, authorization, independent approvals, holds, retries, and failure/recovery behavior through the real API.

## Decisions to settle during design

- Whether physical reservations are created by planning, explicit user action, material-task allocation, or a combination; and when they become binding.
- How partial quantity holds map to lot/serial/container/bin stock and how partial release or scrap emits balancing ledger entries.
- Whether the unified case is an extension of NCR or a new quality-owned case aggregate linked to NCR, investigation, supplier, equipment, and process records.
- Required disposition authorities and customer approval rules by site, product, and disposition; ensure Use As Is/Repair remain distinct from conformance-restoring Rework.
- Which released-order changes require approval, required affected-unit scope, and behavior when some operations are already complete.
- The authoritative site boundary for records that currently carry program, work-center, location, or order context but no direct site key.

## Audit limitations and safe next step

This is a static code, migration, test, and handoff review. It did not exercise live database data or apply migrations. Sprint 9's handoff reports that the full test database is unavailable in the current environment; PostgreSQL-backed integration tests should be rerun against an isolated configured database before implementation baselining. The immediate next project gate is completing Sprint 9 and recording its tested baseline; this gap report can be used as the design input for the first post-baseline implementation slice.
