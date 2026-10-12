# JIRSN FORGE V4
## ISO 9001:2026 QUALITY MANAGEMENT, DIGITAL TRACEABILITY & COMPLIANCE ARCHITECTURE

**STATUS:** V4 Master Architecture Specification  
**PROJECT:** JIRSN Forge  
**BASELINE:** ISO 9001:2026  
**MODE:** Architecture Review and Gap Analysis Only  
**IMPLEMENTATION:** Not authorized

---

## 1. Mission

Develop the V4 architecture for JIRSN Forge using ISO 9001:2026 as the primary quality-management baseline.

Forge V4 should support an organization's ability to establish, operate, monitor, audit, maintain and improve a quality management system while integrating quality controls directly into manufacturing operations.

Forge must not be represented as automatically ISO-certified or as guaranteeing an organization's certification.

ISO conformity depends on the organization's processes, implementation, evidence, leadership, personnel, applicable requirements and independent assessment.

The design objective is:

**Capture evidence during normal work. Apply controlled requirements at the point of action. Preserve traceability. Explain compliance gaps. Support continual improvement.**

## 2. Mandatory architecture rules

1. Inspect the existing Forge repository and all available V1–V3 specifications.
2. Reuse V3 inventory, genealogy, production, document control, qualifications, procurement, tooling, authorization, audit and event infrastructure.
3. Do not create a parallel quality database or separate inventory/genealogy system.
4. Use ISO 9001:2026 as the current baseline.
5. Support mappings to ISO 9001:2015 for organizational transition.
6. Verify detailed normative requirements against an authorized copy of the published standard before claiming full clause coverage.
7. Separate actual ISO requirements from optional Forge enhancements, industry-specific requirements and organization-specific policies.
8. Do not implement V4 during this session.
9. Do not modify active V3 work.
10. Produce an architecture review and STOP for Product Owner approval.

---

# PART I — QMS GOVERNANCE

## 3. QMS scope and organizational context — Clause 4

Support a controlled QMS profile containing:

- Organization and facility
- QMS scope
- Products and services
- Applicable processes
- Process owners
- Internal and external issues
- Interested parties
- Relevant requirements
- Applicable statutory/regulatory requirements
- Customer-specific requirements
- Climate-related considerations where relevant
- Justification for nonapplicable requirements
- Approval, revision and review history

Support process mapping with inputs, outputs, responsibilities, dependencies, resources, risks, monitoring methods and performance indicators.

QMS scope must be organization-defined, not inferred by AI.

## 4. Leadership and quality policy — Clause 5

Support:

- Controlled quality policy
- Policy approval
- Policy communication
- Employee acknowledgment where appropriate
- Quality responsibilities
- Process ownership
- Delegated authority
- Quality culture initiatives
- Ethical-behavior policies
- Leadership accountability
- Policy review history

A signed acknowledgment must not be interpreted as proof that an employee understands or follows a policy.

## 5. Risk and opportunity management — Clause 6

Implement a configurable QMS risk/opportunity register.

Record:

- Risk/opportunity ID
- Context
- Related process
- Description
- Potential consequence/benefit
- Evaluation method
- Owner
- Planned action
- Due date
- Status
- Evidence
- Review
- Effectiveness evaluation

Distinguish risks from opportunities.

Do not automatically impose FMEA, numerical risk scores or risk matrices on every organization.

Support configurable methods.

Integrate operational risks with Forge constraints and Goal Attainment where appropriate.

## 6. Quality objectives

Support measurable quality objectives.

Examples:

- On-time delivery
- First-pass yield
- Supplier acceptance
- Scrap reduction
- Corrective-action closure
- Customer complaints
- Inspection performance
- Training completion

Each objective should have:

- Owner
- Metric
- Target
- Measurement method
- Data source
- Review interval
- Planned actions
- Resources
- Due date
- Actual performance
- Evidence

Where possible, derive metrics from authoritative Forge transactions rather than manual reporting.

## 7. Controlled QMS change management

Support planned changes to the QMS.

Record:

- Proposed change
- Reason
- Intended result
- Affected processes
- Risks and opportunities
- Resource implications
- Responsibility changes
- Required approvals
- Implementation
- Verification
- Effectiveness review

Reuse V3 change/workflow/evidence infrastructure.

