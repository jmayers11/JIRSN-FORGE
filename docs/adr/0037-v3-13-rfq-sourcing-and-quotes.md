# ADR 0037 — V3.13 RFQ, sourcing and quotes

Status: accepted after V3.13 full verification; contextual completion-audit corrections recorded below.

The existing V2.7 requisition is the authoritative sourcing need for production, tooling, engineering and every other supported demand source. RFQ refers to it rather than creating a duplicate need queue. Source selection authorizes the supplier response before an existing requisition becomes a PO line. This adapts the brief's conceptual sequence to the already working request model while preserving one source of truth.

RFQ release uses the existing applicability engine, honoring the need's required item revision, program/customer and required-by date. It snapshots the applicable document revisions and immutable file hashes actually sent, plus documents attached to follow. Later document releases never rewrite that snapshot. PO documentation also honors the requisition's required configuration.

Supplier quotes retain structured quantity, unit price, currency, promised date, validity and alternate proposal, with the original response in the existing reference evidence system. Quotes are immutable; a revised response is another record. Comparison presents recorded terms without treating different currencies as equivalent or choosing for the user. A quote is never supply. An alternate proposal is not engineering approval; the need/configuration must be resolved before that alternative can be purchased.

RFQ release and source selection use existing purchasing approval authority, reasons and a second person. V3.15 replaces fixed role mappings/policies with configurable authority while preserving these defaults. When a requisition has an active RFQ, PO creation requires selected supplier, quantity coverage and unexpired response. Ordinary requisitions without RFQs keep their existing path.

## Architecture impact

| Aspect | Decision |
| --- | --- |
| Existing capability | Requisitions/demand sources, supplier approval, PO workflow, applicability, file evidence |
| Reusable architecture | Existing need and PO records, applicability engine, attachments, command pipeline and outbox |
| Original gap | Earlier procurement had no RFQ or quoted terms |
| Implemented capability | RFQ lifecycle, released package, immutable quotes, comparison and source decision |
| Owner | Procurement owns commercial decisions; Engineering owns configuration and approved alternatives |
| Entities | RFQ refers to requisition; SupplierQuote refers to RFQ and original reference revision |
| Database | Procurement 0005 sourcing tables/history guards; Engineering 0013 additional attachment targets |
| Services | SourcingService; host package resolver reuses DocumentationService |
| API | RFQ list/create/release/cancel; quotes list/record; source select |
| Events | SourcingChanged through the same transactional outbox |
| UI | Need-to-RFQ, original response upload, comparison and reasoned source selection |
| Authorization | Purchasing read/manage/approve plus need program scope and original response markings |
| Audit | Reasons and prior/current RFQ state, immutable response and source-selection identity |
| Integration | Existing technical packages, requisitions, PO commands and shared evidence |
| Failure | Missing/hidden need denied; stale/expired response and unapproved proposed alternative blocked |
| Performance | Indexed RFQ/quote references; V3.28 covers selected reference-factory Analyst budgets |
| Migration | Additive schemas; old requisitions and PO paths preserved; migration counts must advance |
| Tests | Applicable-not-latest package, quote-not-supply, proposal-not-approval, denied/program/audit/retry and selected-source PO; contextual real-flow regression |
| Dependencies | V3.12 evidence; V2.3 applicability; V2.7 requisitions |
| Product decisions | Reuse existing requisition as sourcing need; comparison is factual, and human selects |

V3.13's original full verification passed Planning 266, Architecture 32, Kernel 33 and Integration 94; source commit `20bfd9f`. The final completion-audit gate is recorded in the current checklist and handoff.

## Contextual sourcing and purchase review

RFQ creation and quote recording resolve the actual requisition program before the shared command pipeline and recheck that context inside the write transaction. PO creation requires every actual requisition context; adding a line also requires its prospective context. Independent program membership does not widen a program-scoped action grant. Mixed or unknown sources cannot borrow program-A authority. Source reads filter RFQs, quotes, requisitions, POs, frozen packages and purchase explanations against current actual source contexts; program revocation immediately hides records and denies mutations. Shared supplier/item catalogs retain their existing catalog authority. Existing command audit, outbox and idempotency paths remain authoritative. `ScopedSourcingFlowTests` exercises the real program-A flow and denied B/mixed/revoked paths; final verification outcomes belong in the checklist and handoff.
