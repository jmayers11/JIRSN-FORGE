# ADR 0049: V3.25 Horizon lenses and universal questions

Status: Accepted for V3.25

## Context

Forge has a factory horizon, program/contract views, work-center and machine projections, order pages, workforce views, historical schedule comparison and a deterministic Analyst. The risk is that a “past” lens accidentally reads today's approved schedule, or that each lens invents its own counts and access rules.

## Decision

Use one authorized snapshot for Factory, Program, Contract, Work center, Machine, Order and Workforce. `HorizonLensService` selects Planning's approved schedule in force at the requested instant and applies the same explicit program, contract, work-center, machine, order and person filters before computing distinct order and operation totals. Each lens groups those same source operations. A workforce assignment counts only when its recorded segment intersects the requested window. The “What changed?” comparison continues to use the same approved-version history. The Horizon UI exposes windows from -7d through +2w, an editable anchor, common scope filters, counts, evidence and a recorded timeline. Past windows use the approved projection in force at their start; an absent old projection stays unknown. The current proposal remains available as a distinct current planning view.

Contract attribution requires an explicit order commitment, not a program/part inference. Execution migration 0010 adds the current contract/CLIN reference to the existing production order and records each assignment/clear in its existing domain history, event and audit path. The command validates an active Programs contract in the order's program and an existing CLIN for the order's part, with a reason and ordinary order-management authority. Snapshot queries read that commitment history at the same instant as the schedule. Contract-only views state how many operations have no recorded commitment and exclude those operations from contract totals. Other lenses retain the same explicit commitment filter when it is selected.

Route plain-language questions from the global search into the existing Forge Analyst parser and shared engines. The chosen question remains visible and Forge returns its normal interpreted intent, facts, evidence traces and access-filtered result. No new parallel question parser or generative answer path is introduced.

## Consequences

The older projection and assigned crews come from approved schedule history, while contract attribution comes from explicitly recorded commitment history. No parallel schedule, inventory, genealogy or contract system is added. Current directory names may label historical person IDs, while operation crew labels remain those recorded in the plan. Counts are compared at the same scope, instant and interval; an unlinked order is never assigned to a contract implicitly. Universal questions inherit Analyst's supported intents and will state ambiguities or unsupported requests rather than guessing. A future +2w view uses the latest approved version recorded by its selected instant.

New schedule outputs also record each operation's site from the planning snapshot for facility-scoped authorization. Older projections with no recorded site, and shift-scoped reads with no archived shift context, fail closed; today's hierarchy or calendar is never substituted for unknown historical context. Program, work-center and contract context comes from the archived operations and recorded commitment history.

`HorizonLensTests` creates a plan more than seven days ago after independently approving actual machine outages, explicitly links one order to a CLIN, then replans it around a current machine hold. Every lens checks identical counts for the common committed order/machine/person scope and preserves the older machine and projected finish. The same queries run through a custom role with program-scoped schedule, contract and workforce authority, so membership in another program cannot widen the returned operations. A work-center-scoped schedule reader can browse its archived work while workforce access remains separately required. The test also checks program segregation, rejection of cross-program/wrong-part commitments, exclusion of unlinked orders and preservation of historical attribution after a later clear.
