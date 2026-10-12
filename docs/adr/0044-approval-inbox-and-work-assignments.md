# ADR 0044: Approval inbox and work assignments

Status: Accepted for V3.20

## Context

Forge already owns approval rules in individual bounded contexts. A unified inbox must not replace those decisions, let a requester approve their own work, or suggest that acknowledging an assignment approves its source record.

## Decision

The Host builds a bounded read projection from pending authoritative source records. Every candidate is resolved again through its owning lookup and authorized with its actual program, markings, operation/work-center/facility, contract and known monetary context. A purchase order spanning requisitions in several programs requires access to every recorded source; no money is invented for an unpriced PO or RFQ release. Quote-selection decisions use the actual quote amount, currency and original document markings. Current source state and separation of duties are checked before metadata enters the response. The same current source authorization filters assigned work, so revoking access removes both lists without deleting their history.

The catalog includes the existing NCR, schedule, purchase, project/change, outage, document, manufacturer part, alternate and engineering-review decisions, plus real pending safety-rule publishing, engineering-change approval, shelf-life extension, receiving use-as-is, inventory adjustment, RFQ release, quote selection, root-cause confirmation and temporary-delegation approval. It does not invent approval workflows for commands that permit one actor.

Kernel stores explicit work assignments and append-only status history in the existing tables. Its command service uses the shared pipeline transaction for assignment/status mutation, reasoned audit, outbox events and idempotent retries. The manager and active assignee must both have current read access to the source, and assignment management must cover its complete scope. Only the assignee can progress Assigned → Acknowledged → InProgress → Completed. A repeated assignment preserves its progress. Assignment completion does not mutate the source record.

## Consequences

Approval flows remain owned by their source schemas and permissions. Adding a new approval domain requires adding a source-specific projection mapping. Assignments provide routing and follow-up tracking, not delegated authority. The current UI requires a Forge person UUID when assigning work.

Completing follow-up remains possible after the business source closes, provided current access remains. Contextual NCR and order-quality reads allow a scoped reviewer to open an authorized inbox source while excluding other programs.

`WorkInboxGovernanceTests` exercises private-program and scoped decisions, permission/access withdrawal, real budget and quote boundaries, protected document markings, requester/recipient exclusion, assignment retries and the exact audit/event/history counts. `V320Tests` retains the original sequential lifecycle and source-isolation acceptance.
