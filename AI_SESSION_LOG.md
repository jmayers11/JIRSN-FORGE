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

V3.12 committed as `5db186c` after the full suite passed: Planning 266, architecture 32, kernel 33, integration 92; zero warnings/errors/failures.

### V3.13 full-pass checkpoint

Applied the reviewed RFQ/sourcing draft after the V3.12 commit. Live quick passed: Release build zero warnings/errors, Planning 266, architecture 32. Focused PostgreSQL API gate passed 17/17 (V313, V312, V311, V22 and KernelRoundTrip). Full `verify.cmd` passed, exit 0: Planning 266, architecture 32, kernel 33, integration 94; zero failures and zero Release warnings/errors. The focused and full suites confirm the test actor now reaches the RFQ separation-of-duties check. V3.13 full-suite evidence is in `.forge-logs/verify.log`/`.status`, quick evidence `.forge-logs/quick.*`, and API evidence `.forge-logs/codex-v313.*`. Commit V3.13 before starting V3.14. V3.14–V3.28 remain outstanding.

### V3.13 checkpoint and V3.14 start — 2026-10-11

Confirmed V3.13 committed as 20bfd9f on main; full suite PASS (Planning 266, architecture 32, kernel 33, integration 94), focused API 17/17 and zero Release build warnings/errors. Corrected the top-level handoff checkpoint. Began V3.14 in ignored .forge-logs/review-v313, an isolated checkout. Existing Inventory locations support ExternalProcessor supplier custody and InTransit custody; stock remains in the same company-owned ledger. V3.14 scope is outside processing, evidence/inspection, container and pallet identity, packaging classes/readiness, plus partial shipments and corrections. No V3.14 source changes have been applied or validated yet.

V3.14 processor custody slice implemented and copied into the main worktree (uncommitted). Added POST /api/inventory/outside-processing/dispatch and /return. Commands validate approved PO, open PO line and processor supplier match; use a locked inventory ledger transfer, preserve the same lot and company-owned quantity, cap returned quantity to dispatched amount, and put returned stock in Inspection. Added PostgreSQL integration test V314Tests covering dispatch 5, partial return 3, residual processor stock 2, inspection stock 3, and invalid PO line rejection. Isolated Release solution build passed (0 warnings/errors); focused V314 test passed 1/1 after correcting test fixtures/query URL. Changes are in root main tree. This is only the outside-processing slice of V3.14; packaging class/readiness, containers/pallets, evidence certificates and the partial-shipment/correction acceptance test are still outstanding. No V3.14 commit yet. Next: continue remaining V3.14 requirements, run root quick and focused API/full suite, then commit once the increment is complete.

### V3.14 completed and verified — 2026-10-11

V3.14 is complete. The full `verify.cmd` suite passed with exit 0: Release build 0 warnings/errors; Planning 266, architecture 32, kernel 33, integration 95/95 (19.1339 minutes). Focused PostgreSQL V314 API test passed 1/1; migration/idempotency plus V314 passed 2/2. The suite verifies outside-processing custody and partial same-lot return into Inspection, certificate evidence, packaging-required shipment readiness, container/pallet content reservation and sealed immutability, exact packed shipment contents and shipped association, customer-return overage rejection/correction, and two partial shipments against one demand. Five additive migrations are covered by the 77 migration idempotency assertion. Evidence copied to `.forge-logs/verify-v314.log` and `.forge-logs/verify-v314.status` (PASS); quick evidence is `.forge-logs/quick.*`. ADR 0038 and README/checklist updated. V3.14 source and documentation are ready to commit. Next in the plan is V3.15 configurable roles, scoped/delegated authority, policy-driven separation of duties.

V3.14 committed on main as `2d83b5a` after the full verification PASS. V3.15 is active: implement roles and business permissions as configurable data, scoped authority, delegated authority with expiry, configurable separation-of-duty rules and approvals. Plan acceptance criterion: today's roles reproduce existing permissions exactly; delegation expires automatically; a policy-configured SoD rule is enforced. Continue V3.15–V3.28 sequentially, with each increment documented, fully verified and committed before marking V3 complete.


### V3.15 complete — configurable authority — 2026-10-11

