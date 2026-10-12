# ADR 0034 — V3.10: generalized holds

## Context
The V3 brief (AS) asks for holds of every kind:

- quality, material, production, engineering, customer, shipping, procurement, configuration, safety and
  administrative.

Each hold has a scope, a source, evidence, affected records, a required resolution and its own release authority.
Today's stock, NCR and inspection holds become kinds of these. A hold blocks the configured downstream activity, and
the person who placed a hold cannot release it without release authority.

Until V3.10 Forge had three separate mechanisms:

- stock on quality hold (a stock status);
- Quality's NCR and inspection holds, through `IWorkHoldLookup`;
- V3.9's production holds in Execution.

## Decisions
1. **Holds live in the kernel.** Every context must check them before acting, and the kernel is the one thing every
   context already depends on. The kernel parts are:
   - the contracts (`Forge.Kernel.Holds`);
   - the store (`kernel.hold`, `kernel.hold_kind`);
   - the service (`HoldService`);
   - the gate (`HoldGate.RequireNoneAsync`).

   The kernel never knows the other contexts. The host implements `IHoldTargetResolver`, as it does for document
   targets, so a hold can be placed only on a record that exists. A hold on a program's order belongs to that program.
2. **A hold** has:
   - a kind;
   - a target: an order, an operation (`ORDER/SEQ`), a part, a lot (with its part), a work center, a machine, a
     supplier, a purchase order, a shipment or a program;
   - a source, a reason and evidence;
   - the records it affects;
   - the resolution it needs, which defaults per kind.

   It is released once, with a reason. A trigger keeps everything else unchanged.
3. **Each kind blocks configured activities.** The activities are releasing an order, starting work, reporting output,
   issuing material, shipping, purchasing and receiving. `kernel.hold_kind` starts with these defaults:

   | Kind | Blocks |
   | --- | --- |
   | Quality | starting, reporting, issuing, shipping |
   | Material | issuing, starting, shipping |
   | Production | starting, reporting |
   | Engineering | release, starting |
   | Customer | release, shipping |
   | Shipping | shipping |
   | Procurement | purchasing, receiving |
   | Configuration | release, starting, shipping |
   | Safety | starting |
   | Administrative | everything |

   A security administrator changes what a kind blocks, with a reason.
4. **Release authority is per kind.**
   - Quality: `quality.disposition`. Material: `inventory.approve`. Production: `holds.production.release`
     (supervisors, planners).
   - Engineering: `engineering.release`. Customer: `programs.manage`. Shipping: `inventory.shipping.ship`.
   - Procurement: `procurement.approve`. Configuration: `engineering.changes.approve`. Safety: `safety.rules.approve`.
   - Administrative: `kernel.access.manage`.

   The release command checks the hold's kind and demands that permission. The person who placed it can release it
   only when they hold that authority. This replaces V3.9's "always a different person" rule for production holds;
   placing (`holds.place`) is open to the roles that raise holds.
5. **Enforcement.** Each activity names the records it touches and calls the gate. A hold whose kind blocks the
   activity and whose target is among those records refuses it with `on_hold`, naming the hold and what releases it.

   | Activity | Records checked |
   | --- | --- |
   | Order release | the order, its part, its program |
   | Starting work | order, operation, part, work center, machine, program |
   | Reporting good output | order, operation, part, work center, machine, program |
   | Issuing material | order, component, lot, program |
   | Shipping | parts, lots, shipment |
   | PO approval (purchasing) | supplier, PO, parts |
   | PO receipt (receiving) | supplier, PO, part |

   Readiness names holds that block starting work in its Holds dimension.
6. **One register.** `/api/holds` and the `holds` report list kernel holds together with the holds other contexts keep
   by their own rules, each shown as a Quality hold with how it is released:
   - an undecided NCR (released through disposition);
   - an inspection the work has reached but that has not passed (released by passing);
   - stock on quality hold (released by quality release of stock).

   Those mechanisms are unchanged; their rules and tests stand. The register makes them one list with the same
   fields.
7. **Migration.** V3.9's production holds move into `kernel.hold` as Production holds, keeping their ids and history.
   `execution.production_hold` is dropped.

## Consequences
- Execution's V3.9 hold contracts, service and store are removed. The shop floor, order release, Inventory and
  Procurement call the kernel gate.
- The V3.9 tests now place holds through the kernel API. A quality engineer who places a production hold cannot release
  it; a planner can.
- The Analyst maps "What is on hold?" and "Which quality holds are open?" to the `holds` report, with the kind filled
  in.

## Review corrections and verification

- Release resolves the target's program scope before returning an already-released result or changing state. A person with the kind's release permission but without the owning program cannot release or discover that hold. Release events and audit retain the owning program.
- The inspection portion of the register loads released operation details; order-list headers have no operations. Only a reached, unpassed inspection appears as a hold, and passing it clears that derived entry.
- API regression tests cover program access, inspection hold appearance/clearance, and the actual 0008 migration with populated legacy holds, preserved IDs/release history and the next identity value. Existing kernel migration-idempotency tests remain in place.
- Build and quick verification passed: zero warnings/errors, Planning 262 and architecture 32 tests. Selected API and full verification must pass before this increment is declared complete.

Full verification passed: Planning 262, architecture 32, kernel 33, integration 85. Release build: zero warnings/errors. Selected API gate: 14/14. Saved .forge-logs/verify-v310.* and .forge-logs/codex-v310.*.

## Completion audit: assured release and downstream impact

Kernel migration 0007 extends each existing hold-kind policy with fresh-provider-assurance, explicit signature meaning and downstream-propagation settings. A signature-required release also requires fresh assurance. The host supplies validated authentication evidence; the request cannot manufacture subject, issuer or assurance. Release checks current Forge authority, expected hold version and policy, then records an append-only signoff containing signer identity/roles, authority, assurance/methods/time, record and policy fingerprints, action, meaning, result and correlation before releasing in the same audited transaction. Defaults preserve existing release behavior until policy is configured.

Planning derives downstream lot impact from the existing genealogy graph, material allocations, production orders/operations, shipments and goal relationships. The register filters evidence by current access. The shared hold gate and scheduling/readiness inputs use the same configured factual propagation; no copied child holds or parallel genealogy are created. Releasing the original hold clears its derived effect. `GovernanceHoldTests` exercises actual denied assurance/signature paths, immutable signoff, descendant evidence and prohibited execution/planning behavior. Final verification is recorded in the completion checklist and handoff.
