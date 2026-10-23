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

---

## Session: 2026-10-12 — V3.28 requirement audit and corrections in progress

Agent: Codex. Objective remains completion of V3.1–V3.28 and a reliable Claude continuation record.

The first V3.28 full run failed only the restore test's disposable database cleanup (Npgsql 30-second read timeout); 116/117 integration tests passed in 26.3186 minutes. Saved evidence: `.forge-logs/verify-v328-first-failure.log` and `.status`. Cleanup is now bounded at 120 seconds. The final verifier restores a migrated baseline into every integration-test database, strengthening the roadmap gate “a restored backup passes the full suite.” Focused restored verification passed 6/6 in 4.3650 minutes (`.forge-logs/v328-restored-focused.log`). Reference factories now verify real outside-processor custody, balanced transformation/remnant and completed-shift production answers, including exact quantities and source references. The unusual million-unit order is reachable and asserted. Terminal handler/race/focus tests passed 21/21.

Completion audit exposed missing or weak proof for governance D/F/G/I/J/K despite older complete labels. Corrections are underway for approved scoped delegation; configurable generalized-hold release assurance/signature; factual downstream lot impact; shift material reconciliation; and outside-processing sent configuration/readiness/inspection. Final full suite must cover these corrections. Original BN question catalog is absent; requested its text from the owner while continuing independent work. V3 remains active and incomplete. No production deployment or live Nexus/Sentinel verification has occurred.

Checkpoint update: All unit/architecture suites in the current corrected gate passed (Planning 278, Architecture 32, Kernel 33). Restored integration focus passed17/19 in8.5747minutes; Horizon's proposed-but-unapproved outage fixture and recovery's live-versus-saved timestamp comparison failed and have been corrected, requiring rerun. Evidence preserved as `.forge-logs/v3-corrections-focused-first-failure.log`. Scope audit additionally found a real V3.1 NULL-lot upgrade join bug, omitted inventory-serial multi-location detection, missing full V3.3 serialized/shipment trace, unprojected production stoppages, and contextual inbox/audit gaps. Fixes and explicit regressions are in progress. Original BN source could not be found locally and owner does not know its location; do not invent or claim exact BN verification. V3 remains incomplete, with final full restored-database release gate and commits pending.

### Reconciled focused gates and final full run started

Implemented the requirement-audit corrections using the existing authoritative records and shared command/audit/event paths. Additional corrections cover contextual source decisions and inbox/assignments, private tool-use display references, serial-location integrity, real stoppage capacity, exact full serialized/shipment genealogy, hold-aware FIFO/FEFO candidates and configurable terminal inactivity. The Release build passes with zero warnings/errors; Planning 278/278, Architecture 32/32 and Kernel 33/33 pass. All 46 product JavaScript modules pass syntax checks. Node behavioral/accessibility/authority/configurable-inactivity checks pass 34/34. The refreshed direct/transitive NuGet vulnerability scan reports no vulnerable packages (`.forge-logs/v3-dependency-security.log`).

The combined restored focus passed 24/29 in 8.9457 minutes (`.forge-logs/v3-final-focused-runtime-first-failure.*`). Its failures were fixtures: prepared multi-statement checksum SQL; invalid `Blade` enum instead of `Cutting`; source-read authority missing from a legacy assignment manager; supplier code versus recorded name; and an unnecessary forbidden self-program grant. All were corrected while preserving product gates. Recheck passed 6/7 in 2.7410 minutes; the last case correctly emitted an extra denied-access security event. The final assertion now verifies four assignment business events/history/successful audits plus exactly one `AuthorizationDenied`; its recheck passed 1/1 in 47.2965 seconds (`.forge-logs/v3-inbox-final-recheck.log`). All focused correction cases are green. The initial Host compile helper error and subsequent failed evidence remain preserved rather than relabeled.

Final full `verify.cmd` is running on restored migrated test databases. Source is frozen during the run; documentation is being reconciled. Final outcome, actual counts and commits remain pending. No live Nexus/Sentinel/email, physical terminal, production deployment or production disaster-recovery verification is claimed. The original BN/section54/recovery catalogs remain unavailable; owner does not know BN's location. Preserve factual proxy coverage and the implemented option catalog without claiming exact source parity.

The first full corrected run ended FAIL: 146/147 API tests passed in 38.7318 minutes; Planning 278, Architecture 32 and Kernel 33 passed. All new governance/correction cases, reference-factory budgets and seeded restore/readback/cleanup passed. The sole failure was V313's old `requisition_not_found` assertion after revoked program access; contextual shared-pipeline authorization now denies that command earlier. Preserved `.forge-logs/verify-v3-corrections-first-failure.log`/`.status`. The test now requires Forbidden, no private source title, unchanged Released RFQ state and exactly one denied CancelRfq audit entry. Focused V313 verification and a fresh full gate are required before committing; the failed full run is not relabeled PASS.

### Final scoped purchasing correction

V313's stronger contextual revocation checks pass focused 2/2 in 1.0757 minutes (`.forge-logs/v313-context-final-recheck.log`). A subsequent full run was deliberately cancelled before completion after confirming that a buyer with only program-A scoped purchasing permissions could not perform the actual A sourcing flow. Preserved `.forge-logs/verify-v3-before-scoped-sourcing.log`/`.status` with CANCELLED status; it is not passing evidence. The correction resolves actual requisition programs before RFQ/quote/PO commands and source reads, rechecks inside writes, includes a prospective PO line's source, and preserves fail-closed behavior for B, mixed and revoked sources. A new real-flow regression, fresh full gate and commits are required. The owner's latest response confirms they do not know where the original BN questions are; no further source-location assumption or exact parity claim is made.

The final scoped sourcing gate passed: clean Release build (zero warnings/errors), Architecture 32/32 and API 10/10 in 3.8971 minutes (`.forge-logs/v3-scoped-sourcing-focused.log`/`.status`, PASS). The actual-A path creates an RFQ, records its original-file-backed quote, selects independently, creates a PO, retries with the identical key, approves independently, and reviews frozen package/WhyLine. B/mixed creation, prospective B-line addition and revoked-source reads/writes are denied, with unique success audit/event and reasoned denial proof. The shared PO context helper lives in Procurement.Contracts, preserving architecture rules. Fresh full `verify.cmd` started with source frozen; actual outcome/counts and commits remain pending.

### Final full local verification — 2026-10-12

Full `verify.cmd` PASS with source frozen: Release build zero warnings/errors; Planning 278/278, Kernel 33/33, Architecture 32/32, PostgreSQL Integration 148/148 in 36.2743 minutes, Node 34/34 (zero failures/skips/cancellations). All 46 product JavaScript modules pass syntax checks; refreshed direct/transitive NuGet scan found no vulnerable packages. Migration idempotency remains88. Every integration database uses a restored migrated baseline; the separate seeded backup/restore drill passes business-data readback and cleanup. Saved `.forge-logs/verify-v3-final.log`/`.status` (PASS), syntax and security scan evidence. Earlier failed and cancelled runs remain preserved, with their corrective work documented above.

V3.1–V3.28 implementation and completion-audit corrections are locally verified against the available requirements. Exact parity remains unverified for unavailable original BN questions, section54 example chain and original fifteen-option catalog; owner does not know BN's location. Factual proxies and the documented derived catalog are explicitly distinguished. Consigned legal ownership/loan/customer acceptance, unknown older archive context, recorded versus hypothetical inputs, selected-question performance, targeted Node accessibility, and local versus deployed recovery limits remain documented. Live Nexus/Sentinel/email, physical terminals and production recovery/accessibility remain deployment validation. No push, deployment, production data mutation or credential exposure occurred. Source/product documentation and handoff/log commits follow this verified checkpoint.

Verified source/product documentation committed as c298a7fb9376a2bd8a344de2f37bcdd5e09a0a1b (feat: finish V3.28 hardening and acceptance corrections). The handoff/session log are saved in the immediately following documentation commit; use git log for its hash. Final full suite passed before this source commit; subsequent changes are documentation only. No local implementation or verification gate remains pending. Exact unavailable-source parity and deployment validation retain the explicit limits above.

## Session: 2026-10-12 — V4 architecture review only

Owner request supplied the full V4 ISO 9001:2026 QMS/digital traceability specification and explicitly prohibited implementation/V3 modification, requiring a completed review and approval stop. Produced `docs/v4/architecture-impact-review.md` with all twenty deliverables, scenarios A–L, four configurable archetypes, source evidence, dependency order and D01–D20 pending decisions; `iso-coverage-register.json` covers every sections 3–40 with all requested interpretation/applicability/reuse/new-work/evidence/role/validation/question/classification attributes. Preserved the exact V4 source and recovered original V3 source, and included detailed operations/governance/platform assessments.

Verified ISO 9001:2026 publication on 16 September 2026 using current official catalog and revision overview. No authorized full normative text was supplied or purchased; exact clause meaning/classification and 2015-to-2026 mapping remain RequiresLicensedVerification. Review describes proposed capabilities, not conformity/certification or implemented V4 behavior.

Read-only repository audit found gates needing separately approved work: full document/material/predecessor readiness does not currently reach every start command; finished-product receipt may directly become Released without distinct release evidence; production inspection lacks exact lot/serial/cohort/per-result equipment; calibration failure lacks retrospective investigation; CAPA context, audit markings/export and queued-email reauthorization need strengthening; true process FPY excludes prior rework/repair and needs first-attempt lineage; totals currently aggregate after presentation truncation. Documented design preserves source ownership, current CAPA identity/history, canonical inventory/genealogy, controlled documents, Kernel/event paths, and privileged-immutability limits. No application fix was made.

Source discovery supersedes earlier availability notes: historical attachment fc15aa59-a336-4f38-948b-ff25701a4e85 includes original BG 15 recovery options and BN 9 question categories/population requirements; preserved unchanged in `docs/v4/sources/v3-cloud-implementation-brief.txt`. Static recovery comparison identified absent distinct original Alternate Work Center/Existing WIP/Remnant Transformation/Outside Processing/Partial Shipment cards; formal original acceptance reconciliation remains open. Numeric section54 end-to-end causal example remains unlocated. The other inspected historical attachment is a Nexus master, not a Forge governance supplement. Historical commands are source context, not current implementation authorization.

V3 product baseline remains c298a7fb9376a2bd8a344de2f37bcdd5e09a0a1b (prior handoff e54a679). No source/tests/migrations/dependencies/runtime configuration or V3 product documentation was modified; new review documents and handoff/session metadata only. No application build/test/database/deployment/customer communication ran. Review artifact validation PASS: twenty numbered deliverables, scenarios A–L, 38 complete provisional mapping records, valid decision/evidence references, 65 local links/source-line references and two byte-identical preserved sources with verified SHA-256 hashes. Documentation whitespace checks pass; Git confirms src/tests/migrations/verifier/README/build configuration unchanged from e54a679. Implementation remains unapproved; stop for Product Owner review. User's free-reset-at-exhaustion authorization is retained; no reset was redeemed.

## Session: 2026-10-13 — resumed V3 original-source reconciliation in progress

Active goal: complete V3.1–V3.28 and record evidence/continuation details for Claude. Rechecked HEAD 7092a11 (V4 review docs only) and recovered original V3 source, then closed a bounded all-28/gov-A–L audit in `.forge-logs/v3-resumed-requirements-audit.md`. Historical 525-test PASS does not establish recovered BG/BN parity. V4 implementation remains unapproved; distinct QMS/release-cohort/retrospective-calibration/retention designs are outside this reconciliation.

Sol Ultra agents are correcting original BG recovery catalog/parameterized copied-input trials/shared source readiness; generating three factories through normal authorized commands with 200 records in each original BN category plus all nine question categories; and fixing queued-email current access, linked-NCR CAPA context, classified audit visibility and quantity-state acceptance evidence. The factory expansion exposed a material-reconciliation Analyst question that lost its named order and could return unrelated rows, plus remnant selection that included zero-remnant records; those source query corrections are in progress with exact acceptance assertions preserved.

Parent is adding an Execution-owned operation-start check implemented by Planning's shared deterministic rules in the same command unit of work: selected machine, controlled documentation, material/point of use, predecessors, required tooling, actual operator/trainer document training and safety. The earlier HTTP safety preview is removed as a command authority input. The command pipeline now binds idempotency fingerprints to authenticated actor/source and supports recorded source authority plus nonsecret selected-person identities before replay; actual source markings/programs flow into audit. Added direct API regressions for omitted required tooling, unstaged material, predecessors, another worker's document training, terminal/person retry collisions and current program revocation. Edits are not yet verified; no new PASS or completed V3 claim is made.

Original numeric section54 example remains unlocated; available BI correlation/recorded-causation evidence is mapped independently. No external deployment, push, message, reset redemption or credential disclosure occurred. Next: finish source edits, Release build, focused meaningful gates, repair actual failures, then fresh restored full gate with source frozen and record actual results/commits.

Product Owner steering, 2026-10-13: “V4 approved to start after V3 is completed.” Approval is explicit and conditional: finish and verify V3 first, then start reviewed V4 implementation without asking again for that same approval. The original V4 architecture review stop is superseded for implementation once V3 completion is established; it still does not authorize purchase of a standard, certification claims, deployment or external communication.

V3.22 acceptance clarification, 2026-10-13: owner chose “Use the available examples” for missing numeric section54—real released-engineering-change propagation and the recovered BI order-at-risk example, explicitly distinguishing recorded causation from correlation. This is an owner-approved concrete acceptance substitution, not a recovered-source claim; every other V3 gate remains required. Added positive normal-command CauseMap and combined BI evidence proofs are being prepared before final verification.

### Resumed reconciliation verification and acceptance decisions

The source-definition question is closed by the owner-approved available V3.22 examples; numeric section54 remains historically unavailable. New tests now cover actual engineering-change/order/document propagation through CauseMap and the recovered BI combined machine/material/qualified-crew/valid-tool example with correlation explicitly unlinked. The original 15-option catalog has independent typed selections and a usable private-trial UI; source commands remain separate. Normal-command factory fixtures cover hundreds in all sixteen BN categories and all nine question categories.

Initial Release builds failed for missing namespaces, one nullable-reference warning and four xUnit2031 analyzer findings; all were corrected and their logs retained. The first runtime gate passed Kernel33, Planning290/292, Architecture31/32 and API26/31; recovery fixture maintenance authority, cross-context Inventory implementation calls, a per-actor HTTP-key assertion, prospective PO source scope, CAPA fixture release and notification fixture authority/recipient setup caused the failures. Inventory readiness now crosses through Inventory.Contracts and delegates to the exact read-only source guards used by mutations. The corrected recheck passed Planning292/292 and Architecture32/32; thirteen API cases passed, one notification fixture used a prepared multi-statement SQL command and failed, then collection cleanup timed out and reported additional collection failures (aggregate API count13/28). Saved .forge-logs/v3-resumed-focused-runtime-first-failure.* and .forge-logs/v3-resumed-recheck-runtime-first-failure.* remain FAIL, never relabeled. The notification fixture now uses separate parameterized updates; fresh verification is required.

Read-only PostgreSQL diagnosis found 5217 old generated test databases and no active test backends. The external-server harness previously cleaned only its baseline. It now tracks and cleans every database created by its own fixture, reports cleanup failures, bounds DROP at 300 seconds for observed disk workload and preserves the baseline archive when cleanup fails. It does not enumerate/remove previous runs or user databases. Source readiness contracts are additionally exercised in a real PostgreSQL read-only snapshot with no source/audit/event/idempotency mutation. Node 34/34 passed after scenario UI changes; scenarios.js syntax passed. A literal Packed=0/future-V3.14 projection is corrected from exact produced lots and current shipment/container records, with attribution, cancellation/shipping, deduplication and presentation-cap regressions awaiting execution.

V3 remains active, with final focused corrections, expanded factory budgets, source freeze/full restored gate and commits pending. V4 starts only after V3 completion under the recorded owner approval. No push/deployment/external message/reset redemption or credential exposure occurred.

Final source review found two concrete regressions before the next gate: the transactional start check rejected stopped WIP even after the existing exact Quality CompleteAtOldRevision decision; some selected recovery trials reallocated the pre-scenario material snapshot, dropping a private supplier delay or priority change. Shared Contracts-backed continuation evidence and copied private material alignment are being corrected with meaningful regressions. These are bounded V3 workflow corrections, not new V4 designs. Refreshed preflight passes all 46 product JS/MJS syntax checks and reports no vulnerable direct/transitive NuGet packages. Full V3 completion remains pending actual focused, original-scale factory and restored full verification.

Source corrections are now frozen. Order-readiness/evidence version 7 uses an exact recorded continuation decision in previews, starts and private recovery; private material dates/priorities persist across trials. Focused gate `.forge-logs/v3-reconciled-focused.cmd` started against restored disposable databases. Its Release build passed with zero warnings/errors in 4:20.95; Planning 298/298, Kernel 33/33 and Architecture 32/32 passed. Integration cases and fixture cleanup remain running, so this is partial evidence only. `.forge-logs/v3-reconciled-source-manifest.json` records hashes of 57 changed source/test files. No final PASS or V3 completion is claimed.

That focused gate completed FAIL with API 21/22 in 12.6412 minutes; fixture-owned cleanup completed without the prior timeout or extra collection failures. The sole failure was the new BI test enumerating the OrderMaterialFlow root object instead of its `lines` array. All tested production corrections, exact WIP continuation, source-access/notification cases and packing regressions passed. Preserved `.forge-logs/v3-reconciled-focused-first-failure.log`/`.status` remain FAIL. Corrected only the BI and original-factory flow response reads to use the actual endpoint DTO; all factual quantity/state/evidence assertions remain. A targeted BI recheck is running before original-scale factories and the fresh full gate. Production source has not changed since the focused build.

The targeted BI recheck passed 1/1 in 1.4124 minutes, including cleanup; Release build again had zero warnings/errors (`.forge-logs/v3-reconciled-example-recheck.log`/`.status`, PASS). This verifies the approved combined BI source example without treating correlation as causation. Original V327 factory gate is now running at full recovered BN populations, all nine question categories and the four retained factual questions; the resulting verdict and fresh full verification remain pending. Changed source/test hashes were refreshed after the two test DTO corrections, with the previous manifest retained alongside failed-run evidence.

Original-scale factory execution exposed a real performance failure: unusual-valid and constrained recovery-options questions took 10.9561099 and 18.1983550 seconds against the unchanged 10-second gate. Their full sixteen persisted populations and earlier question assertions passed before that failure; all nine categories are not yet passing. The remaining old compiled variant is finishing. A bounded correction is removing duplicate all-order material loads and unnecessary source facts within the same read snapshot, preserving unseen material demand, the full visible finite queue and all target/source checks. No budget, population or assertion is weakened. Rebuild, meaningful recovery regressions and fresh factory/full gates will follow a new source freeze.

The factory gate completed FAIL: 1/4 passed and 3/4 failed in 17.8088 minutes, with fixture cleanup successful. The consigned-tool extension passed. Healthy passed all nine universal categories but a retained factual shift assertion found REF-ALL versus BN-ALL: the helper added an identical all-day PLANT1 calendar, and the existing current-calendar report chooses the ordinal-code winner. ProductionReport does not store ShiftCode. Corrected this fixture ambiguity by reusing the already controlled REF-ALL calendar, keeping the exact 80-good/5-scrap/REF-ALL assertion and all sixteen populations/200 worker-shift commands. No product shift-report behavior changed. Preserved `.forge-logs/v3-reconciled-factories-first-failure.*` remain FAIL. Bounded recovery read optimization and an actual API hidden-demand regression are being completed before rebuild and fresh gates.

