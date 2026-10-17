# JIRSN FORGE V5 — CONSOLIDATED DEVELOPMENT MASTER
**Version:** V5 master handoff | **Date:** 2026-10-15 | **Status:** Analysis and architecture authorized; production changes require owner approval

## 0. Start here — instructions for Claude, Codex or another coding AI
You are the JIRSN Forge V5 technical lead. Inspect the actual repository, latest V3/V4 specifications, V4 printing appendix, architecture decisions, migrations, tests and current implementation status before making claims. V3 and V4 may be in progress; do not disrupt their branches or rewrite their ownership boundaries. This document combines the original V5 Industry Gap Analysis & Adaptive Operations proposal with predictive supply-chain intelligence, intelligent allocation and operational readiness, plus critical V3/V4 integration acceptance tests. **Start now with repository inventory, evidence gathering, gap assessment, dependency analysis and a prioritized implementation plan. Do not modify production code, database schema or existing workflows until the owner explicitly approves a bounded slice.** If no web access, mark external research pending; never fabricate citations, test outcomes or vendor deficiencies. At the end of each session write a concise Markdown handoff with completed work, evidence, open decisions, exact next action, and tests.

## 1. Product mission and non-negotiable principles
Forge is a manufacturing operations/MRP platform centered on the question: **Can we still achieve the goal, why or why not, and what authorized action changes the outcome?** The system does the complexity; the interface exposes what matters; the human makes the decision. Distinguish behind-plan from goal-at-risk, physical stock from eligible stock, reservation from consumption, and predictions from facts. Favor deterministic domain logic, transparent assumptions, audit evidence and human authorization. Never claim ISO certification or full ERP parity from a feature list.

**Architecture boundaries:** Forge owns manufacturing demand, planning, BOM/routing applicability, inventory, genealogy, execution, quality, purchasing workflows, scheduling, goal scenarios and operational permissions. Nexus authenticates digital identities; Forge authorizes manufacturing actions and qualifications. Sentinel provides defensive cybersecurity analysis. Core, if implemented, owns HR employment records. Do not duplicate ownership or create parallel inventory, label, genealogy, allocation, hold or audit engines.

## 2. Scope and ownership by version
- **V3 foundation:** inventory instances and genealogy; material transformation; locations/custody; allocations; production actuals; controlled alternates; finite/executable capacity; quality holds; procurement and reforecast. Inspect implementation rather than assuming completion.
- **V4 foundation:** ISO 9001-aligned quality controls; barcode scanning; controlled work-order documents and packets; in-context operation-specific label printing; multi-vendor printer adapters; MRB/NCR/CAPA; audit evidence. V4 printing ownership: **PC/Scheduling prints/reissues work orders and controlled travelers**; authorized operators print labels for actual output of assigned operations; Quality controls quality-specific labels and release.
- **V5:** evidenced market gap analysis, adoption/UX improvements, predictive supply intelligence, cross-system allocation optimization, readiness and recovery decision support, optional nonprofit logistics reuse feasibility. V5 must integrate with V3/V4, not silently relocate their authoritative rules.

## 3. Track A — Verified ERP/MRP market gap analysis
Research credible recent practitioner complaints and product documentation for SAP, Oracle, IFS, Epicor Kinetic, Infor, Microsoft Dynamics 365, NetSuite, Acumatica, Odoo, ERPNext, MRPeasy, Katana and other relevant systems. Investigate scheduling, constrained capacity, quality, genealogy, inventory accuracy, supplier coordination, usability, implementation, migration and integrations. Record source URL/date, product/version, claimed issue, real workaround, corroboration and counterexamples. Classify: A genuine functional gap; B feature exists but UX is difficult; C data/configuration/process issue; D integration/licensing issue; E disputed/unknown. For every gap, inspect Forge code/tests and label coverage **proven / implemented unverified / partial / planned / absent / unsuitable**. Recommend no-change, configuration, UX, integration, bounded extension or rejection. Avoid claims that competitors lack features without checking.

Gap register: `GAP-ID | Problem | Source/date | Vendor/version | Class | Evidence/confidence | Workaround | Forge implementation proof | Test evidence | Mitigation | No-change alternative | Customer value | Cost/risk | Priority | Owner decision | Acceptance test`.

