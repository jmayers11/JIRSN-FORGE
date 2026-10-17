# Forge V5 Phase 0/1 Repository and Market Audit

**Audit date:** 2026-10-16  
**Branch / revision:** `main` / `38db86a953f4d73dad3590183341bcf35e14dedf`  
**Working tree:** existing V4 implementation and handoff changes are uncommitted. This audit added documentation only.  
**Authority:** owner sequencing plus [`JIRSN_Forge_V5_Consolidated_Development_Master.md`](JIRSN_Forge_V5_Consolidated_Development_Master.md). The master is a project specification. Its embedded START COMMAND is applicable because V4 has now passed; the implementation gate required owner approval for a bounded slice.

**Follow-up:** The owner approved V5-S1 on 2026-10-16. Implementation and current verification are recorded in [`V5-S1-implementation-notes.md`](V5-S1-implementation-notes.md). The audit below remains the phase 0/1 evidence and proposal as of its original preparation.

## Decision summary

V4's local software gate passed on the current tree. The V5 Phase 0/1 audit is complete enough to make an evidence-backed first-slice recommendation, but it does not establish a market-wide functional gap or an external deployment/certification result.

The highest-risk V5 dependency is the master’s cross-version parent-genealogy invariant. Forge uses composite material identity `(part_number, lot_number)`, records split/merge lineage, and requires a source lot for a remnant. Production byproduct output currently has no exact input-lot edge even though transformation rows have source-part/source-lot fields. A production order association is not equivalent to identifying the actual physical input parent(s). This is a bounded gap in the Inventory-owned transformation path, and should be resolved before V5 relies on complete child-to-parent traceability.

**Recommended first slice for owner approval:** an Inventory-owned, test-first cross-version child genealogy proof and the smallest Inventory-domain correction needed if it fails. Cover one parent split to three child instances, a remnant, one-input and multi-input byproducts, unique child identity/label resolution, reprint without a second stock instance, and isolation of a hold on one child. Keep the existing V3/V4 Inventory, genealogy and label authorities; do not add a parallel V5 subsystem. Scope the follow-up implementation only to the failed invariant; defer allocation optimization, prediction, new schema outside the owning Inventory boundary and external connector work.

## 1. Repository inventory and verification state

### Repository truth

| Area | Observed owner / implementation | Evidence and disposition |
| --- | --- | --- |
| Inventory identity and transformation | `src/Forge.Inventory/InventoryService.Identity.cs`, `InventoryService.Transformation.cs`, `Infrastructure/Migrations/0009_material_identity.sql`, `0010_transformation.sql` | Physical inventory instance is keyed by `(part_number, lot_number)`; split/merge are linked and append-only. Transformation lines support `source_part` and `source_lot`. Remnants require their source lot. Byproduct output presently writes null source identity. |
| Genealogy queries | `src/Forge.Planning/GenealogyGraphService.cs`, `GenealogyEndpoints.cs`, `src/Forge.Inventory/InventoryService.Genealogy.cs` | Read/trace graph is already owned by Planning/Inventory paths. V5 must reuse it. |
| Label identity and print lifecycle | `src/Forge.Host/Identification/LabelContracts.cs`, `LabelService.cs`, `LabelService.Print.cs` | Source-bound copies, immutable print snapshots, template versioning, print attempt reconciliation and replay are implemented. Physical printers/agents remain unverified. |
| MRP and demand planning | `src/Forge.Planning/Engines/MrpEngine.cs`, `MaterialPlanningService.cs` | Deterministic MRP includes stock-status buckets, scheduled receipts, lead time, safety stock, lot sizing, requirement pegs/explanations, and approved-alternate substitution pegs. No predictive model or backtest was found in the inspected engine. |
| Scheduling and readiness | `src/Forge.Planning/Engines/SchedulingEngine.cs`, GoalRisk/Goal services, readiness services and endpoints | Finite schedule, goal risk, machine readiness and recovery evaluation are present. V5 should compose their facts rather than replace scheduling. |
| Lot eligibility and material tasks | `src/Forge.Inventory/Contracts/ShelfLifeContracts.cs`, `InventoryService.Tasks.cs`, `MaterialsEndpoints.cs` | Selection ranks FIFO/FEFO after hard release, custody, location, revision and shelf-life eligibility. Inventory task lifecycle is separate from required/on-hand/eligible/allocated/picked/delivered/issued/consumed quantities. |
| Engineering alternates | `src/Forge.Engineering/AlternateService.cs`, `Contracts/AlternateContracts.cs`, `Infrastructure/PostgresAlternateStore.cs`, `/api/engineering/alternates` | Approved/context-bound alternates, effectivity and policies (`Never`, `Suggest`, `Automatic`) exist. MRP tests cover proposal, status, context and permission boundaries. |
| Quality/MRB and containment | `src/Forge.Host/Api/QualityEndpoints.cs`, `src/Forge.Quality/QualityService.cs`, `Audit/QmsCapaService.cs`, `Investigations/InvestigationService.cs` | NCR, hold, disposition, approval, CAPA and trace investigation are existing quality-owned capabilities. V5 must audit aging/propagation and must not create a second MRB. |
| Supply purchasing and receipt | `src/Forge.Procurement/PurchasingService.cs`, Inventory receiving services/endpoints | Purchase approvals, supplier commitments, receipt and inspection sources exist; predictive interpretation must distinguish duplicate/replacement/cancelled orders and timing effects. |
| Reporting and migration | `src/Forge.Host/Api/ReportExport.cs`, `src/Forge.Host/wwwroot/js` | CSV/XLSX export exists. A guided manufacturing CSV/Excel import flow was not found in the searched host UI/API paths. This is a search result, not proof that no deployment connector or external import exists. |
| Optional humanitarian reuse | No separate feasibility module/prototype found in inspected repository paths | Research-only, isolated synthetic test is appropriate after the manufacturing baseline. No patient or clinical data. |