---

# PART II — RESOURCES, COMPETENCE AND DOCUMENT CONTROL

## 8. Competence and training — Clause 7

Integrate with Forge's existing workforce qualification engine.

Support:

- Required competence
- Qualification criteria
- Training assignments
- Training completion
- Assessment results
- Demonstrated competence
- Trainer authorization
- Qualification expiration
- Retraining
- Effectiveness evaluation
- Evidence

Training attendance must not automatically establish competence.

An authenticated employee must not automatically be treated as qualified.

## 9. Calibration and measurement resources

Extend V3 tooling/calibration controls.

Support:

- Measurement resource identity
- Calibration requirement
- Calibration schedule
- Calibration status
- Calibration certificates
- Traceability of measurement where applicable
- Verification
- Equipment protection
- Out-of-tolerance findings
- Affected product evaluation
- Historical equipment usage

**Critical requirement:**

If a gauge is discovered out of tolerance, Forge should help Quality identify potentially affected historical inspections and products.

Forge must not automatically declare every affected product defective.

Human Quality authority determines disposition.

## 10. Controlled documented information

Reuse V3 document control.

Support:

- Creation
- Identification
- Description
- Format
- Review
- Approval
- Release
- Revision
- Effectivity
- Distribution
- Access
- Retrieval
- Storage
- Protection
- Retention
- Disposition
- Obsolete document control
- External controlled documents

Ensure operators receive the applicable approved work instruction.

Do not assume the newest document revision is always applicable.

## 11. Record retention

Support configurable retention policies.

Potential fields:

- Record category
- Retention requirement
- Effective date
- Retention trigger
- Retention duration
- Owner
- Applicable contract/customer requirement
- Legal hold
- Disposition authority
- Destruction evidence

Prevent unauthorized deletion of records subject to retention or legal hold.

Do not invent a universal ISO 9001 retention period.

---

# PART III — BARCODE IDENTIFICATION & TRACEABILITY

## 12. Unified identification engine

Develop an architecture for unique identification of:

- Personnel
- Work Orders
- Routing Operations
- Production Travelers
- Material Lots
- Batches
- Rolls
- Serial Numbers
- WIP
- Remnants
- Machines
- Work Centers
- Tools
- Gauges
- Warehouse Locations
- Containers
- Packages
- Inspection Records
- Shipments

Reuse authoritative V3 entity identifiers.

Do not use barcode values as a second independent source of identity.

## 13. Barcode standards

Support an extensible scanning abstraction.

Potential symbologies:

- Code 128
- QR Code
- Data Matrix
- Other configured standards

Support:

- USB keyboard-wedge scanners
- Handheld scanners
- Mobile camera scanning where appropriate
- Future industrial scanners

Do not hardcode a scanner manufacturer.

Use namespaced identifiers to avoid collisions between entity types.

## 14. Label lifecycle

Support:

- Label generation
- Printing
- Reprinting
- Replacement
- Voiding
- Label version
- Print timestamp
- Printing user
- Printer/device where available
- Reason for reprint
- Audit

A reprinted label must not create a new physical inventory instance.

A replaced physical label must not erase the original material history.

## 15. Scan-to-execute workflow

Preferred workflow:

EMPLOYEE IDENTIFICATION  
→ WORK ORDER SCAN  
→ OPERATION SCAN  
→ MATERIAL SCAN  
→ MACHINE/WORK CENTER VALIDATION  
→ TOOLING VALIDATION WHERE REQUIRED  
→ READINESS CHECK  
→ EXECUTION  
→ ACTUAL QUANTITY  
→ OUTPUT IDENTIFICATION  
→ QUALITY REQUIREMENTS  
→ COMPLETION / SIGNOFF

The exact required scans must be configurable by operation and process risk.

Avoid forcing unnecessary scans.

## 16. Personnel identification and Nexus

Nexus remains the identity/authentication authority.

Forge remains the manufacturing authorization and qualification authority.

A barcode badge may identify a person or initiate authentication, but a printed barcode alone must not automatically establish secure authentication.

For consequential transactions, require configured authentication assurance.

Support shared-terminal user switching.

Prevent production transactions from being attributed to the previous operator.

## 17. Scan validation

Every scan must validate applicable business context.

