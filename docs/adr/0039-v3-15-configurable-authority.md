# ADR 0039 — V3.15 configurable authority

Status: accepted after full verification.

Roles and permission grants move into the Kernel database. Migration 0003 seeds role definitions and permission grants from the existing role map, excluding system-only roles. The integration parity test compares each seeded permission set with the previous map so the migration does not silently widen or narrow access. Runtime principals load effective grants from the database; system actors and in-memory kernel tests keep the existing static fallback.

A role permission can be unscoped or scoped to a program, facility, work center, contract, customer or shift. Once scopes exist for a role/permission pair, they replace that role's global grant. Resource references carry these contextual dimensions into the single authorizer. Approval threshold policy associates a maximum amount and three-letter currency with a permission; protected commands must include amount/currency in their resource reference for the limit to apply.

Role assignments may expire within one year. Principal resolution and directory listings filter expired assignments at read time. Reassigning a role after an expired assignment renews its row. The initial separation policies make self-role changes and self-program changes configurable through audited commands; disabling self-role protection remains prohibited to prevent administrative lockout. SecurityAdministrator's permission policy is immutable through the new APIs.

Role creation, policy changes, separation policy changes and threshold changes use the normal command pipeline for authorization, reason, idempotency, event and audit. All current permission values form a fixed catalog; custom roles can combine those actions without adding new code permissions.

Verification: quick Release build passed with zero warnings/errors; Planning 266/266 and Architecture 32/32. Full `verify.cmd` passed with Planning 266, Architecture 32, Kernel 33 and Integration 100 tests; the Release build had zero warnings/errors. The V3.15 PostgreSQL acceptance checks passed within the full suite, including exact seeded permission parity, data-backed authorization changes, scope and amount/currency threshold enforcement, expiry, configurable separation of duties and migration idempotency. Evidence: `.forge-logs/quick-v315.log`/`.status` and `.forge-logs/verify-v315.log`/`.status`.

Boundary: a configured amount limit applies when a command supplies both amount and currency in its `ResourceRef`. Existing approval commands without pricing data remain unaffected until their domain models provide those values; this increment establishes and verifies the kernel enforcement mechanism.
