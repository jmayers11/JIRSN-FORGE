# ADR 0015 — V2.4: engineering change control

## Context
V2 §4 and review scenario 1: revisions of documents and items must change under a controlled record that
says why, gets the reviews the plant requires, is approved by a change board and only then put in force —
with separation of duties at every step and a preview of what the change would touch.

## Decisions
1. **A change record** (`engineering.change`, numbered `ECN-0001`…) carries class (Major, Minor,
   Administrative), type (Design, Documentation, Process, Supplier, Correction), priority, the reason,
   an optional program and customer, and a requested effective date. Status: Draft → In review →
   Approved → Released, or Rejected / Cancelled (both with a reason). Closed changes are frozen by trigger.
2. **Lines list affected revisions** (`engineering.change_line`): a document revision (Draft, In review or
   Approved — never a reference document) or a draft item revision, with the revision it replaces
   recorded. One revision is on at most one open change. Lines are removed (with a reason), never deleted.
3. **Revisions on an open change are released only by it.** Standalone document approval/release and item
   revision release reject them with `under_change`. Plants that never raise changes keep the V1/V2.2
   standalone flows unchanged.
4. **Review policies are data** (`engineering.review_policy`): criteria (class, type, item class, program,
   customer — all optional, all must match) and the reviews required (Technical, Quality, Manufacturing,
   Procurement, Program, Maintenance). The reviews of every matching policy are required; with no match a
   Technical review is. Item classes come from item lines and from parts built to listed documents.
   Policies are created and withdrawn by the change board with a reason; never edited or deleted.
5. **Reviews per round** (`engineering.change_review`): submitting starts a round and records the required
   reviews and why. A review is decided by someone other than the originator who holds a role for that
   kind; Reject needs notes and returns the change to Draft; resubmission starts a new round. Decided
   reviews are frozen by trigger.
6. **Change board approval** (`engineering.changes.approve`, roles EngineeringApprover and ChangeBoard)
   needs every review of the round decided, and an approver who is neither the originator nor an author
   of any listed revision.
7. **Release is separate from approval**: `engineering.release` with a reason, by someone other than the
   originator and other than the approver unless a matching policy sets `combine_approval_release` (small
   shops). Every listed revision is released in one transaction at one effective date (today or later):
   item revisions through the same checks as standalone release; document revisions supersede the one in
   force and record the change board approver as their approver (external documents become Applicable).
   If any line fails, nothing is released.
8. **Impact preview is pure** (`Forge.Planning.Engines.ChangeImpactEngine`): for each part the change
   reaches — its own revision, or built to a listed document — future orders, released not started,
   in progress (needs disposition), completed (never changed), open purchase lines, stock by recorded
   revision, where-used, inspection plans and documents. Orders in programs the reader cannot see are
   counted, never listed. Disposition of affected work is V2.5.
