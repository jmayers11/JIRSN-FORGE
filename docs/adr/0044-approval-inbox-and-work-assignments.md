# ADR 0044: Approval inbox and work assignments

Status: Accepted for V3.20

## Context

Forge already owns approval rules in individual bounded contexts. A unified inbox must not replace those decisions, let a requester approve their own work, or suggest that acknowledging an assignment approves its source record.

## Decision

The Host builds a bounded, permission-filtered read projection from pending authoritative source records. It excludes the actor who initiated each request and links to the existing source page; source APIs remain responsible for final authorization and separation-of-duties checks.

Kernel stores explicit work assignments and append-only status history. Assignment metadata contains only a supported source type/id, local page link, title, assignee, reason and lifecycle state. A manager assigns an active Forge person; only that assignee can progress the assignment through Assigned, Acknowledged, InProgress and Completed. Every transition has a reason and history row. Assignment completion does not mutate the source record.

## Consequences

Approval flows remain owned by their source schemas and permissions. Adding a new approval domain requires adding a source-specific projection mapping. Assignments provide routing and follow-up tracking, not delegated authority. The current UI requires a Forge person UUID when assigning work.