## 4. Track B — Predictive supply-chain intelligence
Create a defensible proposal for **Forge Supply Intelligence**, grounded in historical MRP snapshots, planned requirements, actual consumption, scrap, yield, supplier promises/receipts, purchasing records, stock movements and engineering changes. Investigate recurring manual purchases above MRP suggestions, supplier lead-time drift, BOM/yield inaccuracies, safety-stock assumptions, seasonal/intermittent demand and shortages. **Manual purchases are not automatically extra demand**: normalize duplicate/replacement/cancelled POs, timing differences, replenishment and MRO before drawing conclusions. Require provenance, missing-data warnings, backtesting against simple baselines, forecast error and bias, uncertainty, versioning, explainability and human approval. Do not autonomously issue POs, alter BOMs or release held inventory. Connect predictions to V3 goal-risk and scenario engines, with measured outcome-versus-prediction follow-up.

Example: recurring 350 kg additional foil purchases despite an MRP baseline of 1,000 kg. Determine whether consumption, yield, timing, supplier quantity or planner behavior explains the difference before recommending any parameter change.

## 5. Track C — Intelligent material allocation and approved substitutes
**Primary rule:** Allocate eligible inventory already available before planning unnecessary manufacture or purchase, including raw stock, released WIP, semi-finished stock, previously slitted foil, previously machined parts, approved remnants and finished components. Evaluate exact identity, specification/configuration, operation state, quality status, customer/program restrictions, location, quantity, custody, existing reservations and point-of-use readiness. A part that appears dimensionally similar is **not** automatically engineering equivalent.

### 5.1 Allocation lifecycle
Keep `required != physically on hand != eligible != allocated != picked != delivered != issued != consumed`. Support hard/soft reservations, partial coverage, FIFO/FEFO after eligibility checks, concurrent transactions, priority and due-date policies, warehouse picks, reversals and audited reallocation. Never double-reserve the same stock. Hold, expiry, revision mismatch or unapproved substitute blocks eligibility. An allocated order is not necessarily executable until materials are delivered and other constraints are met.

### 5.2 Engineering-approved alternate allocation
Resolve approved alternate relationships under controlled engineering authorization: source item/configuration, alternate item/configuration, customer/program, BOM/routing revision, effectivity dates/lots/serials, permitted operations, approval evidence, mixing restrictions, conversion factors, yield/inspection differences and substitution precedence. Support policies **auto-substitute**, **review before use**, **prohibited**. Only automatically allocate when approval explicitly authorizes automatic use and inventory is otherwise eligible. Keep original BOM requirement, actual allocated/consumed identity, approval version and as-built genealogy. Do not confuse a blanket material equivalence with a specific approved application. When primary is unavailable, distinguish `primary shortage—approved alternate available` from `true material shortage`.

### 5.3 Previously processed material and process-aware netting
If winding requires released foil of a specified width/voltage/attributes, prefer a suitable existing slitted roll instead of generating a new slitting order. If assembly needs 100 machined brackets and 60 released conforming brackets exist, allocate 60 and plan only the remaining 40 plus justified yield/scrap allowances. For partially completed parts, resume only from validated remaining routing operations; do not skip required inspections, heat treatment, coating or customer-specific processing. Support configurable lot/roll splitting and mixing policies. Distinguish an inventory shortage from stock that exists but is not staged.

### 5.4 Explainable allocation decisions
Every proposal must explain: why selected, which alternate approval applies, why candidates were rejected, impact on competing orders, any customer or quality conditions, and what requires human approval. Never silently deallocate committed material to satisfy a higher-priority order. Provide reversible what-if previews before authorized changes.

## 6. CRITICAL CROSS-VERSION RULE — Unique child labels and permanent parent genealogy
**Each physically distinct output inventory instance MUST have its own stable, unique identifier and scannable label; it MUST retain an immutable genealogy link to its actual input parent material(s).** This applies to foil master-roll slitting, cutting, machining, winding, rework, split/merge operations, remnants and other material transformations. It is a V3 inventory/genealogy and V4 label-printing requirement; V5 must verify and demonstrate it, not build a duplicate labeling subsystem.

### 6.1 Canonical example
A raw foil master roll `RAW-100` is slit into three child rolls `SLIT-100-A`, `SLIT-100-B`, `SLIT-100-C`. Each has a **different stable child instance ID**, its own barcode/QR payload, actual width/length/area/weight/attributes, and a genealogy edge to `RAW-100`. Supplier lot, material specification, applicable certificates and transformations remain traceable. One child may be reserved for winding, another held by Quality, another stored as WIP; their states must remain independent. A remnant, if produced, gets its own unique instance ID and genealogy edge. Where multiple inputs contribute to an output, record all parents with quantity/relationship details; do not force a single-parent model.