Implemented and committed V3.15 as `407a8f4` on `main`. Roles and the business-action permission catalog are persisted in Kernel data, seeded from the existing role map with exact permission parity. Effective permissions are loaded from the database; dynamic role creation and permission grant/revoke are supported. Permissions can be restricted by program, facility, work center, contract, customer or shift. Role assignments accept expiry up to one year; expired assignments stop authorizing and disappear from the people directory, and reassignment renews an expired row. Approval policies can limit an action by amount and three-letter currency when those values are present in the command resource. Separation-of-duty rules for self-role and self-program changes are configurable through audited commands; self-role restrictions cannot be disabled and SecurityAdministrator policy remains protected.

Verification: quick Release build zero warnings/errors; Planning 266/266; Architecture 32/32. Full `verify.cmd` PASS: Release build zero warnings/errors; Planning 266/266, Architecture 32/32, Kernel 33/33, Integration 100/100 (22.4353 minutes). V3.15 acceptance coverage passed for seeded permission parity, data-backed policy changes, scoped permission and threshold allow/deny, role expiry, configurable SoD and migration idempotency. Evidence: `.forge-logs/quick-v315.log`/`.status` and `.forge-logs/verify-v315.log`/`.status`. `git diff --check` passed before commit.

Known boundary: amount limits apply only when an action supplies both amount and currency in `ResourceRef`. Existing approvals whose domain models do not carry pricing data are not yet wired to those limits. The kernel enforcement mechanism and acceptance behavior are verified; later increments should wire pricing into relevant approval commands where required.

Updated `README.md`, `docs/v3-completion-checklist.md`, and accepted ADR 0039. V3.16 is next: OIDC/SCIM deactivation integration, provider-backed step-up assurance, shared-terminal visibility/idle lock/quick switching, role/assurance/record-version signatures, Sentinel security events and badge-PIN lockout. Existing development authentication uses `X-Forge-Dev-Subject`; shop-floor signoff and badge/PIN flows exist, but this session only inspected those foundations. None of the V3.16 behavior is implemented or verified yet. V3.16–V3.28 remain incomplete; V3 is not done.

### V3.16 identity, step-up, workstations and signatures — 2026-10-11

Completed and committed V3.16 as `5500fe2` on `main`.

Implemented provider-backed step-up middleware for selected approval endpoints: accepted `acr` values are configurable (default `urn:forge:assurance:high`) and `auth_time` freshness defaults to five minutes. Missing, weak or stale assurance returns 401 with `step_up_required` and an insufficient-user-authentication challenge. Added scoped SCIM `PATCH /api/scim/v2/Users/{subject}` deactivation. It invokes the audited command path, marks the Forge person inactive, blocks API access and excludes the person's badge from shop-floor identification. Existing operator qualification checks remain separate from identity authentication.

The shop-floor screen shows the selected operator, provides explicit Switch user and Lock terminal actions, and clears badge/person state on lock or after 60 seconds. Badge-PIN verification tracks failures durably in a transaction independent from the business command; five consecutive failures lock the badge for ten minutes, and successful verification resets the counter. Electronic execution signoffs now persist signer role, `Badge+PIN` assurance and record version. Added a durable Sentinel outbox subscriber for authorization denials, badge-PIN failures and identity deactivation; it authenticates with a configured bearer token and uses event-ID idempotency.

Focused PostgreSQL gate passed 6/6 (four V3.16 tests, Phase5 signoff regression and migration idempotency). Full `verify.cmd` passed with exit 0: Release build 0 warnings/errors; Planning 266/266, Architecture 32/32, Kernel 33/33, Integration 104/104 in 21.3293 minutes. All V3.16 tests passed in the full run. Evidence copied to `.forge-logs/verify-v316.log` and `.forge-logs/verify-v316.status` (PASS). `git diff --check` passed before commit.

The first full verification attempt was blocked at NuGet restore because sandbox networking could not reach `api.nuget.org`. After requesting the required network access, the same `verify.cmd` completed successfully. This was an environment/network limitation, not a test failure.

Deployment boundary: production must configure Nexus JWT issuer, audience and signing keys, accepted assurance vocabulary and provisioning scope. Tests used Forge development authentication, so no live Nexus connection is claimed. Forge's Sentinel adapter was tested against a fake HTTPS receiver; a compatible real intake endpoint and secret still need deployment configuration. Shared-terminal UI lock behavior has no browser-driven kiosk test yet; a physical kiosk exercise remains deployment validation. See `docs/adr/0040-v3-16-identity-stepup-workstations-signatures.md`, the README V3.16 section, and `docs/v3-completion-checklist.md`.

