# ADR 0033 — V3.9: executable capacity and presence

## Current checkpoint — 2026-10-13

The resumed full `verify.cmd` gate passed on 2026-10-13 against the frozen 80-file source manifest on HEAD `7092a11`; the subsequent source commit is recorded in AI_HANDOFF.md. Release build: zero warnings/errors. Planning 303/303, Kernel 33/33, Architecture 32/32, restored PostgreSQL Integration 163/163 in 59.8970 minutes including cleanup, and Node 34/34: 565 passing tests. Evidence: `.forge-logs/verify-v3-reconciled.log` and `.forge-logs/verify-v3-reconciled.status`. Current counts and provenance are recorded separately from historical results in the completion checklist and source reconciliation. The decisions and version numbers recorded below describe the historical V3.9 implementation and remain audit history. This checkpoint supersedes the historical order-readiness version 5 statement: the shared order-readiness engine is version 7. V39Tests, V25Tests and OrderReadinessTests cover the actual transactional start and exact recorded old-WIP continuation.

Starting an operation now runs `OperationStartCheck` inside the existing command unit of work before labor or equipment-use records are written. It evaluates the selected operation against the actual selected machine through the shared engine, including order state, current engineering, documentation, predecessor work, quality, material allocation and point of use, requirement profile, equipment, tooling, safety, workforce and training. A different ready machine cannot satisfy the selected-machine check. Any failed or indeterminate dimension rejects the start. The existing Execution credential, qualification and trainer checks and generalized hold authority still apply.

The terminal, selected operator and selected trainer must retain access to the order's program and applicable controlled documents. The command retains real document identities and revision sources for applicability, authorization and training, so restricted documents cannot disappear through redaction. Under the configured training policy, the actual selected operator and trainer must satisfy their controlled-document training requirements; another trained available person does not substitute for them. Start evidence records the engine version and controlled-document source references and markings. Read-only readiness remains a preview; the command performs these checks against its own source snapshot.

Version 7 also shares `WipContinuationFacts` between readiness previews, executable-operation answers, recovery source facts and the direct start check. Superseded, already-started WIP can satisfy the Engineering dimension only through one exact existing, resolved `WipDisposition` action with `CompleteAtOldRevision`. The Contracts-backed action must match the order, part and program, include recorded basis, resolver and resolution time, and belong to a released change whose active item-revision line maps the order's old revision to the action's successor revision. Both revision records and their adjoining effective dates must match that change, and release and resolution must be effective at the evaluation time. Open, unrelated, ambiguous or differently resolved actions do not authorize continuation. This recorded decision permits the old engineering revision only; every other start dimension remains enforced. `V25Tests` adds a real stop/restart refusal before disposition and a successful restart after the normal authorized Quality resolution, preserving the original order revision and frozen BOM.

Material explanations used by readiness and start are projected under current owning-source access after full material allocation. MaterialSourceVisibility masks inaccessible supplying-order/PO descriptions and links while retaining every canonical allocation source key and demand row. Public material evidence removes hidden owning-order rows and preserves visible consumer evidence; original engine fingerprints and stored planning/audit bytes remain unchanged.

## Context
The V3 brief (B, AC, §26) asks that every operation answer "can this happen, and if not, why not?". Readiness already
checked the order, engineering, documentation, predecessors, quality, material, the machine's requirement profile,
equipment, safety staffing and qualified people. Four dimensions were missing:

- tooling;
- material at the point of use;
- holds;
- training on controlled documents, under a policy of off, warn or enforce.

Presence also needed levels: scheduled, clocked in, assigned to a work center, active on an operation. The
acceptance case: the machine is ready, the material is ready and the operator is qualified, but the gauge is
expired. The answer must be "not executable", with the reason. A scheduled employee is never counted as present.