### Verification

The current full `verify.cmd` result is recorded in `.forge-logs/verify.status` (`PASS`) and `.forge-logs/verify.log`:

| Gate | Result |
| --- | ---: |
| Runtime | .NET SDK 10.0.401 |
| Restore | Passed |
| Release solution build | Passed; 0 warnings, 0 errors |
| Planning tests | 303 passed |
| Kernel tests | 44 passed |
| Architecture tests | 32 passed |
| PostgreSQL integration tests | 346 passed |
| UI hardening checks | 212 passed across 24 files |
| Total | **937 passed; 0 failed** |

The focused V3/V4 evidence also includes `tests/Forge.Integration.Tests/V327Tests.cs` (healthy, constrained and unusual-valid reference factories, large shared question sets and performance budgets), `V322AvailableExampleTests.cs` (recovered order-at-risk example), and `V322Tests.cs` (recorded causation separated from correlation). `tests/Forge.Integration.Tests/V328Tests.cs` exercises PostgreSQL backup/restore against a disposable database. The full integration run includes cleanup time; these results are local software tests, not external deployment proof.

The existing branch has uncommitted implementation changes. No commit, push, deploy, production change or external communication was made for this audit. `git diff --check` returned exit code 0; Git emitted working-tree LF-to-CRLF notices.

## 2. V3/V4/V5 ownership and code-to-requirement matrix

Coverage labels: **proven** means an inspected current test/source path supports the behavior; **partial** means a related capability exists but not the V5 requirement end-to-end; **absent in inspected code** is limited to the paths searched; **external** requires real deployment or hardware evidence.

| V5 area | Existing owner and evidence | V5 verdict | Boundary / proposed action |
| --- | --- | --- | --- |
| Market gaps | V3/V4 capabilities and competitor sources below | Research only | Do not claim competitors lack scheduling, MRP, lot genealogy, serial tracking or quality. Gather direct customer tasks and repeatable comparative evidence before positioning. |
| Predictive supply intelligence | Deterministic `MrpEngine`, procurement/receipt records, inventory transactions, goal-risk and scenario engines | **Partial** | Historical data may be available in source systems, but no predictive lead-time/demand/yield model or backtest was verified. First inventory data completeness and event semantics; compare simple seasonal/naive baselines; report uncertainty and missingness; never auto-create a PO or change BOM/stock status. |
| Allocation and approved alternates | `LotSelection.Rank`, inventory task/reservation lifecycle, `AlternateService`, MRP pegs and read-only recovery/reallocation evaluators | **Partial** | Per-candidate eligibility and approved alternates exist. A unified, explainable, multi-order comparison for raw, released WIP, processed stock, remnant and approved alternate—with competing-order impact and no write—is not proven. Reuse selection, reservation and Engineering rules. |
| Unique child identity and parent genealogy | Composite `(part_number, lot_number)` identity; split/merge lineage; remnant source lot required; new identity uniqueness enforced; V4 source-bound labels | **Partial / high risk** | Composite identity may satisfy stable identity if barcodes resolve unambiguously; confirm barcode uniqueness and collision scope in test. Byproduct has no exact source-lot edge today. Add one focused Integration proof and remediate in Inventory owner if needed. Do not duplicate labeling or genealogy. |
| Labels and print failures | V4 Label service and `V41LabelLifecycleTests.Real_copy_print_reconciliation_replacement_and_alias_history_preserve_physical_source_and_current_replay_authority` | **Proven in software; external hardware pending** | Existing copy/reprint/reconciliation preserves subject and replay authority. Verify new child identity uses the canonical source subject. Validate real printer profiles/agents separately. |
| Operator readiness and adoption | V3 readiness/machine/goal risk, workforce assignment/qualification, scan-to-execute, traveler/evidence flows; V4 UI read integrations | **Partial** | Existing role screens and readiness facts exist. Need timed role-based user studies, error recovery and task success on actual representative devices before calling usability improved. |
| MRB containment/aging | Existing NCR, quality holds/disposition/approvals, genealogy trace, CAPA and investigation | **Partial** | Reuse existing Quality authority. Walk a fictional aged hold end to end, including hold scope, allocations, descendants and owner escalation; add no second MRB. |
| Adoption/integration/import | Exports and existing APIs; access, idempotency/audit controls | **Partial** | No guided import flow found in inspected host UI/API; deployment connectors remain unknown. Research migration validation and read-only integration first; require reconciliation, duplicate/effectivity checks and idempotency before any write-back proposal. |
| Humanitarian reuse | Shared inventory, kit/BOM, custody, holds, resources and goal concepts exist | **Unknown / research only** | Run isolated synthetic Food Bank and Deployable Hospital Logistics prototypes only after manufacturing acceptance. No patient/clinical data and no generalized ERP rewrite. |