Next increment is V3.17 priority governance. Continue V3.17–V3.28 sequentially, verify and commit each increment, and do not mark V3 complete until all plan acceptance requirements and final handoff records are satisfied.

## V3.17 — Priority governance (2026-10-11)

Implemented priority previews and audited priority changes. Planning computes current and proposed finite-capacity schedules from one snapshot and fingerprints both inputs and outputs. Execution requires the preview fingerprint and timestamp, expected current priority, contextual `execution.orders.manage` authority and an audit reason; it rechecks the target and preview inside the command transaction. Program-hidden impacts remain hidden. The UI presents the target projection and released orders projected later. Scheduler projections honor active holds and quality/inspection blocks.

Focused PostgreSQL acceptance passed 2/2, covering displaced orders, successful commit, stale inputs, unauthorized caller, and hold/quality blocking. Full `verify.cmd` passed: Release build with 0 warnings/errors; Planning 266/266, Architecture 32/32, Kernel 33/33, Integration 106/106 in 23.0942 minutes. Evidence: `.forge-logs/verify-v317.log` and `.forge-logs/verify-v317.status` (PASS). ADR 0041 and README/checklist updated. `git diff --check` passed. Source and product documentation committed as `82eb980` (`feat: complete V3.17 priority governance`).

Next: commit this handoff checkpoint separately. Then implement V3.18 from the numbered plan: shift pass-on and supervisor handoff from current machine/work-center/production state, work-center closeout checklist, explicit created/reviewed/acknowledged/action-completed states, and reconciliation of production, material, WIP, downtime, tooling and unresolved issues without re-entry. Read plan acceptance rule (“Acknowledging an item does not complete it”) and existing service/API patterns before implementation. Continue V3.18–V3.28 sequentially; V3 is not complete.

## V3.18 — Shift continuity — 2026-10-11 — complete

Implemented the Execution shift-continuity slice: work-center checklist configuration; immutable source-fact handoff snapshots; distinct supervisor review and incoming-worker acknowledgment; independently assigned actions with actor/time progression and source/program scope; and unique shift closeout snapshots that reconcile current authorized operational facts and unresolved handoff work. Planning supplies one scoped source snapshot across production, material, WIP, downtime, maintenance, tooling, quality issues and labor. Local site time-zone shift boundaries are validated through DST. Handoff/closeout records do not rewrite source modules; program-restricted facts are filtered against current access. Added the shift-continuity UI, API, additive migration 0009 and ADR 0042.

Focused V3.18 PostgreSQL acceptance passed 1/1. The first full run exposed the stale migration count; after correcting the expected count from 80 to 81, focused V3.18 plus migration idempotency passed 2/2. Final `verify.cmd` PASS: Release build 0 warnings/errors; Planning 266/266, Architecture 32/32, Kernel 33/33, Integration 107/107 (21.7504 minutes). Evidence: `.forge-logs/verify-v318.log` and `.forge-logs/verify-v318.status` (PASS). `git diff --check` and JavaScript syntax checks passed. Product, README, checklist and ADR 0042 committed as `af70b13` (`feat: complete V3.18 shift continuity`). The handoff checkpoint records the result. Next is V3.19 from the numbered source plan; continue sequentially through V3.28. V3 is not complete.

## V3.19 — Notifications and alerting — 2026-10-11 — complete

Implemented additive Kernel migration 0004 for notification rules, alerts, per-person inbox acknowledgments, and durable email delivery/retry state. Rules include trigger/clear event types, top-level payload condition, global/program/work-center/entity scope, separate severity/confidence, role recipients/escalation, in-app/email channels, templates, app-local deep link, delay/reminder/escalation, IANA quiet hours and deduplication. Rule changes and acknowledgments use the command pipeline, audit and idempotency. The event subscriber is attached to the existing at-least-once event pump; current role, program access and marking are rechecked at recipient selection and inbox reads. Clear events resolve the alert and create a distinct unread resolution notice. Email runs through a provider interface in a leased background worker; failures persist and retry without affecting source records. The UI bell and Notifications page use the authorized inbox.