Examples:

- Correct work order
- Correct operation
- Correct material
- Correct revision
- Correct lot
- Correct location
- Material not prohibited by hold
- Quantity available
- Operator qualified
- Machine eligible
- Tool calibration valid
- Required predecessor complete
- Required inspection complete

Invalid scans must produce understandable explanations.

Example:

"Roll R-104 cannot be issued to WO-2048 because its configuration does not match the approved material requirement."

Do not silently override the conflict.

## 18. Scan event model

Capture meaningful scan-driven transactions with:

- Event ID
- Entity
- Entity identifier
- Actor
- Authentication context where required
- Device/terminal where available
- Timestamp
- Work order
- Operation
- Location
- Transaction type
- Validation result
- Resulting authoritative business transaction
- Correlation ID
- Audit

Distinguish raw scan attempts from accepted business transactions.

A scan does not automatically prove physical movement, consumption, inspection or production.

## 19. Material genealogy

Reuse the V3 genealogy graph.

Forge should answer:

- Which supplier lot supplied this material?
- Which work orders consumed it?
- Which operations transformed it?
- Which operators recorded those operations?
- Which machines and tools were used?
- Which child lots were produced?
- Which inspections were completed?
- Which shipments contained the resulting products?

Support forward and backward traceability.

Preserve historical configuration.

## 20. Traceability investigation and containment

Create a controlled traceability investigation workflow.

Example:

A supplier reports that Lot S-104 may be nonconforming.

Forge identifies:

- Remaining raw inventory
- WIP
- Child material
- Finished goods
- Allocated inventory
- Staged shipments
- Shipped products
- Affected customers/orders where traceable

Quality can evaluate containment.

Forge may propose affected material based on genealogy but must not automatically assume defect or initiate unauthorized customer communications.

---

# PART IV — OPERATIONAL QUALITY CONTROL

## 21. Customer requirements — Clause 8

Support controlled review of customer requirements.

Include:

- Product requirements
- Contract requirements
- Delivery requirements
- Quality requirements
- Regulatory requirements
- Configuration
- Special characteristics where applicable
- Acceptance criteria
- Changes and approvals

Link accepted requirements to planning, production, inspection and shipment.

## 22. Design and development controls

Where an organization performs design/development, support or integrate with controlled:

- Design planning
- Inputs
- Reviews
- Verification
- Validation
- Outputs
- Changes
- Approvals
- Evidence

Do not assume every manufacturing organization performs product design.

Allow documented nonapplicability where justified.

Reuse engineering/configuration workflows instead of creating a competing design-control architecture.

## 23. Supplier and external-provider control

Extend V3 Procurement/Quality.

Support:

- Supplier evaluation
- Approval
- Selection criteria
- Monitoring
- Reevaluation
- Supplier performance
- Supplier nonconformity
- Corrective-action requests
- Approved supplier status
- Scope of supplier approval
- Purchasing requirements
- External processing

Supplier approval must not be represented by an unexplained checkbox alone.

## 24. Production process control

Support configurable controlled production conditions.

Potential requirements:

- Approved instructions
- Applicable revision
- Qualified personnel
- Correct materials
- Approved tooling
- Valid measuring resources
- Process parameters
- Setup verification
- Inspection checkpoints
- Environmental conditions where relevant
- Product identification
- Preservation requirements

Reuse V3 Production Readiness and Executable Capacity.

## 25. Inspection and acceptance

Support inspection plans and records.

Fields may include:

- Inspection ID
- Item
- Configuration
- Work order
- Operation
- Lot/serial
- Characteristic
- Acceptance criteria
- Measurement
- Unit
- Result
- Equipment used
- Inspector
- Timestamp
- Evidence
- Disposition
- Signoff where required

Inspection plans must be configurable.

## 26. Product release

Product release must be controlled.

Forge should evaluate whether required acceptance arrangements have been completed.

Support:

- Acceptance evidence
- Release authority
- Release timestamp
- Applicable requirements
- Exceptions
- Approved concessions where permitted
- Traceability to approving person

Produced does not mean accepted.

Accepted does not automatically mean released.

Released does not automatically mean shipped.

## 27. Nonconforming outputs

Extend V3 generalized hold and NCR engines.

Support:

