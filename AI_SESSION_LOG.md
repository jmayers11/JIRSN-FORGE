# AI_SESSION_LOG.md

A chronological history of development sessions. Append new entries at the end; never delete earlier ones.

---

## Session: 2026-10-10 to 2026-10-11

**Agent:** Claude (Claude Opus 5.5), in a cloud session linked to this PC. It edited and packaged files in the cloud, and built, tested and committed on the PC.

### Objective

Continue the V3 plan: finish V3.7 and V3.8, then build V3.9 onward. Each increment needs:

- a domain model and migrations;
- commands with reasons and two-person rules, and events;
- an API, UI, report and Analyst questions;
- an ADR, README sections and sample data;
- unit tests and tests through the real API.

### Work Completed

* **V3.7, goal attainment and reforecast:** verified and committed as `fcc295e`.
  - Production and delivery goals, broken down.
  - A fresh reforecast against the approved schedule.
  - The `goal-attainment` report.
  - Took over the paused goal-risk files (ADR 0020, `GoalRiskEngine`, `GoalRiskTests`).
  - Removed "What V3 starts with" from the README.
* **V3.8, tooling and calibration:** verified and committed as `0232d18`.
  - Tool types and tools, with history as events and validity at a moment.
  - Service cycles and calibrations; gauges as tools.
  - The `tool-status` report.
* **V3.9, executable capacity and presence:** built and packaged (`v39d`); extracted on the PC and not yet tested.
* **V3.10, generalized holds:** built and packaged (`v310`); not applied.
* **V3.11, receiving inspection, FAI and returns:** built and packaged (`v311`); not applied.
* **Additional changes:**
  - Fixed V3.8's inspection-to-gauge foreign key.
  - Renamed `ToolValidity.At` to `AsOf` on the record, which clashed with the static `At()`.
  - V3.9 tests use the coil without lot numbers, because it is not lot-tracked.

### Files Changed

* **V3.7 (committed):**
  - `src/Forge.Planning/Goals.cs`, `GoalService.cs`, `Engines/GoalRiskEngine.cs`, `Infrastructure/PostgresGoalStore.cs`, `Infrastructure/Migrations/0006_goals.sql`;
  - `src/Forge.Host/Api/GoalEndpoints.cs`, `wwwroot/js/goals.js`;
  - `docs/adr/0020-v3-goal-risk.md`, `docs/adr/0031-v3-7-goal-attainment-and-reforecast.md`;
  - `tests/Forge.Integration.Tests/V37Tests.cs`, `tests/Forge.Planning.Tests/GoalAttainmentTests.cs`, `GoalRiskTests.cs`;
  - shared: `Permissions.cs`, `Program.cs`, `QueryParser.cs`, `StandardReports.cs`, `README.md`, the seed script.
* **V3.8 (committed):**
  - `src/Forge.Resources/ToolService.cs`, `Contracts/ToolContracts.cs`, `Contracts/ToolUse.cs`, `Infrastructure/PostgresToolStore.cs`, `Infrastructure/Migrations/0004_tools.sql`;
  - `src/Forge.Quality/Infrastructure/Migrations/0004_gauges_are_tools.sql`;
  - Quality gauge commands now write tools;
  - `src/Forge.Host/Api/ToolEndpoints.cs`, `wwwroot/js/tools.js`;
  - `docs/adr/0032-v3-8-tooling-and-calibration.md`;
  - `tests/Forge.Integration.Tests/V38Tests.cs`, `tests/Forge.Planning.Tests/ToolValidityTests.cs`.
* **V3.9 (in working tree):**
  - `src/Forge.Planning/Engines/ExecutableChecks.cs`, `ExecutableOperations.cs`;
  - readiness engines and snapshot;
  - tool requirements (`resources/0005`), production holds (`execution/0007`) and document training (`engineering/0011`);
  - `src/Forge.Host/Api/ExecutableEndpoints.cs`, `HoldEndpoints.cs`, `wwwroot/js/executable.js`;
  - `docs/adr/0033-…`, `V39Tests.cs`, `ExecutableReadinessTests.cs`.