Focused V3.19 PostgreSQL acceptance passed 1/1 through the real outbox and subscriber pump. It verifies role/program scoping and immediate revocation, duplicate event replay, alert acknowledgment, resolution/second acknowledgment, and failed email delivery retaining resolved alert state plus durable failure status. Migration idempotency passed with the updated total of 82. `node --check` passed for notifications.js and app.js. Final full `verify.cmd` PASS: Release build 0 warnings/errors; Planning 266/266, Architecture 32/32, Kernel 33/33, Integration 108/108 (23.1896 minutes). Evidence: `.forge-logs/verify-v319.log` and `.forge-logs/verify-v319.status` (PASS). `git diff --check` passed. Product source, migration, ADR 0043, README, impact review and checklist committed as `57448e6` (`feat: complete V3.19 notifications and alerting`). Next: V3.20 Approvals inbox and assignments, followed by V3.21–V3.28. V3 is not complete.

## V3.20 — Approvals inbox and assignments — 2026-10-11 — complete

Added Kernel migration 0005 with active-person work assignments and append-only status history. The Host exposes `/api/work-inbox`, composing a bounded approval projection from pending quality, planning, procurement, project, maintenance and engineering records. Each row is filtered by its action permission, and the requester actor is excluded. Source links navigate to existing pages; all source decisions still use their regular authorization and separation-of-duties checks. My Work displays decision items separately from assigned follow-up work. Supervisors and Security Administrators may assign supported NCR, inspection, work-order, RFQ, project-review and material-task records to active Forge people with a reason. The assignee moves through Assigned → Acknowledged → InProgress → Completed sequentially, each with a reason and immutable history entry. Completing an assignment does not mutate its source record. Permission mappings, ADR 0044, README, architecture impact review and checklist are updated.

Focused PostgreSQL V3.20 acceptance passed 1/1, verifying self-request exclusion, skipped-state rejection, sequential assignment progression, immutable history, and source-state separation. Focused V3.19 delivery test passed 1/1 after removing its real-clock quiet-hours dependency. Full `verify.cmd` PASS: Release build 0 warnings/errors; Planning 266/266, Architecture 32/32, Kernel 33/33, Integration 109/109 (23.5747 minutes). Migration idempotency passes at 83. Evidence: `.forge-logs/verify.log` and `.forge-logs/verify.status` (PASS). `node --check` passed for work-inbox.js and app.js; `git diff --check` passed. Feature committed as `fd4f3fb` (`feat: complete V3.20 approvals inbox and assignments`). Next is V3.21 Data-integrity exceptions; continue through V3.28. V3 is not complete.

## V3.21 — Data-integrity exceptions — 2026-10-11 — complete

Added a read-only `/api/integrity/exceptions` projection and Data Integrity page. The query finds negative inventory balances, duplicate serial numbers across assets, consumed transformation material without a matching earlier order issue, and tool uses recorded beyond the due date of the last recorded calibration. Each finding is filtered by its source-domain read permission and carries evidence, source, observed time, and the neutral `DataOrProcessException` class. No detector changes or repairs source records. ADR 0045, README, architecture impact review and checklist are updated.

Focused PostgreSQL acceptance passed 1/1: the engineered reference factory starts with no findings; four isolated impossible states then trigger all four detectors, without a misconduct classification. Host build passed with 0 warnings/errors; `node --check` passed for integrity.js and app.js; `git diff --check` passed. Full `verify.cmd` PASS: Release build 0 warnings/errors; Planning 266/266, Architecture 32/32, Kernel 33/33, Integration 110/110 (23.4600 minutes). Migration idempotency remains 83. Evidence: `.forge-logs/verify.log` and `.forge-logs/verify.status` (PASS). Feature committed as `0195058` (`feat: complete V3.21 data integrity exceptions`). Next: V3.22 What changed and CauseMap; continue through V3.28. V3 is not complete.

## Session: 2026-10-12 — V3.22 What changed and CauseMap

Implemented optional `CommandContext.CausationId` propagation through `EventFactory`. Engineering change propagation now reads the actual `EngineeringChangeReleased` event ID and records it as the cause of generated downstream events, including the propagation completion event. If the release event cannot be found, the runner leaves causation empty. Added a bounded recursive CauseMap reader to the schedule history endpoint; it filters event seeds and ancestors by current program access and data markings, caps interval seeds at 500 and ancestry at 20, and walks only explicit `causation_id` links. The history UI draws direct recorded edges, lists events without a visible cause separately, and labels concurrent constraints as correlated evidence. No migration was needed, and historical propagation was not backfilled. ADR 0046, README, architecture impact review and completion checklist were updated.

