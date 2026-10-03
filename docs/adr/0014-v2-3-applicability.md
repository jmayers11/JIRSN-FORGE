# ADR 0014 — V2.3: applicability and documentation readiness

## Context
V2 §7 and review items D1–D4 and Q: Forge must know which revision of each document applies to a piece
of work, notice when work does not match it, and stop work on that basis only when the plant has said
documentation is ready to be enforced. Stock and inspection must follow the part revision actually used.

## Decisions
1. **Item revisions list the documents they are built to** (`engineering.revision_document`): a document,
   optionally a pinned revision, optionally one routing operation. On a released item revision a change
   needs a reason. Reference documents are never a requirement. Requirements are removed, never deleted.
2. **Applicability rules are data** (`engineering.applicability_rule`): revision X applies to a program,
   a customer, orders due in a date range, or one order under a deviation (which needs its number).
   Rules are added and withdrawn with document release authority and a reason; they are never edited
   or deleted (triggers). Serial and lot conditions arrive with genealogy.
3. **The applicability engine is pure** (`Forge.Planning.Engines.ApplicabilityEngine`). A pinned revision
   decides; otherwise a deviation for the order; otherwise program, customer and due-date rules, which
   must agree — if they select different revisions the result is *requires review*, never a guess;
   otherwise date effectivity: the newest revision released by that instant and in effect on that day.
4. **Released orders are compared with what applied at release**, reconstructed from history (revision
   release times, effective dates and rule creation/withdrawal times are all kept), so no snapshot is
   needed. States: Current, Pinned, Superseded after release (a revision released or taking effect after
   the order was released — work in progress continues: Pass), Mismatch, Missing, Not released, Obsolete
   (Fail), Requires review (Indeterminate). A requirement added after release is checked against now.
5. **Reissue.** A planner (order release authority) reissues an order's documentation with a reason when
   the floor has the revisions that apply now; from then on the order references what applied at the
   reissue. Reissues are history (`engineering.order_documentation_issue`).
6. **Documentation in readiness is a policy per item class** (`engineering.documentation_policy`, set
   with document release authority and a reason). Enforced: each operation gets a Documentation dimension
   (whole-item documents count for every operation) ranked right after Engineering. Not enforced: the
   order lists Documentation as not evaluated — never Pass. Default: not enforced, so loading documents
   never suddenly blocks the floor.
7. **Segregation.** Documents a reader may not see are shown as "Restricted document" with their state,
   never named, and still counted — hiding one must never make work look ready.
8. **Revisions on stock.** Receipts may state the released part revision; production receipts record the
   order's revision. Transactions and lots carry it. One lot holds one revision; a lot received before
   revisions were recorded is never given one afterwards. Stock without one is "revision not recorded".
9. **Inspection plans per revision.** A plan is for one item revision or for every revision (`'*'`, which
   all existing plans became); the revision's plan wins. Inspections, holds, the due list and finished
   goods receipts use the plan for the revision the order builds.
10. **Surfaces.** `/api/planning/applicability?order=|po=`; Applicable documentation and Reissue on the
    order; Built to on items; rules on documents; the enforcement table on Engineering → Documents; reports
    `documentation-status` and `stock-by-revision`; Analyst "What documentation applies to order N?" and
    "Show orders with drawing mismatches". Applicability for assets arrives with assets (V2.6).
