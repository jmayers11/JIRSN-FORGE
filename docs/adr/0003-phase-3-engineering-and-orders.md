# ADR 0003: Phase 3 engineering and production orders

Date: 2026-10-01. Status: accepted as defaults for owner review.

## Engineering

- **Revisions are Draft or Released.** BOM and routing change only in a draft. A database trigger
  rejects any change to a released revision's BOM or routing, regardless of the code path.
- **Effectivity is a time range.** Releasing a revision sets `effective_from` (now or a future
  date, never the past) and ends the previously effective revision at that instant. A database
  exclusion constraint guarantees no two released revisions of a part are in force at once.
  Withdrawing ends a revision now without a replacement.
- **BOM loops are refused** at the part level across every revision, when a line is added and again
  at release.
- **Release validation:** a made part needs at least one routing operation; every component must have
  a released revision in force at the effective date; routing steps must reference real work
  centers, machines that belong to them with the activity defined, or an activity allowed at the
  work center.
- **Operator qualifications have one source of truth:** the machine's requirement profile
  (Resources). A routing step may add further required qualifications, all of which are required.
- **Release always takes two people** (owner decision, 2026-10-01). Release needs the
  `EngineeringApprover` role, and nobody who created or edited the revision may release it, even if
  they hold both roles. Every editor is recorded on the revision; a database check enforces it.

## Production orders

- **Setting up and releasing an order takes two people** (owner decision, 2026-10-01): engineers
  (`ManufacturingEngineer`) and planners can set up orders; a planner (`execution.orders.release`) who
  did not set it up releases it. A database check enforces it.
- Order numbers are sequential from 10001. The revision is the one in force when the order is
  created; release fails if it is no longer in force ("cancel and re-plan").
- **Release snapshots** the routing (planned minutes = setup + run × quantity) and BOM (required =
  quantity per × order quantity × (1 + scrap %), rounded to six decimals) with the component revision
  in force at release. Later engineering changes never alter released orders.
- Lifecycle in this phase: Planned → FirmPlanned → Released; Hold/Resume (back to the prior state);
  Cancel while no operation has started. Hold, resume and cancel need a reason. InProgress,
  Completed and Closed arrive with shop-floor execution (Phase 5).
- **Program segregation:** creating an order for a program requires access to it. People without
  access get "not found" (HTTP 404) for the order and its readiness, and it is absent from lists.
  Order audit and events carry the program ID.

## Order readiness

- Evaluated per operation, now, with dimensions Order, Engineering, Predecessor, RequirementProfile,
  Workforce and Safety. Equipment, Material, Tooling and Quality are listed as not evaluated.
- **Workforce asks "could qualified people run this?"**: present, non-trainee people in the
  operation's safety area or not yet assigned anywhere, holding the machine's operator qualification
  and any extra required ones, at least crew size. Safety counts only people present in the area.
- Every eligible machine is evaluated; the operation is executable if one is. The best candidate is
  chosen deterministically (executable, then fewest failures, then machine code).
- If the order's revision was superseded or withdrawn after release, Engineering fails until
  engineering confirms: the order keeps its snapshot, but someone must decide whether it continues.