The performance correction builds Release cleanly and passed Planning 299/299, Architecture 32/32 and API 4/5 in 4.1795 minutes. The new hidden-consuming-demand test correctly kept the material shortfall before replenishment and refreshed material to Ready afterward, but its exact positive recovery assertion was still blocked by the base plant's absent shift calendars. Parent added a full-day calendar, work-center assignment and both qualified workers' calendars through normal commands; assertions are unchanged and recheck remains required. Preserved `.forge-logs/v3-reconciled-performance-focused-first-failure.*` remain FAIL.

Read-only review confirmed another concrete original V3 source-segregation error: hidden supplying production-order descriptions/keys can appear in visible material evidence, and the material endpoint's evidence retains hidden order nodes after Output filtering. Persisted planning text also needs current access after grant revocation. A bounded shared Planning visibility adapter and normal API regression are being implemented across affected read paths; actual allocation/supply keys, full demand, dates/quantities, canonical command fingerprints and stored planning/audit bytes remain unchanged. This is a current-source display/segregation correction under original V3, with no new QMS architecture. Parent remains sole build/test lane; next freeze and all required gates remain pending.

The shared current-source display correction is now frozen with four pure facts and one normal API regression. Public material root counts/outcome use visible rows, while internal allocation and true hidden demand remain complete. Protected MRP source/substitution metadata is removed only from display copies; canonical netting, persisted schedule text, audit bytes and command fingerprints remain unchanged. The focused source-visibility gate has started; its outcome is pending. The refreshed source manifest hashes 72 modified/new source and test files. Recovered factory budgets remain ten seconds with all sixteen original populations intact.

The source-visibility build first failed on immutable EvidenceNode properties and a RecoverySourceAuthority constructor mismatch; the next build failed on an OrderInfo fixture argument and two analyzer checks. All were corrected, preserving both failed build logs. The subsequent Release build passed with zero warnings/errors in 22.58 seconds. Kernel 33/33 and Architecture 32/32 passed; Planning passed 300/303, with three new visibility facts failing. One assertion wrongly matched the year 2026 as order 202; one real parser bug treated `order:` inside `purchase-order:` as an order link; one pure recovery fixture lacked operations and a real baseline schedule. The precise reference assertion, token-boundary fix and compatible operation/baseline fixture are being corrected without changing authority, netting or date assertions. All 14 selected API tests passed in 4.2471 minutes, including current/saved planning withdrawal/restoration, true hidden demand, source-context purchasing/CAPA/audit, priority, recovery and Horizon. Fixture-owned cleanup succeeded. The aggregate run remains FAIL in `.forge-logs/v3-reconciled-source-visibility-runtime-first-failure.*`; fresh affected recheck, original-scale factories and full restored verification remain required.

The affected visibility recheck passed: Release zero warnings/errors in 25.58 seconds, Planning 303/303, Kernel 33/33, Architecture 32/32 and normal API privacy regression 1/1 in 57.8703 seconds including cleanup. All four pure visibility facts now pass, including authorized purchase-order links and protected source detail. Saved `.forge-logs/v3-reconciled-visibility-recheck.*` are PASS. The 72 frozen source/test hashes are unchanged. The renewed original-scale V327 factory gate has started against this compiled source, retaining all three variants, every original population, nine BN categories, four BK questions and ten-second read budgets. V3 completion remains pending its outcome and the full restored regression.

The renewed original-scale factory gate passed 4/4 in 10.3012 minutes, including fixture-owned cleanup. Unusual-valid passed in 2m54s, constrained in 2m38s, healthy in 4m6s and the consigned-tool extension in 13s. Each variant verifies the full sixteen-population manifest, all nine BN category answers and four BK factual answers with their unchanged independent ten-second read limits. These are whole test-case/setup times, not question latency measurements. Saved `.forge-logs/v3-reconciled-factories.*` now contain this fresh PASS; the earlier failed factory evidence remains separately preserved. The 72 frozen source/test hashes remain unchanged. Full restored `verify.cmd` has started through `.forge-logs/v3-reconciled-full.cmd`; its resulting log/status will be preserved as `verify-v3-reconciled.*`. No full PASS, source commit or completed V3 is claimed yet.

The first renewed full run is still executing its compiled source but cannot pass: the full log has five actual regressions/fixture failures. Initial tail-only progress checks missed earlier failure blocks; subsequent progress checks inspect all failure markers. Phase8/Phase9 schedule-review fixtures omitted the independent reviewer's required FALCON membership. Phase8 will grant its reviewer normally; Phase9 will use a separate authorized reviewer while preserving its denied private reader. The delegation audit fixture expects two success rows from an Admin who lacks program A; current source-owned proposal audit is correctly hidden. The correction will prove initial filtering, independently grant membership through the existing authorized grantor, then retain exact rows/actors/actions/reasons and unchanged stored audit bytes/hashes. Two product response regressions must retain the existing concealment contract: private hold release and revoked FAI attachment mutation return source-authorization 403 before their former body-level 404 checks. Endpoint mapping will preserve actual-source denied audit and current checks before replay while restoring existing not-found responses; assertions will not be weakened to 403. No source changes are saved during this compiled run. Both owner-accepted V3.22 examples have passed in this run; overall final counts/cleanup and renewed verification remain pending.

The first renewed full run ended FAIL: Kernel 33/33, Planning 303/303, Architecture 32/32; Integration 158/163 in 56.6620 minutes with exactly the five diagnosed cases. All four expanded factory tests passed within this full run, as did both accepted V3.22 examples. Cleanup succeeded; a read-only metadata check shows the unchanged 5217 prior-run test databases and an idle checkpointer, confirming this fixture removed its own databases without deleting earlier runs. The full wrapper skipped Node after the .NET failure; no new full Node PASS is claimed. Preserved `.forge-logs/verify-v3-reconciled-first-failure.*` and `v3-reconciled-full-first-failure-manifest.json`; all 72 frozen source/test hashes were unchanged. Prepared fixes are now being saved after the lane ended. The source/evidence regression preserves active/released/absent-object 404 responses, rejects same-key replay after withdrawal, proves unchanged hold/document-link source state and actual-scope denied audit. Fresh focused and full gates remain required.

All five faithful fixes are saved. The source-specific API boundaries conceal current classification denials only after the shared pipeline records the actual-source denial before replay. Attachment mutation reuses the owning visibility check, preserving ordinary permission denials and caller-supplied marking checks. The normal-command tests verify active/released/absent concealment, successful exactly-once retry, withdrawal then same-key denial, unchanged hold/document-link source state and actual-scope reasoned audit. The delegation fixture proves initial nonmember filtering, independent ordinary access grant, exact success actors/actions/reasons and unchanged stored audit values/hashes. Report/Analyst fixtures use authorized independent reviewers while keeping the private reader ungranted.

The focused authority recheck passed: Release zero warnings/errors in 1:17.54; Planning 303/303, Kernel 33/33, Architecture 32/32 and API 20/20 in 6.4760 minutes, including fixture cleanup. `.forge-logs/v3-reconciled-authority-recheck.*` are PASS. All 80 changed source/test files are now frozen and their hashes are unchanged. A second complete restored `verify.cmd` is running through the full wrapper (parent session 6610). Its actual final counts, timing and Node outcome remain pending; the first full failure remains separately preserved. No final V3 completion or source commit yet, and V4 runtime work has not started.

The second complete restored `verify.cmd` gate passed on 2026-10-13: Release zero warnings/errors; Planning 303/303, Kernel 33/33, Architecture 32/32, PostgreSQL Integration 163/163 in 59.8970 minutes including fixture-owned cleanup, and Node 34/34 with zero failures/cancellations/skips/todos. Total: 565 passing tests. All four recovered original-scale factory/extension cases, both owner-accepted V3.22 examples, all five first-full-run failure repairs, seeded restore and migration idempotency at 88 passed. `.forge-logs/verify-v3-reconciled.log` and `.status` are PASS; `v3-reconciled-full-pass-manifest.json` preserves the frozen 80-file source/test manifest, with every SHA-256 unchanged after the full gate. Fresh preflight passed 44 JS plus two MJS syntax checks and found no vulnerable direct/transitive NuGet packages; dependencies/migrations remain unchanged. The first full FAIL and earlier failed focused runs stay separate historical evidence. The 28-increment/governance-A–L reconciliation and completion documents are being finalized against this result; exact source and handoff commits will be recorded next. V4 runtime work has not started before this V3 record is complete.

V3 completion source/product documentation committed as `c879a9e65a15eecd46fd05bab37b29a6016dd6b9` (`fix: reconcile V3 acceptance with original sources and current authority`), 93 files. The all-28/governance-A–L map, closed correction register, checklist, eight affected ADRs and architecture review now record the fresh 565-test gate and factual limits. Original source archives remain unchanged; the verified 80 source/test hashes remain unchanged. This final handoff/session record is committed separately after the source hash. All authorized V3 completion work is finished, including the accepted example substitution; the persisted V3 goal can now be marked complete. The owner's conditional V4 implementation approval is active. Read-only first-slice analysis is complete; V4 runtime work will start after this committed V3 boundary. No push/deployment/external message/reset redemption occurred.

After V3 source `c879a9e` and handoff/log `ac2fea3` were committed with a clean tree, the persisted V3 goal was marked complete. Conditional V4 implementation approval became active. V4 began with a tracked implementation plan and a bounded read-only canonical identification resolver for production orders and operations. Host owns the source-specific codec/allowed kinds; no new Kernel/domain identity, generic IdentificationRead permission, inventory mutation, UI, label, alias, scan-attempt store, dependency or migration was introduced. Namespace is explicit configuration, not tenant isolation or authentication. Four source files are saved/frozen; bounded static review is clear on strict syntax/limits/namespace, actual operation existence, current source authorization, missing/denied concealment and no-write behavior. New pure/API acceptance tests and the parent-owned focused gate remain pending. This is a started first V4.1 step, not a completed V4 increment or a PASS inherited from V3.

The first V4 identification step passed its separate focused gate: clean Release build zero warnings/errors in 52.67 seconds; Planning 303/303, Kernel 33/33, Architecture 32/32 and Integration assembly 11/11 in 2.6525 minutes including fixture-owned cleanup, 379 tests total. The eleven include four new pure facts, five new API/configuration cases and two retained work-inbox source-authority regressions. The earlier frozen Host-only compile passed with zero warnings/errors in 2:08.52. All six source/test hashes stayed unchanged. `.forge-logs/v4-identification-focused.log`/`.status` are PASS; source manifest and Host-build log remain local evidence. No dependency/migration/JavaScript change or full V4/physical scanner/label gate is claimed.

Committed first V4 resolver/plan/tests as `46a917c31429b7587e6dec85f2047d2a490dac32` (`feat: start V4 canonical work identification`), eight files. Exact current-source cases prove actual status transitions, A-only scoped read despite A+B membership, absent operations, identical missing/unreadable response, same-code permission/program withdrawal and restoration, safe invalid/foreign/unconfigured outcomes, no authentication grant and exact unchanged operational/audit/event/idempotency rows during resolution. No runtime source fix was needed after the first build/test gate. Full V4.1 remains open. Claude's next work is material/location reference resolution, optional transient staging, existing transactional pick/start validation, then governed labels/attempts and device exercises as listed in `docs/v4/implementation-plan.md`. Final handoff/session records this source hash in a separate local commit. V3 goal completion is preserved; no new V4 completion goal was inferred. No push/deployment/external message/reset redemption occurred.

## V4 completion work resumed — 2026-10-14

Owner approved the local preview and explicitly directed continuation through all V4, with existing free resets at exhaustion. Created an active V4.1–V4.6 goal; starting HEAD b22496f was clean. Preserved original source archives and historical V3/first-V4 evidence. Added the tracked all 38-capability/scenario A–L checklist and current authorization to the implementation plan. Parallel ownership: material/location/typed-pick backend; optional scan UI; Quality-owned governed profile/process/policy/requirement foundation. Root owns shared permission/registration/source-access wiring, final evidence/docs and the only build/test/commit lane.

Optional scanning UI gate passed 54/54 with zero failures/cancellations/skips/todos in 1.0398392 seconds, `.forge-logs/v4-scanning-ui.log`; all existing terminal/accessibility/authority checks plus new behavioral cases are included. Material/location backend and tests are frozen awaiting .NET verification, with strict 1024-character encoded envelopes/40-character actual keys, exact point lookup, current facility/WC access and source-safe identity-only lots. Optional typed pick tuple preserves null legacy fingerprints, validates the actual task inside the transaction and binds replay. Root added MaterialTask alias to the existing current-source branch before replay. QMS foundation source/UI remains in progress and is not claimed verified. Account reports 71% weekly used and 3 available free resets, ordinary usage allowed; no reset redemption or paid purchase occurred.

The governed foundation was completed and locally verified on 2026-10-14. Combined scanning/QMS browser gates passed 78 with no failures/cancellations/skips/todos; the final run takes 3.575556 seconds. All 48 JS/MJS modules pass syntax checks. Quality0006 stores typed governed definitions and exact immutable source/lifecycle history; Kernel0008 seeds five QMS permissions/three roles plus distinct unassigned quality.product.release. Main local preview had applied Quality0006/Kernel0008, so those checksums were preserved and additive Kernel0009 corrected common role authority/custom-name grants. Registered migration total is 91. Shared command failure scope, facility-scoped audit and source-sensitive notifications preserve canonical history.

Earlier build/analyzer/test-compile and direct-preview DLL-lock failures remain preserved under .forge-logs/v4-governance-material-build-*. Three runtime runs passed Planning 303/Kernel 37/Architecture 32 but Integration 57/67 in 17.7889 minutes, 62/67 in 20.0206 minutes and 65/67 in 15.6313 minutes. The faithful fixes supplied canonical QMS seeds/common grants, the runner's extension-free migration ID, ordinary independent engineering/material-handler fixture authority and unambiguous SQL row aliases for snapshot assertions. All QMS cases and the root real presentation-access case passed before the final two targeted repairs. The correction gate passed its two actual regressions without weakening assertions.

The coherent foundation gate then passed 517 tests: Planning 303, Kernel 37, Architecture 32, Integration 67 and browser 78. Release build has zero warnings/errors in 27.36 seconds; Integration completes in 32.6996 minutes including fixture-owned cleanup. All 665 frozen product/test SHA-256 values remain unchanged, preserved in v4-governance-material-full-pass-manifest.json. v4-governance-material-focused.log/.status are PASS; all failed evidence remains separate. This verifies material/location resolution, typed picks, QMS independent lifecycle/source immutability and affected recovery/audit/notification/hold/replay/work-inbox regressions. It is a foundation checkpoint, not full V4 completion.

Committed verified source/product documents as aa47bd1290b251b30f759b247b386a5b5be986d9 (feat: add V4 governed QMS definitions and material identification), 46 files. Claude records are saved separately after the exact source hash. New policy/labels/UI/server-session/exact acceptance packages remain in ignored next-slice staging and require promotion, integration and fresh gates. Parent owns shared hooks and the only build/test/restore/commit lane; all 38 capabilities and scenarios A–L remain in the tracked completion checklist. Kernel0010 labels/0011 sessions are reserved. Physical/licensed/org-policy/deployment evidence remains bounded.

At actual usage exhaustion all three agents failed. A free reset was subsequently observed applied externally, returning account usage to 0% with two resets remaining; root resumed all agents. Latest inspection reports 12% used, ordinary usage allowed and the same two free resets. Root did not invoke a redemption mutation, purchase or upgrade. Browser dashboard was signed out and handed to the user; no authentication was automated. Free-reset authorization persists. The direct preview process was verified and stopped to unlock builds; restart an isolated compiled runtime on port 5080. The V4 goal remains active and work continues through the remaining implementation and acceptance.

Restored the verified aa47bd1 foundation preview from a separate immutable ignored runtime directory; parent session 2178 serves http://localhost:5080 and HTTP 200 is confirmed. Snapshot copies only compiled runtime artifacts, tracked appsettings and verified wwwroot; forge.local.cmd is neither read nor copied and is consumed only in memory by the helper. Its initial missing environment mapping was corrected after an authentication failure. Main preview now applied Kernel0009 additively; Quality0006/Kernel0008/0009 checksums stay immutable. Source builds can continue without locking the displayed runtime. Policy package hashes were verified (15 files) and its 11 new source/test files plus three foundation extension patches were promoted; current new source is unverified. All prior checkpoint evidence remains attached to aa47bd1.

## V4.3 improvement case and unmeasured-result handling — 2026-10-14

Added an actual-source improvement case linked to an Engineering change. It exercises the approved Improvement definition, case assessment, owned implementation task, separate independent verification, implementation assessment, effectiveness review and closure. The acceptance rejects an unsupported one-sided before/after value and stores both measures as null with an explicit limitation when no comparable owner source exists; no quantitative benefit is inferred. The shared `QmsPlanningCaseTests` suite now passes3/3 (risk, controlled change, improvement) in1m03s; `.forge-logs/qms-planning-cases-current.log`/`.status` are PASS. The focused improvement case passed1/1 in `.forge-logs/qms-improvement-case-current.log`/`.status`. Preserved the earlier constructor compile failure in `.forge-logs/qms-improvement-case-compile-failure.*`; implementation uses the actual seven-field `QmsImprovementEvaluation` contract. Full V4.3/whole-tree acceptance remains pending.

## V4.3 controlled QMS change workflow acceptance — 2026-10-14

Added a real database-backed QMS change case to `QmsPlanningCaseTests`. It links the QMS change definition to an effective actual QMS process and an actual Engineering change source/revision, then traverses human impact assessment, independent review/approval, owned implementation task, separate task verification, implementation-verification assessment, effectiveness review, independent acceptance and closure. Generalized the case-test assessment helper to accept the exact definition/evidence source while preserving the existing risk test.

The focused test passes1/1 and the full case-test class passes2/2; logs/statuses are `.forge-logs/qms-controlled-change-current.*` and `.forge-logs/qms-planning-cases-current.*`. Preserved two earlier fixture failures separately; they exposed invalid business-code punctuation and the explicit required process-monitoring list. No validation was relaxed. Current whole-tree wrapper remains unrun against these latest changes; V4 is incomplete. Improvement workflow, V4.1 scan movement/attempt retention and V4.4–V4.6 work remain outstanding, with all external evidence limits preserved.

## V4.2 trace investigation promotion and focused verification — 2026-10-14

Promoted the trace-investigation contracts, owner stores, source-aware resolver/API, Quality0011 migration, and real-source tests into the active working tree. The first fixture used the creator as releaser and correctly hit separation-of-duties; a second test binary predated the separate planner's membership edit and produced a stale 403. Rebuilt the current assembly, reran the focused trace suite, and it passed 6/6 in 1m23s. `.forge-logs/trace-investigation-current.log` contains the successful run. Current tests include actual supplier-lot split/issue/work/output/shipment evidence, independently approved candidate containment, private downstream access revocation that redacts the live view while preserving captured bytes, and a failed calibration interval linked to an unchanged passing inspection. No release or defect is inferred from investigation evidence.

