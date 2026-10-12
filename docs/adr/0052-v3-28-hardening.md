# ADR 0052: V3.28 hardening

Status: Accepted for V3.28

## Context

The final roadmap increment requires budgets at reference-factory scale, a backup/restore drill, terminal behavior during outages, accessibility and a dependency security scan.

## Decision

Use the V3.27 command-generated factory fixtures as the performance baseline. Every selected Analyst answer over 200-plus order/inventory records must complete within 10 seconds. A new PostgreSQL integration test backs up a generated `forge_test_*` database with `pg_dump`, restores into a generated clone with `pg_restore`, starts Forge against that clone, and verifies a committed engineering item through the restored host. The test rejects any database name outside the disposable test prefix and removes the restored clone in `finally`. The full verifier enables FORGE_TEST_RESTORE_BASELINE: the fixture starts and migrates a disposable baseline, dumps it once, and restores that archive into every subsequent integration-test database. The seeded marker drill separately proves saved business data survives restoration. Cleanup has a bounded 120-second command timeout after a full-suite disk workload exceeded the default 30 seconds.

On shop-floor API transport failure or a 5xx response, clear the in-memory badge/operator identity, close dialogs and lock the terminal until a fresh badge scan. Do not queue commands offline. Tell the operator the result is uncertain and direct a supervisor to check the source record before a retry. Ordinary 4xx validation responses keep the operator in place so they can correct the request.

Improve the shop-floor screen's screen-reader and keyboard affordances: dialog role/name/modal state, Escape close, focus trap and restoration, named machine selectors, announced operator changes, alert/status toast roles, and announced dialog errors. Add behavioral Node checks for outage-state transitions, delayed responses, duplicate actions, idle/manual/switch/disposal invalidation, actual page handlers, modal focus wrapping/restoration and ARIA contracts. The dependency-free harness provides controlled DOM, fetch and timers. Business-action UI guards also cover data roles and scoped timed delegation. Run `dotnet list Forge.slnx package --vulnerable --include-transitive` against the current NuGet vulnerability source.

The existing authenticated `/api/kernel/me` response carries the validated terminal inactivity setting: `Forge:ShopFloor:IdleTimeoutSeconds`, whole seconds 1–3600, default 60 for absent/invalid values. The page validates that value defensively. Node behavior tests prove expiry at a configured boundary closes the active PIN modal and invalid values preserve the default; API tests cover the configuration contract. See ADR 0040 for deployment configuration.

## Consequences and limits

The 10-second limit applies to the four shared Analyst questions in the generated factory acceptance tests, not every possible report or production workload. The restore drill verifies a disposable local test database and then runs as part of the full suite; it is not a deployed disaster-recovery certification. Accessibility checks are targeted regressions for the touched shop-floor interaction, not a full WCAG audit or assistive-technology user study. The dependency scan reports the current NuGet advisory source and does not assess operating-system packages or runtime images. No package update was needed because the scan reported no vulnerable packages. Hardening itself adds no product schema or dependency. Separate completion-audit corrections add four additive product migrations: temporary delegation, hold release policy/signatures, sent outside-process configuration, and explicit order contract commitments. They extend the existing authoritative records and event/audit paths; their requirements and gates are recorded in the completion checklist and related ADRs.

## Final verification

Full `verify.cmd` PASS, 2026-10-12: Release build zero warnings/errors; Planning 278/278, Kernel 33/33, Architecture 32/32, restored PostgreSQL Integration 148/148 in 36.2743 minutes and Node 34/34. All 46 product JavaScript modules pass syntax checks; the refreshed NuGet direct/transitive scan found no vulnerable packages. Migration idempotency passes at 88. The full suite includes factory performance budgets, the seeded backup/restore drill and its cleanup, and the completion-audit corrections. Source commit and exact original-source/deployment limits are retained in AI_HANDOFF.md and the completion checklist.
