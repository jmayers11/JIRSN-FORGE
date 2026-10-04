# ADR 0017 — V2.6: controlled alternates

## Context
V2 §13–20, review scenarios 2, 3 and 9: a bill of materials line names one component, but plants run on
alternates — equivalents, replacements, program- or supplier-specific substitutes, maintenance-only spares.
Using one must be a recorded, approved decision, never a guess, and MRP must be able to say why it
planned an alternate.

## Decisions
1. **Alternate relationships are data** (`engineering.alternate`): primary, alternate, kind (approved
   alternate, equivalent, superseded by, interchangeable, conditional, temporary, maintenance-only,
   supplier-specific, program-specific, engineering approval required), quantity conversion (alternate
   units per primary unit; required when units differ), conditions (assembly, program, customer, asset,
   machine, operation, supplier, dates), quality notes, evidence and an optional engineering change.
   What was proposed never changes (trigger): a different relationship is proposed instead.
2. **Proposed is never usable.** Anyone with `engineering.alternates.propose` (engineering, buyers,
   quality, maintenance planning) records a proposal; a different person approves or rejects it —
   `engineering.alternates.approve` (Engineering Approver, Change Board), or
   `maintenance.alternates.approve` (Maintenance Planner) for maintenance-only alternates. The database
   enforces decider ≠ proposer. Approved relationships can be expired with a reason; expired and
   rejected ones are history only.
3. **Usability is decided by a pure function** (`Alternates.Evaluate` in Engineering contracts, used by
   MRP, issue, candidates): rejected, expired or out-of-date → not offered; proposed → requires approval;
   each stated condition must match the context, and a condition the context cannot answer is unknown —
   never usable. Interchangeable relationships work both ways at the inverse conversion. Maintenance-only
   relationships are never used in production. Candidates are returned as two lists — approved here, and
   requires approval — never mixed.
4. **BOM line substitution policy** (`engineering.bom_line_policy`): Never, Suggest (default) or
   Automatic. Kept apart from the frozen BOM line so approval authority can set it, with a reason, on a
   released revision; every setting is kept.
5. **MRP** substitutes only approved equivalents (equivalent, interchangeable, approved alternate,
   superseded by) whose conditions hold, only on Automatic lines, and only up to the shortage, from the
   alternate's stock not already needed. Each substitution is a peg of its own — shortage, alternate
   available, policy, relationship, quantities and the explanation — and becomes a requirement on the
   alternate. Alternates are netted after the parts they stand in for. On Suggest lines (or for
   non-equivalent kinds) MRP lists the alternate as an exception and plans the purchase.
6. **Issue in place of a part** (`alternateFor` on issues and returns): only through an approved
   relationship usable for that order (its assembly and program); the ledger moves the alternate and
   records the part, relationship and converted quantity, so order material is counted in the primary's
   terms (no over-issue through alternates).
7. **Where-used both ways** and **candidates** endpoints; the alternates register report; program
   segregation hides program-specific relationships from people without the program.
8. Not yet: material readiness offering alternates, alternates in maintenance readiness (V2.9), and the
   as-built record of alternates used (V2.11).