The active repository now has 100 SQL migration files. The full wrapper remains stale/FAIL from the pre-selector-fix attempt and has not been rerun against current V4.2 source. Existing earlier integration and UI results are not promoted as proof for the trace source. Port 5080 remains the immutable 06f4045 preview. V4 is incomplete; continue through the approved V4.1–V4.6 gates and update the completion checklist only to the exact verified scope. External licensed sources, authorized organization policy decisions, physical devices, external connectors and deployed recovery remain separate validation limits.

## V4 label, policy and server-session integration — 2026-10-14 (verification running)

Promoted frozen policy (Quality0007), all owner identities/label lifecycle, optional browser capture, generic Kernel transaction participant with lazy Host factory, provider/channel-bound server sessions and mixed current/historical source access. Kernel0010–0013 bring active migration files to96; the foundation preview remains isolated on aa47bd1 at localhost:5080. New active source is uncommitted and does not inherit foundationPASS.

Restored ZXing.Net0.16.11 after the restricted network attempt failed; a separately approved NuGet restore succeeded and direct/transitive vulnerability scan found none. Preserved four compile failures, one test-compile failure and the first browser135/138 failure. Corrected null analysis, actual parameter scopes, exact captured/current revision display and the invalid opaque test session id without weakening consequential checks. The seventh Release build succeeds with zero warnings/errors in25.96s. Browser138/138PASS in7.8139972s, no failure/cancel/skip/todo; syntax50/50 modulesPASS.

Added original-hash compatibility/tamper facts, real accepted-context fingerprint/audit privacy checks and a populated custom-role upgrade case. Fixed the new role seed's exact kernel.delegations.propose name before its first database execution; already-applied migrations remain unchanged. Session revalidation retains opaque code and exact typed presentation read refs; historical accepted rows use their own saved selections and public snapshots exclude internal binding hashes. Governed terminal/identified-worker mode requires validated provider session/workstation, while current-operator policies require the authenticated person; badges never supply provider authentication.

Parent frozen focused gate is running (session1242, v4-label-session-focused-first.*), source/test manifest732 files. It rebuilds and runs Kernel/Planning/Architecture plus new real policy/identity/label/session/source/collision and round-trip cases against individually restored fixture databases. Actual result/count/time/cleanup and coherent final PASS remain pending. Root source remains frozen throughout. Acceptance25-file package, separate child NCR/CAPA source adapter, investigationsQuality0009, planningQuality0010 and commercialQuality0011 drafts continue independently; all remain unexecuted until root promotion/verification. All V4 increments and remaining38/scenarioA–L gates remain active.

Usage observation44% with two free resets; no new reset/purchase/push/deployment/external communication.

The initial label/session focused gate finished FAIL after cleanup: Kernel44/Planning303/Architecture32 passed; Integration57/63 in11.6621 minutes. All732 frozen source/test hashes stayed unchanged. Six failures exposed FOR SHARE in a read-only snapshot, incomplete scanner option binding, missed private required-instruction history and a fixture trying to retire an in-use machine. Corrections preserve write-side locking and retirement safeguards, parse explicit server scanner configuration, retain complete required-source history and use actual current machine read revocation. The targeted correction gate passed9/9 in4.1491 minutes including cleanup, with Release zero warnings/errors in2:22.15. Its evidence and the initial FAIL remain separate.

A fresh coherent gate is now running as session1729, v4-label-session-coherent.*, on733 frozen source/test hashes. Release has zero warnings/errors in31.44s; Kernel44/44, Planning303/303 and Architecture32/32 pass. Integration/final browser/cleanup remain pending; no full slice PASS or commit is claimed. The earlier compile chronology contains four failed builds including a test compile, rather than four plus one additional failure. Usage most recently61% used, ordinary allowed, two free resets; none redeemed by root. V4.1 scan movement/telemetry, quantity acceptance/returns/investigation, planning/retention, commercial, audits/CAPA and frozen management review/mappings remain active. Continue through all required increments after this gate, keeping the preview on5080 running from its independent foundation snapshot.

The first coherent label/session gate finished FAIL after cleanup: Release zero warnings/errors31.44s; Kernel44/Planning303/Architecture32 PASS; Integration121/122 in29.1048 minutes. The sole failure was the recovery test's expected91 versus actual96 registered migrations. Node was skipped by the wrapper after .NET failure. The one-line expected count was corrected only after session1729 ended. The post-run hash process was still executing when that edit was saved, so it reports the one known V328Tests.cs change; the other732 hashes match. This is not recorded as a pre-edit unchanged manifest result. A fresh733-file manifest precedes the second coherent gate, now session7157 under v4-label-session-coherent-second.*; source remains frozen again. All first-failure evidence is preserved without overwriting. Root management review draft now includes immutable version storage, exact definition lifecycle/source authority and a captured PostgreSQL repeatable-read snapshot identity design; it remains ignored, uncompiled and incomplete.

The second combined label/policy/server-session gate passed639: Planning303,Kernel44,Architecture32,Integration122,browser138. Release zero warnings/errors1:20.38; Integration29.3522min including cleanup; browser1.8832098s with no failure/cancel/skip/todo. All733 frozen source/test hashes remain unchanged. Registered migration files96, syntax50PASS, ZXing.Net0.16.11 restored/dependency scan none. First focused57/63 and first coherent121/122 failures remain separate. Committed109 verified source/product-doc files as06f4045e7361b45a2054365b426f0e8f0a2aeb3f (feat: add governed V4 labels and authenticated scan sessions). V4 remains incomplete.

Refreshed independent preview snapshot to06f4045-presentations-sessions (30 runtime files), session83801, localhost:5080 HTTP200 confirmed. Initial attempt reused a locked old log; unique log filename and stopping only the verified old snapshot executable fixed it. Both snapshots remain preserved. Source builds stay unlocked, credentials consumed in memory only. Next acceptance25/planning16 hashes were checked and match; no source promotion yet. Return package's exact typed enum-options correction is authorized/refrozen18 files at7A42B639...; product/schema/assertions unchanged. Investigation/commercial/audit drafts continue. Root review now stages immutable actual snapshot capture, human review/owned action/qualified mapping services, API, rule/two real-command tests; no execution or completion claim. Source access/UI/KPI/Analyst/notification/movement/telemetry and all remaining gates must be finished. Latest usage69% with two available resets; no redemption/purchase/push/deployment/external communication. Goal API still shows prior blocked metadata and has no active-resume mutation; authorized meaningful work proceeds.

## V4 acceptance, QMS planning and retention — 2026-10-14

Continued the owner-authorized V4.1–V4.6 implementation from the verified `06f4045` checkpoint. Promoted the exact acceptance/product-release integration and QMS risk/objective/task/retention planning work into the working tree, including the new migrations Quality0008, Quality0010 and Inventory0018, API and dependency-injection wiring, current owner/source access, and browser code. These source files remain uncommitted and did not inherit the preceding 639-test checkpoint.

Fixed four issues exposed by actual build and integration runs: registered the QMS planning endpoints/services with the Host; changed the acceptance fixture to have the order created and released by different actors; normalized JSON-loaded `DateTimeOffset` values to UTC for Npgsql `timestamptz`; and corrected the calibrated-gauge race gate. The race gate uses a separately held PostgreSQL tool row and observes application connections blocked by that exact backend, then requires the inspection and both normal calibration APIs to succeed after release. Its test-only connection timeout accommodates the coordinated wait. Temporary console logging was removed from `ForgeApp` after diagnosis. Earlier acceptance/planning gate evidence remains recorded as separate failed logs; assertions were not relaxed.

Fresh Release solution build passed with zero warnings/errors. Focused acceptance/planning PostgreSQL gate passed 31/31 in 7.5558 minutes including fixture cleanup: `.forge-logs/acceptance-planning-corrected.log` and `.status` (PASS). This includes the actual 1,000-unit/920-released/80-held example, exact legacy-release blocking, held packing/shipping rejection, concurrent independent product release/ledger consumption/shipping retries, measured gauge use and calibration concurrency, source withdrawal/identical replay, private rejected child NCR/CAPA and current membership, QMS risk-task closure, unknown/indefinite retention blockers, current legal-hold release, immutable file bytes, retained prior duration on classification revision and exact metric cohort/period filtering. `git diff --check` passes with working-copy line-ending notices only.

The verified preview at `http://localhost:5080` has been restarted and responds HTTP 200 over IPv4 and IPv6. It uses the preserved immutable `06f4045-presentations-sessions` runtime; it is the earlier label/policy/session UI, not the current uncommitted acceptance/planning source. Keep that snapshot intact during source builds. Usage is 2% weekly, ordinary calls allowed, one free reset available; none needed or redeemed. Next continue scan movement/retention and V4.2 investigation/trace gates, then the remaining V4.3–V4.6 workflows and the fresh full regression/restore/UI gates. No commit, push, deployment, external message, purchase or certification claim was made in this checkpoint.

## Full V4 integration pass and UI selector repair — 2026-10-14

Corrected the migration-count assertion in `tests/Forge.Integration.Tests/V328Tests.cs` from 98 to 99 after both the basic migration idempotency check and the PostgreSQL backup/restore gate demonstrated that the current registered set contains 99 migrations. Preserved the preceding full run as `.forge-logs/verify-migration-98-failure.log` and `.status`; it recorded 278/279 integrations passing with only the stale V328 expectation failing.

The next complete repository run built the Release solution with zero warnings/errors and passed kernel 44/44, planning 303/303, architecture 32/32, and integration 279/279 in 1.3127 hours. V328 backup/restore passed at 54 seconds. The wrapper then reached the UI hardening checks and failed because `tests/ui/browser-harness.mjs` naively split descendant selectors on whitespace inside quoted attribute values such as `[aria-label="Current shop-floor operator"]`. This was a test-harness bug, not a browser implementation failure. Archived the complete run at `.forge-logs/verify-integration-279-pass-ui-harness-failure.log` with its FAIL wrapper status so the original failure is retained.

Updated the fake-DOM selector parser to split comma groups and descendant chains only outside quoted/bracketed attribute values. After that repair, the exact three `verify.cmd` UI suites pass 34/34; QMS planning UI passes 16/16; together all 50 UI tests pass in `.forge-logs/v4-ui-all-final.log` and `.status` (PASS). All 62 JS/MJS files pass syntax checks (`.forge-logs/v4-js-syntax-final.status`), and `git diff --check` passes with only line-ending notices. `verify.status` still says FAIL because the canonical wrapper has not been rerun since the test-harness-only repair. Product source did not change after the 279/279 integration pass.

At this checkpoint the V4.2 product acceptance/release and QMS risk/objective/task/retention implementation is present but uncommitted, with its separate focused 31/31 gate recorded earlier. V4 overall remains incomplete. Preview `http://localhost:5080` is live from the preserved older `06f4045-presentations-sessions` snapshot and does not show the latest uncommitted UI. Continue remaining V4.1–V4.6 capability and A–L scenario gates; do not label the project complete from this checkpoint.

## V4 commercial property and review evidence — 2026-10-15

Continued the owner-authorized V4 effort. Added a PostgreSQL acceptance case for actual supplier-owned tool custody and preservation. It uses a real supplier, registered fixture, approved property agreement scoped to the actual work center/activity, actual tool movement/use events and measured 22 C environment reading. The tool-use attempt is explicitly rejected before preservation evidence exists without appending a tool event. The exact event shows return to the supplier, but recording PropertyReturn is rejected until an independent human OwnerAcceptance observation is recorded. The final case passes1/1 in Release: `.forge-logs/v44-supplier-property-custody-acceptance-retry5.log`.

The source-driven complaint/feedback workflow now asserts a real shipped-source traversal through production order, lot, inventory transactions, acceptance inspection and actual partial customer return. Its complaint keeps the inspection/release/return references, independently closes after disposition, and the associated customer-response proposal is approved for human-controlled use without sending it. Return cohort partial-consumption, exhausted scope and duplicate/concurrent retry are separately guarded by `ReturnAcceptanceTests`. A distinct post-delivery FieldFailure case passes1/1 in `.forge-logs/v44-post-delivery-field-failure-acceptance.log`, retaining actual shipment, acceptance inspection and release facts through independently approved investigation/disposition/closure. The combined commercial, QMS mapping and management-review regressions pass9/9 in `.forge-logs/v44-v46-commercial-review-final3.log` (3m22s). It covers the actual scoped provider evaluation and purchase gate, effective customer requirements through order release, complaint/feedback/communication, supplier property, post-delivery, edition mapping, and frozen review.

The new supplier-property test initially produced five preserved failures: tuple deconstruction compile error (`v44-supplier-property-custody-acceptance.log`), missing procurement authority (`...-retry.log`), invalid activity identifier (`...-retry2.log`), unresolved facility scope (`...-retry3.log`), and correct rejection of use before preservation (`...-retry4.log`). Each was corrected in the fixture/workflow; assertions were retained and the final rerun passes. The stricter complaint genealogy and supplier-transfer assertions were then rerun with the complete 8/8 commercial/review slice.

Current other evidence: focused Audit/CAPA gate27/27 (`.forge-logs/v45-audit-capa-final-gate.log`); QMS edition mapping1/1 (`.forge-logs/v46-edition-mapping-independent-gate.log`); all178 UI tests (`.forge-logs/v4-ui-all-v46-final.log`); migration idempotency and PostgreSQL restore11/11 against the observed107 migrations (`.forge-logs/v4-migration-107-idempotency-restore-final.log`). Migration-count expectations in `KernelRoundTripTests.cs` and `V328Tests.cs` now match107. `docs/v4/completion-checklist.md` and this handoff have been updated with focused evidence and remaining acceptance gates.

V4 is not complete. No current-source canonical `verify.cmd` run has passed after the latest V4 changes; the older 279/279 integration run is historical to an earlier source state and its wrapper failed on the then-unfixed UI harness. Remaining capability/scenario gates are listed in the checklist; notably V4.1 movement/retention, broad A–F, competence/document reuse, warranty/recall and complaint-linked CAPA/design cases, full G/H, complete KPI/dashboard/Analyst/notifications, J/K and all cross-cutting current-source regressions. Fresh full verification is pending. A fresh Release solution build passed with zero warnings/errors in 35.48 seconds (`.forge-logs/v4-current-release-build.log`). The local preview was refreshed from an isolated Release publish at `.forge-logs/preview-runtime/v4-current-20261015`; `http://localhost:5080` returns HTTP200. The old preview snapshot remains preserved. Startup logs a sandbox-denied shared Windows DataProtection-key path warning; the host still serves the page. Source remains uncommitted; no push, deployment, outbound message, purchase or certification claim was made.

## V4.1 scan-bound inventory movement — 2026-10-15

Added transaction-time scan-session binding for existing `PickMaterial` and `DeliverMaterial` commands. A submitted session must be current, authenticated, unexpired and under the current adopted execution policy. Its exact actual order and operation must match an active lot-tracked Stage/Kit task; its staged canonical material must match the task part/lot, and its staged and submitted location must match the task source on pick or destination on delivery. Existing owner inventory commands still create the only physical movement and ledger transaction. Operation start/report retains the stricter default: a pending task does not count as delivered material.

The database-backed test builds a separate released lot-tracked raw part/product/routing/order and scoped effective policy, then proves wrong location and wrong typed part rejection, actual pick and delivery, retained audit/event/correlation/selection evidence, exactly-once replay, start blocked before delivery and accepted after delivery. Focused test passes1/1 in `.forge-logs/v41-scan-material-movement-test-final.log`; the complete scan-session class plus the new movement test passes14/14 in `.forge-logs/v41-scan-session-and-movement-regression.log`. Earlier attempt logs remain separate: the first shared-fixture lot was explicitly not lot-tracked; the second fixture draft correctly hit separation of duties; the next exposed the terminal-versus-badge operator identity boundary; and one test observation initially queried only open tasks. All were corrected without relaxing checks.

`ScanSessionAuthority`, `ScanSessionService` and the transaction participant now permit an exact pending material task only during scan staging or the matching movement command. Production command validation keeps the strict delivered/net-issued rule. Current tree builds as part of the Release test command. `git diff --check` passes with working-copy line-ending notices only.

The same current-source scan regression exposed that the legacy no-session pick mismatch detail included submitted and expected scan text, which the shared failure audit retained. Changed this to a fixed generic location/lot mismatch message. The 14/14 rerun also posts a unique sensitive bad location through the legacy route and proves it is absent from both the HTTP response and `kernel.audit_log`. This keeps raw scan values unpersisted by default. Dedicated bounded attempt telemetry and a retention decision for any future raw payload remain open; audit history continues under the existing audit-record rules.

V4 remains incomplete. Dedicated bounded/minimized attempt telemetry, full current-source `verify.cmd`, V4.2–V4.6 remaining acceptance, and a refreshed preview from a new immutable runtime snapshot remain pending. The current port snapshot predates this source change. See `docs/v4/completion-checklist.md`; no commit or deployment was made.

## V4.1 bounded attempt telemetry and current combined scan regression — 2026-10-15

Added optional, minimized rejection-attempt telemetry for server scan sessions. Raw scan values are never persisted in this table; rows hold a generic validation code, session/version, command/outcome, audit/correlation IDs, authenticated actor, session operator, assurance summary, one-way workstation binding and actual resource reference. Telemetry is disabled by default and only enabled when configuration explicitly supplies a valid retention day count (1–3650) and per-session cap (1–1000). Successful scan acceptance remains in its existing immutable transaction evidence; age/cap pruning applies only to the separate attempt telemetry. A PostgreSQL advisory lock serializes a session's cap updates, and an identity sequence makes ordering deterministic when timestamps tie.

The `CommandPipeline` now creates its participant before source-authority resolution so an expired-session rejection can be recorded after the audited failure is committed. The callback still verifies the real owner principal and session, and it records only minimized safe fields. The focused telemetry regression proves invalid scan values are absent from the HTTP response, telemetry and evidence table text; cap keeps the newest two attempts; an expired-session attempt is recorded; and the configured age prunes prior attempt rows. First telemetry run failed due random UUID tie ordering; the next exposed expiry rejection occurring before participant creation. Both original failures are preserved as `.forge-logs/v41-scan-attempt-retention-focused-retry1.log` and `.forge-logs/v41-scan-attempt-retention-focused-retry2.log`. The corrected focused test passes1/1 in `.forge-logs/v41-scan-attempt-retention-focused-retry3.log`.

Added Kernel migration0018 and raised the registered migration assertions to108. Kernel round-trip/idempotency plus populated backup/restore pass11/11 in `.forge-logs/v4-scan-attempt-migration-restore-focused.log`. The combined current-source scan session, exact-lot movement and telemetry regression passes15/15 in `.forge-logs/v41-scan-combined-current-regression.log`. The checked-in preview is still an older immutable publish; no current-source full `verify.cmd` run, preview refresh, commit, push or deployment has occurred. V4 remains incomplete; follow `docs/v4/completion-checklist.md` for remaining V4.2–V4.6 capability and A–L scenario work.

## V4.4 design applicability, complaint CAPA linkage and warranty/recall review — 2026-10-15

Integrated exact design applicability into product acceptance without inventing a design policy for ungoverned parts. When an organization has effective DesignApplicability records for a part, an exact current part/revision determination is required. Approved applicable determinations require controlled evidence for planning, inputs, review, verification, validation, outputs and changes. The exact CommercialQuality owner resource and mixed current source reads are captured with acceptance, and the design fingerprint joins the frozen configuration fingerprint. Inspection, release and actual inventory consumption recheck current design applicability; the accepted product is blocked from consumption if the effective design decision is withdrawn. The current-source design acceptance passes within the12/12 combined ProductAcceptance/CommercialQuality regression.