### Domain ownership map

| Concern | Owning version/module | V5 integration rule |
| --- | --- | --- |
| Item/lot identity, quantity ledger, split/merge, transformation, reservations and physical inventory | V3 Inventory | Extend only this domain for physical output facts; never mirror stock or invent a second allocator. |
| Approved substitute relationship and authorization | Engineering / V3 | Read the current approval and exact context; a similarity score cannot grant equivalence. |
| MRP, schedule, order risk, recovery options | V3 Planning | V5 may compare explanations and scenarios; preserve baseline plan and provenance. |
| Label subject, template, copy, print and scan resolution | V4 Identification | Create/print only against committed canonical child identity; retries never create stock. |
| NCR, hold, disposition, inspection, release, trace investigation and CAPA | V3/V4 Quality | V5 routes to the existing Quality record and decisions; no autonomous release, closure or scrap. |
| Human identity, scopes, approval and audit | V3 Kernel / Nexus integration | Reuse authorization, separation and audit; never infer identity from a barcode or UI. |
| Forecast/inventory comparison UI | V5 surface, if approved | Read-only first; explicit source coverage, reasons, uncertainty and human-authorized follow-up. |

## 3. Highest-risk implementation findings

### P0 — exact byproduct parent genealogy is not recorded

`src/Forge.Inventory/InventoryService.Transformation.cs` describes remnant `SourceLot` as the issued lot it came from and rejects a remnant without that source at the remnant branch. The byproduct branch creates a new lot and transaction associated with an order, but passes null `source_part` and `source_lot` to the transformation line. `tests/Forge.Integration.Tests/V33Tests.cs` includes a byproduct accounting scenario, but its demonstrated linkage does not prove the actual parent-lot relationship required by V5 §6. V5 also requires multiple parents when inputs combine.

**Risk:** trace from a byproduct child to the actual consumed parent lot(s) can stop at the production order and become ambiguous. This matters for containment, quantity accounting and label claims. The precise fix may need source relationships and quantity apportionment in the owning Inventory transformation model; do not decide schema shape before focused tests and migration review.

**Identity nuance:** V3/V4 stores lot identity as `(part_number, lot_number)` rather than a separate globally assigned UUID. This is a valid candidate for stable identity, but the V5 test must prove its canonical encoding is unique, unambiguous and stable across aliases/labels and all supported transformation types. Do not label this as a known failure until that proof runs.

### P1 — current allocation is not yet proven as a shared, process-aware optimizer

Existing lot selection performs hard eligibility before FIFO/FEFO ranking, and material tasks preserve their lifecycle states. Approved alternate evaluation is context-bound. These are strong building blocks. The V5-wide scenario engine still needs evidence for concurrent competing orders, partial coverage, held/expired/revision-mismatched stock, reservations, approved alternates, processed WIP/remnants and remaining routing/inspection steps in one explainable no-write comparison. An order readiness preview or a one-part stock selection read is not proof of the whole requirement.

### P1 — forecasting proposal requires data-quality audit first

