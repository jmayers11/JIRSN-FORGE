# ADR 0046: Recorded causes in schedule history

Status: Accepted for V3.22

## Context

Horizon history already compares approved schedules and lists evidence recorded between two moments. The event envelope also has a causation identifier, but event creation did not preserve causation from command context and change propagation did not link downstream events to the released change. A shared correlation identifier or nearby timestamp is useful context, but neither proves that one event caused another.

## Decision

Carry an optional causation ID from `CommandContext` into events created by `EventFactory`. Engineering change propagation loads the recorded `EngineeringChangeReleased` event and uses it as the explicit cause for events it creates. If the release event is unavailable, propagation records no cause rather than inventing one.

Extend schedule comparison with a permission- and marking-filtered CauseMap. It walks recorded causation links from interval events to visible ancestors, with bounded interval seeds and chain depth. The display draws arrows only for direct recorded links. Constraints and other contemporaneous evidence remain evidence; same correlation and timing never create arrows. Events whose incoming cause is absent or not visible are shown separately as having no visible causal link.

## Consequences

Historical propagation records created before V3.22 remain without causation and are not backfilled. Missing or restricted ancestors cannot be presented as known causes. The existing event log remains the source of truth; no second history store or migration is needed. Tests cover a recorded cause/effect chain, a same-correlation unlinked constraint, and live engineering-change propagation.