* **V3.10 (packaged):**
  - `src/Forge.Kernel/Holds/Holds.cs`, `HoldService.cs`;
  - `src/Forge.Kernel.Postgres/PostgresHoldStore.cs`, `Migrations/0002_holds.sql`;
  - `execution/0008_holds_move_to_kernel.sql`;
  - `src/Forge.Host/Documents/HoldTargetResolver.cs`;
  - `src/Forge.Planning/HoldRegister.cs`;
  - gates in order release, the shop floor, issues, shipping, receipts and PO approval;
  - `docs/adr/0034-…`, `V310Tests.cs`, `HoldPolicyTests.cs`.
* **V3.11 (packaged):**
  - `src/Forge.Quality/DeterminationService.cs`, `Contracts/DeterminationContracts.cs`, `quality/0005_determinations_and_fai.sql`;
  - `src/Forge.Inventory/InventoryService.Receiving.cs`, `Contracts/ReceivingContracts.cs`, `Contracts/ReceivingQueries.cs`, `inventory/0013_receiving_and_returns.sql`;
  - `src/Forge.Host/Api/ReceivingEndpoints.cs`, `wwwroot/js/receiving.js`;
  - `docs/adr/0035-…`, `V311Tests.cs`, `FaiStatusTests.cs`.

### Tests and Verification

* **Tests executed:** on the PC, `quick.cmd`, `p7b.cmd` and `verify.cmd` for V3.7 and for V3.8.
* **Results:**
  - V3.7 passed: 239 unit, 32 architecture, 33 kernel and 76 integration tests.
  - V3.8 passed: 245 unit, 32 architecture, 33 kernel and 78 integration tests.
  - The first V3.8 build failed with CS0102 (the `At` name clash), and the first V3.8 integration run failed in Phase7 (the gauge foreign key). Both were fixed and the runs repeated.
* **Tests not executed and why:**
  - V3.9, V3.10 and V3.11 have not been built or tested yet.
  - The cloud session has no .NET SDK, so every build and test runs on the PC, one increment at a time.

### Outstanding Problems

* V3.9, V3.10 and V3.11 still need to compile and pass their tests. Compile errors are possible, because they were written without a local build.
* Git leaves `.lock` and `tmp_obj` files it cannot delete from the linked session. This is housekeeping only.

### Decisions Made

* Holds belong in the kernel (V3.10) because every context must check them. Release authority is set per kind, and the placer cannot release a hold without it. This replaces V3.9's "always a different person" rule.
* Training records on controlled documents live in Engineering, because Competency cannot depend on Engineering.
* Point-of-use material is checked only where a work center has a point-of-use location, so plants that do not stage material see no change.
* FAI pending determination never allows acceptance and is never shown as complete.
* The replacement from a return to vendor is a new lot, linked by `Replaces`.
* Trade-off: V3.10's register shows NCR, inspection and stock holds as kinds of hold without rewriting their mechanisms.

### Next Recommended Actions

1. Test V3.9 on the PC (quick, p7b, verify) and commit it with `v39msg.txt`.
2. Apply V3.10, move the three removed Execution hold files to `.forge-logs/removed/`, then test and commit. Do the same for V3.11.
3. Start V3.12 (business-record evidence) from the V3 plan doc.

**Session Status:** Partial. V3.7 and V3.8 are done; V3.9 is in testing; V3.10 and V3.11 are packaged.

---

## Session: 2026-10-11 — Codex starts local V3.9 verification

**Agent:** Codex, working directly in `C:\Users\wrest\Documents\JISRN Forge`.

### Objective

Start work on the existing project and leave a current handoff and session log for Claude. Chose verification of the already-extracted V3.9 increment as the first task, using the prior handoff as project context.

### Work completed