No predictive model/backtest was found in inspected Planning code. Do not treat manual purchases as extra demand without normalizing duplicate/replacement/cancelled POs, timing, replenishment and MRO. First inventory historical snapshot, requirement, consumption, scrap/yield, promise/receipt, supplier, stock movement and engineering-change completeness. A forecast needs explicit missingness, a simple baseline, held-out backtesting, bias/error, intervals, versioning and human review.

### P2 — MRB age/propagation and migration UX need demonstrated workflow evidence

Forge has existing NCR/hold/disposition/approval/CAPA and reports. V5 work should prove that an aging hold remains visible and blocks exact affected inventory/descendants/allocations, then closes only after verified human disposition. Import guidance and role-specific usability remain insufficiently evidenced; research and synthetic walkthroughs precede code.

## 4. Market pain-point register and competitor reality check

Research was reviewed on 2026-10-16. Vendor documentation supports capability claims; reviewer comments are self-reported point evidence, not prevalence estimates. Product edition, version, configuration and add-on/licensing status matter. The sources below are not a statistically representative buyer study.

### Competitor feature reality

| Vendor/product | Verified feature reality | Gap implication for Forge |
| --- | --- | --- |
| Microsoft Dynamics 365 SCM | Official docs describe finite-capacity planning with required configuration and registered resource capacity; tracked-components capability associates component batch/serial values with finished product batch/serial values (feature availability/version requirements apply). | Finite scheduling and component genealogy are not broad market gaps. Test a narrow workflow interoperability or operator guardrail only if user evidence identifies it. |
| Oracle Fusion Cloud SCM | Oracle 26C documents unique execution-run child lot numbers with the parent batch number and associated genealogy for split work orders. | Unique parent/child lot handling is already addressed by a major suite; Forge differentiation cannot rest on claiming otherwise. |
| IFS Cloud | IFS 26R1 docs describe finite scheduling that considers resource/labor sharing, priorities, calendars and maintenance schedules, with configurable objectives. | Advanced finite scheduling exists in the market. Compare actual target factory workflow and effort, not feature checklists. |
| SAP S/4HANA | Official help documents finite-capacity production planning/scheduling and routing operation scheduling. | Scheduling is established capability; avoid “competitors lack finite capacity” language. |
| Oracle Fusion genealogy / SAP trace | Official product docs cover product genealogy and item/lot trace. | Genealogy/trace are not a broad absence; test exact quantity-accurate multi-parent relationships and workflow usability. |
| Epicor Kinetic | Current vendor product page describes production-management capabilities across batching/sequencing, planning, quality and manufacturing execution. | Broad manufacturing operations already exist. Differentiate through an evidenced narrow end-to-end task, if any. |
| Infor CloudSuite Industrial Manufacturing | Current vendor pages position capacity planning/scheduling, quality and traceability within industrial manufacturing products. | Vendor marketing supports presence, but detailed parity/configuration needs product/version evaluation. |
| NetSuite | Oracle help lists manufacturing, mobile, lot/serial and supply planning capabilities across SuiteApps/bundles. | Add-on/package boundaries are a possible class-D integration/licensing issue, not evidence the feature is absent. Confirm which bundle/version before comparing. |
| Acumatica, Odoo, ERPNext, Katana, MRPeasy | Public docs describe combinations of MRP/work orders, lot/serial tracking, quality, barcode, manual allocation or inventory workflows. Exact scope differs by edition and workflow. | SMB competitor comparisons require versioned tests; do not infer exact parity or deficiency from category-level pages. |

### Pain-point evidence register

