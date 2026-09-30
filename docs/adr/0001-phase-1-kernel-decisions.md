# ADR 0001: Phase 1 kernel decisions

Date: 2026-09-30. Status: accepted with the architecture v0.1 approval.

- **Build order:** reordered phases (architecture section 31), not the spec's M-order.
- **Database:** PostgreSQL is the primary target from the start; one schema per bounded context.
- **Runtime:** .NET 10 (LTS), because that is the installed SDK.
- **Kernel persistence uses raw SQL (Npgsql), not EF Core.** Outbox, event log and audit are
  append-heavy and hash-sensitive; exact control over what is written matters more than mapping.
  Systems of record in later phases may use EF Core.
- **Stored JSON is canonical text, not `jsonb`.** `jsonb` rewrites key order and number formatting,
  which would break fingerprints and audit hashes.
- **Audit is one global hash chain** serialized by an advisory lock. Adequate for single-site
  write volumes; revisit (per-day or per-partition chains) if audit throughput becomes a limit.
- **Access-management audit is not program-segregated** so administrators can always see the grants
  they make. Program-owned records in later phases carry a program ID and are filtered.
- **Roles and permissions are code in v1** (`Forge.Kernel.Security.Roles`), reviewed like code.
- **Removed a "last administrator" check** because separation of duties already guarantees one
  administrator always remains: only an active administrator can revoke, never from themselves.

Open items carried from architecture section 33 remain open; none of them block Phase 1.