Complaint closure now revisits each actual CAPA referenced by a Complaint `CorrectiveAction` observation. It serializes with governed CAPA changes, rechecks the current source and permission, requires the original CAPA core state to be independently Verified, and additionally requires a governed CAPA case to be Closed. The real API test proves an open CAPA and an implemented-but-unverified CAPA each block complaint closure, while independent verification allows it.

Added real shipment-bound Warranty and RecallReview cases. Each preserves the actual shipment, acceptance inspection and product-release decision source; independent review/approval is recorded, investigation cannot close the case before a human disposition, and the case closes only after that disposition. The test records no external notice and does not infer affected product. Existing FieldFailure, partial return, shipment complaint, and approved-unsent response tests remain in the same combined regression. ProductAcceptanceTests plus CommercialQualityTests pass12/12 in `.forge-logs/v4-acceptance-commercial-current-source-regression.log` (5m02s). Earlier failed compile/key/evidence drafts are kept separately in their `...focused.log` and `...retry1.log` files.

Updated `AI_HANDOFF.md`, `docs/v4/completion-checklist.md` and `docs/v4/implementation-plan.md`. V4 remains incomplete; competence/document reuse, broader governance, V4.2 A–F, full audit G/H, dashboard/Analyst/notifications, J/K and current-source full verification remain open. The local preview is stale; no commit, push, external communication or deployment occurred.

## V4 software completion and canonical verification — 2026-10-15

This entry supersedes earlier V4 checkpoints in this log that say the local implementation or full `verify.cmd` is pending. The user explicitly approved starting V4 after V3 and later directed continuing through completion; that direct request superseded the attached architecture-only review STOP for this project work.

The final current-source `verify.cmd` completed with PASS. It used .NET 10.0.401 and the local PostgreSQL configuration loaded in memory from `forge.local.cmd`; the file contents were not read or printed. Restore reported all projects up to date. Release build succeeded with 0 warnings and 0 errors in 1m26s. The complete gate passed 759 tests: Planning303/303, Kernel44/44, Architecture32/32, PostgreSQL Integration346/346, and UI hardening34/34. The integration phase took 1.7275 hours including disposable test-database cleanup. Authoritative artifacts are `.forge-logs/verify.log` and `.forge-logs/verify.status` (PASS).

The current-source suite includes V4.1 exact-lot scan-session movement and minimized attempt retention; V4.2 inspection/release quantities, trace investigations and calibration impact; V4.3 governance, risk, objective/dashboard, controlled change, improvement and retention; V4.4 customer/provider/property/post-delivery controls; V4.5 audit/CAPA; and V4.6 management review, edition mapping, notification access and Quality Analyst paths. The final QMS Analyst additions answer authorized questions about open audit findings, overdue unverified corrective actions, and completed quality objectives below target using complete authorized source cohorts. Earlier failed diagnostic logs remain preserved and are not the final result.

V4 local software implementation and acceptance are complete. The tree remains uncommitted; no commit, push, deployment, external communication, certification claim or paid purchase occurred. No usage reset was needed. The missing original BN/section54 example question list was not recovered; the user approved proceeding with available examples, so exact parity is not claimed.

External follow-up remains: authorized review of the licensed ISO text, actual organization scope/policy/owner decisions, production Nexus/Sentinel/email setup, physical scanners/printers/shared-terminal exercises, assistive-technology study and production disaster-recovery/accessibility validation. These are environment and human-approval boundaries; Forge tests do not certify conformity. The current disposition is in `docs/v4/completion-checklist.md`; Claude should treat it and the top checkpoint in `AI_HANDOFF.md` as current, with older dated entries as history. Do not repeat the full 1.7-hour test suite absent source changes or a concrete risk.

Refreshed the local preview from the verified Release binaries into the immutable `.forge-logs/preview-runtime/v4-complete-20261015` snapshot. Confirmed the listener process path is that snapshot and `http://localhost:5080/` responds HTTP200. The host logs an existing shared Windows DataProtection key-ring read warning while serving the UI; the shared key directory was not changed.

## Claude review of the V4 handoff: icons and UI gaps (2026-10-15)

**Agent:** Claude (Claude Opus 5.5), in a cloud session linked to this PC.

**Objective.** The user asked Claude to review Codex's V3/V4 handoff and add anything that was missed, especially missing icons and UI that could be implemented.

**Work completed.**
- Audited the nav icons in `app.js` against `wwwroot/img/icons/`.
  - Two entries referenced PNGs that did not exist: Notifications (`bell`) and Shift Continuity (`timeline`).
  - Eleven entries shared four icons, and Shipping borrowed `suppliers`.
- Drew 13 icons in the house style and wired them into the nav. `tools/make-nav-icons.py` regenerates them.
- Found that `pages/qms-planning.js` (V4.3) was built and tested but never imported, so users could not open it. Routed it as `#/qms-planning`.
- Compared all 647 API routes with the UI's fetch paths. The confirmed gaps are recorded as a prioritized backlog in `AI_HANDOFF.md`, under "Claude review of the V4 handoff". The largest are:
  - V4.2 trace investigations and measurement findings;
  - product acceptance and release;
  - workforce and competency administration;
  - safety rules;
  - security administration;
  - FAI recording.
- Recorded three other things the handoff missed:
  - the lower handoff sections are stale (V3.9 era);
  - V4 is still uncommitted;
  - `verify.cmd` runs only 3 of the 15 UI test files.

**Verification.**
- `node --check` passes on `app.js`.
- `node --test tests/ui/*.test.mjs` passes 180 of 180.
- No server code changed, so the C# suite was not rerun.
- The immutable preview snapshot does not include these UI changes.

**Files.**
- New: `src/Forge.Host/wwwroot/img/icons/icon-{bell,handover,horizon,integrity,qms,target,audit,review,commercial,scenarios,inbox,factory,truck}.png`.
- New: `tools/make-nav-icons.py`.
- Modified: `src/Forge.Host/wwwroot/js/app.js`, `AI_HANDOFF.md` and `AI_SESSION_LOG.md`.
- Backup: `.forge-logs/app.js.before-icons`.

Nothing was committed, pushed or deployed.

## Claude: implementing the UI backlog (2026-10-15)

**Agent:** Claude (Claude Opus 5.5), in a cloud session linked to this PC.

**Objective.** Continue from the handoff review and build the screens for APIs that had none.

**Work completed.** The details are in `AI_HANDOFF.md`, under "UI backlog built".
- New pages:
  - Investigations: V4.2 trace investigations and measurement findings.
  - Product Acceptance: criteria, inspection results sheet, exact-quantity release, look up, withdraw and consume.
  - Safety Rules.
  - Access Control: people, roles, programs, role policy, separation and thresholds.
- A Workforce "Manage people" panel for worker, presence, badge and qualification commands.
- New actions on existing screens:
  - Receiving: FAI decide and record, RTV credit, return preparation, and governed-restock fields;
  - Tools: move and record use;
  - Plant Setup: site closures;
  - Shipping: containers.
- Moved the role catalog to `roles.js` and added the three audit roles.
- Added four icons.
- `verify.cmd` now runs all UI test files.

**Verification.**
- All 18 UI test files pass (202 checks).
- Checked live on port 5090 against the local database as dev-admin, qm, qe and eng:
  - all pages rendered;
  - all icons loaded;
  - the only console errors were expected 403s;
  - no commands were submitted.
- The full `verify.cmd` was not rerun, because no server code changed.

**Open.**
- List endpoints for acceptance inspections and releases.
- GETs for site closures and for role policy state.
- Low-priority read-only endpoints.
- Grouping the 35-entry nav.

**Files.**
- New pages: `pages/{investigations,acceptance,safety-rules,access,workforce-admin}.js` and `roles.js`.
- Changed: `app.js`, `workforce.js`, `receiving.js`, `tools.js`, `plantsetup.js` and `shipping.js`.
- New icons: `icon-{acceptance,trace,safety,access}.png`.
- New tests: `tests/ui/{investigations,acceptance,admin-pages,screen-actions}.test.mjs`.
- Also changed: `verify.cmd`, `tools/make-nav-icons.py`, the handoff and this log.
- Added `.forge-logs/ui-check.cmd`, which is gitignored.

## Continuation: Claude UI handoff — 2026-10-16

**Agent:** Codex

### Objective

Continue the actionable items in Claude's latest V4 UI handoff, keeping source authorization intact and updating the handoff for the next developer.

### Changes

- Added bounded recent-inspection and product-release list reads. Each candidate is individually checked with the same current source, data marking, adopted-policy and historic-evidence access rules as a direct record read. The Product Acceptance History tab shows only authorized results and opens the existing detailed lookups.
- Rendered site closures in Plant Setup. The existing resources read already returned them, so this was a presentation fix rather than a duplicate server endpoint.
- Added `/api/kernel/access-policy`, protected by `kernel.access.manage`, to read active role grants and scopes, separation-policy states and configured approval thresholds. Access Control now displays current state instead of offering both separation switches regardless of state.
- Grouped navigation under Plan, Make, Quality and Admin.
- Confirmed that Inventory's released quantity is already sourced from the inventory ledger, and Shipping already uses released stock and compares it with demand. No derived duplicate release quantity was added.
- Updated this handoff and session log. No commit or deployment was made.

### Verification

- Release build of `Forge.Host`: passed with zero warnings and zero errors.
- PostgreSQL integration scenario `Real_thousand_unit_output_has_exact_920_release_and_80_segregated_hold_with_no_double_decision_consume_or_shipment`: passed, including assertions for both new list endpoints.
- PostgreSQL integration scenario `Denials_and_rule_violations_are_explained_and_audited`: passed, including active role/separation-policy response and denial without access-management authority.
- All UI files under `tests/ui/*.test.mjs`: 205 passed, zero failed, skipped, cancelled or todo. Output is retained in `.forge-logs/ui-followup-pass.log`.
- `verify.cmd` did not reach build/test because NuGet restore could not access `api.nuget.org` and restore warnings are errors. An offline restore with audit disabled also failed because two test dependencies were not cached (`Microsoft.Extensions.TimeProvider.Testing`, `NetArchTest.Rules`). `.forge-logs/verify.status` correctly remains `FAIL` for that restore attempt. The focused integration tests were run with `forge.local.cmd` loaded in-process; its contents were not printed or copied.

### Remaining

- Rerun full `verify.cmd` when NuGet restore is available.
- Lower-priority UI integrations remain for goal-risk, Analyst brief, per-machine readiness, reallocation preview, inventory selection, scan-acceptance lookup and current QMS-policy lookup. Signature verification already has an evidence-attachment action.
- The local preview on port 5080 is the immutable 2026-10-15 publish and was not refreshed.
- Licensed normative review, organization approval, real connector/device exercises and production validation remain external. Forge does not claim ISO certification or conformity from this local work.

## V5 master intake — queued pending V4 gate (historical state, 2026-10-16)

The owner supplied `C:\Users\wrest\Downloads\JIRSN_Forge_V5_Consolidated_Development_Master.md` and said V5 is for after V4 is completed. I reviewed its scope and copied the exact source into `docs/v5/JIRSN_Forge_V5_Consolidated_Development_Master.md` so the next developer can work from the project checkout.

The document covers evidence-based ERP/MRP market gaps, predictive supply intelligence, explainable inventory allocation and approved alternates, unique child labels with permanent parent genealogy, role-based readiness, MRB containment, adoption/integration, optional nonprofit feasibility, and required audit/demo/handoff outputs. Its embedded “start now” instruction is subordinate to the owner's sequencing request: V5 remains queued until the V4 software backlog and full verification gate recorded in `AI_HANDOFF.md` are complete. After that, begin with V5 repository/evidence analysis and a bounded-slice proposal; production code, schema or workflow work still needs the specific V5 owner approval required by the master.

No V5 code or research was started. No V4 status was changed by this document intake; the current V4 changes remain uncommitted, the lower-priority UI reads remain open, and the full suite still needs a successful rerun when package restore is available.

## V4 acceptance and V5 Phase 0/1 — 2026-10-16

**Agent:** Codex

### V4 completion

- The owner had asked to begin V5 after V4. The current full `verify.cmd` completed successfully, so V4's local software gate is now complete.
- `.forge-logs/verify.status`: `PASS`. `.forge-logs/verify.log`: .NET SDK 10.0.401, successful restore, Release solution build with 0 warnings/0 errors, Planning 303, Kernel 44, Architecture 32, PostgreSQL Integration 346 and UI hardening 212 across 24 files. Total: 937 passing checks, no failures.
- The seven lower-priority UI reads from Claude's handoff are now connected: goal risk, Analyst brief, per-machine readiness, workforce reallocation preview, eligible inventory selection, scan-acceptance lookup and current QMS policy. Earlier in this continuation the acceptance/release history reads, site closures display, access-policy read and nav grouping were also completed.
- Updated `docs/v4/completion-checklist.md` to distinguish the current 937-check gate from the historical 759-check run. External normative review, organization approval, real connector and device exercises, accessibility study and production recovery validation remain outside local software verification.

### V5 Phase 0/1 evidence

- Updated `AI_HANDOFF.md`; added `docs/v5/phase0-1-audit.md` and `docs/v5/V5_SESSION_HANDOFF.md`. Preserved the owner’s master exactly; source and project copy SHA-256 match `96E0A43DB167D4D2EDD7AC2FCC92AA30E272CFFBA7CDF3A2A6B0668044A1C4F2`.
- Repository audit confirms V3/V4 already own inventory identity, MRP/scheduling, approved alternates, readiness, NCR/MRB, genealogy, QMS and labels. Market documentation shows broad competitor support for finite scheduling, component tracking and parent/child lots. Practitioner pain evidence is limited and mixed, so no market-wide “competitors lack core manufacturing” claim is supported.
- Highest risk: transformation byproducts are linked to the production order but currently persist null exact source part/lot, whereas remnant creation requires a source lot. Composite `(part_number, lot_number)` is current inventory identity; barcode/label identity uniqueness remains a test question, not an assumed defect. See `InventoryService.Transformation.cs`, migrations `0009_material_identity.sql` and `0010_transformation.sql`, `V33Tests.cs`, and label lifecycle integration coverage.
- No production source, schema or workflow changes were made for V5. Working tree remains uncommitted, branch `main`, HEAD `38db86a953f4d73dad3590183341bcf35e14dedf`. `git diff --check` returned 0 with Git LF-to-CRLF notices.

### Next decision

The V5 master §§12–14 requires owner approval for one bounded production slice. Proposed V5-S1 is an Inventory-owned integration proof for split children, remnants, single-/multi-parent byproducts, child scan/label identity, reprint without duplicate stock, hold isolation and quantity conservation. If the tests confirm the byproduct parent edge is missing, authorize only the smallest owning Inventory correction plus migration/rollback notes and relevant tests. Do not begin allocation optimization, forecasting, new MRB work or external connectors in that slice. Await the owner's explicit approval before implementation.

## V5-S1 child identity and genealogy — 2026-10-16

**Agent:** Codex

**Authorization.** The owner replied “Approved please start” to the bounded V5-S1 proposal. The authorized work was the Inventory-owned split/remnant/byproduct child identity and genealogy proof, with the smallest Inventory correction if the parent-edge gap was confirmed, migration/rollback notes and focused/full acceptance checks. Allocation optimization, forecasting, new MRB work and external connectors stayed excluded.

**Implementation.** A test-first byproduct trace confirmed the gap: tracked byproducts had an order association but no explicit physical input parent. Added `TransformationParent` to transformation contracts, explicit parent validation to `InventoryService.Transformation.cs`, append-only persistence/loading in `PostgresInventoryStore.Transformation.cs`, additive migration `0020_transformation_line_parents.sql`, parent identity filtering, and parent-to-child edges in the existing Planning genealogy graph. Tracked/serialized byproducts now require real issued parent lots and positive parent quantities; attribution across outputs cannot exceed the recorded consumed quantity for that parent lot. These metadata do not alter stock reconciliation. Existing historical byproducts were not backfilled because their physical sources are unknown.

**Acceptance evidence.**

- `V32Tests` resolves three split child lots through exact canonical roll scan codes.
- `V33Tests` records a multi-parent byproduct, checks parent quantities and the retained order-to-output edge, rejects missing/unissued/over-attributed parents, and reconciles both source lots.
- `V41LabelLifecycleTests.Reprinting_a_child_label_keeps_the_same_lot_and_holding_one_child_leaves_its_sibling_released` proves one-parent child trace and persisted data, resolves both original/reprint QR artifacts to the same composite child identity, compares inventory ledger state before/after reprint, and proves a hold on one child leaves the sibling released.
- Combined focus: `dotnet test tests/Forge.Integration.Tests/Forge.Integration.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~V32Tests|FullyQualifiedName~V33Tests|FullyQualifiedName~V41LabelLifecycleTests"` passed 7/7.
- `node --test` over all `tests/ui/*.test.mjs` passed 212/212.
- `git diff --check` passed (exit 0); Git emitted only existing LF-to-CRLF notices for the mixed working tree.

**Full gate.** `verify.cmd` stopped at restore before build/test because `api.nuget.org` was blocked (`NU1900`/`NU1301`). A solution restore with `--ignore-failed-sources -p:NuGetAudit=false` also failed because `Microsoft.Extensions.TimeProvider.Testing` and `NetArchTest.Rules` are unavailable in the local package cache. `.forge-logs/verify.status` is `FAIL` for this incomplete full-gate attempt. The prior V4 937-check result is pre-V5 evidence, not a V5 full-suite pass.

**Handoff and migration.** Added `docs/v5/V5-S1-implementation-notes.md` with additive migration and rollback guidance; updated `docs/v5/V5_SESSION_HANDOFF.md`, the Phase 0/1 audit decision, `AI_HANDOFF.md` and this log. Re-run `verify.cmd` when NuGet restore is available. The working tree is uncommitted on `main` at `38db86a953f4d73dad3590183341bcf35e14dedf`. No commit, push, deployment or external communication was made. `forge.local.cmd` was only invoked in-process and its contents were not printed or copied.

## Claude: V5-S1 verification, gap fixes and running Forge (2026-10-16/17)

**Agent:** Claude (Claude Opus 5.5), in a cloud session linked to this PC.

**Objective.** The owner asked Claude to review Codex's V5 checkpoint, run any missing gaps, and get Forge up and running.

**Findings.**
- Codex's full-gate "NuGet unreachable" result came from its own sandbox blocking sockets. From the desktop, restore worked at once.
- No log survives for the 937-check V4 pass; `verify.log` had been overwritten.
- The V5-S1 order-page byproduct dialog could not supply the newly required parent lots.
- V5-S1 did not update the migration-count assertions (108 to 109).

**Work completed.**
- Ran the full `verify.cmd` from the desktop:
  - build clean;
  - Kernel 44, Planning 303 and Architecture 32 all passed;
  - integration 345/347, the two failures being the stale migration counts.
- Fixed the counts in `KernelRoundTripTests` and `V328Tests`.
- Reran them with a Release rebuild: 11/11 passed. All UI tests: 213/213 across 25 files.
- Added parent-lot entry to the byproduct dialog (`genealogy.js` `byproductParents`), with a new UI test.
- Started Forge on port 5080 from the tested Release build through `.forge-logs/run-forge-release.cmd`. That run applied migration 0020 to the local database, and a smoke check passed.