- Identification
- Containment
- Segregation
- Disposition
- Rework
- Repair
- Authorized concession
- Supplier return
- Scrap
- Reinspection
- Release approval
- Evidence

Do not permit unauthorized personnel to release nonconforming material.

Do not erase the original nonconformance history.

## 28. Preservation and customer property

Support relevant controls for:

- Handling
- Storage
- Packaging
- Contamination prevention
- Shelf life
- Environmental conditions
- Transportation
- Customer-owned property
- Supplier-consigned property where applicable

**Ownership, custody, location, availability and quality status must remain separate.**

## 29. Post-delivery activities

Support or integrate with:

- Customer returns
- Complaints
- Warranty issues where applicable
- Field failures
- Product investigations
- Recall/containment actions where applicable
- Corrective actions

Connect returned products to their original shipment and genealogy.

---

# PART V — PERFORMANCE, AUDITS AND IMPROVEMENT

## 30. Quality performance dashboard — Clause 9

Develop a QMS dashboard using authoritative data.

Potential indicators:

- First-pass yield
- Scrap rate
- Rework rate
- Supplier rejection rate
- On-time delivery
- Open NCRs
- Overdue corrective actions
- Inspection backlog
- Calibration compliance
- Training/qualification gaps
- Customer complaints
- Audit findings

Distinguish calculated metrics from manually entered assessments.

Every calculated KPI should have a defined numerator, denominator, scope, time window and data source.

## 31. Customer satisfaction

Support configurable customer-feedback processes.

Examples:

- Complaint records
- Customer scorecards
- Delivery performance
- Product acceptance
- Customer surveys
- Supplier-rating feedback received from customers

Customer satisfaction must not be inferred solely from on-time delivery.

## 32. Internal audit management

Implement a configurable QMS audit-management capability.

Support:

- Audit program
- Audit scope
- Audit criteria
- Audit schedule
- Auditor
- Auditor competence
- Impartiality considerations
- Checklist
- Objective evidence
- Findings
- Nonconformities
- Observations
- Opportunities for improvement
- Report
- Corrective-action linkage
- Follow-up
- Closure

Do not require a single universal checklist.

Audit criteria may include ISO clauses, company procedures, customer requirements and regulatory requirements.

## 33. ISO clause-to-evidence matrix

Create an extensible compliance mapping architecture.

Potential fields:

- Standard
- Edition
- Clause
- Requirement reference
- Applicability
- Process owner
- Implementing procedure
- Forge capability
- Required evidence type
- Linked evidence
- Last evaluation
- Assessment result
- Gap
- Corrective action
- Auditor notes

Do not equate a populated evidence link with confirmed conformity.

A qualified human auditor must assess whether the evidence is adequate.

## 34. Management review

Support controlled management reviews.

Potential inputs:

- Prior action status
- Context changes
- Customer feedback
- Quality objective performance
- Process performance
- Product conformity
- NCR/CAPA performance
- Audit results
- Supplier performance
- Resource adequacy
- Risk/opportunity actions
- Improvement opportunities

Potential outputs:

- Decisions
- Actions
- Improvement needs
- QMS changes
- Resource needs
- Assigned owners
- Due dates
- Follow-up evidence

The organization determines its review process and frequency.

## 35. Corrective action — Clause 10

Support:

- Problem statement
- Immediate correction
- Containment
- Investigation
- Root-cause analysis
- Related nonconformities
- Corrective-action plan
- Owner
- Due date
- Implementation evidence
- Effectiveness verification
- Closure approval
- Recurrence monitoring

**Correction is not the same as corrective action.**

Example:

Replacing a defective roll may correct an immediate problem.

Corrective action addresses the cause of the problem to prevent recurrence.

Do not fabricate root causes.

## 36. Continual improvement

Support an improvement register and controlled improvement workflow.

Link improvements to:

- Audits
- Corrective actions
- Quality objectives
- Customer feedback
- Operational performance
- Employee suggestions
- Risk/opportunity assessments
- Management review

Record measurable before/after results where practical.

---

# PART VI — INTELLIGENCE, NOTIFICATIONS AND SECURITY

## 37. Forge Quality Analyst

Extend Forge Native Intelligence.

Potential questions:

"Show all open Quality holds."

"Which supplier lots were used in this shipment?"