| ID | Evidence and source/date | Class | Confidence / counterevidence | Forge coverage and action |
| --- | --- | --- | --- | --- |
| M-01 | Gartner Peer Insights Epicor Kinetic review, CFO/manufacturing, 2026-05-21: reviewer says financial reporting/KPI work was difficult amid overlapping product choices and limited internal deployment resources; the same review rates the experience 4/5 and notes manufacturing/distribution capabilities are useful. | C (configuration/process), possibly D (packaging) | One reviewer; Gartner states reviews are user opinions, not objective product facts. Not prevalence evidence and not a manufacturing function absence. | Forge has report and QMS Analyst outputs. Potential UX signal only; verify with direct task study before proposing configuration simplification. |
| M-02 | G2 MRPeasy review by a Supply Chain Manager, 2026-05-25, 4.5/5: praises intuitive operations/onboarding, says advanced integrations/reporting could improve. The profile marks the review seller-invited/incentivized. | D (integration) / B (UX) | Low confidence: one incentivized review; same reviewer strongly praises UI and onboarding. Counterevidence argues against a blanket usability complaint. | Forge integration/import and reporting need a specific scenario; no broad vendor weakness claim. |
| M-03 | G2 MRPeasy review, 2026-09-08, small manufacturer, 5/5: says migration/setup worked and Shopify integration supported their operations, while calling UI old-school. Seller-invited/incentivized. | B (UX) and C (adoption), mixed | One reviewer with a conflicting positive value/setup account; low confidence. | Research direct workflows across multiple users; treat as hypothesis, not roadmap evidence. |
| M-04 | Official Microsoft tracked-components docs specify serial/batch association and component quantity controls; Oracle 26C documents parent/child run lots; IFS documents configurable finite scheduling. | E (disputed if claimed as a market gap) | Primary product documentation is strong feature-presence evidence, but does not prove all editions/customers implement it or that UX is equivalent. | Reject broad gaps “no genealogy,” “no unique child lots,” and “no finite scheduling.” Compare end-to-end process and user effort. |
| M-05 | Oracle NetSuite documentation separates some manufacturing/mobile/trace/supply capabilities into SuiteApps or bundles. | D (licensing/package boundary) | Product documentation establishes package boundaries, but customer-specific entitlements vary. | Treat as integration/licensing and total-cost evidence only after verifying actual target bundle. |
| M-06 | ERPNext batch traceability docs warn sequential automatic mapping may not represent actual material use, while providing exact manual mapping. | B (guardrail/usability) | Official product docs prove an available workflow and warn about a specific mapping risk; this does not mean Forge customers experience it. | Potential acceptance-test design input: compare recorded causation to physical input mapping; avoid claiming absence of traceability. |

### Gap register disposition fields

The market evidence is early. Unknown customer value and owner decisions are intentionally left unresolved rather than fabricated.

| ID | Likely workaround / counterevidence | Forge implementation proof and test evidence | Mitigation and no-change option | Customer value / cost / risk / priority | Owner decision and acceptance test |
| --- | --- | --- | --- | --- | --- |
| M-01 | Configure existing dashboards/reports and deployment scope; the same reviewer says manufacturing/distribution capabilities are useful. | Existing reporting and QMS Analyst sources; full Planning/Integration/UI verification passes. No direct Epicor comparison test exists. | Interview users and observe KPI setup; no-change if the complaint does not reproduce. | Value unknown; low-cost discovery; low engineering risk; priority 4. | Undecided. Accept only if multiple target users reproduce a measurable setup burden and a usability change improves task completion without changing metric meaning. |
| M-02 | Use supported integration/API options or a staged reporting workflow; the reviewer praises onboarding and overall usability. | Forge has APIs, exports and read-only reporting; no guided manufacturing import was found in inspected host UI/API paths. | Document supported integration/setup; no-change if existing paths meet the user task. | Value unknown; low-cost workflow study, medium connector scope; priority 5. | Undecided. Require named source/destination, reconciliation rules, idempotency plan, measured setup effort and synthetic import test before build. |
| M-03 | Existing MRPeasy customer successfully migrated and uses Shopify; reviewer also requests a more modern UI. | Forge has role-specific pages and UI tests but no user study benchmark. | Use task observation and design iteration; no-change if existing screen/task success is strong. | Value unknown; medium research/design cost; moderate risk of spending on cosmetic rather than operational issues; priority 5. | Undecided. Require timed tasks, completion/error/recovery baseline and repeated issue across target roles before changing UI. |
| M-04 | Existing vendor modules/configuration include documented finite scheduling, tracking or hierarchy; implementation/edition may vary. | Forge already implements deterministic MRP, scheduling, genealogy and labels; relevant proof is in `V327Tests`, `V33Tests`, `V41LabelLifecycleTests` and 937-check verification. | Reject broad absence claim; compare one exact end-to-end workflow only. No-change is preferred absent a failing requirement. | Building duplicate core features has high cost and architectural risk; priority is discovery only. | Undecided. Acceptance requires a versioned, executed competitor workflow and Forge comparison with a specific uncovered customer outcome. |
| M-05 | Select or license the applicable SuiteApp/bundle; boundaries differ by subscription and configuration. | Forge APIs and exports exist; actual external entitlement is unknown. | Map entitlement and read-only integration first; no-change if customer has existing capability. | Value depends on verified licensing/total cost; medium research effort; medium integration risk; priority 4. | Undecided. Require exact product edition/bundle, entitlement proof and a working synthetic data reconciliation before proposing connector work. |
| M-06 | ERPNext offers manual batch mapping, which can accurately represent inputs when sequence automation is insufficient. | Forge genealogy and transformation records exist; `V33Tests` covers accounting and remnant trace, but byproduct exact parent source is not proven. | Test actual input attribution; do not claim ERPNext lacks traceability. No-change if Forge accurately records all parents. | Clear correctness value if exact parents are missing; small focused test cost, potentially medium Inventory schema cost; priority 1 due V5 critical invariant, not market evidence. | Proposed V5-S1. Accept only when parent/child identities, multi-parent quantity links, hold isolation and label/reprint cases pass in existing Inventory/Identification owners. |

