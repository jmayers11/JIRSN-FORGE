# ADR 0016 — V2.5: release propagation and verification

## Context
V2 §6 and §9, review scenario 1: once a change is released, everything it reaches must follow it — or a
person must decide why not — and Forge must be able to prove that it did. Completed work and what was
already sent to suppliers are evidence and never change.

## Decisions
1. **Propagation is a subscriber** to `EngineeringChangeReleased` (`ChangePropagationSubscriber`). Release
   writes the propagation record (`engineering.change_propagation`, status Running) in its own
   transaction, so a released change always shows its propagation state.
2. **One step per record** (`engineering.propagation_step`), planned once (never re-planned differently)
   and done once (frozen by trigger):
   - future orders (Planned, Firm planned) at another revision of a changed item → **moved** to the new
     revision;
   - released orders with nothing started (no operation started, no material issued) → **moved** — their
     released copy is re-issued from the new revision's routing and bill of materials — and their
     **documentation reissued** with the revisions in force;
   - work in progress (an operation started, material issued, or held) → a **disposition action**; the
     order is never edited;
   - completed orders → **checked unchanged**;
   - open lines of approved purchase orders for parts the change reaches → a **purchase review action**;
     the technical package already sent is never changed;
   - people whose home work center uses a changed work instruction or procedure → a **training
     acknowledgment action**.
3. **A named system actor.** Every change propagation makes is a normal command run as
   `system:propagation` (`CommandContext.SystemActor`, role `system.ChangePropagation`, which no person
   can be granted) under the release's correlation id, so the audit answers "who moved order 48172 to
   rev G": propagation of ECN-0042, released by …. Idempotency keys are per change and step, so a
   redelivered event or a retry never acts twice.
4. **Failures are visible, not lost.** A failed step keeps its error and attempt count; the change shows
   "propagation incomplete". Steps that need the change in force wait for its effective date. Incomplete
   propagations are retried every five minutes and on demand (`/propagation/retry`, release authority).
   The subscriber itself always advances, so one bad record never blocks later changes.
5. **Verification record**: expected, done, waiting and failed per kind of step, actions raised and still
   open, and a summary; Complete only when every step is done.
6. **Actions** (`engineering.change_action`) are raised once per change, kind and target and decided once:
   WIP dispositions by quality (`quality.disposition`: complete at the old revision with a deviation,
   rework, scrap, or re-plan — basis required), purchase reviews by a buyer (`procurement.manage`: no
   impact, supplier notified, line amended or cancelled — reference required unless no impact), training
   acknowledgments recorded by a training coordinator (`competency.manage`), never for themselves.
7. **Technical packages** (`procurement.line_package`): when a purchase order is approved, each open line
   records the item revision in force and every document it is built to or the order is attached to
   follow, at the revision that applies, with the file's SHA-256 — written once, never changed (trigger).
   Packages are shown whole to purchasing readers except documents they may not see, which stay counted
   but unnamed.