The focused CauseMap PostgreSQL test passes: a recorded release→effect edge appears, while a constraint sharing the correlation ID remains unlinked. The V2.5 propagation scenario passes with the real downstream outbox events relayed and persisted before checking the release-to-effect cause link. Full `verify.cmd` PASS: Release build 0 warnings/errors; Planning 266/266, Architecture 32/32, Kernel 33/33, Integration 111/111 (21.8486 minutes). Migration idempotency remains 83. Evidence: `.forge-logs/verify.log` and `.forge-logs/verify.status` (PASS). `node --check` passed for `reports.js` and `app.js`; `git diff --check` passed. Product implementation and product documentation committed as `e4fdeff` (`feat: complete V3.22 what changed and CauseMap`). This session log and Claude handoff are committed separately. Next: V3.23 Scenarios and recovery options; follow the exact numbered plan and acceptance gate, then continue through V3.28. V3 is not complete.

## Session: 2026-10-12 — V3.23 Scenarios and V3.24 indirect/support work

Implemented V3.23 private planning scenarios. The Planning migration `0007_private_scenarios.sql` stores owner-scoped inputs. Create/delete go through the command pipeline with reason, event and audit; reads and deletion enforce ownership. Scenario runs use a consistent authorized read snapshot, then apply one change to copied schedule, material and capacity inputs. They return baseline/proposed order impacts, material readiness and Skills MRP deltas over a bounded 14-day window. The UI supports create, run, view and delete and links each of 15 recovery options to the owning domain workflow. No production mutation happens during preview; the target workflow's normal command remains responsible for consequential changes. ADR 0047 records the design and documents that the master brief's exact canonical 15-option list was not included in the Forge folder, so the present catalog is derived from the roadmap and requires source reconciliation if that brief becomes available.

Focused PostgreSQL acceptance passed 1/1: owner isolation, all 15 cards with feasibility/consequences/permission/evidence, and no outbox/audit row changes during preview. `node --check` passed for `scenarios.js` and `app.js`; Release build passed with 0 warnings/errors; `git diff --check` passed. Full `verify.cmd` PASS: Planning 266/266, Architecture 32/32, Kernel 33/33, Integration 112/112 (25.3356 minutes), migration idempotency count 84. The full suite includes V3.23 private scenario acceptance. Evidence: `.forge-logs/verify.log` and `.forge-logs/verify.status` (PASS).

V3.24 is accepted against existing behavior: a badge or supervisor confirmation records attendance; direct labor remains separately attached to production operations; a reallocation preview offers configured machine-free support activities when their safety rule permits the work and shows that the original machine operation stops. The full verification suite passed `Attendance_and_direct_labor_stay_separate_and_reporting_completes_the_order` and `Spec_79_options_cover_replacement_one_person_work_moving_michael_and_the_schedule`. No schema change is needed. ADR 0048 records the architecture and limitation that support activity time is not a separate cost ledger.

Product and documentation commits are `ef874e5` (`feat: complete V3.23 scenarios and recovery options`) and `499047e` (`fix: include V3.23 scenario API endpoints`). The endpoint file was present in the full verified build but was caught unstaged in the post-commit status check and added in the follow-up. ADR 0047 records V3.23; ADR 0048 records V3.24. This handoff/session checkpoint is being committed separately. Next: implement V3.25 Horizon lenses and universal questions from the numbered source plan. Continue sequentially through V3.28; V3 is not complete.

## Session: 2026-10-12 — V3.25 Horizon lenses and universal questions

Added Horizon links to Factory, Program, Contract, Work center, Machine, Order and Workforce views, preserving each existing source service and its authorization. The “What changed?” view now provides −8h, −7d and +2w time-window shortcuts; its historical projection remains `ApprovedAsOf` at the earlier instant, not the current schedule. A phrase in global search routes into Forge Analyst, carries the question in the URL, and auto-runs the existing deterministic parser/services with interpretation, facts and traces shown. ADR 0049 documents that lenses stay as source-owned views and universal questions reuse Analyst rather than introducing parallel systems.