## Decisions
1. **Tooling is a requirement plus validity at the moment.**
   - `resources.tool_requirement` says that an activity (on one machine, or on any machine) needs N tools of a type.
     A machine-specific requirement replaces the general one for the same tool type.
   - Readiness counts the tools of that type that are valid for the machine and activity at that moment, using
     `ToolingCheck.For`, which builds on V3.8's `ToolValidity.At`. When there are too few, the dimension fails and
     names what is wrong with each tool, for example "CAL-1 was due for calibration 2026-09-30".
   - Tooling is no longer listed as "not evaluated", in either order readiness or current-work readiness.
   - Setting a requirement needs `resources.tools.manage`. Removing one needs a reason; the audit keeps it.
2. **Production holds are Execution's own.**
   - A hold in `execution.production_hold` is on an order, an operation, a part, a work center or a machine, and
     carries a reason.
   - Starting work where a hold applies is refused with `on_hold`. An order or part hold also stops good output
     being reported, but not scrap.
   - A hold is released once, with a reason, by a different person from the one who placed it. A trigger and a
     check constraint enforce this.
   - Quality's holds (an undecided NCR, an inspection not passed) stay in the Quality dimension.
   - Readiness evaluates holds for each candidate machine. A hold on Slitter A therefore moves the work to
     Slitter B; a work-center hold stops it.
   - Placing and releasing holds needs `execution.holds.manage`, granted to manufacturing engineers, supervisors,
     planners, quality engineers and quality managers.
3. **Material at the point of use applies only where the plant has a point of use.**
   - The check covers the first operation of an order, while it has not started.
   - What is still to be issued must be released, usable stock at a point-of-use location of the operation's work
     center: Production, Staging or MachineSide, from V3.1.
   - A work center with no point-of-use location issues from stores. For it, the dimension is a fact (Info), not a
     check, so plants that do not stage material see no change.
4. **Training on controlled documents lives with the documents.**
   - The records are in `engineering.document_training` (append-only): a person trained on one revision. A new
     revision needs new training.
   - Engineering owns documents, and Competency cannot depend on Engineering, so the records live in Engineering.
     Recording needs `competency.manage`.
   - One plant policy (`engineering.document_training_policy`) sets Off, Warn or Enforce and the document types it
     covers. The default is Off, for work instructions and procedures. Changing it needs a reason.
   - The documents an operation is worked to are the order's documentation (V2.3), at the revision at release or
     the one that applies.
   - Of the qualified, present candidates, enough to make up the crew must be trained on every one of those
     documents. Under Warn the shortfall is a fact that names who is untrained, and work is not stopped. Under
     Enforce the dimension fails.
5. **Presence has levels, and a schedule is never presence.** `ExecutableChecks.Presence` gives each worker one of:
   - not present;
   - scheduled: on shift by the schedule, with no presence evidence; never counted;
   - clocked in or supervisor confirmed;
   - assigned to a work center;
   - active on an operation: open direct labor.

   Readiness counts only clocked-in or confirmed people, as before. No reason for an absence is shown.
6. **One answer for every operation.**
   - `/api/readiness/operations` and the `executable-operations` report run the same order-readiness engine as the
     order page, for every open operation of released and held orders. Material allocation, points of use and the
     policy are read once.
   - Each operation's answer is "can run now", or its primary constraint and that dimension's detail.
   - The precedence for naming the primary constraint is: Order, Holds, Engineering, Documentation, Predecessor,
     Quality, Material, PointOfUse, RequirementProfile, Equipment, Tooling, Safety, Workforce, Training.
7. **Analyst.** These questions map to the new reports, with the constraint or level filled in:
   - "Which operations can run now?"
   - "Which operations are on hold?" and "Which operations are blocked by tooling?"
   - "Who is present?" and "Who is scheduled but not clocked in?"

## Consequences
- The order-readiness engine is now version 5, and current-work readiness is version 2.
- Readiness reads tools, holds, training and open labor into its snapshot.
- Plants that define no tool requirements, no points of use, no holds and no training policy see the same answers as
  before. The only difference is that Tooling is now evaluated (it passes) instead of listed as not evaluated.
- Production holds are the first kind of the generalized holds that V3.10 adds: kinds, sources, evidence, affected
  records, the resolution required, release authority for each kind, and the activities each kind blocks.
