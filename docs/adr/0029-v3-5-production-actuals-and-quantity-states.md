# ADR 0029 — V3.5: production actuals and quantity states

## Context
The V3 brief (L, K, AT) asks for production actuals that keep apart things Forge recorded loosely or not at all:
- why a machine did not run (only maintenance downtime existed, with free text);
- a reason someone reported versus a proven root cause;
- expected versus actual setups and changeovers;
- rework, repair, use as is and scrap, without rework rewriting what was reported;
- where an order's quantity is: started, produced, pending inspection, accepted, released, hold, rework, scrap,
  packed, staged, shipped.

## Decisions
1. **Reason codes are data, not code.** `execution.reason_code` holds the plant's own words for downtime, scrap
   and rework. A downtime reason belongs to exactly one stoppage category. Codes are retired, never deleted, so
   what was recorded with them keeps them. Only `execution.reasons.manage` (manufacturing engineer) defines them.
2. **A stoppage has one category and one reason.** `execution.stoppage` records a machine not running:
   planned, unplanned, setup, changeover, maintenance, waiting, starved or blocked. Its reason code must be of
   that category. One stoppage is open per machine (a unique index); a stoppage may be reported up to 7 days
   after the fact but never overlaps the machine's previous one; it is ended once and otherwise never changes
   (a trigger).
3. **Setups and changeovers name what they prepare.** They must name an order operation that runs on the
   machine. At that moment the routing's setup minutes for the operation are copied onto the stoppage as the
   expected minutes, so expected and actual compare even if the routing changes later.
4. **A reason code is not a root cause.** `execution.root_cause` holds a cause and its evidence, proposed by
   whoever reports downtime. It is proven only when someone with `execution.rootcause.confirm` (manufacturing,
   quality, maintenance planning) other than the proposer confirms it, with a reason. A stoppage has at most one
   proven cause; after that no more are proposed.
5. **Stoppages are production actuals, not capacity.** Maintenance downtime windows remain the capacity record
   that scheduling and the shop floor check. A stoppage does not block starting work; it explains time lost.
6. **Scrap can name a scrap reason code.** The shop-floor report takes `scrapReasonCode`; the free-text reason
   defaults to its description. Production reports stay append-only.
7. **Rework, repair, use as is and scrap are distinct.**
   - NCRs gain the `Repair` disposition: usable, not to the drawing. Like use as is, it needs a second person's
     approval.
   - `quality.rework` takes good output back from an order operation for rework or repair. Rework names an NCR
     with a rework disposition or a rework reason code; repair needs an NCR with an approved repair disposition,
     and never more than the NCR covers.
   - Closing records how much came out good and how much was scrapped (they must add up). A record is opened,
     then closed once (a trigger). The production reports it came from are never changed.
   - Production receipts subtract what is taken back from the last operation: it is not there to receive.
8. **Quantity states are derived, never stored.** `OrderQuantityStates` reads production reports, rework
   records, inspections, NCRs and the inventory ledger.
   - So far: ordered, started, produced, accepted, received, shipped, scrapped.
   - Now: in process, rework, repair, produced not received, pending inspection, released, hold, packed, staged.
   - Accepted is what passed the order's last inspection point plus NCR quantities accepted as they are.
   - Stock states follow the lots the order produced; a part that is not lot-tracked is shown only as received.
   - Packed is 0 until V3.14 records packaging.

## Consequences
- Reports: `stoppages` (by category, reason, machine; proven or proposed cause; changeovers against expected)
  and `quantity-states`. The Analyst answers "Which machines were starved this week?" and "How much of order N is
  in rework?".
- The Work Centers page shows stoppages and reason codes; the order page shows quantity states, rework and repair.
- Migrations `execution/0006` and `quality/0003`; migration count 58.
