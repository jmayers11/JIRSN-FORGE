# ADR 0051: V3.27 reference factories and extensibility

Status: Accepted for V3.27

## Context

The roadmap asks for healthy, constrained and strange-but-valid reference factories at hundreds of records, injected disruptions, answers to BN questions, and one workflow absent from the brief modeled with existing primitives. The Forge folder contains the numbered roadmap and governance review, but not the master brief's BN question catalog.

## Decision

Keep the reproducible large-factory generators in PostgreSQL integration tests. Each factory is built through the ordinary Forge APIs and creates at least 100 orders and 100 traceable inventory lots. Ten orders are released for real readiness projections. A further released order records 100 units issued, 80 consumed, 5 scrap and a 15-unit reusable remnant, plus an actual shop-floor report of 80 good and 5 scrap. An approved service purchase and dispatch leave 5 identified units in outside-processor custody. The host's test clock advances after that recorded full-day shift so the last-shift question resolves to the completed production window. Analyst checks assert these exact quantities, identities, custody, reconciliation status and source references as well as the report interpretation. Each answer has a 10-second budget at factory scale.

The constrained variant adds an authorized machine production hold and proves that readiness switches the affected work from SLITTER-A to eligible SLITTER-B while retaining executable status. The unusual-but-valid variant explicitly asserts both very small fractional orders and a million-unit unreleased order without introducing invalid records. All variants must keep the integrity exception projection empty and answer the common V3.26 custody/outside-processor, material outcome/remnant and last-shift question set through Analyst. The test catalog is an explicit proxy and must be reconciled if the original BN list is provided.

Exercise a consigned customer fixture repair and temporary-production-use story through the existing Resources tool lifecycle: preserve its serial, log the customer custodian, use supplier service/return, record the fit check, and return custody to the customer. Do not add another workflow or identity store.

## Consequences and limits

Current tool history preserves one serial across service, use and movement, but does not represent legal owner, loan authorization as a structured relationship, initial receipt directly into customer custody, or a distinct customer-return transaction. The exercise places authorization references in audited reasons and use references; that is traceable text, not a formal customer agreement. A production process must not treat `WithPerson` as ownership or use it as a substitute for customer acceptance. Extend the existing shared identity/custody/evidence model if the workflow is adopted. The generated factories are integration-test fixtures, not a production sample-seed command. No schema or dependency change is made in this increment.