**Not done.**
- A single uninterrupted `verify.cmd` on the final tree.
- Diagnosing why `run-forge.cmd` (`dotnet run`) stalls after restore.
- Committing. Nothing has been committed since `38db86a`.

**Files.**
- Changed: `src/Forge.Host/wwwroot/js/genealogy.js`, `tests/Forge.Integration.Tests/KernelRoundTripTests.cs`, `tests/Forge.Integration.Tests/V328Tests.cs`, `AI_HANDOFF.md` and this log.
- New: `tests/ui/byproduct-parents.test.mjs`.
- Ignored logs and launchers in `.forge-logs/`: `rerun-fixed.*`, `run-forge-release.cmd`, `forge-5080.log` and the verify log copies.

## Claude: clean full gate and simulation amendment intake (2026-10-17)

**Agent:** Claude (Claude Opus 5.5), in a cloud session linked to this PC.

**Full gate.** Stopped Forge, ran `verify.cmd` from the desktop and got **PASS**:
- 939 checks in total: Kernel 44, Planning 303, Architecture 32, integration 347 and UI 213;
- a clean Release build;
- the log is in `.forge-logs/verify-pass-20261017-final.log`.

Forge was then restarted on port 5080 with `.forge-logs/run-forge-release.cmd`.

**Simulation amendment.** The owner supplied the "Full-System Digital Factory Simulation Amendment", saved verbatim in `docs/v5/`.
- Repository discovery and the proposed test architecture are written up in `docs/v5/V5-simulation-discovery-and-architecture.md`. It covers:
  - the module and API inventory;
  - the existing fixtures to reuse (`ForgeApp`, `PostgresFixture`, `OriginalReferenceFactory`, the planning engines and scenario service);
  - integration coverage by area;
  - the coverage matrix;
  - limits (62-day scenario horizon, no cost model, overtime not modeled, no device drivers);
  - phases A–D.
- The owner approved Phase A (test code only), 300 orders and 70 operators, a separate `simulate.cmd`, and quantities and time for economics.

**Files.** Changed: `AI_HANDOFF.md` and this log. New: the two `docs/v5` files.

## Claude: V5 simulation Phase A (2026-10-17)

**Agent:** Claude (Claude Opus 5.5), in a cloud session linked to this PC.

**Done.**
- Wrote `tests/Forge.Simulation.Tests` (engine, plus scenarios S09, S10 and S15, plus reproducibility).
- Every expected outcome comes from the seeded plan and the specification. Checked the routes and records against `ReceivingEndpoints.cs`, `QualityEndpoints.cs` and ADR 0035 before running.
- Added the project to `Forge.slnx`, added the `Category!=Simulation` filter to `verify.cmd`, and created `simulate.cmd`.
- The first build failed on CA1822 (`SimApi.SendAndLoseResponse`), which is now static. The second build was clean: 0 warnings, 0 errors.
- `simulate.cmd`: **PASS**, 10/10 tests and 351 expectations. See `docs/v5/V5-simulation-phaseA-results.md`.
- Forge was stopped for the run, because it shares the Release DLLs.

**Not done.** Phase B. A full `verify.cmd` rerun with the new filter.

**Files.**
- New: `tests/Forge.Simulation.Tests/**`, `simulate.cmd` and `docs/v5/V5-simulation-phaseA-results.md`.
- Changed: `Forge.slnx`, `verify.cmd`, `AI_HANDOFF.md` and this log.

## Claude: simulation controls, spec sources and full gate (2026-10-17)

**Agent:** Claude (Claude Opus 5.5), in a cloud session linked to this PC.

**Done.**
- Added negative controls (`Mutation`: HoldWrongLot, OracleOffByOne, SelfApproveUseAsIs, RetryWithFreshKey). All 4 were caught.
- Tagged each expectation with its basis and source (Spec, Invariant, Plan, Inferred, Code only). Results tables and `RunSummary` now list the rules that need an owner ruling.
- Reproducibility now runs for S09, S10 and S15. JSON escaping fixed. `simulate.cmd` clears old results before a run.
- `simulate.cmd`: **PASS**, 16/16 tests, log `.forge-logs/simulate-pass-20261017-controls.log`.
- `verify.cmd`: **PASS**, 939 checks, log `.forge-logs/verify-pass-20261017-sim-filter.log`.

**Not done.**
- Owner rulings on 1 code-only rule and 5 inferred rules.
- Phase B.
- Forge restart: computer access expired and needs re-approval.

**Files.**
- New: `Engine/Mutation.cs`, `Engine/RunSummary.cs`.
- Changed: the simulation engine and scenarios, `simulate.cmd`, `docs/v5/V5-simulation-phaseA-results.md`, `AI_HANDOFF.md` and this log.

## Claude: ADR 0055 rulings, split dispositions and adjustable RTV (2026-10-18)

**Agent:** Claude (Claude Opus 5.5), in a cloud session linked to this PC.

**Owner decisions.**
- Accepted the six rulings: idempotency; concurrent repeats; one disposition per quantity; one rejection per
  inspection; adjustable return to vendor with the remainder kept on hold; what counts as open.
- Approved implementing them.

**Done.**
- ADR 0055 and migration 0021 (`split_from` plus forward-only triggers).
- Service: `DispositionRejection.Quantity`, `ReduceReturnToVendor`, `SplitRejectionAsync`.
- New endpoint, receiving UI and README row.
- Tests: `V5RejectionSplitTests`, UI test, migration counts set to 110.
- Simulation: S11 and the `ReleaseReducedPart` control; rule sources re-cited.

**Gates.**
- Focused `.forge-logs/rerun-split.cmd`: PASS, 24/24 integration and 216/216 UI.
- `simulate.cmd`: PASS, 21/21.
- `verify.cmd`: PASS, 944.

Committed, then restarted Forge on port 5080.

**Next.** Phase B of the simulation.

## Claude: V5 simulation Phase B (2026-10-18)

**Agent:** Claude (Claude Opus 5.5), in a cloud session linked to this PC.

**Owner decision.** "Please start phase B" (test code only).

**Done.**
- Scenarios S01–S08, S11–S14, S16 and S17 (S10b already added with ADR 0055).
- KPI collector, impact matrix, defect register, scorecard with I and X scenarios.
- `simulate.cmd` honours `FORGE_SIM_FILTER`.
- Results in `docs/v5/V5-simulation-phaseB-results.md`.

**Oracle corrections (not Forge defects).** Presence expires after eight hours; a sole person shows as
`WouldBeUnstaffed`; the work-order list wraps each order; a foreign barcode answers 400; a second shipment of the
same lot may be refused at creation.

**Defect found.** D1: same-instant PM completion returns 500. Not fixed; it needs approval.

**Gates.**
- `simulate.cmd`: PASS, 51/51 (`.forge-logs/simulate-pass-20261018-phaseB.log`).
- `verify.cmd`: PASS, 944 (kernel 44, planning 303, architecture 32, integration 349, UI 216), 0-warning build (`.forge-logs/verify-pass-20261018-phaseB.log`).

Committed, then restarted Forge on port 5080.

**Next.** Owner decisions: fix D1; add the I and X scenarios the gate still lacks, or accept the gap and move to
Phase C (the full factory and 30/60/90-day runs).

## Claude: Phase B gate closed and ADR 0056 (2026-10-18, evening)

**Agent:** Claude (Claude Opus 5.5), in a cloud session linked to this PC.

**Owner decisions.** The same-instant maintenance completion is not a bug (work takes time); write the extra scenarios; record the rulings in an ADR.

**Done.**
- ADR 0056: capacity follows home work center; scanned-code answers; maintenance timing. S03, S04 and S14 cite it.
- Scenarios S18–S27; every amendment area now has an I and an X scenario.
- Oracle corrections: labour-hour rounding tolerance; presence by supervisor confirmation; gauges checked only for measured characteristics; SLITTER-B in S19; planned outages need a second planner's approval; report periods.

**Gates.**
- `simulate.cmd`: PASS, 71/71 tests (64 scenario runs, 2,466 expectations, 7/7 negative controls; 26 of 26 areas with an I and an X scenario).

Committed, then restarted Forge on port 5080.

**Next.** Phase C (the full factory and 30/60/90-day runs) when the owner approves it.

## Claude: parallel work plan and handoff for Codex UI/UX (2026-10-18, evening)

**Owner decision.** Codex starts the UI/UX upgrades from the Gemini review in parallel with Claude's simulation Phase C.

**Done.** Handoff section with file ownership, rules for sharing the PC (one heavy gate at a time, explicit staging,
Forge stop/restart, shared handoff files, git locks), corrections to the Gemini review (D1 ruled not a defect; scan
staging defaults need an owner ruling; verify claims first; §5.7 stays deferred; ADR 0056 §2 code answers), and the
suggested sprint order.

**Next.** Codex: Sprint 1. Claude: simulation Phase C.

## Codex: UI/UX Sprint 1 (2026-10-18, evening)

**Scope.** Applied Sprint 1 from `jirsn_forge_ui_ux_architecture_review.gemini.md`, following the scope split and
owner rulings in `AI_HANDOFF.md`. Frontend and UI tests only; no C#, schema or migration changes.

**Done.**
- Added missing shared layout styles for sections/cards/stacks/rows and related components; enabled wrapping segmented
  controls; styled info notices and selected table rows.
- Updated muted text, primary/selected orange, and success/error status colors to meet WCAG AA for normal text on the
  intended dark surfaces. Added computed contrast checks to `tests/ui/accessibility.test.mjs`.
- Connected `confirmWithReason()` to `manageDialogFocus()` and replaced the work inbox's native prompt with the shared
  accessible, audited-reason dialog.
- Changed QMS governance, planning, audits/CAPA and label dialogs to retain form values and show an inline warning on
  network/5xx uncertainty. The submit action stays disabled; closing the dialog blocks further mutations until a
  refresh reads the owning state.
- Updated prior uncertainty tests to assert this safer recovery flow and added a work inbox integration test.

**Checks.** `node --test tests/ui/*.test.mjs` passed, **221/221**. `node --check` passed for all modified JS modules.
`git diff --check` reported no whitespace errors. The full `verify.cmd` gate was not run because this was a static
UI-only slice and it requires stopping the shared Forge process on port 5080.

**Not started.** Sprint 2 repeater and entity lookup work; Sprint 3 kiosk changes other than the two V4.1 scan-policy
items awaiting an owner ruling; Sprint 4 navigation/object pages/performance work. Preserve the explicit scan-policy
and backend exclusions recorded in the handoff.
## Codex: Gemini UI/UX review, Sprints 1–4 (2026-10-18)

**Request.** Implement the UI/UX improvements from `jirsn_forge_ui_ux_architecture_review.gemini.md` and keep the
handoff useful for Claude. Follow the existing UI-only scope split and preserve Claude’s concurrent simulation work.

**Implemented.**
- Sprint 1: contrast and responsive shared styling, accessible dialog focus handling, uncertain-command form recovery,
  and audited work-inbox assignment reasons.
- Sprint 2: structured repeaters and searchable entity lookups for the reviewed forms, retaining API payload shapes
  and parser compatibility.
- Sprint 3: shop-floor kiosk behavior, idle countdown, touch numeric/PIN keypads, shift pass-on banner, and
  permission-gated work-instruction, material, NCR, and hold shortcuts.
- Sprint 4: permission-filtered collapsible navigation and show-all toggle; command-based search-cache invalidation;
  tabbed order/work-center pages; direct Receiving and Tools & Calibration shortcuts; lazy inventory tab loading;
  material-readiness KPI; auto-loaded 30-day QMS metrics; reusable table filtering and sorting.
- Added and updated UI regression tests. Left the owner-reserved V4.1 reason-prefill and scan-cadence inference
  unchanged. No backend or database changes.

**Validation.** `node --test tests/ui/*.test.mjs` passed **242/242**. `node --check` passed for modified frontend
modules and `git diff --check` found no whitespace errors. `verify.cmd` restored packages and built Release; Kernel
44/44, Planning 303/303, and Architecture 32/32 passed. Integration tests reported 90/349 passed and 259 failures
from PostgreSQL fixture startup (`57P03: the database system is in recovery mode`, followed by connection resets).
The focused integration retry hit a PostgreSQL connection reset as well. The Docker fallback was unavailable.
Latest `.forge-logs/verify.status`: `FAIL`; details in `.forge-logs/verify.log`. The shared port 5080 had no listener
before verification and remains stopped.

**Handoff.** Current status, scope, exclusions, and the exact remaining verification step are at the top of
`AI_HANDOFF.md`. Resume the full gate and live role smoke once PostgreSQL is healthy. Claude’s simulation Phase C is
independent and its test/code files remain untouched.

## Codex: PostgreSQL recovery check and UI preview verification (2026-10-18)

**Request.** Confirm whether the Gemini UI/UX upgrades are in place after PostgreSQL was expected to be available.

**State.** The reviewed Sprints 1–4 are implemented in the frontend and UI tests. The owner-reserved V4.1 reason
prefill and scan-cadence inference remain excluded pending the ruling already recorded in this handoff.

**Checks.** PostgreSQL service `postgresql-x64-18` is running and TCP `127.0.0.1:5432` is reachable. The focused
database-backed UI integration smoke test passed (1/1). The frontend suite passed (242/242). The live preview at
`http://127.0.0.1:5080/` returned HTTP 200. NuGet restore completed, but the full `verify.cmd` rerun failed during
Release build because the live Forge `.NET Host` process (PID 59208) has the shared output DLLs locked
(`MSB3027`/`MSB3021`); `.forge-logs/verify.status` is **FAIL**, with details in `.forge-logs/verify.log`. No process
was stopped so the owner can continue inspecting the preview. A no-build rerun of all non-simulation tests was also
started against the recovered database, then stopped before completion while integration fixtures were still running;
it has no full-suite result and is not counted as a pass.

**Next.** Once preview inspection is complete, stop Forge with its normal project procedure and rerun `verify.cmd`,
then perform role-based browser smoke. An isolated output directory is an alternative if the preview must stay up.


## Claude, 2026-10-19: simulation Phase C done (commit 07b0feb)

- Full factory (300 orders, 70 operators, 90 days; baseline + disrupted) passed every check. Results and the
  defect-register candidates: `docs/v5/V5-simulation-phaseC-results.md`; outputs 1-10 in
  `.forge-logs/simulation-factory/factory/seed-42/factory-report.md`.
- Run it with `simulate-factory.cmd` (about 40 minutes; stop Forge first). `simulate.cmd` now excludes `Mode=Factory`.
- Candidates for the owner (no production change made): UTC "today" in about 50 places; scheduler cannot plan past an
  inspection that has not happened; late sub-orders make orders unschedulable; schedule/floor material disagreements.
- PostgreSQL on the PC was reinitialised on 2026-10-18 (old data folder kept as `data-old`); the old live `forge`
  data is gone from the running server. Load the sample plant again with `seed-sample-plant.cmd` if needed.
- Not yet done: a regression run of `simulate.cmd` (Phases A/B) against the Phase C engine changes. Codex: these two
  notes files still contain your uncommitted edits; this section is appended, not committed.

## Codex: Sprint 5 UI polish (2026-10-19)

**Request and owner decision.** The owner supplied `jirsn_forge_ui_polish_sprint5.md` and directed work on the sprint.
For Sprint 5.2, the owner explicitly approved “prefilled reasons and cadence-based mode.” This supersedes the older
Sprint 1 handoff hold on those two UI behaviors.

**Implemented.**
- Repeater forms now render configured minimum rows when their value is empty, and `formModal` binds the newest open
  modal. Existing tests that filled the first row now use the seeded row rather than adding an empty second row.
- Scan staging defaults its start/reference audit reasons, provides one-tap reason alternatives, infers
  `KeyboardWedge` from at least four fast keystrokes (average interval ≤25 ms), and suggests `Manual` otherwise when
  allowed. The selector remains editable, and camera/adapter capture evidence is retained.
- Active navigation gradient now uses the approved dark contrast stops. QMS review dialogs use the common focus trap
  and preserve entries/disable retry after uncertain network or 5xx outcomes.
- Return-cohort lookup now renders a structured card and can prefill inspection with the cohort's subject and
  quantity. Shift continuity uses the people directory for assignee selection and readable acknowledgment names;
  the kiosk displays an inline acknowledgment action only after reading the handoff and confirming the current
  operator has not already acknowledged it.
- Added focused regression tests for all Sprint 5 behaviors. No C#, database, migration, or Claude simulation files
  were changed by this sprint.

**Checks.** `node --test tests/ui/*.test.mjs` passed **252/252**. `node --check` passed for all 63 JavaScript modules
under `src/Forge.Host/wwwroot/js`; `git diff --check` reported no whitespace errors (only Git LF-to-CRLF notices).
`verify.cmd` and database-backed tests were not rerun for this frontend-only sprint; earlier verification and the
historical 5080 preview check are recorded in `AI_HANDOFF.md`. Claude's Phase C work remains untouched, and its
Phases A/B regression remains pending.

## Codex: ADR 0057 UI screens (2026-10-19)

**Request.** Continue the ADR 0057 handoff and build the three UI screens for site time, worker moves/transfers, and
routing operation set-up families.

**Implemented.** Plant Setup now shows a ticking clock for every site in that site's configured IANA zone. Adding a
site requires an explicit zone and never guesses from the device; editing the zone remains an audited action. Manage
People now has separate temporary-move and permanent-transfer commands, movement/transfer history, move extend/renew/
early-end actions, home-site date defaults, and a worker-state refresh after changes. Engineering item details now
show routing operations by revision, let engineers create a draft when needed, add operations with optional machine
and qualification constraints and full timing, and record set-up family plus same-family minutes. Family values are
validated as a pair and released revisions are read-only. No backend, schema, database, or simulation files changed.

**Checks.** `node --test tests/ui/*.test.mjs` passed **256/256** and `node --check` passed for all **63** JS modules.
The focused tests cover site clock/timezone inputs, worker move/transfer payloads and site-local date defaults, and
routing validation/submission/display. Full `verify.cmd` was not rerun for this UI-only slice.

**Preview/handoff.** The prior `.forge-logs/check-adr57.log` had completed 353 integration tests with 347 passed and
6 failed; its status file still says `RUNNING integration` and `FAIL`. PostgreSQL was reachable and no test or host
process was active, so the documented Release preview was started. `http://localhost:5080/` returns HTTP 200 and the
host serves the latest `wwwroot` (PID 9352 at startup); the three updated JS modules also returned HTTP 200. Startup
applied the current workforce move, setup-family, inspection-time, and schedule-impact migrations to the local
`forge` database. The log records failed decryption of existing DPAPI-protected Data Protection keys and a denied
Windows Event Log write, but the host started. The full database suite was not rerun for this UI change. Details and
the existing integration failure are at the top of `AI_HANDOFF.md`.

## Codex: production-order workspace and all-pages UI review (2026-10-19)

**Request.** The owner asked for a UI pass across the routed pages and called out production work-order search, a specifics header, Operations and BOM tabs, and operation-level work-instruction printing.

**Implemented.** Production Order details now show a structured order-facts header and separate Operations and Bill of materials tabs. Each operation with applicable controlled Work Instructions offers a print packet using the exact document revision recorded at release (item-wide and operation-specific applicability are both handled). The packet previews browser-printable files and offers downloads for other file types while retaining the existing Engineering read authorization. The order list now has a dedicated search for number, part, revision and status, with a live result count even when only one row is present. The shared table enhancer gained an option to retain sortable headings while suppressing a duplicate generic filter on this page.

