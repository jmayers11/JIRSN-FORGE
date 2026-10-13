# ADR 0052: V3.28 hardening

Status: Accepted for V3.28

## Current checkpoint — 2026-10-13

The resumed source reconciliation expanded reference acceptance from four questions over 100 orders/100 receipt lots to the recovered BN sixteen populations and all nine categories, plus four factual BK questions. The renewed full gate passed all four V327 cases: healthy 3m 48s, constrained 3m 38s, unusual-valid 2m 52s and the consigned-fixture extension 12s. These fixture runtimes include construction and are separate from the thirteen individual 10-second BN/BK read budgets. The resumed full `verify.cmd` gate passed on 2026-10-13 against the frozen 80-file source manifest on HEAD `7092a11`; the subsequent source commit is recorded in AI_HANDOFF.md. Release build: zero warnings/errors. Planning 303/303, Kernel 33/33, Architecture 32/32, restored PostgreSQL Integration 163/163 in 59.8970 minutes including cleanup, and Node 34/34: 565 passing tests. Evidence: `.forge-logs/verify-v3-reconciled.log` and `.forge-logs/verify-v3-reconciled.status`. Current counts and provenance are recorded separately from historical results in the completion checklist and source reconciliation. The 2026-10-12 PASS below remains historical verification of its then-current code and narrower scope.

Node reports zero failures, cancellations, skips and todo cases. Migration assertions pass at 88. Refreshed preflight checks cover 46 JS/MJS modules and direct/transitive NuGet packages across all 20 projects, with no vulnerable packages. This resumed correction adds no dependency or migration.

## Context

The final roadmap increment requires budgets at reference-factory scale, a backup/restore drill, terminal behavior during outages, accessibility and a dependency security scan.

## Decision

Use the V3.27 command-generated factories as the performance baseline: three variants, each with 400 parts, 200 records in each of the other fourteen business populations, and 800 actual worker events. Every selected read answering the nine BN question categories through Analyst/shared source services, and each of the four additional BK Analyst answers, must complete within 10 seconds. Fixture generation and supporting population checks are separate from these read budgets.

The PostgreSQL integration drill backs up a generated `forge_test_*` database with `pg_dump`, restores into a generated clone with `pg_restore`, starts Forge against that clone, and reads a committed engineering marker through the restored host. It rejects database names outside the disposable prefix and removes the clone in `finally`. The full verifier enables `FORGE_TEST_RESTORE_BASELINE`: the fixture starts/migrates a disposable baseline, dumps it once, and restores it into subsequent integration-test databases. The marker drill separately checks that saved business data survives restoration. Per-fixture cleanup covers only the disposable names created by that fixture, including when a PostgreSQL connection was supplied externally; individual drop commands have a bounded 300-second timeout and cleanup failures remain visible. The earlier 120-second timeout is historical and has been superseded for this larger workload.

On shop-floor API transport failure or a 5xx response, clear the in-memory badge/operator identity, close dialogs and lock the terminal until a fresh badge scan. Do not queue commands offline. Tell the operator the result is uncertain and direct a supervisor to check the source record before a retry. Ordinary 4xx validation responses keep the operator in place so they can correct the request.

Improve the shop-floor screen's screen-reader and keyboard affordances: dialog role/name/modal state, Escape close, focus trap and restoration, named machine selectors, announced operator changes, alert/status toast roles, and announced dialog errors. Add behavioral Node checks for outage-state transitions, delayed responses, duplicate actions, idle/manual/switch/disposal invalidation, actual page handlers, modal focus wrapping/restoration and ARIA contracts. The dependency-free harness provides controlled DOM, fetch and timers. Business-action UI guards also cover data roles and scoped timed delegation. Run `dotnet list Forge.slnx package --vulnerable --include-transitive` against the current NuGet vulnerability source.

The existing authenticated `/api/kernel/me` response carries the validated terminal inactivity setting: `Forge:ShopFloor:IdleTimeoutSeconds`, whole seconds 1–3600, default 60 for absent/invalid values. The page validates that value defensively. Node behavior tests prove expiry at a configured boundary closes the active PIN modal and invalid values preserve the default; API tests cover the configuration contract. See ADR 0040 for deployment configuration.

## Consequences and limits

The 10-second limit applies to the nine selected BN category reads and four BK Analyst answers in the generated factory acceptance tests, not every possible report or production workload. The restore drill verifies disposable local databases and runs as part of the full suite; it is not a deployed disaster-recovery certification. Accessibility checks are targeted regressions, not a full WCAG audit or assistive-technology user study. The dependency scan covers the NuGet advisory source, not operating-system packages or runtime images; the refreshed scan's no-vulnerability result is recorded in the current checkpoint, separately from the historical scan below. Hardening itself adds no product schema or dependency. The earlier completion-audit corrections added four additive migrations: temporary delegation, hold release policy/signatures, sent outside-process configuration and explicit order contract commitments. They retain the existing authoritative event/audit paths.

## Historical verification — 2026-10-12

Full `verify.cmd` PASS, 2026-10-12: Release build zero warnings/errors; Planning 278/278, Kernel 33/33, Architecture 32/32, restored PostgreSQL Integration 148/148 in 36.2743 minutes and Node 34/34. All 46 product JavaScript modules pass syntax checks; the refreshed NuGet direct/transitive scan found no vulnerable packages. Migration idempotency passes at 88. The full suite includes factory performance budgets, the seeded backup/restore drill and its cleanup, and the completion-audit corrections. Source commit and exact original-source/deployment limits are retained in AI_HANDOFF.md and the completion checklist.