- Read the existing handoff, log, README, ADR 0033, verification scripts and relevant V3.9 implementation/tests.
- Confirmed HEAD is `0232d18` (V3.8) with substantial pre-existing V3.9 changes. Preserved those changes.
- Confirmed .NET SDK `10.0.401` is installed and compatible with the configured roll-forward policy.
- Ran `.forge-logs/quick.cmd`. Initial sandboxed restore failed with NU1301 because access to NuGet sockets was denied. The elevated retry restored dependencies and passed.
- Release solution build: **0 warnings, 0 errors**. Planning tests: **257 passed**. Architecture tests: **32 passed**.
- Kernel unit tests separately passed **33/33** with `dotnet test tests/Forge.Kernel.Tests/Forge.Kernel.Tests.csproj --no-build -c Release -nologo`.
- Ran `.forge-logs/p7b.cmd` for V39, V23, V38, Phase5 and KernelRoundTrip integration tests: **13 passed, 3 failed**. All V39 failures were missing operation rows from the plant-wide readiness API.
- Fixed the root cause in `ExecutableOperations`: `ListOrdersAsync` returns order headers with empty operations; load each authorized order with `FindOrderAsync` before evaluating its released operations and material snapshots. Preserved status/program filtering and ordering.
- Selected integration retry after the readiness fix: **15 passed, 1 failed**. Both tooling/holds and point-of-use/presence V39 tests passed. The remaining training assertion expected 422 for `revision_not_found`; the shared API mapping returns 404 for `*_not_found`.
- Corrected the expected status in `V39Tests.cs`, preserving the error-code assertion and other rejection expectations. Reran all three V39 tests through a new ignored `.forge-logs/codex-v39.cmd`: **3/3 passed**, exit code 0, `codex-v39.status` PASS. All verification processes launched in this session have exited.
- Added a current checkpoint to `AI_HANDOFF.md`, distinguishing current verification from the older V3.8 PASS files.

### Files changed by Codex

- `AI_HANDOFF.md` — current verified state and continuation instructions.
- `AI_SESSION_LOG.md` — this appended session; earlier entries retained.
- Generated/ignored build output and `.forge-logs/quick.*`, `.forge-logs/p7b.*` changed through verification.
- `src/Forge.Planning/ExecutableOperations.cs` — load full order details for plant-wide readiness.
- `tests/Forge.Integration.Tests/V39Tests.cs` — assert the established 404 status for a missing revision.
- `.forge-logs/codex-v39.cmd`, `.log`, `.status` — ignored focused verification artifacts.
- No commits at this checkpoint. V3.10/V3.11 packages remain unapplied.

### Continuation

1. Run `verify.cmd` for the current V3.9 tree. This session completed the first verification/fix checkpoint, not the entire increment; the previous full verification PASS is from V3.8 and does not validate the current tree.
2. Fix any full-suite failures and update the handoff with the final counts. `p7b.status` is FAIL from the intermediate assertion mismatch; `codex-v39.status` is PASS from the final focused retry. Logs and statuses have deliberately not been relabeled.
3. Review and commit V3.9 only after full verification. Apply later packages in order, using the prior Claude notes and the V3 plan document (not supplied locally in this session).

**Session status:** Initial V3.9 verification and fixes complete. Full-suite verification, commit and later increments remain for the next session. No source changes beyond the two fixes listed above, and no running test process remains.

---

## Session: 2026-10-11 — continue to complete V3

**Agent:** Codex. **Product Owner request:** Run until version 3 is done and write that down.

- Created an active completion goal for V3 through the previously referenced V3.28 plan; no claim of completion.
- Started full V3.9 `verify.cmd` with approved NuGet/local PostgreSQL access. Release build and Planning 257, architecture 32 and kernel 33 tests passed; integration run is still in progress at this checkpoint.
- Searched local project/document names and archived project documentation for the missing numbered plan; it was not found. Requested the base V3 brief/plan while continuing defined work.
- Product Owner supplied the 55-section operational governance expansion brief. Recorded its requirements in `docs/v3-governance-requirements.md` and reviewed existing architecture/ownership/dependencies in `docs/v3-architecture-impact-review.md`. Current direct authorization to complete V3 is distinguished from the supplied brief's embedded review/stop wording.
- Extracted V3.10 and V3.11 into ignored `.forge-logs/staging-v310` and `staging-v311` for review only. Application source remains V3.9 while full verification runs.
- Prepared V3.10 fixes in staging: resolve program scope before releasing any hold (including already released holds), retain program metadata on release events/audit; load complete authorized order operations when composing inspection holds; preserve the prior V39 404 assertion fix. Added API regression tests for hidden program hold release and inspection hold appearance/clearance. Staged changes are unverified.
- Full V3.9 pass is required before committing it and applying V3.10. V3.10 and V3.11 then need their own build, relevant API tests and full verification. Base plan reconciliation and later increments remain outstanding.