**All-page review.** Added `docs/ui/ui-ux-audit-2026-10-19.md`, covering every routed page module and listing prioritized follow-up areas for list search, long record hierarchy, narrow-screen tables, list feedback and accessibility regression coverage. This was a source review; a role-by-role visual sweep against seeded database records remains a separate validation step.

**Checks.** `node --test tests/ui/*.test.mjs` passed **262/262**. `node --check` passed for all **63** frontend modules. `git diff --check` reported no whitespace errors; Git printed its existing LF-to-CRLF working-copy warnings. No backend, schema, database, or simulation files were changed, and full `verify.cmd` was not run.

**Preview attempt.** PostgreSQL service `postgresql-x64-18` was Running and `127.0.0.1:5432` accepted TCP connections. `run-forge.cmd` failed its package restore because NuGet vulnerability-feed access returned `NU1900` as an error. The existing Release build was then started with restore disabled; it reached PostgreSQL and exited on `28P01` password authentication for user `forge`. No credentials were printed or changed. Port 5080 is not listening; no live browser verification was possible. The handoff records the recovery step.

## Claude: review fixes to the production-order workspace (2026-10-19)

The owner asked me to review Codex's production-order changes, fix what I found, and commit the UI work.

- Work-instruction printing now prints only revisions that apply: Current, Pinned, or LegitimatelySuperseded. For a
  Mismatch, Obsolete, NotReleased, Missing or RequiresReview document, the operation shows "not printable. See
  Documents" instead of a print button, so an outdated revision cannot reach the floor.
- After "Reissue documentation" the order view is rebuilt, so the print buttons use the reissued revisions
  (`appendApplicability` takes an optional `onChanged`).
