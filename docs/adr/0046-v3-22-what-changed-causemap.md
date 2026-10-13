# ADR 0046: Recorded causes in schedule history

Status: Accepted for V3.22

## Current checkpoint — 2026-10-13

The owner accepted the available real engineering-propagation scenario and recovered original BI order-at-risk example as V3.22 acceptance. The original numeric section 54 example remains unavailable; this acceptance does not reconstruct it. The resumed full `verify.cmd` gate passed on 2026-10-13 against the frozen 80-file source manifest on HEAD `7092a11`; the subsequent source commit is recorded in AI_HANDOFF.md. Release build: zero warnings/errors. Planning 303/303, Kernel 33/33, Architecture 32/32, restored PostgreSQL Integration 163/163 in 59.8970 minutes including cleanup, and Node 34/34: 565 passing tests. Evidence: `.forge-logs/verify-v3-reconciled.log` and `.forge-logs/verify-v3-reconciled.status`. Current counts and provenance are recorded separately from historical results in the completion checklist and source reconciliation. Both accepted real-source examples passed. Earlier PASS records describe their historical source/code/test scope.

`V25Tests` now reads exact normal-command `EngineeringChangeReleased`, two `OrderRevisionChanged` and `OrderDocumentationReissued` event IDs. An effects-only CauseMap window must recover the earlier release through recorded causation and preserve source context, entity reference, timestamp, reason and event trace. Revision propagation and document reissue are recorded children of the release; execution order does not create an additional edge between them.

`V322AvailableExampleTests` combines the recovered BI observations in one normal-command fixture: an at-risk M-04-only order, a failed machine, the MR-22 material request not delivered to its point of use, qualified present crew and a valid required tool. Readiness evidence retains each independent constraint and satisfied dimension. The machine and material commands share an actual API correlation identifier but no causation identifier; CauseMap must show their real events without inventing an arrow. The earlier synthetic correlation-versus-causation regression remains a separate negative test.

## Context

Horizon history already compares approved schedules and lists evidence recorded between two moments. The event envelope also has a causation identifier, but event creation did not preserve causation from command context and change propagation did not link downstream events to the released change. A shared correlation identifier or nearby timestamp is useful context, but neither proves that one event caused another.

## Decision

Carry an optional causation ID from `CommandContext` into events created by `EventFactory`. Engineering change propagation loads the recorded `EngineeringChangeReleased` event and uses it as the explicit cause for events it creates. If the release event is unavailable, propagation records no cause rather than inventing one.

Extend schedule comparison with a permission- and marking-filtered CauseMap. It walks recorded causation links from interval events to visible ancestors, with bounded interval seeds and chain depth. The display draws arrows only for direct recorded links. Constraints and other contemporaneous evidence remain evidence; same correlation and timing never create arrows. Events whose incoming cause is absent or not visible are shown separately as having no visible causal link.

## Consequences

Historical propagation records created before V3.22 remain without causation and are not backfilled. Missing or restricted ancestors cannot be presented as known causes. The existing event log remains the source of truth; no second history store or migration is needed. Tests cover a recorded cause/effect chain, a same-correlation unlinked constraint, and live engineering-change propagation.
