# ADR 0051: V3.27 reference factories and extensibility

Status: Accepted for V3.27

## Context

The roadmap asks for healthy, constrained and strange-but-valid reference factories at hundreds of records, injected disruptions, answers to BN questions, and one workflow absent from the brief modeled with existing primitives. The Forge folder contains the numbered roadmap and governance review, but not the master brief's BN question catalog.

## Decision

Keep the reproducible large-factory generators in PostgreSQL integration tests. Each factory is built through the ordinary Forge APIs and creates at least 100 orders and 100 traceable inventory lots. Ten orders are released for real schedule/readiness projections. The constrained variant adds an authorized machine production hold. The unusual-but-valid variant includes very small fractional quantities and a large unreleased order without introducing invalid records. All variants must keep the integrity exception projection empty and answer the common V3.26 custody/outside-processor, material outcome/remnant and last-shift question set through Analyst. The test catalog is an explicit proxy and must be reconciled if the original BN list is provided.

Exercise a consigned customer fixture repair and temporary-production-use story through the existing Resources tool lifecycle: preserve its serial, log the customer custodian, use supplier service/return, record the fit check, and return custody to the customer. Do not add another workflow or identity store.

## Consequences and limits

Current tool history preserves one serial across service, use and movement, but does not represent legal owner, loan authorization as a structured relationship, initial receipt directly into customer custody, or a distinct customer-return transaction. The exercise places authorization references in audited reasons and use references; that is traceable text, not a formal customer agreement. A production process must not treat `WithPerson` as ownership or use it as a substitute for customer acceptance. Extend the existing shared identity/custody/evidence model if the workflow is adopted. The generated factories are integration-test fixtures, not a production sample-seed command. No schema or dependency change is made in this increment.