- `tabifyPanels`: the back-link check compared `tagName` with lower-case `'a'`; it now uses `'A'`.
- `qms-review.test.mjs`: the meeting due-time assertion no longer depends on the machine's time zone.
- Checks: `node --check` on the changed modules; `node --test tests/ui/*.test.mjs` passed 263/263 in UTC and in
  America/New_York. There was still no live browser check (see Codex's preview note above).

## Codex: Engineering parent-part workspace (2026-10-20)

**Request.** The owner asked to implement a centralized parent-part view with BOM, operations and engineering revision together, so engineering can maintain the definition from one place.

**Implemented.** Added the searchable Engineering > Parent Parts workspace at `#/engineering/structure`, with a selected part/revision header and BOM and Operations tabs. Draft BOM lines can be added, updated and removed; routing operations can be added, updated and removed. The item detail page links into this workspace. Engineering revision is labelled separately from MRP low-level code. All writes use audited, permission-checked draft commands, update the revision contributor list, and preserve frozen released revisions and existing release separation.

**Backend.** Added atomic audited update operations for BOM lines and routing operations in `EngineeringService`, PostgreSQL storage and `/api/engineering` endpoints. No schema change was required. Added a real API integration test that changes both definitions, reads them back, releases the revision with an independent approver, then verifies both update paths reject edits to the released revision.

**Checks.** Host build passed with zero warnings/errors. All UI tests passed **266/266**. All JavaScript modules passed syntax checks, and `git diff --check` passed with only existing line-ending notices.

**Database test limitation.** The integration test assembly compiled, but `ParentPartEngineeringTests` did not execute: fixture startup requires Docker or `FORGE_TEST_POSTGRES`; Docker Desktop is not available in this session. The failure occurred before the test body. No app preview was started. Rerun the focused integration test when PostgreSQL fixture access is available.
## Codex: Sprint 6 MRP visual and planner UX polish (2026-10-20)

**Request.** The owner supplied `jirsn_forge_mrp_polish_sprint6.md` and asked me to take on Sprint 6.

**Implemented.** Added five-count MRP tabs, recommendation filters that preserve source indexes, shared table search
and sorting, Items filters, 14-period balance sparklines, inspection/hold pills, a time-phased netting summary and
color-coded projected balance bars. Exceptions now offer direct actions to Engineering, Procurement, alternates or a
past-due recommendation's Why drawer. Draft PO uses active suppliers, prioritizing only eligible approved sources;
when lookup data is missing it provides the brief's text-entry fallback. Demand `sourceKey` is a searchable lookup for
open work orders, approved project material lines and active tools, with direct custom `kind:key` entry still allowed.

The project exposes tools at `/api/resources/tools` (not the `/api/tools` path written in the Sprint 6 brief). Project
options are exact active material-line keys such as `project:CODE/3`; the project detail read is needed to avoid
offering invalid bare project IDs. No backend, database, schema or migration code was changed.

**Checks.** `node --test tests/ui/*.test.mjs` passed **272/272**; `node --check` passed for **57** JS files;
`git diff --check` reported no whitespace errors, with only standard LF-to-CRLF notices. A seeded-data live browser
check and database-backed suite were not run for this frontend-only sprint.

## Codex: Sprint 7 Commercial Parity & Market Leadership (2026-10-20)

**Request.** The owner supplied `jirsn_forge_commercial_parity_roadmap_sprint7.md` and asked me to start Sprint 7.

**Implemented.** Engineering now offers item creation, importable item and draft BOM CSVs, revision release/withdrawal,
and a cycle-guarded multi-level BOM tree with extended quantity and critical-path lead-time rollups. A reusable CSV
wizard supports items, BOMs, demand and suppliers, while enhanced tables export their currently visible rows. The
proposed-schedule Gantt has zoom windows, status filters and keyboard-operable operation bars; its drawer displays
schedule context and constraints, previews and confirms priority changes through the existing audited APIs, and links
to order detail and a prefilled recovery scenario. Scenario forms use selected directory/resource/item records.

Settings now combines audit-chain verification with an entity-filtered, searchable and CSV-exportable audit explorer.
Factory Horizon has role-aware action chips for approvals, downtime, constraints, shortfalls and work-inbox items.
Purchase order lines store optional unit prices and currencies; order totals display in Procurement and server-side
authorization resources carry the calculated amount/currency into V3.15 approval threshold checks. Unknown-price POs
do not satisfy a configured threshold, and a PO cannot mix currencies. Migration `0006_purchase_order_pricing.sql`
adds the pricing columns. Reports adds the Accounting, Receiving & Grant Reconciliation CSV/JSON feed.

The Sprint brief's shipment URL does not exist in this checkout; the feed uses `/api/inventory/shipments`. The
Horizon action bar uses `/api/work-inbox`, and priority preview uses `/api/orders/{id}/priority-preview`.

**Checks.** UI tests passed **274/274**; all **57** frontend JavaScript files passed `node --check`. Host build and
integration-test project build passed with zero warnings/errors. New focused UI assertions cover CSV parsing and
row-level failures, single-row table export, item make/buy class validation, Gantt inspection and scenario prefill,
and accounting export data.

**Remaining verification.** The updated PO approval integration test compiles. I attempted it, but fixture startup
failed before the test body because `FORGE_TEST_POSTGRES` is not configured and the Docker engine is unavailable.
No live database/browser smoke was performed, so migration `0006` has not been applied during this session. Rerun
the focused database test and smoke the import pages, audit explorer and Horizon against fixture-backed Forge data.

## Codex: Workforce Kanban and Plant Overview first implementation slice (2026-10-22)

**Request.** The owner supplied `Forge_Workforce_Kanban_Consolidated_Codex_Prompt.md` as a feature specification and
asked to implement the new enhancements.

**Architecture finding.** Forge already has current workforce assignment/presence, qualifications, effective safety
rules, active-operation readiness and the Factory Horizon overview. It does not have an approved effective-dated
person-to-work-center weekly roster, a persisted multi-move workforce scenario, or an atomic workforce schedule
commit. Treating current assignments or the approved production-operation schedule as a weekly employee roster would
be misleading; repeating immediate assignment commands would not be atomic. The audit and safety boundaries are in
`docs/workforce-kanban-architecture.md`.

**Implemented.** Added the supervisor overview entry and a responsive, accessible `#/workforce-kanban` current-state
board with filters, per-worker shift/presence/assignment/qualification details, drag/drop plus keyboard-select move
staging, explicit activity/machine selection, and browser-session draft save/reset. A user can request a sequential
read-only API preview over one consistent snapshot. Added workforce-scope authorization for every moved worker and
destination, a 100-move limit, duplicate-person rejection, a bulk qualification lookup, and cascade preview engine.
The page offers the existing private scenario route to model absence without writing attendance. It labels production
schedule facts as operation-only and calls out the missing approved employee roster. No operational writes or new
database schema were introduced.

**Tests.** UI suite: 278/278 passed. Planning `ReallocationTests`: 8/8 passed. All 58 frontend JavaScript modules
passed `node --check`. Host and integration-test project builds passed with zero warnings/errors using separate
temporary output because the live process holds normal host output files open. Added a real API integration test for
sequential cascade visibility and no live mutation; it compiles but was not executed because `FORGE_TEST_POSTGRES` is
unset and the Docker engine is unavailable. No live browser smoke was performed.

**Adjacent UI check.** Confirmed Forge Analyst's existing Clear questions button clears the question text, prior
answers and brief. Added a UI regression test showing that an in-flight answer cannot return after the clear action.

**Follow-up / limits.** This slice does not meet the full prompt acceptance. Build a versioned, effective-dated roster,
server-persisted private/shared drafts, full-schedule validation/approval, and an atomic idempotent concurrency-safe
commit with audit and projection refresh before exposing Commit Schedule. Extend scenario analysis to actual planning
engines only when the data supports those claims. No current database or Forge process was modified/restarted. A
feature branch could not be created because `.git` is read-only; pre-existing working changes were preserved.

## Codex: VNext Visual Plant Operations — Machine Readiness Kanban (2026-10-22)

**Request.** The owner provided `Forge_VNext_Visual_Plant_Operations_Kanban.md` as the next UI upgrade specification.

**Discovery.** Factory Horizon and the prior Workforce Kanban already cover plant status and scenario-only staffing.
Forge also provides operation-level readiness, active-work readiness, recorded downtime, production asset status,
maintenance work orders, and permissioned workforce/qualification data. Repository search found no live machine
telemetry source, Nexus/time-clock adapter, or configurable PM warning horizon. Existing Work Centers and Maintenance
pages offer readiness tables and workflow controls, but not a unified machine signal Kanban. The full matrix is
`docs/vnext-visual-kanban-implementation-matrix.md`.

**Implemented.** Added `#/machine-kanban` and links in Plant Overview and Workforce Kanban. The new read-only board
loads existing Forge endpoints, filters by work center/signal/machine/order, and prioritizes Blocked, Attention, Ready
and Unknown lanes. Cards show readiness evidence, order context, recorded downtime/restrictions, overdue PM/inspection
orders, authorized staff/qualification detail, source read time, and links to their owner pages. Hard blockers take
precedence and remain individually visible. Missing readiness is Unknown. Green indicates that an eligible operation
passed Forge readiness, never machine RUNNING or telemetry. No warning interval was invented, and the board cannot
write or clear maintenance, quality, safety, workforce or schedule state. Added a supervisor quick-start and Claude
review checklist.

**Tests.** All UI tests passed 282/282. `node --check` passed for all 59 JavaScript modules. Focused tests cover
simultaneous blockers and precedence, due PM Attention, passing operation readiness, unknown-on-failed-read, access
gate, evidence rendering, order links and signal filtering. `dotnet build src/Forge.Host/Forge.Host.csproj --no-restore
-p:BaseOutputPath=C:\Users\wrest\AppData\Local\Temp\ForgeVNextMachineBuild\ -v minimal` passed with zero warnings
and errors. `git diff --check` found no whitespace errors; Git printed its normal LF-to-CRLF notices. No database-backed
or live-browser check was performed.

**Deferred.** The full VNext mission is not complete. No telemetry/freshness event model, causal exception graph,
Nexus attendance, alternate module boards, user-tested workflow, workforce roster persistence/commit, or offline and
concurrency support was added. The page reads existing endpoints separately and explicitly says this is not an atomic
or real-time snapshot. No API, schema, migration or operational record was changed.

## Codex: Workforce and Machine Kanban density pass (2026-10-22)

**Request.** The owner asked to simplify Workforce Kanban to employee names, defer safety warnings until a proposal is
analyzed, collapse production schedule context, and show only machine name and location until a supervisor opens a
Machine Readiness card.

**Implemented.** Workforce cards now show employee names only by default; selecting one reveals proposal controls,
and drag-and-drop remains. Removed current presence, shift, qualification, assignment, lane-health and KPI details
from the default board. Removed preliminary readiness, downtime and qualification reads from the page. Safety and
staffing analysis still uses the existing read-only backend preview after the supervisor clicks Analyze, and its
comparative impacts are grouped by affected work center. Production schedule context and employee filters are
collapsed by default. Machine cards now show only machine name and work-center location in their summary; opening a
card reveals Forge readiness, maintenance, staff and order evidence. Removed machine KPIs and collapsed filters and
the signal guide while preserving the warning that Ready is not telemetry or a running state.

**Checks.** Full UI suite passed 282/282. Both changed JavaScript modules passed `node --check`; `git diff --check`
reported no whitespace errors. This frontend-only change did not run a live browser/PostgreSQL smoke or backend build.
No backend contract, schema or operational state changed.

## Codex: Visual plant-floor Workforce Kanban and official titles (2026-10-22)

**Request.** The owner asked to replace Workforce Kanban's sideways work-center lanes with a whole-plant map that uses
work-center pictures, shows employee titles, and lets plant setup staff arrange centers to resemble the facility. The
owner approved maintaining official titles in Forge.

**Implemented.** Replaced the horizontal Workforce Kanban lanes with a responsive floor map grouped by site safety
area. Each active work center reuses its configured photo and shows employees with current presence and their current
assignment. Employee cards show official job title and machine assignment when available. Workers without current
presence appear in a collapsed section. The view describes Forge's presence and assignment data and does not claim live
physical tracking. Setup-authorized users can drag tiles or use arrow controls, swap positions when dropping on an
occupied cell, and save the full map in one reason-required audited command. Layout fields are nullable and use a
four-column/100-row per-safety-area grid; first-use positions are filled deterministically.

Added a workforce-managed optional official title with audit reason/event. The workforce API includes `jobTitle`; the
Workforce Admin page adds a title action for `workforce.manage`. Added resource and workforce migrations `0006_floor_layout.sql`
and `0006_official_job_titles.sql`, the complete-map route `POST /api/resources/work-centers/layout`, and the
title route `POST /api/workforce/workers/{personId}/job-title`. Updated [ADR 0058](docs/adr/0058-workforce-visual-floor-map-and-official-titles.md)
and the README.

**Checks.** Full UI suite passed **283/283**. All **59** frontend JavaScript modules passed `node --check`;
`git diff --check` passed. Host and integration-test project build passed with **0 warnings and 0 errors** using an
isolated output path because an active Forge process held the normal outputs. Added integration assertions for
permissions, reasons, duplicate-cell refusal, persisted layout and title reads; the project compiles.

**Not run.** PostgreSQL-backed integration execution was unavailable: `FORGE_TEST_POSTGRES` is unset and the Docker
daemon pipe is missing. No migration was applied to a live database, no Forge process was restarted, and no browser
smoke was performed. Run the focused `PlantSetupTests` when a test PostgreSQL is reachable, then restart Forge through
its normal startup migration path and smoke the supervisor and setup-authorized views. No commit was made.

## Codex: Compact Workforce Kanban (2026-10-22)

**Request.** The owner said the visual plant map remained cumbersome and asked for a centered, screenshot-inspired
staffing view that fits almost all work centers on one screen. The attached redesign brief also calls for compact
Kanban columns while preserving staged-only staffing proposals.

**Implemented.** Added a compact default staffing board with a single horizontal row of work centers, small configured
photos, safety-area labels, headcounts, employee names, official titles, shift and on-site status. Additional centers
scroll inside the board. Name/title/ID search and area/shift filters stay visible; search updates only the board so its
input remains mounted. Production schedule context stays collapsed, while scenario actions use a condensed sticky bar.
The existing spatial Facility map remains available with one click for layout setup; unused empty map drop cells are
hidden outside edit mode. Existing drag-and-drop and selectable proposal controls remain in place.

**Limits.** This is a front-end density pass; no API, backend, migration, database, or live assignment change was made.
The page uses existing presence, assignment, shift and title data and existing read-only scenario analysis. It does
not yet bring in worker photos, qualifications, training restrictions, safety roles, confirmed absence categories,
machine readiness, required staffing, or computed downstream production impact. No schedule commit/approval flow or
safety override was introduced.

**Checks.** `node --test tests/ui/*.test.mjs` passed **283/283**. `node --check
src/Forge.Host/wwwroot/js/pages/workforce-kanban.js` and `git diff --check` passed. The local Forge tab opened at the
development sign-in screen, so browser visual review was not possible; the tab was restored to its prior `#/analyst`
URL without signing in. No screenshot was produced and no production data was touched.

**Follow-up.** Review desktop and tablet widths after signing in. Map any requested qualification, restriction,
machine-health or staffing-rule details to their real source APIs before adding them; preserve the current staged-only
proposal flow and facility-map view. No commit was made.

## Codex: 4×4 workforce board and image-backed plant map (2026-10-22)

**Request.** The owner asked for the plant-floor view to mix horizontal and vertical layout in a 4×4 setup, use the
attached plant-floor screenshot as the map reference, show seeded workers at locations, support employee photos, and
let management or administrators upload a site map and move work centers.

**Implemented.** Workforce Kanban now has a responsive 4-column staffing board (four rows show sixteen centers on a
desktop-width view) and a separate image-backed map. Cropped the supplied reference image into
`src/Forge.Host/wwwroot/img/plants/sample-plant-floor.png`; site maps without an upload are explicitly labeled as a
reference example. Map markers show on-site headcount and faces, expand to employee names/titles, and can be moved by
dragging or keyboard-accessible arrow controls. Saving sends a complete center layout with an audit reason. Added
upload/replace/remove for PNG/JPEG/WebP site maps (8 MiB cap), protected image reads and storage metadata, and
Facilities Manager upload/replace/remove of optional employee headshots through the narrow
`workforce.photos.manage` permission, with initials fallback. Existing sample seeding
now gives eight sample employees official titles and positions centers over the reference map; the seed workflow was
not executed against a live database.

Added the narrow `resources.floor-map.manage` permission and granted it to FacilitiesManager, ManufacturingEngineer
and SecurityAdministrator; FacilitiesManager also receives workforce read/supervise and photo-management access for
this page. Full plant setup and general workforce edits remain separately permissioned. Added resource map and workforce photo schema migrations
`0007`, kernel role migration `0020`, plus API/service wiring, ADR 0058 revisions and README usage notes.

**Checks.** `node --test tests/ui/*.test.mjs` passed **283/283**. JavaScript syntax checks and `git diff --check`
passed. Host and integration projects compiled into a temporary output directory with zero warnings/errors. The
focused PostgreSQL integration test compiled but could not start because `FORGE_TEST_POSTGRES` is unset and the Docker
engine pipe is unavailable. No application migration or live data write, restart or browser screenshot was performed.
No commit was made; existing worktree changes were preserved.

**Next.** Run `PlantVisualTests` with a reachable test PostgreSQL, allow the ordinary startup path to apply migrations,
then smoke-check the 4×4 board, FacilitiesManager map upload/reposition permissions and workforce-manager photo flow
after signing in. Visually review desktop and tablet widths. Run the sample seeder only against the intended development
database if seeded faces/titles/locations are needed.

## Workforce Kanban map access and shift control (2026-10-22)

The owner asked how to move work centers on the map after entering as `dev-admin`, and requested an easier shift
changer. Moved the existing shift filter into the Workforce Kanban page header and reduced the secondary filter row
to employee search and safety area. Added a focused regression test proving the selected shift filters workers while
the header control stays mounted; `node --test tests/ui/workforce-kanban.test.mjs` passed **4/4**.

The open preview was an older Release backend: the map-layout POST route returned 404 and `dev-admin` did not have
`resources.floor-map.manage`. A Release rebuild exposed that migration `0020_facilities_visual_map_access.sql`
granted `resources.floor-map.manage` and `workforce.photos.manage` before registering those permission names, causing
a permission-catalog foreign-key failure. Added both catalog entries before the grants. Rebuilt
`Forge.Host.csproj` in Release (**0 warnings, 0 errors**) and restarted through `.forge-logs/run-forge-release.cmd`.
Startup applied the migration; `/health/ready` returned `ready`, and the live `dev-admin` principal now has both
permissions. No map positions were edited and no sample seeder was run.

To arrange centers, open **Plant map** → **Arrange work centers**, move a pin by dragging it or using arrow controls,
then save and enter an audit reason. The current local database only has `ALL` (“Around the clock”), so separate
day/night options require creating shifts in Plant Setup and assigning employees to them. The app is running at
`http://localhost:5080`; refresh the open page to load the updated permission set. No commit was made.

## Workforce Kanban planning rail (2026-10-22)

The owner asked for a Workforce Kanban closer to the supplied planner reference, with movable workers on the right
and a place to see upcoming PTO, vacation, call-outs and other unavailability.

Moved unassigned, confirmed-on-site people into a right-side **Available to assign** rail alongside the 4-column
work-center grid. Staff can be dragged into a work center or selected to stage a destination; those actions remain
scenario proposals and do not write assignments. Added a **Callouts & upcoming time away** list for saved approved
PTO, call-outs, unavailable windows and training, plus a collapsed roster for people not confirmed on site without
an absence record. The page requests a 30-day range from new read-only `GET /api/workforce/unavailability`, protected
by `workforce.read` and capped to 90 days. Responsive CSS stacks the board at narrow widths.

Added UI coverage for the right rail and proposal flow, and extended the real-API Phase 4 PTO test. Focused UI tests
passed **5/5**; the full UI suite passed **285/285**. The Release host build succeeded with **0 warnings and 0
errors**. The Phase 4 integration test project compiled, but execution stopped before the test because PostgreSQL
was unavailable (`FORGE_TEST_POSTGRES` is unset and the Docker engine pipe is missing). The previously locked host
process was stopped so the build could replace its assembly, then Forge was restarted at `http://localhost:5080`.
`/health/ready` returned HTTP 200 and an authenticated read of the new unavailability endpoint also returned HTTP
200. The current local database has 9 registered workers and 7 work centers, with no time-away windows in the next
week. Refresh the Workforce Kanban page to load this version. No commit was made; the integration test remains
pending a reachable PostgreSQL test database.

## Pre-shift workforce planning (2026-10-22)

The owner asked to plan workforce before employees clock in. Added a **Pre-shift roster** containing active workers
who are not confirmed on site and have no current unavailability. Supervisors can drag them to a work center or
open a worker card and stage a destination. A staged person appears in that lane with a dashed card and the explicit
**Planned · not clocked in** label; the lane displays proposed count separately from people on site. Active absence
records remain in the time-away panel. The roster omits a worker after they are staged to a work center so the same
person is not duplicated in the rail and lane.

Updated the board summary to separate confirmed on-site and not-clocked-in counts. Clarified that the existing
read-only safety analysis evaluates active work at the current snapshot and does not certify future shift coverage;
the preview calls for another review when attendance and qualifications are current. No live assignment changes or
database schema changes were added.

**Checks:** focused Workforce Kanban tests **5/5**; full UI suite **285/285**; Release build **0 warnings, 0 errors**;
Forge restarted and `/health/ready` returned HTTP 200. No commit was made. The earlier real-API PTO test is still
unable to execute in this environment because the test PostgreSQL/Docker connection is unavailable.

## Specialized workforce placement safeguards (2026-10-22)

The owner asked for move restrictions in specialized work such as Slitting or Winding unless the worker is qualified
or has a trainer present first. Workforce Kanban now checks the machine activity's configured
`operatorQualifications` against current Competency records before allowing the scenario to be analyzed. A current
qualification permits independent placement. An unqualified worker must have a recorded `InTraining` record and a
currently qualified trainer already clocked in/confirmed at that exact work center. The trainer must hold both the
underlying qualification and a current `Trainer`-kind qualification whose prerequisite is that qualification; a
trainer who is only planned to arrive does not count. The trainer must be active, available, not a trainee and on the
same selected shift when the trainee has a shift. An unavailable, expired or inaccessible qualification feed blocks
specialized placements. Work-center activities without configured qualification requirements remain unaffected.

The Kanban displays the blocking reason on the worker card and above the board, and disables **Analyze impact** until
the missing training or trainer condition is fixed. This remains a staged, read-only planning flow and does not write
live assignments. Shop-floor execution checks remain authoritative at operation start. Added `competency.read` to the
Facilities Manager role and migration `0021_facilities_manager_competency_read.sql` so staffing managers can verify
qualification coverage.

Added UI regressions for an independently qualified worker, a supervised trainee with a qualified trainer already
at Slitting, a trainee blocked until the authorized trainer is present at Winding, and fail-closed behavior when the
planner lacks competency access. Focused Workforce Kanban tests passed **9/9**; the full UI suite passed **289/289**;
Release host build passed with **0 warnings and 0 errors**; `git diff --check` passed. Local Forge restarted at
`http://localhost:5080`, migration `0021_facilities_manager_competency_read` applied, and `/health/ready` returned
`ready`. No worker, qualification or assignment data changed. No commit was made.

## Supervisor Kanban Polish Sprint 8 (2026-10-22)

Implemented the requested frontend work from `jirsn_forge_supervisor_kanban_polish_sprint8.md`. Expanded employee
cards now offer permission-gated title editing, headshot upload/removal, an inline training and qualification drawer,
presence confirmation, callout/PTO recording and a reason-confirmed live-assignment action. Before a specialized
assignment is committed, the page rereads current workers and qualification coverage and applies the trainer guard
again; if the worker is in training, the saved assignment is marked `asTrainee`. The confirmation form only offers
presence types accepted by the existing API.

Added the `#/workforce?personId=...` profile deep link and auto-selection in Manage people. Document training uses
the active-person directory as a dropdown and falls back to manual ID entry if that directory read is unavailable.
Machine Readiness now displays blocked, attention, ready and unknown totals plus matching lane counts; totals track
filters. The plant-map off-site/unassigned accordion stays open when an expanded employee is rendered. Updated dark
status colors for WCAG contrast. No endpoint, C# backend or database changes were added for Sprint 8.

**Validation:** focused Sprint 8 tests passed; all UI tests passed **293/293**; `node --check` passed for the changed
modules; `git diff --check` passed. Tests verify reason-gated action presentation, current trainee marking,
profile selection, directory fallback, filtered KPIs and map accordion state. No employee, qualification, presence,
photo or assignment records were changed. Gemini has not rerun its review, so a 100/100 score is not yet verified.
The local server returned HTTP 200 from `/health/ready`; its updated Workforce Kanban, Machine Readiness, Workforce
and CSS assets also returned HTTP 200. No commit was made. Refresh the local pages for a visual check, then rerun
Gemini review against this implementation.

## Sprint 9 WhiskerWorks map, sign-in and operator kiosk (2026-10-23)

Implemented the UI portion of `jirsn_forge_whiskerworks_plant_map_and_kiosk_master_spec_sprint9.md` while preserving the existing Sprint 8 changes. Replaced the stacked login images with a responsive split-screen sign-in, one hawk icon plus CSS JIRSN/FORGE wordmark, a Development-only fictional/sample-data reminder, and a one-click `Launch Operator Kiosk` flow. The OIDC button remains organization sign-in.

The locked Shop Floor page now presents a large station HUD, a site-local clock using the app's configured time zone, scanner-focus feedback and an explicit safety/IPC gate reminder. Active production shows a segmented Good/Scrap progress strip and Target, Good, Scrap and Remaining tiles. One shared keypad switches between the Good and Scrap inputs and adds `+1`, `+5`, `+10`, `+25` and Fill remaining controls. Scrap reasons have touch tiles. The work queue is now touch-friendly job cards with scheduled-for-you state, machine selection and the existing server-side start/hold/clock-in rules. An authorized maintenance reporter sees `Report machine down`; its reason-required modal submits to the existing audited maintenance endpoint.

Created `src/Forge.Host/wwwroot/img/plants/sample-plant-floor.svg`, an architectural reference map without static status text, and switched the Workforce Kanban map to this file by default. Added deterministic coordinate defaults for the WhiskerWorks `WC-*` codes. The map derives machine status with the existing `deriveMachineCards()` function, reading readiness and maintenance endpoints according to role permissions; each center displays its worst machine signal, expanded centers list machine signals, and machine selection opens current readiness evidence, maintenance and staffing information. This uses Forge state and leaves unknown sources Gray/Unknown.

Added UI regressions for locked scanner state, active progress/shared keypad/reason tiles, job cards and the clock-in gate, downtime command payload/permission, and WhiskerWorks map signal provenance. The machine readiness drawer also shows required operator qualifications and offers a Stage action only for currently present workers with every required unexpired qualification; this selects the machine's exact activity and asset in a staged proposal. An extended regression verifies the proposal can be analyzed and that the action does not directly alter the live assignment. Updated an expired Workforce presence fixture from October 2026 to October 2030 so the on-site test remains deterministic relative to the current date.

**Checks:** `node --test tests/ui/*.test.mjs` passed **299/299**. `dotnet build src/Forge.Host/Forge.Host.csproj --configuration Release --no-restore` passed with **0 warnings and 0 errors**. `git diff --check` passed. The full `dotnet test Forge.slnx --configuration Release --no-restore` command was attempted: unit suites that do not need PostgreSQL passed (Kernel 44, Planning 321, Architecture 32), but 266 integration tests failed to initialize because the local Docker engine pipe is unavailable and `FORGE_TEST_POSTGRES` is unset. The solution run additionally reports an unresolved `TestSite` name in `tests/Forge.Integration.Tests/SlittingPlant.cs` when compiling `Forge.Simulation.Tests`. Local `/health/ready` and updated JS/CSS/SVG asset routes all returned HTTP 200. No database rows, badges, employee photos, assignments or production records were changed; no schema changes or migrations were added; no commit or screenshot was made.

The broad master specification is **partially complete**, not all 32 assertions complete. There is still no isolated `WW-DEMO`/`WW-P01` deterministic seed, no Demo Scenario Control Center, no `S01`–`S16` scenario engine/assertion set, no simulated clock/baseline/evidence export, no kiosk photo endpoint scoped to the current operator, and no demo badge feed. The legacy PNG with status artwork remains as an unused reference file; the page uses the new SVG. Map pan/zoom, composable layers and searchable cross-machine qualified coverage remain for follow-up. The selected-machine drawer does display required qualification codes and supports safe staging for present workers with current qualifications. These gaps are listed with status in the Sprint 9 section at the top of `AI_HANDOFF.md`. Do not reset or relabel the existing sample plant as WhiskerWorks; seed only a dedicated demo tenant after strict scope and reset-reason protections are implemented.

Follow-up read-only discovery of `GET /api/resources/` on the running preview returned only `PLANT1` (“Sample Plant”). The current API surface does not expose a separate `WW-DEMO` tenant identity or a verified tenant-wide reset boundary. No WhiskerWorks site or other records were written. Before adding a seed/reset operation, establish and test tenant isolation across every affected Forge domain; a site-code check alone is not evidence that orders, inventory, workforce and quality records are isolated.

## Redtail hero and WhiskerWorks foundation continuation (2026-10-23)

The owner asked to restore the original redtail hawk to the opening screen and continue WhiskerWorks. Restored `bg-hero-hawk.jpg` as a full showcase image in the split sign-in, with responsive sizing and descriptive alt text. Added a UI regression proving the image is referenced with alt text and cover styling.

Added the fictional WhiskerWorks phase-one manifest and audited-API seed CLI. The manifest declares 15 safety areas, 16 work centers mapped to unique 4×4 cells, 24 machines, 52 workers, three shifts, five product-family identifiers for later work, 22 named qualification types with prerequisites, and two independently approved two-person safety rules. Each center has two day-shift workers in the preview roster; the Fabrication weld pair has the join qualification, the Heavy Assembly day-shift pair has the heavy-assembly qualification, and CNC Lathe has a qualified trainer present. Fixed an initial roster mismatch where two heavy-assembly workers referenced standard-assembly machines.

The seed CLI defaults to a no-network dry run. Its apply path requires an explicit localhost base, administrator subject and a separate preflight reader with engineering, orders, inventory, procurement, quality, maintenance, competency and safety-rule read permissions. It preflights global stores for foreign site/catalog/operations data, registers demo principals before commands use them, performs audited API writes only, and has no reset/delete command. The tool was not applied because the local database still contains `PLANT1` and the API reads are not tenant-scoped. Continue only against a separate empty local Forge database; this CLI's empty-database guard does not itself prove tenant isolation. No database rows, identities, credentials, badges, qualifications or assignments were written.

**Validation:** `node --check` passed for both seed modules; manifest/dry-run tests passed **6/6**; accessibility tests passed **20/20**; `git diff --check` passed. The complete UI suite passed **306/306**. The preview health route, sign-in page, hawk image, stylesheet and app bundle all returned HTTP 200. The apply preflight was exercised with `--apply`, `dev-admin`, and the broad-read `eng` identity; it completed all reads and refused on the existing `PLANT1` site before any write. No host restart or database row change was made in this continuation. The rest of Sprint 9 remains open: products and BOMs/routings, suppliers/customers, sales and work orders, inventory lots, maintenance/quality evidence, 16 scenario controls/assertions, simulated clock/baseline/evidence export, and tenant-wide isolation.

**Spec reconciliation note:** The seed preserves all 22 named qualification gates because the source summary's target of 18 conflicts with its more detailed matrix. Forge's prerequisite contract currently requires every listed prerequisite, so it cannot yet express the CNC Setup requirement of either Mill or Lathe authorization; that alternate path remains open. Fourteen fixed support stations are in the manifest, but are not seeded as commissioned machines in this phase.

## Post-Sprint-9 Inventory, MRB, and Work-Order Upgrade Audit (2026-10-23)

Reviewed `C:/Users/wrest/Downloads/Forge_Post_Sprint_9_Inventory_MRB_Work_Order_Upgrades.md` as the product brief. Completed Phase 0 static review and wrote `docs/post-sprint-9-inventory-mrb-work-order-gap-analysis.md`; added its current status and next gate to `AI_HANDOFF.md`. No application code, database, or seed data was changed, and all existing uncommitted Sprint 8/9 work was preserved.

The audit found strong existing foundations in the append-only inventory ledger and stock dimensions, kernel holds and transactional audit/outbox/idempotency, engineering BOMs/routings, released production-order snapshots, NCR/rework/CAPA/investigations, and maintenance work orders. The principal gaps are physical stock reservations/ATP, one quantity-scoped containment and disposition flow across existing quality/hold/inventory records, versioned amendments and linked rework orders, a part-centered inventory workspace, and a consistently demonstrated site boundary. The handoff still marks Sprint 9 partial and unapplied, so post-Sprint-9 feature implementation remains pending its tested isolated-database baseline.

**Validation:** `git diff --check` passed (Git reported only its existing LF/CRLF normalization warnings). This was a documentation-only audit; no runtime checks or migrations were run. No commit was made.

## Sprint 9 implementation continuation (2026-10-23)

The owner directed continuation of Sprint 9 and asked to defer testing until implementation is complete. No build or test was run in this continuation.

Extended the WhiskerWorks data definition with the five master-spec product names and a shared fictional catalog: 95 components plus five finished goods, 26 complete make-item BOMs/routings, ten fictional suppliers and customer identities, 28 deterministic demand lines, and 42 deterministic production-order definitions. Added a separate dry-run-first `tools/seed-whiskerworks-commercial.mjs` that requires explicit local target and identities, validates the existing WhiskerWorks foundation and empty/demo-only data scope, versions command idempotency keys, and assigns production orders to a `WW-DEMO` program. It also seeds eight inventory locations, bought-part receipt lots (including the 250 m sisal and named motor lots), approved audited opening-balance adjustments for make subassemblies, 24 active machine assets with PM plans, three current gauges, five final inspection plans, and three released internal-document records with plain-text files explicitly marked as fictional non-operating placeholders. Added distinct inventory adjustment approval authority to the demo setup. No seeder was applied; `PLANT1` remains untouched.

Added a single 16-scenario catalog module shared by the seed tooling and Scenarios page. The page detects `WW-P01`, exposes four private preview entry points for existing planning scenario kinds, and provides a current authorized read-snapshot baseline, count comparison, and JSON export. Evidence omits source records and reports sources that are unavailable. It explicitly says the previews do not inject events and that the clock is not simulated.

Open work remains substantial: authoritative event injection and assertions for S01–S16, Start Shift, a site-scoped simulated clock, a persisted baseline, reset with tenant-wide transaction guarantees, the remaining preview mappings, support-station commissioning, and the CNC either/or prerequisite remain incomplete. The new API call sequences have not been exercised; their integration, expected counts and workflow outcomes must be verified only after implementation is complete, per the owner's instruction. No database writes, commit, host restart, or screenshots occurred.

## Sprint 9 scenario action continuation (2026-10-23)

Continued the implementation while honoring the request to defer builds and tests. Added real, reason-required, idempotent scenario controls for the supported S02/S03/S04/S06/S08/S16 source actions: deterministic Day Shift roster handling, callout and explicit presence-end commands, CNC-01 unplanned downtime, exact motor-lot inventory hold, and the 250 m sisal split plus 5 m scrap ledger flow. S16 runs the S02/S04/S06 source commands in sequence and keeps each completed step in Forge if a later action fails. The UI requires the visible database to contain only WW-P01 and only WhiskerWorks item codes before enabling source-changing controls; that client check does not establish server-side tenant isolation.

Extended the evidence export to include current downtime, holds, unavailability, stock positions and sisal inventory transactions, with explicit checks for the controls the UI can substantiate. Added tested-later pure helper coverage for deterministic roster selection, safe sisal replay states, and exact child/split/scrap reconciliation. Corrected the commercial order seeder to reuse exact WW-DEMO signatures and resume the intended Planned → Released → Held transition on rerun instead of issuing duplicate create commands. No database/preview state was touched, no seeder was applied, and no build or test was run. The complete 16-scenario execution/assertion matrix, isolated site clock, server baseline/reset and remaining unsupported source events remain open; see the top Sprint 9 entry in `AI_HANDOFF.md`.

## Sprint 9 implementation continuation — safety evidence and scope (2026-10-23)

Continued implementation without running tests, builds or database operations, per the owner's request. The ASM-03 scenario now reads actual qualification records and only records a callout when exactly two active, current, qualified Day Shift workers are present on the machine. Its evidence export checks the source CallOut window and current qualified staffing. The sisal evidence helper also verifies each released child lot is at `WW-RAW`, in addition to reconciling the 245 m split, 5 m scrap and zero parent balance.

The Scenario Center now describes its 16 rows as definitions, not passed acceptance tests, and labels each row as an audited source command, private what-if preview, existing workflow requiring manual evidence, or unavailable. It includes a read-only Machine Readiness path for the baseline. Source controls require the visible WW-P01 site, 100 WhiskerWorks items, 16 work centers, 24 machines and 52-worker foundation shape. That client check remains defense-in-depth only; it cannot replace backend tenant scoping. Added deferred regressions for the qualification/presence gate and raw-location ledger proof.

Implemented a scoped operator-photo read path for the Shop Floor kiosk. `POST /api/shopfloor/worker-photo` accepts the current scanned badge and returns only that holder's protected image under the existing `ShopFloorRead` permission; the terminal account does not gain broad `workforce.read`. The UI fetches the image without putting a badge in the URL, shows it with the operator's home work center and readiness-gate reminder before start and during active production, keeps the initials avatar when no photo exists, and revokes its object URL on user switch, lock, or page disposal. Added deferred kiosk UI regressions for image loading and the no-photo fallback. No seed was applied; the running preview remains untouched. No tests/builds were run, as requested; implementation and source APIs still need the deferred verification.

## Sprint 9 implementation continuation — sensor shortage source action (2026-10-23)

Added S07 as an audited Procurement source action. It deterministically selects the earliest visible approved/open `WW-SENS-PROX-05` PO line with remaining quantity, advances its promised date by four calendar days through the existing purchase-line date command, and requires both Purchasing read/manage permissions plus the demo-shape guard and reason modal. It refuses when no line is eligible and prevents a second line extension after an S07 entry is recorded in the browser session. The evidence snapshot now reports sensor PO line status/date, and the S07 export assertion checks the exact recorded line and target date. The UI explicitly says that this source date change does not prove supplier correspondence or an MRP shortage. Added deferred helper coverage for deterministic line selection, eligibility and calendar-boundary date arithmetic. Updated `AI_HANDOFF.md`; S07 supplier correspondence and downstream MRP-shortage validation remain open. No tests/builds, server restarts, seeding, or database writes were run; the user's test deferral remains in effect.

## Sprint 9 implementation continuation — S01 baseline evidence (2026-10-23)

Added an S01 source-snapshot evaluator to the Scenario Center. A baseline capture now evaluates four explicit claims: all 24 active machines have Active asset records and a Ready Forge signal; no blocking Safety/Quality readiness dimensions or active holds are visible; every open operation on released orders has an executable readiness answer; and every active Day Shift worker has current confirmed presence without an overlapping unavailability window. It reports missing source permissions as Indeterminate and keeps absence details to worker IDs, not private reasons. The evidence export includes the baseline and current-state check results, with a note that this is partial S01 evidence, not a complete S01–S16 suite. Added deferred pass/fail/indeterminate helper coverage. No tests/builds, server restart, database write or seed apply was run.

## Sprint 9 implementation continuation — recovery action board (2026-10-23)

Extended the existing Shift Continuity actions with optional due dates and dependencies on existing actions from the same handoff and program scope. Forge refuses transitions into InProgress or Completed while a prerequisite action remains open. Completing an action now requires evidence; the evidence is persisted and the audit change records the full before/after action state. The Shift Continuity UI now presents these fields as a recovery action board, shows overdue-date input/dependencies/completion evidence, and captures completion evidence in a reasoned form. Added a deferred integration regression for dependency blocking, due-date persistence, and evidence retention; the migration is additive (`execution` migration 0012). This remains a manual workflow rather than an automated S14 scenario injector/assertion. No migration was applied and no tests/builds were run.

## Sprint 9 implementation continuation — fixed support-station assets (2026-10-23)

Extended the commercial seed to register and commission the 14 manifest support stations as non-production Forge assets at their owning work centers. The records are typed as Building, Utility, TestEquipment, Tooling or Other; they keep `MachineCode` null so they do not create capacity or qualify for machine-specific routing. The seed preflight now allows only the declared 24 machines and 14 fixed stations, verifies existing fixed-asset definitions before reuse, and preserves fail-closed behavior without overwriting mismatches. Added deferred manifest regression coverage for unique station IDs, valid work-center ownership and non-machine typing. Updated `AI_HANDOFF.md` to reflect the new source capability. No seeder, tests, build, migration, preview or database operation was run; support-asset state remains unapplied until an isolated demo database is available.

Extended the commercial seed with one approved, open, fictional `WW-SENS-PROX-05` supplier line and a separately approved manufacturer part for S07. It proposes the fictional MPN as the engineer and approves it as the engineering approver, creates the approved source as the purchasing approver, drafts the PO as the buyer, then approves it as the independent purchasing manager. Retry discovers the exact existing records, accepts only the original or four-day-delayed date, and refuses conflicting/duplicate records before writes. The same preflight rejects foreign-part purchase orders and supplier/source mismatches. Updated dry-run and handoff descriptions. This makes the S07 audited promise-date control usable on a properly seeded isolated demo database; it still does not establish supplier correspondence or guarantee an MRP shortage. No tests/builds or database writes were run.

## Sprint 9 implementation continuation — CNC prerequisite alternatives and gauge expiry action (2026-10-23)

Implemented grouped OR-prerequisites in `Forge.Competency`: definitions persist as named groups, qualification-type reads return the groups, cycle checks include their edges, mandatory prerequisites remain AND, and certification accepts any one current credential in each alternative group. Added an audited, permission-gated API command and additive migration. The WhiskerWorks foundation defines CNC setup as requiring Mill **or** Lathe authorization. Its seed now validates and appends that group on a compatible rerun, while refusing mismatched or unexpected existing group definitions before the seed begins writes. Added a deferred integration test for a Mill-only operator succeeding, an operator with neither credential being rejected, the API contract, and cycle protection.

Added an S11 Scenario Center action that posts dated calibration events through the existing audited Quality command for the fictional torque and optical gauges. It calculates yesterday's due date relative to the WW-P01 site date, only updates gauges that are not already expired, and keeps historical calibration events intact. The scenario records an evidence index for the action. This exercises existing expiry/readiness behavior without simulating time; clock-driven timing and automated acceptance remain open.

No tests, build, migration, seeding, database write, host restart, or commit was performed. The user asked to defer the test pass until implementation is complete. The updated handoff lists the remaining S01/S05/S07/S09/S11–S16 work and keeps the current `PLANT1` preview untouched.

## Sprint 9 implementation continuation — CNC setup authorization and S07 MRP evidence (2026-10-23)

Connected CNC setup to protected `CNC_SETUP` machine activity profiles on CNC-01 and CNC-02 without adding machine assets or capacity. Added `QUAL-CNC-TOOLPATH` for Toolpath / First-Article Verification as a mandatory prerequisite to `QUAL-CNC-SETUP`, which still requires either current `QUAL-CNC-MILL` or `QUAL-CNC-LATHE`. The WhiskerWorks foundation assigns this full fictional chain to one mill setup lead. The seed adds the new profiles and prerequisite on compatible existing demo records, but refuses unknown or conflicting profiles before applying writes. Work-center activity entries are deduplicated. The workforce map's qualified-worker picker now follows the OR semantics used by the assignment service.

Added deferred integration coverage for direct mill-to-lathe assignment rejection, setup denial before setup credentials exist, and successful CNC setup assignment after Toolpath plus Mill certification. S07's authorized source snapshot now reads live MRP for `WW-SENS-PROX-05`, and its export assertion checks that the exact delayed PO receipt and an earlier requirement appear with a `RescheduleIn` exception naming the same PO line. This is evidence instrumentation only; the result remains unexecuted and does not claim broader bearing shortage propagation.

No tests, builds, migrations, seed applies, database writes, preview restarts, or commits were performed. Testing remains deferred until implementation is complete, and `PLANT1` remains untouched.
## Sprint 9 implementation continuation — S12 scanner / label outage rehearsal (2026-10-23)

Added a dedicated WW-P01 S12 control in the Scenario Center. It arms only when the existing demo-shape and workforce-supervision gates pass, records a browser-session evidence marker, and opens the Shop Floor rehearsal. The kiosk now supports separate simulated barcode-staging and label-printer 503 outcomes, preserves entered demo fields in `sessionStorage`, locks its local terminal state after each simulated outage, and prevents duplicate attempts. A label print remains `PendingReconciliation` until a supervisor with `workforce.supervise` and a Forge person identity records the observed outcome and a note. Closing the rehearsal requires both flows to be completed.

The rehearsal contacts no scanner or printer and issues no source-changing API command; the normal kiosk may still perform read-only data loads. It creates no Forge audit or production record. It demonstrates the fail-closed and reconciliation UI only; real device fault injection and authoritative server audit remain open. Evidence export now reports the local rehearsal state while explicitly marking it non-authoritative. Added unrun state-machine regression coverage for preserved drafts, one-shot failures, required signoff, and closeout.

No tests, builds, migrations, seed applies, database writes, host restarts, or commits were performed. The owner asked to defer testing until implementation is complete; `PLANT1` remains untouched.
## Sprint 10 start — workforce intelligence discovery and first UI slice (2026-10-23)

Read `Forge_Sprint_10_Training_Performance_Workforce_Intelligence.md` as the implementation specification and completed the required static architecture gap analysis in `docs/sprint-10-training-performance-workforce-intelligence-gap-analysis.md`. Confirmed the project already has canonical qualifications, supervised operation training, labor intervals, production reports, shift calendars, readiness/capacity, MRB, the inventory ledger, and material tasks. The material gaps and proposed ownership boundaries are documented before adding feature stores.

Added a source-backed Training & Performance Command Center section to Workforce with configurable 30/60/90-day qualification windows, shift/work-center filters, expiring and expired qualification counts, machine/shift gaps, existing readiness blockers, recorded operation labor and quantity context, and explicit `N/A` for attendance reliability and labor efficiency while prerequisites are missing. The view uses existing read APIs and keeps absence reasons out of the UI. Added unit/UI regression coverage for expiration, training-vs-authorization, shift-scoped coverage, program-visible blockers, evidence gaps, and privacy handling.

No tests, builds, migrations, database writes, server restarts, seed applies, or commits were performed. This is an incomplete Sprint 10 slice; Sprint 9 remains incomplete and the current `PLANT1` preview remains untouched. Continue with additive attendance/labor evidence and approval workflows, then the operator scrap/material request, stockroom notification, and verified custody vertical slices before the deferred full validation and commits.
## Sprint 10 implementation completion pass — 2026-10-23

Continued the approved Sprint 10 work after the owner requested implementation and commits through the remaining scope. Added the source-backed Workforce Command Center, separate training course/practical assessment/machine authorization records, effective-dated attendance policies and opportunities, independent attendance review/corrections, development nomination workflow, and operation labor metrics with evidence-based N/A when accepted-output basis is missing. Added the Workforce attendance period selector for calendar quarter, month-to-date, rolling 90 days, and year-to-date with visible scheduled/eligible/on-time/late/callout/no-show/excluded/pending counts.

Completed the material-control vertical slice against canonical Forge Quality and Inventory systems: suspect scrap is distinct from disposition; replacement requests are one-to-one linked; released-order staging requests cover only remaining BOM demand; additional material stays approval-gated and does not affect BOM readiness; partial picks remain requestable; two-person handoff uses badge/PIN receiver verification; custody exceptions append audit evidence; and Inventory now exposes the custody history. Material and quality disruptions link from the Workforce `!` feed. Default material notifications are in-app through the existing durable, permission-checked outbox. Added tests for demand boundaries, excluded additional material, attendance counters/period controls, and custody-history rendering.

No tests or builds had been run when this entry was written; the user's sequencing request was to test after implementation was finished. The implementation report records limits still requiring isolated scenario replay: ranked what-if recovery, partial receipt/rejection and return/reversal custody workflows, configured email delivery, expanded Sprint 10 WhiskerWorks fixtures, and accepted-output labor standards. The existing Sprint 9 foundation and current `PLANT1` preview were not seeded or mutated. Verification and commit are the next actions.

## Sprint 10 verification pass — 2026-10-23

Ran validation after the implementation pass. `node --test tests/ui/*.test.mjs` passed 335/335. `dotnet build src/Forge.Host/Forge.Host.csproj --no-restore -c Release -p:BaseOutputPath=.forge-logs/isolated-build/` passed with 0 warnings and 0 errors; a custom output path was required because the live preview held the normal Release DLLs open. `dotnet test tests/Forge.Planning.Tests/Forge.Planning.Tests.csproj --no-restore -c Release -p:BaseOutputPath=.forge-logs/isolated-build/` passed 322/322. A filtered deterministic integration run for attendance, material demand, and labor metrics passed 12/12. The integration test project compiles after correcting a tuple-unpack error in a shifted-workforce test. `git diff --check` passes.

The first integration run exposed two real attendance-calculation cases: explicitly overlapping policy windows had been silently shortened, and the correction test had accidentally linked its replacement to a different scheduled opportunity. The calculator now preserves authored overlap as an ambiguous/missing-policy result while deriving the end of a prior open-ended version from the next valid revision. The test correction now supersedes the event on the same opportunity. Both cases pass in the final 12-test run.


## Sprint 10 custody settlement increment — 2026-10-23

Extended the canonical material-task custody flow to accept separate accepted and rejected quantities. Accepted stock moves to the requested point-of-use location; rejected stock moves into non-nettable quarantine; any balance still unreceived remains in transit. A reason and a same-site quarantine location are required for rejected quantities, and the receiver remains a distinct active badge/PIN-authenticated worker. Added Inventory Controller return-to-stores and receipt-correction commands. They move ledger quantities, append custody evidence, update received/rejected/returned/reversed counters, and recalculate demand coverage with stale-update protection. The Inventory UI now exposes split receipt, rejection history and authorized correction actions; Workforce intelligence surfaces unresolved rejected quantities. Additive migrations are Inventory `0026` and Kernel/Postgres `0027`.

Verification for this increment: isolated Release host build passed with 0 warnings/errors; focused inventory/workforce UI tests passed 15/15; pure `MaterialTaskDemandTests` passed 6/6; the updated integration test project compiled. The database-backed scan/custody test could not run because Docker Engine is unavailable and `FORGE_TEST_POSTGRES` is unset. No migrations, seeds, database changes, or preview restarts were performed. Remaining Sprint 10 gaps include ranked recovery what-if plans, the expanded 60-worker/38-scenario replay, provider-backed email, and Sprint 9's isolated regression gate.

## Sprint 10 ranked workforce recovery increment — 2026-10-23

Added `/api/readiness/workforce-recovery-preview` and a supervisor action on machine capability gaps. The deterministic Planning engine ranks up to three visible, currently present candidates using existing qualification, machine authorization, equipment/tooling, safety, and reallocation checks. It excludes unsafe direct moves and supports a backfill-first two-worker plan when the source work would otherwise stop. The preview is read-only, checks Workforce supervision/manage scope for candidate source and target, and the UI lists evidence and explicit unassessed dimensions. It is intentionally limited to current workforce readiness: material availability, due-date and cost changes, and future-shift roster feasibility remain unassessed and are called out in the UI.

Added deterministic engine tests for stable top-three qualified ranking, machine authorization as a hard gate, and source backfill sequencing; all 3 passed. The Workforce recovery UI interaction tests passed 2/2. The isolated Release host build passed with 0 warnings and 0 errors. An initial parallel test/build collided on shared compiler output; rerunning the test after the build completed passed. No database or seed changes were made. Sprint 10's 60-worker / 38-scenario fixtures and material-triggered recovery remain open.

## Sprint 10 WhiskerWorks roster and acceptance matrix increment — 2026-10-23

Extended the fictional workforce fixture from 52 to 60 workers while preserving the original 52 identity IDs and ordering. The eight additional fictional profiles cover Swing and Weekend staffing, bringing the shift counts to 32 Day, 16 Swing, and 12 Weekend. Updated the Scenario Center demo-shape guard to require all 60 workers. Added a 38-case acceptance matrix (22 core and 16 material extensions) with explicit invariants and pending isolated-database replay status; the catalog is not evidence that those scenarios passed.

Verification: focused seed, roster, matrix, and Scenario Center guard tests passed 11/11; `node --check` passed for the seed script, manifest, and Scenario Center page. No database, seed, or migration was applied. Realistic attendance/performance histories and isolated scenario replay remain open, alongside material-triggered recovery impacts, provider-backed email, and Sprint 9 regression acceptance.

## Sprint 10 material recovery priorities increment — 2026-10-23

Extended Workforce material alerts into a ranked source-action queue. Priorities account for request/custody state, rejected quantities, the linked operation's current readiness constraint and work center, and its order due date. Each alert identifies the next authorized stockroom, receiving, approval, or Inventory Controller action and links to the material task and operation evidence. A material constraint is explicitly described as a production blocker; no labor move is recommended to solve it. This remains a current-state exposure view and does not calculate a hypothetical finite-capacity completion-date change.

Verification: Workforce intelligence model and page tests passed 12/12, including rejection priority, due-date ordering, center filtering, linked blocker detail, and the explicit no-labor-move boundary. `git diff --check` passed. No database or seed changes were made. Forecasted recovery deltas and future-shift options remain open.

## Sprint 10 material receipt recovery preview — 2026-10-23

Added a permission-gated read-only preview to Workforce material alerts for in-transit, released-order BOM staging tasks. It assumes independent acceptance of the remaining quantity at the current site date, pegs the hypothetical supply to the order so it cannot be consumed by another order, and compares the canonical material allocation and finite-capacity schedule before and after. The preview reports the target order and collateral status/completion changes for other visible orders. It creates no receipt, transaction, assignment, or approval; actual receiving, point-of-use, quality, safety, and authorization gates still apply. Added `/api/readiness/material-receipt-impact-preview`, Planning registration, the Workforce button, and calculator/UI regression coverage. Corrected a stale material-flow test expectation: partial delivered/picked quantities yield 85 allocated units, not 110 requested units.

Verification: UI suite 344/344; Planning suite 328/328; isolated Release host build 0 warnings/errors; receipt preview calculator tests 2/2; `git diff --check` passed. Existing database-backed integration and scenario replay remain unavailable without Docker/`FORGE_TEST_POSTGRES`; no database or seed was changed. Remaining Sprint 10 gates are documented in `AI_HANDOFF.md` and `docs/sprint-10-implementation-report.md`.

## Sprint 10 projected-shift workforce recovery — 2026-10-23

Extended the existing read-only workforce recovery preview with an upcoming site-date and shift mode. The mode uses Forge's shift calendar and recorded unavailability to identify scheduled workers, projects their home-center presence, checks qualification and machine-authorization expiry at shift start, and loads the safety-rule version effective at that future instant. The UI distinguishes scheduled candidates from clocked-in candidates and warns that projected attendance is not confirmed. Forge does not persist future operation assignments, so the preview does not claim that a source work center will remain covered; a supervisor must review that before planning/committing. No assignment or attendance record is written.

Verification: broad UI suite 344/344; Planning suite 330/330; isolated Release host build 0 warnings/errors; projected recovery engine tests 5/5, including qualification expiry; Workforce recovery page tests 2/2; `git diff --check` passed. No database, seed, or migration was applied. Isolated scenario replay remains blocked by unavailable Docker/`FORGE_TEST_POSTGRES`.