### V3.9 full verification result

`verify.cmd` exited 0 with PASS: Planning **257**, architecture **32**, kernel **33**, integration **81**, no failures. Release build had zero warnings/errors. Integration execution took about 20.6 minutes. Saved version-specific evidence: `.forge-logs/verify-v39.log` and `.forge-logs/verify-v39.status`.

Also staged V3.11 fixes/tests for original-receiver separation of duties, rejection of any pre-existing replacement lot and serialized counting of concurrent customer returns. These are not applied/verified yet.

V3.9 committed as **`411c3c9`**. Applied reviewed V3.10 staging, including the prepared fixes/tests. Moved only obsolete Execution hold source files to `.forge-logs/removed/v310/` after verifying source/destination paths stay within the Forge workspace; preserved SQL migration 0007. Started `.forge-logs/quick.cmd` for V3.10. Ignored `.forge-logs/codex-v310.cmd` selects V310, V39, Phase7 and kernel round-trip API tests for the next verification gate.

**Session status:** Active work; V3.9 complete, V3.10 applied and in initial verification. Continue V3.10, then reviewed V3.11 and reconcile the outstanding base plan.

### V3.10 quick verification and API gate

Quick verification passed: Release build zero warnings/errors; Planning **262**, architecture **32**. Saved `.forge-logs/quick-v310.*`. Added a meaningful migration regression using retained embedded 0007/0008 SQL with populated active/released holds, checking IDs, release history, old-table removal and the next identity value. Started `.forge-logs/codex-v310.cmd` after applying that test; selected API verification is in progress. No application source mutation while the suite runs.

### Codex continuation — base V3 plan recovered

Owner supplied `JIRSN Forge V3 Plan.md` in the project root. Read the full 28-increment roadmap, coverage and acceptance table, including its twelve recommended decisions. Continue the authorized implementation using those recommendations and the governance expansion, preserving existing architecture. V3.10 selected API gate passed 14/14 in 3.6149 minutes. Started full `verify.cmd`; completion is still pending. V3.11 remains staged, with integrity regressions awaiting execution. Rewrote the current handoff header to remove stale current-version claims while preserving older architectural/history sections.

### V3.12 preparation during V3.10 regression

Prepared an unapplied `.forge-logs/staging-v312` evidence draft and ADR 0036. It extends V2.2 reference attachments to receipt/inspection/shipment/FAI targets, evidence kinds, immutable source-signature verification and a Quality FAI gate requiring both results and verified signed-file evidence. Syntax checks passed for attachment, receiving and shipping JS. Further V3.11 staging fixes enforce document access on FAI recording and list visibility; added a hidden-program report regression. Created an isolated ignored `.forge-logs/review-v312` compilation checkout so staging can be compiled without changing source or binaries used by the ongoing V3.10 regression. All V3.11/V3.12 runtime verification remains pending.

### V3.10 full-pass checkpoint

Full `verify.cmd` passed, exit 0: Planning 262, architecture 32, kernel 33, integration 85 (20.0847 minutes); zero Release warnings/errors. Saved `.forge-logs/verify-v310.log` and `.status`. The selected API gate also passed 14/14. Committing generalized holds and its reviewed access/register/migration fixes before applying V3.11. Isolated V3.11/V3.12 compilation caught an existing package collision with the InspectionResult enum; renamed the new structured record ReceivingCharacteristicResult across receiving/FAI contracts and callers. Draft rebuild is running.