"Who inspected this lot?"

"Was the inspection gauge calibrated at the time?"

"Which work orders used this nonconforming material?"

"What products may be affected?"

"Which corrective actions are overdue?"

"Which audits have open findings?"

"What evidence supports clause 8.5.2?"

"Which quality objectives are below target?"

"What changed since the last management review?"

"Which qualifications expire next month?"

"Which shipments lack required release evidence?"

Forge must answer from authoritative records.

AI explanations are not independent proof of conformity.

## 38. Notifications

Reuse the V3 Notification & Communications Engine.

Potential notifications:

- Qualification expiring
- Calibration due
- Calibration failed
- Document revision released
- Required inspection pending
- Quality hold applied
- Quality hold released
- NCR assigned
- Corrective action overdue
- Audit scheduled
- Audit finding assigned
- Supplier performance concern
- Customer complaint received
- Management review action due
- Traceability investigation opened
- Product release blocked

Respect permissions and data classification.

## 39. Security and record integrity

Use Nexus for identity/authentication.

Use Forge for operational authorization.

Require appropriate controls for:

- Role permissions
- Contextual authorization
- Segregation of duties
- Electronic signoff
- Record integrity
- Audit history
- Retention
- Backup/recovery
- Data export
- Access to sensitive records

Do not permit administrative convenience to bypass controlled Quality authority.

## 40. Evidence integrity

Evidence records should support:

- Unique identity
- Source
- Associated business record
- Author/creator
- Creation time
- Revision/version
- Integrity verification where appropriate
- Access control
- Retention
- Change history
- Approval/signoff where required

Do not claim a record is immutable if the underlying architecture cannot guarantee it.

Corrections must preserve appropriate history.

---

# PART VII — VALIDATION SCENARIOS

## 41. Required scenarios

**Scenario A — Wrong revision**

Operator scans work order and material.

The applicable controlled instruction differs from the instruction presented.

Forge prevents prohibited execution and identifies the revision conflict.

**Scenario B — Unqualified operator**

Operator authenticates through Nexus.

Forge determines required qualification is expired.

Operation cannot start.

**Scenario C — Defective supplier lot**

Supplier reports potentially defective raw material.

Forge traces affected WIP, finished goods and shipments.

Quality determines containment.

**Scenario D — Failed calibration**

Gauge fails calibration.

Forge identifies historical inspections during the potentially affected interval.

Quality reviews impact.

**Scenario E — Duplicate barcode**

A label is accidentally printed twice.

Forge detects duplicate use or conflicting physical state where supported.

No duplicate inventory is silently created.

**Scenario F — Nonconforming output**

Production reports 1,000 units.

80 are placed on hold.

Forge does not permit the held quantity to be treated as released inventory.

**Scenario G — Audit evidence**

Auditor requests evidence for an applicable production-control requirement.

Forge locates controlled procedures, relevant transactions, qualifications, inspection results and approvals.

Human auditor assesses adequacy.

**Scenario H — Corrective action**

Repeated material discrepancies generate a nonconformity investigation.

The organization determines the cause, implements corrective action and verifies effectiveness.

**Scenario I — Customer complaint**

Customer reports a defective shipped lot.

Forge links complaint to shipment, production history, material genealogy, inspection and any corrective action.

**Scenario J — Management review**

Management reviews QMS performance.

Forge assembles available metrics, audit findings, supplier performance, customer feedback and outstanding actions.

Decisions and assigned actions are recorded.

**Scenario K — QMS revision transition**

Organization maps existing ISO 9001:2015 procedures and evidence to ISO 9001:2026.

Forge identifies mappings requiring review without declaring automatic compliance.

**Scenario L — Shared terminal**

Two operators use the same terminal.

Forge prevents accidental attribution of Operator B's work to Operator A.

---

# PART VIII — ARCHITECTURE REVIEW DELIVERABLES

## 42. Required deliverables

Produce:

1. V4 Executive Architecture Summary
2. ISO 9001:2026 Clause Coverage Matrix
3. ISO 9001:2015-to-2026 Transition Mapping
4. V3 Capability Reuse Assessment
5. Missing QMS Capability Assessment
6. Barcode/Identification Architecture
7. Scan-to-Execute Workflow Design
8. Quality Domain Model Proposal
9. Audit and Evidence Model Proposal
10. QMS Governance Model Proposal
11. Authorization and Nexus Integration Review
12. Event and Notification Impact Review
13. Database/Migration Impact Review
14. API Impact Review
15. UI/UX Impact Review
16. Security and Record Integrity Review
17. Reporting/Quality Dashboard Proposal
18. Testing and Validation Plan
19. Implementation Dependency Map
20. Product Owner Decision Register

For each ISO-related requirement, identify:

- Standard edition and clause
- Requirement interpretation
- Whether the requirement is conditional/applicable
- Proposed Forge capability
- Existing V3 capability to reuse
- New V4 work required
- Evidence produced
- Responsible human role
- Validation approach
- Open questions
- Whether it is mandatory, conditional, organizational policy or optional enhancement

Do not invent exact normative requirements when the official standard is unavailable.

Mark those entries as requiring verification against licensed source text.

## 43. Proposed implementation sequence

Evaluate this initial sequence:

**V4.1 — Identification & Scanning Foundation**

Unique identifiers, barcode labels, scan validation, personnel/work-order/material identification.

**V4.2 — Quality Execution & Traceability**

Inspection checkpoints, release evidence, scan-to-execute, genealogy investigations.

**V4.3 — QMS Governance**

QMS scope, process mapping, policies, quality objectives, risks/opportunities.

**V4.4 — Supplier & Customer Quality**

Supplier evaluation, complaints, customer returns, external-provider quality.

**V4.5 — Audit & Corrective Action**

Internal audits, findings, CAPA, effectiveness verification.

**V4.6 — Management Review & Compliance Evidence**

Clause mapping, evidence retrieval, management review, QMS dashboards.

Adjust sequence based on actual V3 implementation dependencies.

## 44. Architectural challenge

Demonstrate that the architecture can support:

- A small ISO 9001-certified machine shop
- A high-volume capacitor manufacturer
- A build-to-print aerospace supplier
- A manufacturer with outsourced special processes

without requiring four separate QMS engines or four separate traceability systems.

Company-specific and industry-specific requirements must remain configurable.

Do not falsely claim that ISO 9001 alone satisfies AS9100, IATF 16949, medical-device or other sector-specific requirements.

## 45. Non-negotiable principles

- ISO alignment is not certification.
- Barcode scanning is not automatically required by ISO 9001.
- Scan identity is not authentication.
- Authentication is not authorization.
- Authorization is not qualification.
- Recorded production is not automatically accepted production.
- Accepted production is not automatically released production.
- A document attachment is not proof of compliance by itself.
- A completed checklist is not proof of process effectiveness.
- An audit finding is not automatically a product defect.
- Correction is not corrective action.
- Root cause must be supported by evidence.
- Calibration validity must be evaluated at the relevant time.
- Historical records must retain their historical configuration.
- QMS controls must be proportional to applicable requirements and risk.
- Quality decisions remain with authorized humans.
- V4 must reuse V3 authoritative records.
- The system should reduce unnecessary manual entry.
- The system must explain why an action is blocked.
- The system must preserve evidence for consequential actions.
- No AI-generated compliance conclusion may substitute for an authorized audit.

## 46. Final instruction

Inspect the repository and existing V3 architecture.

Review this V4 specification against the published ISO 9001:2026 information available to you.

Identify what is already supported, what must be extended, what is genuinely missing and what requires a licensed-standard verification.

Challenge unnecessary complexity.

Produce the complete V4 Architecture Impact Review.

**DO NOT IMPLEMENT V4.**

**DO NOT INTERRUPT OR MODIFY ACTIVE V3 DEVELOPMENT.**

**STOP AND WAIT FOR PRODUCT OWNER APPROVAL.**

---

**JIRSN FORGE V4 PRINCIPLE**

IDENTIFY THE WORK.  
VERIFY THE PERSON.  
CONTROL THE PROCESS.  
TRACE THE MATERIAL.  
PRESERVE THE EVIDENCE.  
MEASURE THE PERFORMANCE.  
IMPROVE THE SYSTEM.

**THE SYSTEM DOES THE COMPLEXITY. THE INTERFACE EXPOSES WHAT MATTERS. THE HUMAN MAKES THE DECISION.**