`node --check` passed for app.js, analyst.js, reports.js and timeline.js. Focused PostgreSQL history regression passed 1/1: the pre-replan approved version and machine are returned at the earlier comparison instant and the later approved version appears at the second instant. Full `verify.cmd` PASS: Release build 0 warnings/errors; Planning 266/266, Architecture 32/32, Kernel 33/33, Integration 112/112 (25.4026 minutes). The full suite also passes the earlier schedule comparison and Analyst access tests. Evidence: `.forge-logs/verify.log` and `.forge-logs/verify.status` (PASS); `git diff --check` passed. Product and ADR/README/checklist commit: `629b8ce` (`feat: complete V3.25 horizon lenses and questions`). This handoff/session checkpoint is committed separately. Next: V3.26 Analyst and reporting surfaces; continue through V3.28. V3 is not complete.

## Session: 2026-10-12 — V3.26 Analyst and reporting surfaces

Added the `tool-use-history` report. Each recorded `Used` event is evaluated with `ToolValidity.At` at its own timestamp, and a recognized tool code filters the question's result. The report states that event history does not record machine/activity, so it cannot establish operation-specific eligibility. Analyst resolves “last shift” from configured local shift start, duration, operating days and current site clock; order material-outcome questions route to material reconciliation. Existing stock-position, reconciliation and genealogy reports continue to answer custody/outside-processor, remnant and material-outcome questions. Added a recovery-options intent that lists only caller-owned scenarios and runs at most 20 through current authorized schedule/readiness services. It is read-only. ADR 0050 records design, scope and limits; README, architecture impact review and checklist are updated.

Planning tests pass 272/272. Focused PostgreSQL tests pass 3/3, covering private scenario answers and owner isolation plus historical tool validity through report and Analyst. Full `verify.cmd` PASS: Release build 0 warnings/errors; Planning 272/272, Architecture 32/32, Kernel 33/33, Integration 112/112 (23.7949 minutes). `node --check` passed for app.js, analyst.js, reports.js and timeline.js; `git diff --check` passed. Evidence is `.forge-logs/verify.log` and `.forge-logs/verify.status` (PASS). Product and product-doc commit: `1c19d7b` (`feat: complete V3.26 Analyst and reporting surfaces`).

Next is V3.27 reference factories and extensibility. The only supplied source plan gives acceptance at the category level; the original master brief's canonical question list is not included in this Forge project. Use the included governance review and existing BN-facing reports/Analyst queries, and record this source limitation in the ADR. Continue through V3.28; V3 is not complete.

## Session: 2026-10-12 — V3.27 reference factories and extensibility

Added PostgreSQL integration generators for healthy, constrained and unusual-but-valid reference factories. Each variant creates 100 orders and 100 lot-tracked receipts through ordinary Forge commands, releases ten orders, and asks Analyst the shared V3.26 material-outcome, remnant and last-shift question set. The constrained factory adds a machine production hold; the unusual variant includes small fractional quantities and a large unreleased order. All three answer through existing services and return no data-integrity exceptions.

Modeled a consigned customer fixture through one existing Resources tool identity: customer custody, receipt for repair, outside service/return, a recorded fit-check use, then return to the customer. The test preserves serial and event history. ADR 0051 records the workflow weakness: Forge lacks structured ownership, loan authorization, direct receipt into customer custody and customer acceptance. Reason/reference strings are evidence pointers, not legal or custody semantics. The supplied project does not contain the master brief's BN question catalog, so this test set is an explicit proxy awaiting reconciliation.

Focused PostgreSQL factory tests passed 4/4 and the fixture lifecycle passed 1/1. Planning tests passed 272/272. Full `verify.cmd` PASS: Release build 0 warnings/errors; Planning 272/272, Architecture 32/32, Kernel 33/33, Integration 116/116 (25.0193 minutes). Evidence: `.forge-logs/verify.log` and `.forge-logs/verify.status` (PASS); `git diff --check` passed. Product/docs/tests commit: `d76dc70` (`test: complete V3.27 reference factories and extensibility`).

Next: V3.28 hardening—performance budgets at reference scale, a disposable PostgreSQL backup/restore drill, shop-floor terminal behavior during API outages, accessibility checks/fixes and a dependency vulnerability scan. V3 is not complete.