V3.10 committed on main as f01f611. Applied reviewed V3.11 from staging, including receiving integrity fixes, FAI report visibility and ReceivingCharacteristicResult naming correction. V3.11 quick/API/full verification now required; V3.12 remains an unapplied draft.

V3.11 build review exposed package-level type collisions (InspectionResult and ReplacementRequest) and CA1826 collection-use errors. Fixed them as ReceivingCharacteristicResult/RtvReplacementRequest and direct indexed selection. Reloaded mutable supplier rejection/approval, RTV and customer-return state under the existing inventory advisory lock; added concurrent return-disposition regression. No analyzer settings were weakened. Quick gate retries continue after these corrections.

V3.11 quick verification PASS: Release build clean, Planning 266 and architecture 32 tests. Saved quick-v311 log/status. Selected API gate codex-v311.cmd starting (receiving/FAI/returns, V3.10 holds and kernel migration/idempotency). Full verification follows after any API corrections.

V3.11 selected gate initially passed 12/14, with use-as-is approval and customer returns failing existing ledger checks. Extended the shipment-reference constraint for CustomerReturn and its QualityRelease/Scrap dispositions; preserved requester/approver identities on the use-as-is ledger transaction. Focused V311 retry passed 5/5 in 1.4248 minutes (codex-v311-fix.status PASS). Earlier selected logs saved as codex-v311-initial.*. Starting full verify.cmd next. V3.12 isolated draft build also passed, zero warnings/errors, including its report/Analyst/tests. Its API/full gates remain pending.

V3.12 staging additionally inherits FAI report scope on targets and uploaded supporting evidence, stores immutable target markings on attachment links, centralizes link visibility for API/report paths, and adds program-revocation regression. These changes remain unapplied and require isolated rebuild plus API/full gates. V3.13 sourcing model/store/service drafts have begun in staging-v313; they are not yet wired, compiled or verified.

### V3.11 full-pass checkpoint

Full verify.cmd PASS, exit 0: Planning 266, architecture 32, kernel 33, integration 90 in 20.2745 minutes. Release build zero warnings/errors. Saved verify-v311.log/status; focused V311 gate passed 5/5 after the two schema/actor corrections. Final fixes include receipt-actor separation, genuinely new replacement lot, replacement quantity reconciliation and original PO/supplier context, locked/reloaded mutable receiving transitions, concurrent return/disposition limits, hidden report access, and package type/analyzer corrections. Committing this checkpoint before V3.12 application. V3.12 isolated build passes; V3.13 is only an incomplete staged sourcing draft.

V3.11 committed on main as 4f18d85. Applied V3.12 reviewed evidence implementation from staging. Its isolated build passed; live quick/API/full gates now required. V3.13 draft remains ignored and unapplied.

V3.12 live quick verification PASS: clean Release build, Planning 266, architecture 32. Saved quick-v312.*. Starting selected evidence/FAI/V2.2 attachment/kernel API gate codex-v312.cmd. Stronger parent scope and link immutability changes are compiled and covered by the new program-revocation regression.

V3.12 selected API gate PASS: 15/15 in 3.5144 minutes. First run passed 14/15: receipt fixture omitted required reason; fixture corrected, rebuilt and rerun. Full `verify.cmd` PASS, exit 0: Release build zero warnings/errors; Planning 266, architecture 32, kernel 33, integration 92; zero failures. Saved `.forge-logs/verify.log` and `.forge-logs/verify.status` (PASS). Commit this verified V3.12 checkpoint before applying V3.13.

V3.13 draft stayed isolated in `.forge-logs/staging-v313` through V3.12 verification. Isolated Release build passed with zero warnings/errors. First API run found the RFQ creator in the separation-of-duties test lacked approval permission and hit 403 before the rule; test now gives that actor the existing PurchasingManager role. Focused V3.13 API run then passed 2/2 using the configured PostgreSQL. V3.13 remains unapplied and needs quick/API/full verification in the main tree after the V3.12 commit.