### 6.2 Label data and scan behavior
The label should visibly include at least child ID, parent raw-material reference (or parent tracking ID), item/material specification, required production attributes, output quantity/unit, and appropriate status where policy demands. The **primary barcode encodes the child identity**, not the parent identity. A second parent barcode is optional and must be explicitly labeled; never allow two different physical rolls to share a child barcode. Scan resolves the live authoritative child record, current location/hold/eligibility and genealogy. Historical label print snapshot and template version are preserved, even if live stock status later changes. The printed parent reference is informative; authoritative genealogy is the recorded transformation graph, not a free-text label field.

### 6.3 Lifecycle and failures
Print only after a committed output transaction creates the child instance; if policy allows labels for pending inspection, label and scan status must clearly indicate the hold and block unauthorized consumption. Print/reprint/retry **never** creates an extra inventory instance or repeats production. Damaged-label reprints use the **same child ID** with controlled print history, reason and actor. Replacement/void procedures must prevent two active conflicting identifiers. Printer outage cannot corrupt the production transaction; queue and recover safely. Enforce scan-back validation, uniqueness constraints and concurrency safety. V4 Universal Print Service should support SATO, Zebra and other models through printer profiles/adapters and local print agents, with no vendor-specific logic in genealogy.

### 6.4 Mandatory genealogy acceptance tests
1. One master roll → three slitted child rolls: three distinct child IDs, three scannable labels, one correct parent relationship each.
2. Reprint one child label twice: still exactly three child inventory instances, with print audit entries.
3. Two labels accidentally present the same child barcode on distinct rolls: detect and block misidentification, investigate rather than silently merge.
4. Hold one child: only that child blocked unless the hold's explicit scope covers parent lot/siblings; trace all affected descendants for containment.
5. One child feeds winding and later finished product: scan finished output → child roll → original master → supplier lot, with quantities.
6. Split remnant and multi-parent transformations: preserve complete genealogy without losing quantities.
7. Printer offline or print agent failure: output transaction remains correct; print job can safely retry.
8. Engineering-approved alternate previously slitted roll: allocate under the correct approval, preserve its own ID and original raw-material parent.
9. Concurrent scanning/allocations: no duplicate stock consumption or conflicting reservations.
10. Wrong revision, held lot or unqualified operator: printing a label never bypasses the block.

## 7. Track D — Operator readiness, usability and measurable performance
Develop role-specific views. PC/Scheduling sees work-order readiness, constrained schedule, allocations, shortages, late risks and controlled packet printing. Operators see only assigned operations, verified material, relevant machine/tooling, scan actions, output reporting and authorized operation labels. Quality sees holds, inspection and MRB. Purchasing sees supplier commitments and validated shortages. Track availability losses caused by waiting for material, machine downtime, tooling, missing approvals or staffing separately from operator-controlled production performance. Explain blockers and route notifications to responsible owners; avoid turning readiness data into unfair productivity judgments. Measure clicks/scans/time, task success, training time, error recovery, and exception handling with actual user tests.

## 8. Track E — MRB and quality containment integration
Audit the **existing** Forge MRB page and its backing services; do not create a second MRB. Verify NCR creation, suspect-material identification, immediate containment, holds propagating into inventory/allocation, affected genealogy, assignment and aging escalation, authorized dispositions (scrap/rework/return/concession), disposition execution verification, CAPA linkage, and immutable evidence. A 2023 master roll repeatedly breaking during slitting, sitting untagged without apparent disposition, is a **fictionalized acceptance scenario**, not production data: discovery → hold → investigation → authority decision → verified action → closure; no silent auto-scrap or release. ISO 9001 requires control of nonconforming outputs, not necessarily a committee named MRB. Do not assert organizational compliance based solely on software.

## 9. Track F — Adoption and integration readiness
Investigate guided CSV/Excel migration, BOM/routing validation, duplicate item detection, revision/effectivity checks, clean setup defaults, industry starter configurations, read-only ERP integration before write-back, idempotent APIs, data ownership and reconciliation, performance, auditability and deployability. Include a manufacturing end-to-end demo: demand → MRP → procurement → receiving/inspection → allocate primary or approved alternate → schedule → PC prints traveler → operator reports transformation → unique child labels/parent genealogy → Quality release → inventory movement → shipment → goal-risk reforecast. Introduce a machine breakdown or supplier delay and demonstrate realistic recovery. Do not claim the demo works until executed and tested.