**Provisional market conclusion:** Current credible evidence supports investigation of integration/configuration burden and narrow workflow guardrails. It does not support an A-class genuine functional gap shared across vendors. Better differentiation hypothesis: one auditable, readable workflow connecting exact eligibility → explicit approved alternate (if any) → partial quantity/remaining operations → committed output identity and genealogy → human-authorized quality state, with measurable time/error outcomes. This remains a hypothesis until direct user interviews and broader, balanced practitioner evidence exist.

### Selected sources

All links accessed 2026-10-16; vendor capabilities can change and must be rechecked before external positioning.

- [Microsoft Learn: finite capacity planning and scheduling](https://learn.microsoft.com/en-us/dynamics365/supply-chain/master-planning/planning-optimization/finite-capacity) — describes capacity-aware planned production scheduling and setup dependencies.
- [Microsoft Learn: register and track batch/serial components](https://learn.microsoft.com/en-us/dynamics365/supply-chain/production-control/tracked-components) — describes component-to-finished-product batch/serial association and prerequisites.
- [Oracle Fusion 26C: track parent and child lots for multiple execution runs](https://docs.oracle.com/en/cloud/saas/readiness/scm/26c/mfg26c/26C-mfg-wn-f44515.htm) — exact child lots and parent batch hierarchy for split runs.
- [IFS Cloud 26R1: Manufacturing Scheduling and Optimization](https://docs.ifs.com/ifsclouddocs/26r1/lang/en/Manufacturing/AboutMSO.htm) — finite scheduling and configurable objectives/resources.
- [SAP S/4HANA: finite-capacity scheduling](https://help.sap.com/docs/SAP_S4HANA_ON-PREMISE/f899ce30af9044299d573ea30b533f1c/d805b3d3eeba4f529fc5c9d7093945d8-892.html) and [routing scheduling](https://help.sap.com/docs/SAP_S4HANA_ON-PREMISE/000598f44016404fa82337e65a45264c4/4484b6535fe6b74ce10000000a174cb4.html).
- [Oracle: product genealogy overview](https://docs.oracle.com/en/cloud/saas/supply-chain-and-manufacturing/26a/faumf/overview-of-tracing-product-genealogy.html); [Microsoft: trace raw materials and finished goods](https://learn.microsoft.com/en-us/dynamics365/supply-chain/inventory/trace-items-raw-materials-inventory-production-sales).
- [Epicor Kinetic Production Management](https://www.epicor.com/en/products/enterprise-resource-planning-erp/epicor-kinetic/production-management/); [Infor industrial manufacturing](https://www.infor.com/industries/industrial-manufacturing).
- [Oracle NetSuite SuiteApps](https://docs.oracle.com/en/cloud/saas/netsuite/ns-online-help/article_3134602194.html); [NetSuite supply planning](https://docs.oracle.com/en/cloud/saas/netsuite/ns-online-help/section_N2294794.html).
- [Acumatica Manufacturing Edition 2025 overview](https://www.acumatica.com/media/2025/09/Manufacturing_Edition-DS-MFG-20250923.pdf); [Acumatica allocation documentation](https://beacon.acumatica.com/r/Manufacturing-Edition-Guide/Producing-Items/Production-Processing-Item-Availability-and-Allocation).
- [Odoo 19 lot/serial manufacturing](https://www.odoo.com/documentation/19.0/applications/inventory_and_mrp/manufacturing/workflows/manufacture_lots_serials.html); [Odoo 18 product tracking](https://www.odoo.com/documentation/18.0/applications/inventory_and_mrp/inventory/product_management/product_tracking.html).
- [ERPNext work orders](https://docs.frappe.io/erpnext/work-order); [batch traceability and manual material mapping](https://docs.frappe.io/erpnext/serial-and-batch-traceability-report).
- [Katana lot/batch support](https://support.katanamrp.com/en/collections/3335396-batch-lot-tracking); [selecting ingredient batches](https://support.katanamrp.com/en/articles/5966901-selecting-ingredients-from-batches-on-manufacturing-orders).
- [MRPeasy product](https://www.mrpeasy.com/); [Gartner Peer Insights: Epicor Kinetic reviews](https://www.gartner.com/reviews/product/epicor-kinetic); [G2: MRPeasy pros and cons](https://www.g2.com/products/mrpeasy/reviews?qs=pros-and-cons).

## 5. Proposed ranked V5 backlog

The first five are ranked by correctness risk and dependency, not by presumed market demand. Cost is relative and requires refinement after owner selection.

| Rank | Candidate | Type / owner | Relative cost and risk | Acceptance proof / no-change option |
| --- | --- | --- | --- | --- |
| 1 | Cross-version child identity, labels and exact parent genealogy proof; close byproduct source-lot gap in Inventory if proven | Correctness dependency; V3 Inventory with V4 Identification integration | Medium; high containment/recall risk if omitted | Test three children from one parent; distinct stable canonical identity and scan; byproduct with one and multiple actual input parents and quantity relationship; remnant edge; hold isolation; reprint is same instance; no duplicate consumption. No-change only if current implementation passes all exact-parent cases. |
| 2 | Read-only one-order-line allocation comparison for existing eligible stock and approved alternates | V5 Planning/UI, consumes Inventory + Engineering + Planning | Medium/high; risk of misleading eligibility or hiding committed-order impact | Exact revision/hold/shelf-life/custody/location/reservation checks; partial quantity; remaining process steps; alternate status/context; rejected-candidate reasons; competing order impact; no reservation/write. No-change if user study shows current screens already answer it clearly. |
| 3 | Supply Intelligence data audit and backtest prototype | V5 Planning research/prototype; read-only | Medium; high risk of spurious forecasts and bad data | Provenance/completeness report; normalize PO lifecycle and MRO; naive/seasonal baselines; held-out error and bias; uncertainty; versioned reproducible results; no PO/BOM/stock mutation. No forecast feature if data cannot support a meaningful baseline. |
| 4 | Fictional aged NCR/hold containment walk-through and source-backed age/propagation checks | Quality-owned V3/V4, V5 orchestration | Low/medium; high risk if a held child or descendant is missed | Discover → identify scope → contain → trace descendants/allocations → authorized disposition → verify execution → CAPA/close; unrelated material remains available. No new MRB engine; no change if current scenario and tests satisfy it. |
| 5 | Role-based manufacturing demo and guided migration/readiness study | V5 UX/integration research; existing UI owners | Medium; adoption risk; no real connector | Time task completion/errors/recovery for planner, PC, operator, quality, purchasing; synthetic demand-to-shipment demo; import validation/duplicate/effectivity report; no connector writes. Keep research only if users do not identify a repeated burden. |

### Predictive design constraints

If rank 3 is selected, begin with an audit of historical MRP snapshots, planned requirements, issued/consumed quantities, scrap, yield, supplier promises versus receipts, cancellations/replacements, stock movements, inventory adjustments and engineering revisions. The initial comparison should include simple “last observed lead time,” trailing median and seasonal-naive baselines where data supports them. Use rolling time-based backtests, show sample size and data coverage, measure error and bias, and include uncertainty bands. Never infer demand from unnormalized manual purchases. Predictions are advisory; only an authorized human can approve a purchase, change a BOM, or release held stock.

### Allocation proposal constraints

If rank 2 is selected, constrain the initial implementation to one demand line and a read-only preview. Reuse V3 eligibility, approved alternate context, existing allocation/reservation and current Planning explanations. Display `required`, `on hand`, `eligible`, `allocated`, `picked`, `delivered`, `issued`, and `consumed` separately; explain selected and rejected candidates, partial coverage, competing orders and remaining validated routing/inspection steps. No autonomous reallocation, deallocation, reservation or PO creation.

## 6. Child identity / genealogy integration test plan

The V5 master’s invariant is cross-version. Test through existing Inventory + Identification + Planning trace services rather than a V5-local model.

1. Start with supplier-traceable `RAW-100`; one committed split yields three physical children with different canonical identities, correct quantities/attributes and one parent edge apiece.
2. Resolve/scan each child’s own barcode to the live child record; each label displays the child identity and informative parent reference; hold one child and confirm only its explicit scope blocks siblings.
3. Reprint a damaged child twice with an actor/reason. Verify unchanged child count, identity and genealogy; preserve copy/print history.
4. Transform an issued input into consumed quantity, loss/scrap, remaining quantity and remnant; enforce the accounting equation and direct remnant parent link.
5. Create a byproduct from one input lot and then from two input lots. Persist the actual source parent identities and input/output quantities/relationships. Verify child → all parents → supplier lot trace; no order-only substitute for genealogy.
6. Attempt a duplicate canonical child label or lot key; reject and retain investigation evidence rather than merging stock.
7. Apply alternate stock only with current Engineering approval and exact context; preserve its own upstream identity and genealogy.
8. Concurrent scans/allocations cannot duplicate consumption/reservation; current holds, wrong revision and unauthorized actors block consequential action.
9. Simulate label-agent/printer failure after committed output. Retrying or reconciling print must not repeat transformation or create inventory.
10. Include split/merge with multiple parents and quantity reconciliation; trace must distinguish recorded causation from mere temporal/order correlation.

## 7. MRB, UX, integration and nonprofit research disposition

- **MRB:** current Quality owns NCR, holds, dispositions, approvals, investigations and CAPA. V5 research should use the fictionalized 2023 “repeated foil breaks during slitting and material sits untagged” acceptance scenario from the master. Identify actual existing page/routes, map state changes and age visibility, then prove hold propagation to child lots/descendants and allocations. No automatic scrap/release or duplicate MRB.
- **Role UX:** reuse current readiness and authorization. A synthetic demo should record task duration, number of screens/scans, first-pass success, confusion, exception recovery and training support for planner, PC/scheduler, operator, Quality and purchasing. Do not convert readiness into unfair operator ranking.
- **Integration/import:** map CSV/Excel item, BOM, routing, revision, effectivity, lot, supplier and open-order prerequisites; report duplicates and unresolved mappings before any commit. Begin ERP connectors read-only and reconcile totals/source ownership. A write path needs separate approval, idempotency, audit and rollback evidence.
- **Humanitarian:** no-go for manufacturing-core changes at this stage. Later, isolated synthetic prototypes can classify shared engine reuse as unchanged/configuration/adapter/major rewrite/not suitable for a Food Bank and a Deployable Hospital Logistics kit workflow. Exclude patient and clinical data.

## 8. Fictional reference-factory demo and measurable outcomes

Use synthetic factory data only. Do not claim the demo works until implemented and executed end to end.

1. Demand enters for a finite mix of finished assemblies. Run deterministic MRP and finite schedule; show shortage pegs and machine/resource constraints.
2. A planner opens one-line allocation comparison. Show released eligible primary stock, partial coverage, rejected stock with reasons, competition impact and a separately approved alternate. Preview does not write.
3. Receive and inspect remaining material; release only accepted quantity. The PC prints a traveler from its controlled source.
4. Operator scans the assigned operation and verified material. Report a transformation into child roll(s), remnant and any byproduct with exact actual parent(s), child quantity/attributes, current hold state and unique label per child.
5. Reprint one label and scan it. Show the same physical child identity and current record; do not create extra stock.
6. Quality holds one child; show only explicit affected stock/descendants blocked and containment sources. Approve and verify a disposition through existing Quality ownership.
7. Move accepted stock, consume/release it as allowed, ship, and reforecast goal risk from actual recorded facts.
8. Introduce a machine breakdown or supplier delay; explain revised schedule/material risk and compare recovery options without silent deallocation.

Measure allocation decision time, incorrect candidate selections, prevented stale/held use, trace completion, print retry correctness, schedule recovery time, operator task completion and import reconciliation exceptions against a documented baseline. Define thresholds only with owner/user input.

## 9. Owner decision and next gate

The V5 master §12–14 required approval for one bounded implementation slice and specifically says a missing V3/V4 invariant should be addressed in its owning domain with an integration test, not by duplicating the subsystem.

**Approved V5-S1 scope:** “Add and run the focused Inventory-owned integration tests for exact parent genealogy across split children, remnants and single-/multi-parent byproducts, including canonical child scan/label identity, reprint and hold isolation. If the tests confirm the byproduct parent-edge gap, authorize only the smallest Inventory-domain model/schema/service correction plus migration/rollback notes and the focused/full tests needed to close those acceptance criteria. Do not begin allocation optimization, forecasting, new MRB work or external integration in this slice.”

The focused tests confirmed the byproduct parent-edge gap. The narrow Inventory change and acceptance tests are complete; no other V5 work was started. Focused PostgreSQL tests passed 7/7 and UI tests passed 212/212. The full `verify.cmd` could not reach build/test because the NuGet service was unavailable; offline restore also lacked two test dependencies. See [`V5-S1-implementation-notes.md`](V5-S1-implementation-notes.md). The next gate is a full verification rerun when restore is available, followed by review of the uncommitted changes.