## 10. Track G — Optional nonprofit/humanitarian reuse feasibility
**Research/prototype only, isolated from manufacturing code.** Test synthetic `Community Food Bank` (donations, expiry, cold storage, volunteer qualifications, kits, distribution) and `Deployable Hospital Logistics` (equipment/consumables, generators, transport, custody, deployment kits and qualified personnel; **no patient or clinical data**). Determine whether shared inventory, BOM/kit, custody, hold, resource and goal engines work unchanged, by configuration, small adapter, major rewrite or not suitable. No assumed real nonprofit requirements, no premature generalized ERP rewrite, and no regression to manufacturing.

## 11. Security, integrity and architecture constraints
Use role- and scope-based authorization with separation of duties and Nexus identity; audit manual overrides and electronic approvals. No unauthorized stock adjustments, quality releases, substitutions or packet reissues. Preserve controlled document revisions, immutable events/evidence, idempotent command handling, optimistic concurrency or equivalent, transactions and rollback/retry strategy, traceable allocation decisions and secure print agents. Require HTTPS/TLS for application and service connections; do not copy insecure legacy-system behavior. Treat any real workplace examples as anonymized synthetic test data. Do not scan, access or test external employer systems without authorization.

## 12. Work plan and implementation gates
**Phase 0 — Repository truth audit (begin immediately):** Identify branch/version, modules, implemented V3/V4 slices, existing allocation, alternate, genealogy, label, MRB, print and forecasting code. Produce evidence with exact paths/tests. Run existing safe tests if feasible; report failures and limitations.

**Phase 1 — Market/architecture assessment:** Deliver gap register, verified competitor reality check, code-to-requirement matrix, current risks, dependency map, ADR candidates and ranked proposals. Separate bug fixes, core correctness gaps, UX improvements and genuinely new V5 functionality.

**Phase 2 — Owner approval:** Request approval for one bounded, dependency-safe slice. If a critical V3/V4 invariant is missing (e.g., unique child IDs/parent genealogy), report it as a blocking dependency for that owning version; propose an integration test and coordinated fix rather than silently duplicating its subsystem in V5.

**Phase 3 — Approved incremental development:** For each slice: acceptance criteria → tests → smallest code/schema change → integration tests → migration/rollback notes → security review → demo → handoff. Never start the next slice merely because a tool session remains available.

**Phase 4 — Validation:** Synthetic end-to-end manufacturing reference factory, constrained factory, strange-but-valid factory, and optional nonprofit feasibility. Compare performance and human effort against baselines where evidence is available.

## 13. Required outputs
1. Current-state repository and implementation audit (paths, APIs, migrations, tests, honest coverage).
2. Market pain-point evidence register with source URLs/dates and counterevidence.
3. Competitor feature reality check (native/configurable/add-on/integration/unknown).
4. V3/V4/V5 dependency and ownership matrix; no duplicated domain engines.
5. Prioritized architecture and gap-remediation proposals with cost, risk and no-change option.
6. Intelligent allocation and engineering-alternate policy/design proposal, including processed WIP and remnants.
7. Child-ID/parent-genealogy/label cross-version test plan and current implementation verdict.
8. Predictive supply intelligence design, data requirements, baselines, backtesting and confidence controls.
9. MRB containment and aging integration audit.
10. Role-based UX/readiness and implementation/migration recommendations.
11. Optional nonprofit reuse feasibility with clear go/no-go.
12. A numbered V5 incremental backlog with dependency-safe acceptance tests and owner approval gates.
13. Fictional manufacturing demo script and measurable outcomes.
14. `V5_SESSION_HANDOFF.md` with work completed, verified facts, decisions needed, tests and next slice.

## 14. First response expected from the coding AI
Return: (a) repository inventory and observed version status; (b) a short list of blockers and high-risk missing invariants; (c) the first five ranked V5 research/development candidates; (d) exact evidence and tests; (e) a proposed first **bounded** slice and the specific owner approval requested. Do not start production implementation without approval. Do not ask for V3/V4 files that are already in the repository; search first.

## 15. Original research brief and continuity
Preserve the intent of `JIRSN_Forge_V5_Industry_Gap_Analysis.md` (October 11, 2026), including the evidence-first vendor research, humanitarian feasibility experiment and realistic demo deliverables. Use the latest V3 plan and V4 quality/printing documents as authoritative for their domains. This consolidated document supersedes earlier scattered V5 handoff prompts only where they conflict; it does not override owner-approved V3/V4 specifications.

---
**START COMMAND:** Inspect the repository and referenced specifications now. Produce Phase 0 and Phase 1 evidence and a proposed first approved slice. Preserve all ongoing V3/V4 work. Stop before production code changes and await owner approval.
