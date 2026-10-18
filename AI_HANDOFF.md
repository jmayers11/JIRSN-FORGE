# AI_HANDOFF.md
## ADR 0055 owner rulings implemented: Claude, 2026-10-18 (current)

**Owner-approved production change.** A supplier rejection is decided per quantity, and an open return to vendor can
be reduced. The details are in `docs/adr/0055-v5-owner-rulings-idempotency-and-rejection-quantities.md`.

- **Disposition by quantity.** `POST /api/inventory/rejections/{id}/disposition` takes an optional `quantity`. Less than
  the rejected quantity decides that part; the rest becomes a new pending rejection with `splitFrom`, still on hold.
- **Reducing a return.** `POST /api/inventory/rtvs/{id}/reduce` (quality disposition, reason required, open returns
  only) only lowers the quantity. The reduced part stays on hold as a new pending rejection.
- **Migration** `inventory/0021_split_supplier_rejections`. The migration count is now **110**: update tests that
  assert it. The new triggers refuse raising a rejection's or return's quantity, and changing a return once it is
  not open.
- **Receiving screen.** A quantity field on Disposition, a Reduce action on open returns, and "split from" shown on
  remainders.
- **Tests.**
  - New: `V5RejectionSplitTests` (2) and `tests/ui/split-dispositions.test.mjs` (3).
  - Simulation scenario **S11** plus the negative control `ReleaseReducedPart`.
  - All six formerly unwritten rules now cite ADR 0055; no check needs a ruling.

**Gates, 2026-10-18.** All three passed, with a 0-warning build:
- `verify.cmd`: 944 checks;
- `simulate.cmd`: 21/21 tests, 662 expectations, 5/5 negative controls caught;
- the focused check.

Forge was restarted afterwards and applied migration 0021 to the local database.

## V5 simulation Phase A: Claude, 2026-10-17 (current)

**Follow-up, same afternoon.**
- Negative controls: 4 deliberately broken runs, all caught.
- Every check is tagged with its basis and spec source.
- The reproducibility test now covers all three scenarios.
- One summary page per run: `.forge-logs/simulation/summary.md`.
- `simulate.cmd` **PASS**, 16/16 tests.
- Full `verify.cmd` with the simulation filter: **PASS**, 939 checks.

**Owner rulings needed** (listed in `docs/v5/V5-simulation-phaseA-results.md`):
- the idempotency contract, which exists only in a code comment;
- five inferred rules about concurrent retries and supplier rejections.


**`simulate.cmd` PASS: 10/10 tests, 351 expectations, 0 failed.** Test code only, as the owner approved. No production code or schema changed.

- New project `tests/Forge.Simulation.Tests`, added to `Forge.slnx`, with tests tagged `Category=Simulation`. It contains:
  - the engine: clock, seeded event log, API driver, invariants, report and factory;
  - S09 quality hold scope, S10 supplier MRB dispositions and S15 network interruption, each run with seeds 7, 42 and 1009;
  - a reproducibility test.
- `verify.cmd` now runs `dotnet test` with `--filter "Category!=Simulation"`. The new `simulate.cmd` runs only the simulation, logging to `.forge-logs/simulate.log` with per-scenario results in `.forge-logs/simulation/`.
- Results and limits are in `docs/v5/V5-simulation-phaseA-results.md`. No Forge defects were found.
- **Next, Phase B (still test code):**
  - the 300-order, 70-operator factory generator;
  - 30/60/90-day horizons;
  - the remaining disruptive events;
  - production NCR and MRB dispositions;
  - workforce impact.

## Full verification of V5-S1, plus fixes: Claude, 2026-10-16/17 (current)

This section is authoritative over the V5-S1 checkpoint below wherever they differ.

**The "NuGet unreachable" block was Codex's sandbox, not an outage.** Codex's restore log shows `An attempt was made to access a socket in a way forbidden by its access permissions (api.nuget.org:443)`. Run from the owner's desktop, the same `verify.cmd` restored in seconds. The blocked attempt is kept as `.forge-logs/verify-v5s1-restore-blocked-20261016.log`. The earlier 937-check V4 `verify.log` had already been overwritten by that blocked attempt, so no log of it survives; it is recorded only in this file's text.

**Full `verify.cmd` on the V5-S1 tree** (desktop session, 2026-10-16 13:56):

| Stage | Result |
|---|---|
| Restore | OK |
| Release build | 0 warnings, 0 errors |
| Kernel | 44/44 |
| Planning | 303/303 |
| Architecture | 32/32 |
| PostgreSQL integration | 345/347 |

The two failures were a real gap left by V5-S1:
- `KernelRoundTripTests.Migrations_are_idempotent` and `V328Tests.PostgreSql_backup_restores_a_disposable_database_and_the_restored_host_reads_it` still asserted **108** registered migrations.
- Migration `inventory/0020_transformation_line_parents` makes it **109**. The V328 test baseline is built from the current migrations at fixture start, so 109 is correct.
- Both assertions are fixed.

Because the run stopped at the integration failures, the UI step did not run. The log is kept as `.forge-logs/verify-20261016-two-stale-count-failures.log`.

**Focused rerun after the fixes** (`.forge-logs/rerun-fixed.cmd`, log `rerun-fixed.log`, status `PASS`, 2026-10-17 02:21):
- Release rebuild: 0 warnings, 0 errors.
- `KernelRoundTripTests` and `V328Tests`, with the restored baseline: **11/11** passed.
- All UI test files: **213/213** passed across 25 files.

Together with the full run, every check has now passed on the current source:
- Kernel 44, Planning 303, Architecture 32.
- Integration 347 (345 in the full run, plus the 2 fixed tests in the focused rerun).
- UI 213.

**Clean full gate, 2026-10-17 11:28–12:56 (desktop session): `verify.cmd` PASS on the final tree.**

| Stage | Result |
|---|---|
| Release build | 0 warnings, 0 errors |
| Kernel | 44/44 |
| Planning | 303/303 |
| Architecture | 32/32 |
| PostgreSQL integration | 347/347, 1.42 hours |
| UI | 213/213, across 25 files |
| **Total** | **939 checks, 0 failures** |

The log is preserved as `.forge-logs/verify-pass-20261017-final.log`. Forge was stopped for the run and restarted afterwards with `.forge-logs/run-forge-release.cmd`, at http://localhost:5080.

**UI regression fixed (V5-S1).** Tracked byproducts now require explicit parent lots, but the order page's "What became of the material" dialog (`genealogy.js`) had no way to supply them. Every lot-tracked or serialized byproduct recorded from the screen was therefore refused. The fix:
- The dialog now has a "Byproduct came from" column, with a quantity per issued lot.
- The exported `byproductParents()` sends only lots with a positive quantity and refuses rows without a lot.
- A hint explains the consumed-quantity cap.
- New test: `tests/ui/byproduct-parents.test.mjs`.
- Backup of the old file: `.forge-logs/genealogy.js.before-byproduct-parents`.
- Checked live: the column and hint render on order 10002.

**Forge is running for the owner at http://localhost:5080.**
- The old immutable V4 preview (2026-10-15 snapshot) was stopped by the owner.
- `run-forge.cmd`, which uses `dotnet run`, hung twice right after "Restore" with no further output: once while tests ran, and once alone. The cause has not been found.
- Workaround: `.forge-logs/run-forge-release.cmd`, which is gitignored. It runs the tested Release build (`src/Forge.Host/bin/Release/net10.0/Forge.Host.dll`) with `--contentRoot` set to `src/Forge.Host`, so screens use the live `wwwroot`. It logs to `.forge-logs/forge-5080.log`, and closing its window stops Forge. Rebuild Release after any C# change.
- At startup it applied `inventory/0020_transformation_line_parents` to the local `forge` database.
- Smoke check signed in as `stores`: grouped nav (35 entries), and Inventory, Acceptance, Shipping, Workforce, QMS Planning and Analyst all render with no console errors.
- Browsers that had the old preview open may keep old JavaScript cached. Use Ctrl+F5 once.

**Full-system simulation amendment (owner, 2026-10-17).**
- The amendment is saved verbatim as `docs/v5/V5_Full_System_Simulation_Amendment.md`.
- The discovery and proposed architecture are in `docs/v5/V5-simulation-discovery-and-architecture.md`.
- Owner decisions:
  - **Phase A is approved, as test code only**, with no production or schema changes;
  - the long run uses **300 orders and 70 operators**;
  - simulations run from a **separate `simulate.cmd`**, not inside `verify.cmd`;
  - economics are reported in **quantities and time only**.

**Still open.**
- Why `dotnet run` (`run-forge.cmd`) stalls after restore.
- Commit the V4 follow-ups, V5-S1 and these fixes. Nothing has been committed since `38db86a`.
- Further V5 slices need the owner's separate approval: allocation, forecasting, MRB and connectors are still excluded.

## Earlier continuation checkpoint (superseded where the section above differs) — V5-S1 implemented; focused checks pass — 2026-10-16

V4's pre-V5 local software gate passed with 937 checks: Planning 303, Kernel 44, Architecture 32, PostgreSQL Integration 346 and UI hardening 212. V5-S1 is now implemented in the approved Inventory-owned boundary. The focused PostgreSQL run passed all 7 selected tests across V32, V33 and V41 label lifecycle; all 212 UI checks also pass. The latest full `verify.cmd` attempt is **blocked at NuGet restore**, not a code failure: `api.nuget.org` is unreachable (`NU1900`/`NU1301`). The offline restore also cannot supply `Microsoft.Extensions.TimeProvider.Testing` and `NetArchTest.Rules`. `.forge-logs/verify.status` therefore records `FAIL` for that incomplete full gate; see [V5-S1 implementation notes](docs/v5/V5-S1-implementation-notes.md).

The latest V4 UI continuation also completed the seven lower-priority read integrations that followed Claude's handoff: goal risk, Analyst brief, machine readiness, workforce reallocation preview, eligible inventory selection, scan-acceptance lookup and current QMS policy. Earlier work added acceptance/release history reads, site closure presentation, access-policy state and navigation grouping. The full UI hardening suite passes with these changes. The 2026-10-15 baseline of 759 checks is retained below as history.

The owner-provided [V5 master](docs/v5/JIRSN_Forge_V5_Consolidated_Development_Master.md) is copied byte-for-byte (SHA-256 `96E0A43DB167D4D2EDD7AC2FCC92AA30E272CFFBA7CDF3A2A6B0668044A1C4F2`). V4 is complete, so its Phase 0/1 START COMMAND has been carried out. See [V5 Phase 0/1 audit](docs/v5/phase0-1-audit.md) and [V5 session handoff](docs/v5/V5_SESSION_HANDOFF.md) for repository evidence, competitor feature reality, pain-point register, version ownership, current risks, test plan, ranked backlog and fictional demo.

**V5-S1 result:** the original missing byproduct parent edge was confirmed. Tracked byproducts now require explicit issued parent lots and quantities; parent attribution is capped by recorded consumed quantity, persisted append-only, and exposed as exact child genealogy while preserving the order-to-byproduct edge. Existing byproducts remain order-linked only because their historical physical parents cannot be inferred. Canonical child scans, governed label reprint, no-stock-duplication behavior, sibling hold isolation, and quantity reconciliation pass focused integration tests.

**Next gate:** rerun `verify.cmd` after NuGet connectivity is available. The owner approved V5-S1 with no permission to begin allocation optimization, forecasting, new MRB work or external connectors. Do not commit, push or deploy without separate authorization.

Current branch is `main`, HEAD `38db86a953f4d73dad3590183341bcf35e14dedf`; pre-existing V4 work and V5-S1 changes are uncommitted. No commit, push, deployment, external communication or certification/conformity claim was made. Preserve `forge.local.cmd` as in-memory configuration only. External normative review, organization policies/approvals, Nexus/Sentinel/email setup, physical devices, accessibility study and production recovery remain outside local software acceptance.

## V5 intake checkpoint — 2026-10-16 (historical)

The owner supplied the V5 master and specified that it should follow V4. At intake it was correctly queued; after V4's full gate passed, Phase 0/1 started and is now documented above. The master is a project specification. Its production implementation gate does not inherit prior V4 authorization and remains in force.

## Previous V4 completion checkpoint — 2026-10-15 (historical baseline)

**At that checkpoint, the V4 local software implementation and canonical acceptance were reported complete.** The owner explicitly approved implementation after V3 and then authorized continuing through completion; that later request supersedes the attached architecture-only STOP statement for this software work. This is a historical baseline; the 2026-10-16 continuation above is authoritative for current source and remaining verification.

At that checkpoint, the current uncommitted source passed the full `verify.cmd` on 2026-10-15. `.forge-logs/verify.status` was `PASS`; `.forge-logs/verify.log` records .NET 10.0.401, successful restore, a Release solution build with 0 warnings and 0 errors (1m26s), and 759 passing checks: Planning303, Kernel44, Architecture32, PostgreSQL Integration346, and UI hardening34. The integration phase took 1.7275 hours including fixture-owned database cleanup. No source changes had been made after that canonical run at the time of the earlier checkpoint.

The final-source gate includes the latest V4.1 transactional scan-to-PickMaterial/DeliverMaterial binding and bounded raw-value-free attempt telemetry; V4.2 exact quantity release, source-backed trace investigation and calibration impact review; V4.3 QMS risk, objectives/dashboard, controlled change, improvement and retention; V4.4 customer/provider/property/post-delivery controls; V4.5 audit/CAPA; and V4.6 management review, edition mapping and authorized Quality Analyst paths. QMS Analyst now reports authorized open audit findings, overdue unverified CAPA actions and completed objectives below target from current source records. Historical measurement/evidence is retained, unsupported inferences are withheld, and consequential outcomes remain human controlled.

There is no remaining local implementation or canonical test gate identified in the current checklist. Keep these external validation boundaries explicit: authorized review of the licensed ISO text and applicability, the organization’s actual scope/policies/owners and approvals, real Nexus/Sentinel/email configuration, physical scanner/printer and terminal exercises, assistive-technology study, and production disaster-recovery/accessibility validation. Forge must not claim ISO certification or conformity from software tests. The original BN/section54 reference question list was unavailable; the owner directed use of available examples, so exact parity with that missing list is not claimed.

The current review preview runs from the immutable Release publish `.forge-logs/preview-runtime/v4-complete-20261015` at `http://localhost:5080/` and returns HTTP200. It is a local Development-mode preview. Startup reports errors reading the existing shared Windows DataProtection key ring but still starts and serves the UI; do not modify the global key directory as part of this work.

The working tree is uncommitted and contains the V4 implementation. The 2026-10-15 verification logs and earlier failed diagnostics are preserved under `.forge-logs/`; do not rerun the 1.7-hour canonical suite unless source changes or a concrete risk requires it. No commit, push, deployment, external communication, certification claim, paid purchase, or physical retention purge was made. No usage reset was needed for this checkpoint.

**Claude continuation at that checkpoint:** use `docs/v4/completion-checklist.md` as the then-current disposition and that 2026-10-15 checkpoint as authoritative over older dated notes. No local code gate was identified then. Newer continuation work and verification are described at the top of this file. Preserve `forge.local.cmd` as an in-memory configuration source only; never print or copy it.

## UI backlog built (2026-10-15, Claude)

This section records Claude's 2026-10-15 implementation state; the current disposition and later backend/UI continuation are above. At that snapshot, only JavaScript, icons, tests, `verify.cmd` and docs changed. The 2026-10-16 continuation also changes C#, API reads and integration tests.

**Built: new pages, all routed in `app.js` with their own icons.**

- **Investigations** (`#/investigations`, `pages/investigations.js`), for V4.2:
  - Trace investigations:
    - open one from a supplier lot or an approved finding;
    - view a snapshot and choose among earlier ones;
    - see what the snapshot knows, with gaps and unknowns listed rather than implied complete;
    - assess each candidate (propose, approve, withdraw);
    - link an active matching hold to an approved assessment;
    - take a new snapshot;
    - close, acknowledging gaps.
  - Measurement findings: record, edit while a draft, submit, approve, withdraw, and "Trace uses" from an approved finding. Unknown suspect bounds stay null.
- **Product Acceptance** (`#/acceptance`, `pages/acceptance.js`), for V4.2:
  - Acceptance criteria:
    - list them;
    - define and edit drafts;
    - submit, approve, make effective, withdraw;
    - start a new revision.

    Characteristics are entered one per line: `number | name | Variable/Attribute | nominal | lower | upper | unit`. Documents are entered as `DOC REV`.
  - Inspect: choose the exact part, lot and location (plus a return cohort for returned product), the effective criteria, the quantity and the units inspected. A results sheet then asks for every characteristic of every unit, with a gauge for each measured value.
  - Release an exact quantity from passed inspections.
  - Look up an inspection, release or return cohort by number, then withdraw or consume a release.
- **Safety Rules** (`#/safety-rules`, `pages/safety-rules.js`):
  - draft, publish, discard a draft, retire, and draft a new version;
  - role slots are entered one per line: `name | count | qualification codes | trainee yes/no`.
- **Access Control** (`#/access`, `pages/access.js`):
  - register a person, with handling markings;
  - grant a role (optionally expiring) and revoke it;
  - grant and revoke program access;
  - create a role, and add or remove a role permission;
  - add or remove a permission scope;
  - turn the two separation policies on or off;
  - set or remove an approval threshold.

  Read-only for anyone without `kernel.access.manage`.

**Built: actions added to existing screens.**

- **Workforce** gains a "Manage people" panel (`pages/workforce-admin.js`), shown when you hold a relevant permission:
  - register a worker, set their shift, assign work and end the assignment;
  - record presence, departure and unavailability;
  - issue or revoke a badge (entered as a password field);
  - start training, certify with evidence, suspend and revoke qualifications;
  - add a qualification prerequisite.
- **Receiving** (`receiving.js`):
  - "Decide FAI" for a pending determination (`quality.plan`);
  - "Record FAI" when FAI is required. Results are one per line, `characteristic | requirement | measured | pass/fail`, plus the signed report document and revision;
  - "Record credit" for a shipped return to vendor (`purchasing.approve`);
  - "Prepare inspection" for a customer return;
  - the customer-return disposition form now sends the optional governed-restock `decisionId`, `expectedDecisionVersion` and `quantity`. Before this, it could not complete a governed restock.
- **Tools** (`tools.js`): "Move" (custody, location, custodian) and "Record use".
- **Plant Setup**: "+ Closure" per site, a dated plant shutdown with a note.
- **Shipping**: a Containers panel to create, pack released stock into, and seal containers.

**Supporting changes.**

- The role catalog moved from `app.js` to `roles.js`, so pages can use it without importing `app.js`, which starts the app when imported. `app.js` still re-exports `roleTitle`.
- Added the missing role titles AuditPlanner, InternalAuditor and AuditApprover.
- Four new icons, generated by `tools/make-nav-icons.py`: `acceptance`, `trace`, `safety`, `access`.
- `verify.cmd` now runs every `tests/ui/*.test.mjs` file, one `node --test` per file, instead of only three.
- Backups of the edited existing files are in `.forge-logs/*.before-ui-gaps`.

**Verification.**

- **UI tests:** all 18 UI test files pass, 202 checks.
  - Four new files: `investigations`, `acceptance`, `admin-pages` and `screen-actions`.
  - They check request bodies, permission gating and state-dependent actions.
- **Live check:** the current source ran on port 5090 against the local `forge` database, through `.forge-logs/ui-check.cmd`, and was viewed in the browser pane.
  - Users signed in: dev-admin, qm, qe and eng.
  - Every nav icon loaded, and every new page rendered real data or a clean permission message.
  - The only console errors were expected 403s.
  - Receiving showed "Decide FAI" on the seeded COIL-AL receipt, and its dialog opened.
  - Safety Rules listed the seeded SLIT-AREA rule.
  - Workforce showed the Manage people actions, and Plant Setup showed "+ Closure".
  - No command was submitted, so the sample data is unchanged.
- **Full suite:** the 1.7-hour `verify.cmd` was not rerun, because no server code changed. Run it before release; it now includes all UI tests.

**Disposition of Claude's 2026-10-15 open UI list (updated 2026-10-16).**

1. Acceptance inspection and product-release lists are implemented with bounded reads and individual source/policy authorization. Inventory and Shipping already display ledger-backed released quantity and available stock; no duplicate decision aggregate was added.
2. Site closures are displayed from the closure records already included in the resources GET response.
3. Current role grants/scopes, separation policy state and approval thresholds are available from the access-manager-only access-policy endpoint and shown in Access Control.
4. **Low-priority read-only endpoints** still need UI integration:
   - `/api/planning/goal-risk`
   - `/api/analyst/brief`
   - `/api/readiness/machines/{m}`
   - `/api/readiness/reallocation-preview`
   - `/api/inventory/selection`
   - document signature verification
   - scan-acceptance lookup
   - `/api/quality/qms/policy-current/{purpose}`
5. **The nav has 34 main entries plus Settings.** The main entries are grouped into Plan, Make, Quality and Admin; Settings remains at the bottom.

## Claude review of the V4 handoff: icons and UI gaps (2026-10-15)

Claude reviewed this handoff against the working tree after Codex's V4 checkpoint. It found two problems that this file did not record: missing navigation icons, and API features with no screen. Claude fixed the icons and the one unreachable page. Everything else is a backlog for the next session.

**Changed in this review.** These changes are uncommitted, in the same working tree as V4.

* **New icons in `src/Forge.Host/wwwroot/img/icons/`.**
  - Two nav entries pointed at PNGs that did not exist, so the browser showed a broken image:
    - Notifications used `bell`.
    - Shift Continuity used `timeline`.
  - Eleven entries shared four icons:
    - six used `quality`;
    - two used `workorder`;
    - two used `planning`;
    - Horizon and Quality Metrics both used `analytics`.
  - Shipping borrowed `suppliers`.
  - Added 13 icons in the house style (glossy orange, gunmetal, light grey, transparent background, 56px): `bell`, `handover`, `horizon`, `integrity`, `qms`, `target`, `audit`, `review`, `commercial`, `scenarios`, `inbox`, `factory` and `truck`.
  - `tools/make-nav-icons.py` regenerates them. It needs Python with Pillow and takes the output folder as its argument.
* **`wwwroot/js/app.js`.**
  - The nav now uses the new icons. Every nav icon resolves to a PNG that exists.
  - **The QMS Planning page is now reachable.** `pages/qms-planning.js` was built in V4.3 and covered by `tests/ui/qms-planning.test.mjs`, but nothing imported it, so users could not open it. It covers:
    - evaluation methods;
    - risks and opportunities;
    - metric definitions;
    - objectives;
    - changes;
    - retention schedules;
    - improvements.
  - It is now routed at `#/qms-planning` ("QMS Planning"), after Quality Management. The original `app.js` is saved at `.forge-logs/app.js.before-icons`.
* **Verification.**
  - `node --check app.js` passes.
  - All `tests/ui/*.test.mjs` pass: 180 of 180.
  - The C# suite was not rerun, because no server code changed.
  - The preview at `.forge-logs/preview-runtime/v4-complete-20261015` is an immutable snapshot, so it does not show these changes. Republish the preview to see them.

**UI not yet implemented (backlog).** *Most of this was built later the same day; see "UI backlog built" above for what remains open.*

Claude compared all 647 routes in `src/Forge.Host/Api/*.cs` with the `/api/...` paths in `wwwroot/js`, then checked each candidate by hand. The routes below are covered by API tests and, in most cases, by the sample-plant seed, but no screen calls them. Priority reflects what an operator or quality lead would hit first.

*High: V4 features that cannot be used without a screen.*

1. **Trace investigations and measurement findings (V4.2).**
   - Routes:
     - `/api/quality/investigations/trace` for list, create, refresh, assessments, hold-links and close;
     - `/api/quality/investigations/assessments/{id}/…`;
     - `/api/quality/investigations/measurement-findings` for list, create, edit and decide.
   - Nothing in the UI calls them.
   - Suggested home: a tab on Quality, linked from genealogy trace results and from calibration impact.
2. **Product acceptance and exact-quantity product release (V4.2).**
   - Routes:
     - `/api/quality/acceptance/criteria`, including revisions, edit and lifecycle;
     - `/api/quality/acceptance/inspections`;
     - `/api/quality/acceptance/product-releases`, with `withdraw`;
     - `/api/inventory/product-releases/{id}/consume`;
     - `GET /api/quality/acceptance/return-cohorts/{id}`.
   - Suggested home: Quality, with release status shown on the Shipping and Inventory lot views.
3. **Workforce and competency administration.** Only the qualification-type list is read, by Plant Setup. The Workforce page is read-only, and none of these writes has a screen:
   - `POST /api/workforce/workers/{id}` to register a worker, plus `/assignment`, `/assignment/end`, `/presence`, `/departure`, `/unavailability` and `/shift`;
   - `/api/workforce/people/{id}/badge` and `/badge/revoke`;
   - `/api/competency/people/{id}/training`, `/certifications`, `/suspensions` and `/revocations`, and `GET /api/competency/people/{id}`;
   - `/api/competency/qualification-types/{code}/prerequisites`;
   - `POST /api/readiness/reallocation-preview/{id}`.
4. **Safety rules.** `/api/safety-rules` handles list, drafts, publish, discard and retire, and has no UI at all.
5. **Security administration.** These routes have no UI:
   - `/api/kernel/roles`, including permissions and permission-scopes, with grant and revoke;
   - `/api/kernel/people/{id}/roles`, with revoke;
   - `/api/kernel/separation-policies/{policy}`;
   - `/api/kernel/approval-thresholds/{permission}`.

   Settings shows only the user's own identity, delegations and the audit log. Roles can be changed today only through the API or the seed.

*Medium: actions missing from screens that already exist.*

6. **FAI (V3.11).**
   - Receiving shows FAI status, but no screen can:
     - record an FAI determination (`POST /api/quality/fai-determinations`);
     - list FAI records (`GET /api/quality/fai`);
     - record an FAI (`POST /api/quality/fai`).
   - Without these, a receipt blocked on "FAI required" cannot be cleared from the UI.
7. **Receiving.** `receiving.js` has Ship back and Replacement buttons, but none for:
   - RTV credit (`POST /api/inventory/rtvs/{id}/credit`);
   - customer-return prepare-inspection (`POST /api/inventory/customer-returns/{id}/prepare-inspection`).
8. **Containers.** `/api/inventory/containers` handles list, create, contents and seal, with no UI.
9. **Tools.** `tools.js` has no action to move a tool (`/tools/{code}/move`) or record a use (`/tools/{code}/uses`). Use-limit validity therefore only changes through the API.
10. **Site closures.** `POST /api/resources/sites/{site}/closures` has no screen. Plant Setup shows shifts but cannot add a plant shutdown.

*Low: read-only endpoints that could enrich existing pages.*

11. These read endpoints have no UI:
    - `/api/planning/goal-risk` (would fit Programs or Horizon);
    - `/api/analyst/brief` (would fit as Analyst's opening summary);
    - `/api/readiness/machines/{machine}`;
    - `/api/inventory/selection` (a FIFO/FEFO pick preview on Inventory);
    - `/api/engineering/documents/{n}/signature-verification/{rev}` (a "verify signature" button);
    - `/api/identification/scan-acceptances/{id}`;
    - `/api/quality/qms/policy-current/{purpose}`.

**Not counted as gaps.**

* `/api/inventory/tasks/{id}/pick` and `/deliver` are reached through the V4.1 scan session, by design.
* By-id GETs used only by tests and detail fetches were left out.
* `/health/live` is not a UI route.

**Other items this handoff missed.**

* **Stale lower sections.** "Historical Project Status → Planned Features", "Known Issues" and "Recent Changes" below still describe the V3.9 state. For example, they say "Finish V3.9, then V3.10…" and "V3.9 is extracted on the PC and waiting for its test run". They are historical, and the current open work is the backlog above.
* **Uncommitted work.** The whole V4 implementation (about 145 paths) is uncommitted, and the last commit is `aa56b5d`. Commit V4 as its own change, with the icon and nav work as a separate commit, so the verified V4 tree can be recovered on its own.
* **The UI test list in `verify.cmd`** (fixed later the same day; it now runs every UI test file). It runs only three UI test files: terminal-state, accessibility and ui-authority. The other twelve files in `tests/ui/`, including every QMS page test, scan-session and identification, are not part of the canonical gate. Add them, for example with `node --test tests/ui/*.test.mjs`. They take about 2 seconds.

## QMS change and improvement acceptance checkpoint — 2026-10-14

V4.3 R07 and R36 now have passing end-to-end PostgreSQL acceptance cases in `QmsPlanningCaseTests`. Controlled change links the approved QMS change to an actual Engineering change and effective process, then requires independent impact review, an owned task, independent task verification, implementation verification, effectiveness assessment, independent acceptance and closure. Improvement links an actual Engineering origin and traverses evaluation, implementation, independent verification, effectiveness and closure; incomplete before/after measurement is explicitly retained as null, and a one-sided/unsupported numeric claim is rejected. The current case-test class passes3/3, including the earlier risk lifecycle. Evidence: `.forge-logs/qms-planning-cases-current.log` and `.status` (PASS); focused new case logs are `.forge-logs/qms-controlled-change-current.*` (1/1 PASS) and `.forge-logs/qms-improvement-case-current.*` (1/1 PASS). Two change-test fixture failures and an improvement-test compile failure are preserved separately. Canonical record codes, required monitoring, and the actual seven-field improvement assessment contract were used; no governance rules were weakened.

These close only focused QMS change/improvement case paths. Full V4.3 acceptance remains open. The whole-tree `verify.cmd` has not been rerun against current source. See the trace investigation checkpoint below for current Quality0011 evidence and migration count.

## Trace investigation checkpoint — 2026-10-14

The newly promoted V4.2 investigation slice compiles in Release and its six database-backed trace tests pass against the latest test assembly, including complete recorded supplier-lot traversal, independent human hold review, source-revocation redaction with retained snapshot bytes, and failed-calibration interval review while preserving the original passing measurement. Evidence: `.forge-logs/trace-investigation-current.log` (PASS 6/6; 1m23s). A previous private-program 403 came from a test binary older than the fixture membership edit; the rebuilt/current run passes. The separate approver fixture retains separation-of-duties and current program membership.

Quality migration `0011_trace_investigations.sql` is newly registered; repository SQL migration files total 100. Full `verify.cmd` has not been rerun after this V4.2 source promotion, and the latest integration/UI evidence still belongs to the earlier source checkpoint. The trace work covers specific supplier-lot and gauge cases; remaining V4.1 movement/staged scan retention, broader V4.2 controlled-production and trace edge cases, all V4.3–V4.6 workflow scenarios, and a fresh whole-tree acceptance gate remain open. Do not describe V4 as complete.

The preview at `http://localhost:5080` still serves the preserved immutable `06f4045-presentations-sessions` snapshot. Build a new immutable preview only after the current source is fully verified. External licensed-source, actual organization approval, physical device, live integration and production deployment limits remain as documented below.

## Historical V4 verification checkpoint — 2026-10-14 (superseded by the current checkpoint above)

V4 is still incomplete and all acceptance rows/scenarios in `docs/v4/completion-checklist.md` remain in force. The uncommitted working tree builds in Release with zero warnings/errors. Repository-wide `verify.cmd` passed all 279 PostgreSQL integration tests, including the corrected 99-migration idempotency and backup/restore checks. Its wrapper then returned FAIL in the UI phase because the fake DOM selector parser split whitespace inside a quoted `aria-label`. Fixed `tests/ui/browser-harness.mjs` to split selector groups and descendant chains outside quoted attribute values. Fresh UI tests pass 50/50 (the wrapper’s three suites plus QMS planning); all 62 JS/MJS files pass `node --check`; `git diff --check` passes with only working-tree line-ending notices. Logs: `.forge-logs/verify-integration-279-pass-ui-harness-failure.log` preserves the full integration PASS plus pre-fix UI failure; `.forge-logs/v4-ui-all-final.log` and `.status` (PASS); `.forge-logs/v4-js-syntax-final.status` (PASS). The latest `.forge-logs/verify.status` remains FAIL because `verify.cmd` has not been rerun after the harness fix; the product source has not changed since its 279/279 integration run. The full run took 1.3127 hours. Preserve all failed logs.

V4.2 acceptance/release and risk/objective/task/retention packages remain uncommitted and have only the focused 31/31 database gate plus full integration coverage, not full increment/scenario completion. The current migration count is 99. The preview at `http://localhost:5080` responds but is the preserved immutable `06f4045-presentations-sessions` runtime; it does not include the latest acceptance/planning code. Keep that snapshot intact until a separately built current preview is verified. No reset was redeemed; no external communication, purchase, certification, push or deployment occurred.

Next: continue V4.1 movement bindings and bounded/redacted scan retention; finish V4.2 complete trace/calibration investigation and containment scenarios; continue V4.3 change/improvement and competence; implement V4.4 provider/customer/property/post-delivery, V4.5 audit/CAPA and V4.6 complete KPI/dashboard/Analyst/notifications/frozen management review/mappings. Keep Claude continuation records current, run focused gates per slice, then run the full canonical gate after source stabilizes. Root owns build/test/restore/commit.

## Earlier focused checkpoint — 2026-10-14

V4 remains in progress. Since source commit 06f4045e7361b45a2054365b426f0e8f0a2aeb3f, the working tree now contains the V4.2 product-acceptance/product-release slice (Quality0008, Inventory0018), the risk/objective/task/retention planning slice (Quality0010), their Host APIs/DI/source access and actual UI/tests. These latest files are not committed and inherit no earlier gate. Release solution build passed with zero warnings/errors; the focused acceptance/planning gate passed31/31, total7.5558 minutes including fixture cleanup. It exercises exact 920/80 physical scope, accepted release versus legacy release, blocked held packing/shipment, concurrent decision/consume/ship replay, measured-gauge history and row-lock races, criteria withdrawal, private child NCR/CAPA source access, QMS planning/retention lifecycle, and exact recorded metrics. Evidence: `.forge-logs/acceptance-planning-corrected.log` and `.status` (PASS). `git diff --check` passes (Git reports only working-copy LF-to-CRLF notices). This is a focused checkpoint, not the full V4 gate.

Prior verified policy/label/server-session source/product-doc commit remains 06f4045e7361b45a2054365b426f0e8f0a2aeb3f,109 files. Its gate passes639 tests: Planning303,Kernel44,Architecture32,Integration122,browser138. Release zero warnings/errors1:20.38; Integration29.3522 minutes includes fixture cleanup; browser1.8832098s has no failures/cancellations/skips/todos. All733 frozen source/test hashes remain unchanged. Active migrations total96. All50 JS/MJS modules pass syntax; restored ZXing.Net0.16.11 direct/transitive vulnerability scan finds none. Evidence: `.forge-logs/v4-label-session-coherent-second.log`/`.status` (PASS), `v4-label-session-full-pass-manifest.json` and `coherent-second-result.json`. ADR0054 records that bounded result.

Preserve initial focusedFAIL57/63 (11.6621min), targeted correctionPASS9/9 (4.1491min), first coherentFAIL121/122 (29.1048min), compile and135/138 browser failures separately. The last failure was recovery's stale91 versus actual96 expectation; restored seeded backup/read now passes. The first post-run hash process overlapped that one-line test edit and correctly reports that known change plus732 matching files; only the fresh733-file second-run manifest is the final unchanged gate evidence.

Implemented: all19 primitive owner identities, actual controlled Code128/QR/DataMatrix SVG artifacts in Engineering storage, governed template/issuance/copy/alias/print reconciliation, exact effective policy adoption, mixed retained/current source reads and generated-file protection. Server sessions bind validated provider authentication/current workstation, actual operator/operation/machine, required exact instructions/materials, policy/source fingerprints, expiry and channel generation. Start/report ordinary owner commands persist immutable actual accepted audit/event links, preserve legacy null hash bytes and recheck current authority before original retries. Trusted-terminal versus current-authenticated-worker modes are explicit independent human policy inputs. No code supplies authentication.

The preview is live at http://localhost:5080 (HTTP200 over IPv4 and IPv6), from immutable `.forge-logs/preview-runtime/06f4045-presentations-sessions`, parent session83801. It shows the previously verified label/policy/session checkpoint; it does not yet contain the uncommitted acceptance/planning slice. Its30 runtime files keep source builds unlocked. Previous aa47bd1 snapshot remains preserved. Main preview migrations run forward on startup; never rewrite any applied migration. Never read/print/copy/commit forge.local.cmd; helpers consume configuration only in memory.

Next: complete the broader V4.2 trace/calibration investigation and current production-condition gates, V4.1 inventory movement bindings and bounded/redacted scan-attempt retention, then V4.3–V4.6 provider/customer, audit/CAPA, dashboard/Analyst/notifications and frozen management-review/mapping acceptance. Re-run the full solution/integration/browser/restore gates against current source before committing this slice or updating the preview. InvestigationsQuality0009, commercialQuality0011, audit/CAPAQuality0012 and reviewQuality0013 remain isolated mutable drafts. Acceptance-returnQuality0014/Inventory0019 has an authorized typed-enum-options refreeze manifest, but still needs promotion, implementation verification and acceptance. Root is the sole build/test/restore/commit lane.

InvestigationsQuality0009, commercialQuality0011 and audit/CAPAQuality0012 remain isolated active drafts. Root reviewQuality0013 now has contracts/rules, immutable stores, definitions, snapshot management review/actions, qualified mapping/edition services, API and provisional rule/two API tests under .forge-logs/v4-next-drafts/review; it is mutable/uncompiled/incomplete. Remaining hooks include callback-only owner readers, ordinary UI, paging, historical/current mixed permission checks, complete KPIs/dashboard/Analyst/notifications, inventory movement scan bindings and bounded/redacted raw telemetry. Do not rerun the non-idempotent session preparation scripts. Root is the sole build/test/restore/commit lane; agents draft only ignored owned packages.

Latest account observation this session: 2% weekly used, ordinary usage allowed, one free reset available. No reset was needed or redeemed. Existing free-reset authorization persists at exhaustion. Continue all V4.1–V4.6/38-capability/scenarioA–L gates and keep these records current. Licensed normative, real organization approval, physical/live-deployment evidence stays separately bounded; no certification/push/deployment/customer message/purchase is claimed.

## Active goal — complete all V4, 2026-10-14

The owner approved the local preview and explicitly directed V4 work until everything is completed, authorizing existing free usage resets at exhaustion. The persisted V4.1–V4.6 goal remains active. Direct implementation authorization supersedes the preserved historical review STOP. Read docs/v4/completion-checklist.md for all 38 capabilities and scenarios A–L, then implementation-plan.md. This foundation checkpoint does not complete the full increments or V4.

Verified foundation source/product-document commit: aa47bd1290b251b30f759b247b386a5b5be986d9, 46 files. V3 source c879a9e/full 565-test PASS remains complete. Initial V4 resolver 46a917c/focused 379-test PASS remains historical. The new foundation adds exact material/location identification, optional UI staging and typed transactional picks, plus Quality-owned typed QMS profile/process/policy/requirement revisions, independent governance and mixed current-source audit/notification access. Deployment organization and namespace are explicit; no actual organization decision or licensed applicability approval is invented.

Foundation gate PASS: 517 tests — Planning 303, Kernel 37, Architecture 32, PostgreSQL Integration 67 and browser 78. Release build has zero warnings/errors in 27.36 seconds. Integration finishes in 32.6996 minutes including fixture-owned cleanup; final browser gate has no failures/cancellations/skips/todos in 3.575556 seconds. All 48 product JS/MJS modules pass syntax checks. All 665 frozen source/test SHA-256 values remain unchanged. Local evidence: .forge-logs/v4-governance-material-focused.log/.status (PASS), v4-governance-material-full-pass-manifest.json and v4-qms-and-scanning-ui-final.log. Earlier build/analyzer/test-compile/preview-lock failures and three runtime failures are preserved separately. The runtime failures passed 57/67, 62/67 and 65/67; faithful migration, ordinary fixture authority and snapshot-alias fixes passed the targeted two-case correction before the coherent gate. No assertions or source checks were weakened.

Quality migration 0006 and Kernel0008/0009 bring the registered total to 91. Kernel0008 was applied to the local main preview database and remains checksum-immutable; 0009 is the forward common-authority/custom-name safeguard. Real custom-role collision acceptance still belongs to the next verification. QMS record/revision ownership, contributors, source references and lifecycle history remain immutable; effective intervals retain historical revisions. Exact normative references stay RequiresLicensedVerification. Product release is a separate unassigned quality.product.release permission, not shipping authority.

Next-slice drafts are isolated under ignored .forge-logs/v4-next-drafts/ and are unverified until promoted. Policy package owns Quality0007, exact approved adoption/reader rules and unknown-scope fail-closed behavior. Labels package owns all 19 owner-backed categories, controlled template/copy/alias/print lifecycle, current-versus-printed configuration and exact SVG artifact bytes in the existing Engineering content store. UI package is frozen at 11 source/test files. Parent session package contains generic Kernel transaction participation, a lazy Host factory to avoid service construction cycles, provider-channel/worker/operation/policy binding, generation CAS, expiry/staging and immutable accepted audit/event links. Session acceptance agent is independently reviewing and writing real API cases. Acceptance package owns exact criteria/inspection/product release, normal 1000→920 released/80 held stock lineage, concurrency and tool-use-event evidence. No draft PASS is claimed.

Parent remains the only build/test/restore/commit lane. Promote packages only after this committed foundation/record boundary. Kernel labels0010 and sessions0011 are reserved; subsequent permission/audit owner-context hooks need distinct numbers. Root still must wire Program/permissions, full mixed source history/notifications, generated-content use/download protection before replay and inside body, scanner/session provenance, dependency restore/security and final gates. Remaining V4.2–V4.6 work includes calibration/investigation, risk/objectives/change/retention, providers/customer/property, audit/CAPA and complete KPI/frozen management review/edition assessments/Analyst/notifications.

The verified foundation preview is running at http://localhost:5080 from the immutable ignored runtime .forge-logs/preview-runtime/aa47bd1-foundation (parent session 2178), with HTTP 200 confirmed. This snapshot keeps source Release DLLs unlocked during subsequent builds. Main preview database has Quality0006/Kernel0008/Kernel0009 applied; do not rewrite their checksums. An initial snapshot helper missed the existing environment mapping and exited on authentication failure; the helper was corrected without reading or printing credentials. Never read, print, copy or commit forge.local.cmd; helpers may consume it only in memory.

One free reset was observed applied externally after usage exhaustion stopped all agents. Capacity returned to 0%; the latest account inspection is 12% used with two free resets remaining and ordinary usage allowed. Root did not redeem it through a tool, and no purchase/upgrade occurred. No reset mutation tool is available. Official browser dashboard redirected to sign-in; its tab3 is marked for user handoff and no authentication was automated. Existing free-reset authorization persists for later exhaustion.

Continue all authorized software implementation and verification. Licensed normative review, real organization approvals, physical devices/assistive technology and live deployment integration remain accurately bounded external evidence. No push, production deployment, customer communication, paid purchase, multi-tenancy or destructive physical retention action has been requested.

## Historical verified checkpoint — first V4 identification step, 2026-10-13

**V3.1–V3.28 are complete and locally verified.** Source/product documentation: `c879a9e65a15eecd46fd05bab37b29a6016dd6b9`; final V3 handoff/log: `ac2fea3`. The persisted V3 completion goal is marked complete. Its restored full gate passed 565 tests; exact evidence, recovered BG/BN provenance, owner-accepted V3.22 substitution and limits are recorded below. The original numeric section54 remains unlocated without an exact-reconstruction claim.

**V4 began after that committed V3 boundary under the owner's explicit conditional approval.** First identification source/plan/tests commit: `46a917c31429b7587e6dec85f2047d2a490dac32`. Read `docs/v4/implementation-plan.md` for the dependency sequence and remaining decisions. This completes the first resolver step within V4.1; full V4.1 and V4 remain in progress. The historical review STOP below does not require another generic implementation approval.

**Separate V4 focused gate PASS: 379 tests.** Planning 303/303, Kernel 33/33, Architecture 32/32 and Integration assembly 11/11 in 2.6525 minutes including cleanup. The eleven cases are four new parser facts, five new real API/configuration cases and two existing work-inbox source-authority regressions. Release build has zero warnings/errors in 52.67 seconds; the earlier frozen-Host compile passed in 2:08.52. All six changed source/test SHA-256 values remained unchanged through verification/commit. Evidence: `.forge-logs/v4-identification-focused.log`/`.status` (PASS), `v4-identification-source-manifest.json`, and `v4-identification-host-build.log`. No dependency, migration or JavaScript change is included. The V3 full gate remains the V3 evidence, not a completed V4 release/device/scan workflow claim.

The new authenticated `POST /api/identification/resolve` accepts `{code}`. Explicit `Forge:Identification:Namespace` is required; no deployed organization namespace has been invented. Codes are `forge:v1:<namespace>:order:<number>` and `forge:v1:<namespace>:operation:<order>:<sequence>`. Host owns the strict source-specific codec; Kernel and existing operational identities are reused. Missing/invalid configuration fails closed with 503; malformed/foreign codes use clear 400 outcomes; missing and inaccessible sources share 404. The current principal and ALL actual owning-source resources are checked for every resolution; an operation sequence must actually exist. Tests prove scoped authority despite broader membership, same-code withdrawal/restoration, current real statuses and exact unchanged source/audit/event/idempotency rows. This read neither authenticates a badge nor grants execution authority or creates business facts.

**Earlier Claude next-action record — 2026-10-13 (superseded by the current checkpoint above):** continue V4.1 with material `(part, lot)` and location references through owner-owned point lookup contracts, deterministic key escaping and same-lot/different-part tests. Then connect optional keyboard/manual staging to existing task/shop-floor commands, invalidate delayed selections on operator/session changes, and verify typed material inside existing command transactions. Labels/aliases/printing/reprint/replacement/void, retained raw attempts, scan-to-execute and real scanner/printer validation remain open. Define relevant copy/retention/device policies before those consequential features. Continue the reviewed sequence with minimal approved V4.3 profile/policy brought forward ahead of dependent V4.2 release/execution, then supplier/customer quality, audit/CAPA and metrics/review. Do not reopen the closed V3 audit or treat the accepted missing numeric example as a completion blocker.

Use the latest source/plan and current records before historical sections. Freeze source before each parent-owned build/test gate; preserve actual failures separately and record exact passing counts/commit. Organization-specific D01–D20 policies and licensed ISO normative verification remain explicit inputs, not invented approvals. Namespace is collision protection, not tenant isolation; prefer one organization per deployment. No certification, production deployment, external communication, purchase, destructive retention operation or full scanner/assistive-technology claim is made. Existing free usage resets at exhaustion are authorized, but no authenticated redemption mechanism was established or reset redeemed here. Never read, print, copy or commit `forge.local.cmd`; verification may consume it in memory.

## Verified V3 completion — 2026-10-13

**V3.1–V3.28 are locally verified, including the recovered original BG/BN acceptance and governance A–L.** Product Owner requested completion and a continuation record for Claude. Verified source/product-document commit: c879a9e65a15eecd46fd05bab37b29a6016dd6b9. The complete gate verified the 80 changed source/test files exactly; all SHA-256 values remained unchanged through verification and commit. This handoff/session log is committed separately after that source hash. V3 completion work is finished.

**Full `verify.cmd` PASS: 565 tests.** Planning 303/303, Kernel 33/33, Architecture 32/32, restored PostgreSQL Integration 163/163 in 59.8970 minutes including fixture-owned cleanup, and Node 34/34 with zero failures/cancellations/skips/todos. Release build has zero warnings/errors. The seeded backup/restore drill confirms the restored host reads saved business data; migration idempotency passes at 88. Fresh preflight passes 44 JS + 2 MJS syntax checks and finds no vulnerable direct/transitive NuGet packages; reconciliation adds no dependency or migration. Local evidence: `.forge-logs/verify-v3-reconciled.log`/`.status` (PASS), `v3-reconciled-full-pass-manifest.json`, `v3-reconciled-js-syntax.log` and `v3-reconciled-dependency-security.log`. These ignored logs are local evidence; tracked counts/provenance are preserved here and in the session log.

**Acceptance provenance:** `docs/v3-source-reconciliation.md` maps all 28 increments, governance A–L and the closed R1–R8/Q1 corrections. Original BG contains the exact fifteen recovery options. Original BN factories each prove 400 parts, 200 records in fourteen other business populations, and 800 real worker events; all three variants answer nine BN categories plus four BK questions within independent ten-second read budgets. Construction is untimed. All four factory/extension cases pass. The owner selected “Use the available examples” for V3.22: normal released-engineering-change propagation and the recovered BI order-at-risk example, with actual event/source/causation assertions and correlated machine/material events remaining unlinked. Both pass. Numeric original section54 remains unlocated; exact-source reconstruction is not claimed. Preserved original request archives remain unchanged.

The closed corrections reuse existing owners and shared rules: complete operation-start gates inside the transaction; exact approved old-WIP continuation (readiness engine version 7); actor/current-source authority before replay; current notification/CAPA/audit access; exact produced-lot packing and accepted/released projections; independent private recovery trials with actual source-command guards. Current planning display copies hide inaccessible supplying WO/PO evidence across live/saved views, reports/exports, Analyst and Horizon while preserving full demand, quantities/dates, canonical keys/fingerprints and stored schedule/audit bytes. Access restoration reveals unchanged canonical sources. The first full run failed five cases (158/163); faithful fixes passed a focused 20/20 recheck and then this full gate. Earlier failed logs remain separately preserved and accurately described in `AI_SESSION_LOG.md`.

**V4 authorization:** the owner's direct “V4 approved to start after V3 is completed” supersedes the historical review STOP instructions below. The V3 gate and required completion records are finished; begin the dependency-safe identification foundation without asking again for the same implementation approval. Read `docs/v4/README.md` and the architecture review. The prepared first plan is a versioned canonical identifier resolver through existing owner contracts and current source access, followed by optional keyboard/manual staging into existing commands. It introduces no inventory identity or authentication authority. Labels/printing/aliases/voids, retained attempts, scan-to-execute, exact release coverage and QMS policies require their own implementation/acceptance. The next action is the first V4 identification implementation step; V4 runtime code has not started at this V3 checkpoint and cannot inherit a PASS from V3.

**Claude continuation:** use the latest checkpoint and current completion/reconciliation documents before historical sections. Do not reopen the closed V3 audit or treat unavailable numeric section54 as an endless completion gate. Continue the approved V4 dependency sequence: V4.1 identification; bring forward minimal V4.3 approved profile/policy; V4.2 exact execution/release/investigation; then supplier/customer, audit/CAPA and metrics/review. Pending D01–D20 organization-specific choices are not fabricated by chat implementation approval. Exact ISO normative clause verification remains RequiresLicensedVerification; no certification claim. Prefer one organization per deployment; identifier namespaces do not establish tenant isolation.

Remaining local/deployment limits: unsupported controlled alternate routing/split selections stay Infeasible; outside processing proves dispatch readiness and partial shipment proves release of already packed contents; scheduling uses a 14-day horizon/40 alternate-machine trials; consigned ownership/loan/customer acceptance weakness is recorded. Live Nexus/SCIM/assurance, Sentinel, email, physical terminals/scanners, deployed recovery and broader accessibility remain deployment validation. Existing free usage resets at exhaustion are authorized, but no authenticated redemption mechanism was established or reset redeemed. Never read, print, copy or commit `forge.local.cmd`; verification may consume it in memory. No push/deployment/external message occurred.

## Historical checkpoint — V4 architecture review, 2026-10-12

**Historical V4 review complete; its approval stop is superseded by the conditional owner approval recorded above.** The review source is the pasted V4 Master Architecture Specification, preserved unchanged in `docs/v4/sources/v4-master-request.md`. Its DO NOT IMPLEMENT V4 and STOP AND WAIT FOR PRODUCT OWNER APPROVAL instructions governed that review checkpoint. The later direct owner request authorizes V3 completion and V4 implementation after the V3 gate. Deployment and licensed-standard purchases remain separate.

Start with `docs/v4/README.md` and `docs/v4/architecture-impact-review.md`. The review has all twenty requested deliverables, scenarios A–L, four archetypes, proposed dependency order and twenty pending owner decisions. `docs/v4/iso-coverage-register.json` has all 38 capability sections (3–40) and all required mapping attributes. The operations, QMS governance and platform assessments contain detailed source evidence. ISO's official current catalog confirms ISO 9001:2026 publication on 16 September 2026; exact normative clause meanings/classifications/transition mappings remain RequiresLicensedVerification. No authorized full standard was supplied; no certification or full conformity claim is made.

Inspected V3 product baseline: c298a7fb9376a2bd8a344de2f37bcdd5e09a0a1b, prior handoff e54a679. Previous local tests passed Planning 278, Kernel 33, Architecture 32, Integration 148, Node 34. No application build/test was rerun for this documentation-only review and that historic PASS does not prove V4 acceptance. V3 application source/tests/migrations/build configuration remain unchanged.

Review gates before future implementation: complete applicable readiness must be checked inside start/issue commands rather than only read-side preview; distinct product-release decisions need exact acceptance/quantity/lot coverage; CAPA/audit/export/delayed-email need real current source scope and markings; FPY and complete QMS aggregates need proper first-attempt/no-prior-rework cohorts and aggregation before pagination; retrospective calibration and complete traceability investigations need explicit uncertainty. These are documented findings, not fixes made now. Reuse existing owners/contracts, CAPA IDs, ledger/genealogy, documents, Kernel and event paths. Retention metadata is proposed under Engineering; physical purge, offline production and multi-tenant deployment require separate approved designs.

**Source-availability correction:** a historical attachment was recovered and preserved as `docs/v4/sources/v3-cloud-implementation-brief.txt`. Its BG section contains the original fifteen recovery options; BN contains nine universal question categories and broader reference-factory populations. Earlier “BN/fifteen catalog unavailable” statements below and in old sessions are historical and superseded. Static comparison identified missing distinct original Alternate Work Center, Existing WIP, Remnant Transformation, Outside Processing and Partial Shipment recovery cards; original BN service/test/population reconciliation also remains open. Numeric section54's causal example is still unlocated. No catalog/test correction or exact-original acceptance claim was made in this review. Seek a concrete Product Owner authorization for any later reconciliation or baseline correction.

Claude continuation: read the integrated review, supporting assessments, register and pending D01–D20 decisions; await owner approval of a dependency-safe slice and its baseline corrections. Qualified licensed-source review must finalize exact normative mappings. Do not treat silence, a populated evidence link or AI assessment as approval/conformity. User authorized use of existing free usage resets at quota exhaustion; current tools can inspect usage but no authenticated reset redemption was established in this chat. Never print/copy/commit `forge.local.cmd` credentials.

## Verified V3 checkpoint — Codex, 2026-10-12 (historical baseline)

**V3.1–V3.28 implementation and completion-audit corrections are locally verified against the available requirements.** Product Owner requested continued implementation through V3 and a handoff/session log for Claude. The numbered source is `JIRSN Forge V3 Plan.md`, with `docs/v3-governance-requirements.md`, `docs/v3-completion-checklist.md` and the architecture review. Embedded review/stop instructions in source documents are distinct from the owner's direct implementation request.

Final full `verify.cmd` PASS: clean Release build (zero warnings/errors), Planning 278/278, Kernel 33/33, Architecture 32/32, PostgreSQL Integration 148/148 in 36.2743 minutes, and Node behavior/accessibility/business-authority/inactivity 34/34. All 46 product JavaScript modules pass syntax checks. The refreshed direct/transitive NuGet scan found no vulnerable packages; dependencies were unchanged. Integration databases are individually restored from a migrated disposable baseline. A separate seeded dump/restore drill proves saved business data is read by the restored host and cleanup succeeds. Current migration count is 88. Saved local evidence: `.forge-logs/verify-v3-final.log` and `.status` (PASS), `.forge-logs/v3-js-syntax.log`, `.forge-logs/v3-dependency-security.log`; these ignored files are local evidence, so final counts are retained here and in the tracked session log.

Completion corrections extend the existing authoritative domain records and shared command/audit/outbox paths: approved scoped delegation; configurable assured signed hold release and factual downstream propagation; historical calibration/qualification; exact shift handoff/reconciliation; sent processor configuration/readiness/inspection; evaluated recovery; shared archived Horizon lenses and explicit order commitments. Additional regressions cover the NULL-lot upgrade/checksum pair, complete serialized/shipment genealogy, recorded stoppage capacity, contextual inbox/assignments, serial-location integrity, private tool references, hold-aware selection and configurable terminal inactivity. The final scoped purchasing flow proves actual-A RFQ/quote/PO creation and review, identical-key replay with unique audit/events, denied B/mixed/prospective B-line sources and current revocation. Its focused gate passed API 10/10 and Architecture 32/32 before the full suite; Procurement.Contracts owns the shared source lookup.

**Exact original-source parity remains unverified.** The original BN question catalog, section54 example chain and original fifteen-option recovery catalog are unavailable. Local searches found no originals; the owner does not know where BN is. Available factual question/causal-propagation tests and the documented fifteen-option implementation have coverage, but are proxies for those unavailable sources. ADR 0051 also retains the consigned-tool limits: legal ownership, loan authorization and customer acceptance are not structured. Unknown older archive facility/shift context fails closed. Recorded facts remain distinct from hypothetical recovery inputs. Do not claim every original acceptance criterion or a deployed release is proven.

Deployment validation remains for real Nexus JWT/assurance/SCIM configuration, Sentinel intake, external email, physical terminals, production recovery and broader accessibility. Selected factory Analyst questions have a 10-second budget over 200-plus records; this does not establish every production workload's performance. Node checks use a controlled DOM/fetch/timer harness, not a full assistive-technology study. No push or deployment occurred. Never read, print, copy or commit `forge.local.cmd` credentials.

Verified source/product-document commit: c298a7fb9376a2bd8a344de2f37bcdd5e09a0a1b. The handoff/session documentation is committed separately after that source checkpoint. Earlier verified product commits include V3.27 `d76dc70`, V3.26 `1c19d7b`; their counts below are historical. Failed runs and the deliberately cancelled pre-scoped-correction run remain preserved as failures/cancellation, not relabeled PASS.

## Earlier verified checkpoints (historical)

**V3.10 full verification PASS, committed `f01f611`.** Quick verification passed: Planning 262, architecture 32; selected API tests passed 14/14. Evidence: `.forge-logs/quick-v310.*`, `.forge-logs/codex-v310.*`. Full `verify.cmd` passed: Planning 262, architecture 32, kernel 33, integration 85; zero build warnings/errors. Saved `.forge-logs/verify-v310.*`. Reviewed fixes enforce program visibility on hold release, load operation details for inspection holds, retain the V3.9 missing-revision 404 assertion, and prove populated legacy hold migration preserves identity/history/sequence. Retired Execution hold sources are preserved under `.forge-logs/removed/v310/`; retain migration 0007 and new migration 0008.

**V3.11 full verification PASS: Planning 266, architecture 32, kernel 33, integration 90; clean Release build; committed `4f18d85`.** Reviewed fixes include receiver separation of duties, new replacement instances and concurrent return limits.

**V3.12 Business-record evidence — full verification PASS; committed `5db186c`.** Clean Release build; Planning 266, architecture 32, kernel 33, integration 92; no failures. Evidence attaches to receipt, inspection, shipment and FAI records, keeps structured inspection results separate from signed evidence, and enforces inherited program/marking access.

**V3.13 RFQ/sourcing — full verification PASS; committed `20bfd9f`.** Quick passed (Release build clean, Planning 266, architecture 32); focused API 17/17; full suite Planning 266, architecture 32, kernel 33, integration 94, zero failures/warnings. Evidence: `.forge-logs/verify.log`/`.status`, `.forge-logs/codex-v313.*`, `.forge-logs/quick.*`.

**V3.14 full verification PASS; committed `2d83b5a`.** Release build zero warnings/errors; Planning 266, architecture 32, kernel 33, integration 95; focused PostgreSQL V314 1/1. Evidence is `.forge-logs/verify.log` and `.forge-logs/verify.status`; quick results are `.forge-logs/quick.log`/`.status`. Five additive migrations bring the idempotency test count to 77. V3.14 implements processor dispatch/return with service-PO and supplier checks, same-lot identity and custody, Inspection returns, OutsideProcess certificate evidence, Packaging item class and shipment gating, explicit container/pallet content reservations and shipment association, and partial shipments/corrections. Decision record: `docs/adr/0038-v3-14-outside-processing-packaging-and-shipping.md`. V3.15 is now complete and committed as `407a8f4`; see ADR 0039 and its recorded threshold boundary. V3.16–V3.28 remain outstanding; V3 is not complete.

**V3.16 identity, step-up, workstations and signatures — full verification PASS; committed `5500fe2`.** Step-up checks require an accepted `acr` and fresh `auth_time`; SCIM deactivation uses a scoped command and blocks API and badge access; shared shop-floor UI shows the operator and clears identity on lock/switch/60-second idle; five wrong PINs lock a badge for ten minutes; signoffs record role, assurance method and record version; a durable outbox adapter forwards security events to a configured Sentinel receiver. Focused PostgreSQL gate 6/6. Full suite: Release build zero warnings/errors, Planning 266, Architecture 32, Kernel 33, Integration 104. See ADR 0040 and the V3.16 README section. Deployment must configure Nexus JWT issuer/audience/keys and the SCIM scope, plus a compatible Sentinel endpoint and bearer secret. Automated tests used development authentication and a fake Sentinel receiver; no live external integration was tested. A shared physical terminal exercise remains deployment validation.

**V3.17 priority governance — full verification PASS; committed `82eb980`.** Priority changes require contextual authority and a reason, and commit only against a current five-minute preview fingerprint. The preview computes finite-capacity schedules before and after the proposed value; the UI shows the target projection and displaced orders. Active holds and quality blocks remain unschedulable in shared scheduling projections. Focused PostgreSQL acceptance passed 2/2. Full suite: clean Release build, Planning 266, Architecture 32, Kernel 33, Integration 106; 23.0942 minutes. See ADR 0041, README V3.17 and `.forge-logs/verify-v317.*`. No production integrations apply to this increment.

## Project Identity

* Project: JISRN Forge (brief also calls it JIRSN Forge), manufacturing operations modular monolith.
* Installed SDK: 10.0.401, accepted by global.json roll-forward.
* Last agent: Codex on this PC; earlier Claude implementation/history retained below.
* Do not expose `forge.local.cmd` credentials. Verification scripts consume it locally. Do not push or deploy without authorization.
* Older version/status statements below describe earlier checkpoints and are historical; this checkpoint is authoritative.
## Project Architecture

* **Languages and frameworks:**
  - .NET 10 / C# (SDK 10.0.100, `global.json`): an ASP.NET Core modular monolith using minimal APIs.
  - The web UI is plain JavaScript ES modules under `src/Forge.Host/wwwroot` (no framework, no build step).
  - The sample-data script runs on Node.js (`tools/seed-sample-plant.mjs`).
* **Database:**
  - PostgreSQL 16 or later, used through raw Npgsql 9 (no ORM).
  - Each bounded context owns its own schema (`kernel`, `competency`, `resources`, `workforce`, `engineering`, `execution`, `inventory`, `quality`, `planning`, …).
  - Each schema has embedded SQL migrations run in context order. Current count is 88; the old 62-migration count described V3.8. The one reviewed V3.1 checksum pair preserves already-applied history.
  - History tables are append-only, enforced by triggers.
* **Authentication:**
  - JWT bearer in production.
  - A development authentication handler (`src/Forge.Host/Security/DevelopmentAuthenticationHandler.cs`) uses an `X-Forge-Dev-Subject` header for local runs and tests.
  - Authorization is role → permission (`src/Forge.Kernel/Security/Permissions.cs`). It is checked on every command by the kernel `CommandPipeline`, which also enforces reasons, idempotency keys and auditing.
* **Key directories:**
  - `src/Forge.Kernel`: commands, the authorizer, events, audit and identity. In V3.10, generalized holds are added here.
  - `src/Forge.Kernel.Postgres`: the unit of work, the migration runner and the outbox.
  - `src/Forge.<Context>`: one project per bounded context. Other contexts use it only through its `.Contracts` namespace, and architecture tests enforce this.
  - `src/Forge.Planning`: the read side. It holds the deterministic engines (`Engines/`), reports, the Analyst (plain-language questions) and readiness.
  - `src/Forge.Host`: the API endpoints (`Api/`), the target resolvers (`Documents/`) and the web UI (`wwwroot/`).
  - `tests/Forge.Planning.Tests` and `tests/Forge.Kernel.Tests` hold unit tests.
  - `tests/Forge.Architecture.Tests` holds the dependency and context rules.
  - `tests/Forge.Integration.Tests` drives the real API against a real PostgreSQL. `SlittingPlant` is the shared fixture.
  - `docs/adr`: decisions through 0052, including completion-audit updates to the affected earlier ADRs. The earlier 0033-uncommitted statement described V3.9 and is historical.
  - `.forge-logs/` (not committed) holds the PC-side scripts, logs and the packages each increment is delivered in.
* **Important configuration files:**
  - `Directory.Build.props`: warnings are errors; analyzers run at latest-recommended; banned APIs are listed in `BannedSymbols.txt`.
  - `Directory.Packages.props`: central package versions.
  - `global.json`, `Forge.slnx`, `compose.yaml` (PostgreSQL in Docker).
  - `src/Forge.Host/appsettings*.json`.
  - `forge.local.cmd` is local only, gitignored and holds the database password. Never copy, print or commit its contents.
  - `forge.local.example.cmd` is the template.
* **External dependencies:**
  - Npgsql, NodaTime and the ASP.NET Core JWT bearer package.
  - Tests use xUnit, Microsoft.AspNetCore.Mvc.Testing, NetArchTest.Rules and Microsoft.Extensions.TimeProvider.Testing.
  - The optional Anthropic API narrator for the Analyst is `src/Forge.Host/Api/AnthropicNarrator.cs`. It uses a key from configuration, never committed.

## Historical Project Status (older snapshot; latest checkpoint above is authoritative)

### Completed and Verified

Every increment below was verified on this PC before it was committed. Verification ran `.forge-logs\quick.cmd` (build, unit and architecture tests), then `.forge-logs\p7b.cmd` (the increment's integration tests), then `verify.cmd` (the full suite).

* **Phases 1–9 and V2.1–V2.12 (committed earlier).** Their features include:
  - the kernel, competency, workforce and safety rules;
  - engineering and orders, capacity and scheduling, the shop floor;
  - materials and MRP, quality and shipping;
  - programs and maintenance, reporting, the Forge Analyst;
  - controlled documents, change control, alternates and unified demand;
  - assets and MRO, facility projects, serial genealogy, and Horizon with "What changed?".
* **V3.1 `e1f77ba` (stock dimensions and locations) through V3.6 `f40d935` (shelf life).** The V3.6 full suite passed 231 unit, 32 architecture and 75 integration tests.
* **V3.7 `fcc295e`, goal attainment and reforecast.** It includes the goal-risk work taken over from the paused tool. The full suite passed: 239 unit, 32 architecture, 33 kernel and 76 integration tests.
* **V3.8 `0232d18`, tooling and calibration.**
  - Tools are resources with validity at the moment of use, service cycles and calibrations.
  - Gauges are tools.
  - The full suite passed: 245 unit, 32 architecture, 33 kernel and 78 integration tests.

### Implemented but Not Verified

* **V3.9, executable capacity and presence.** It is extracted in the PC working tree, uncommitted, from `.forge-logs/v39d.tgz`.
  - Tooling requirements, production holds, material at the point of use, and training on controlled documents (policy off, warn or enforce).
  - Presence levels.
  - `/api/readiness/operations` and the `executable-operations` and `presence` reports.
  - Built and tested locally by Codex: Planning 257/257, architecture 32/32, kernel 33/33, focused V39 API 3/3. Full-suite verification is still pending; see the latest checkpoint above.
* **V3.10, generalized holds.** It is packaged as `.forge-logs/v310.tgz` and not applied.
  - Holds move into the kernel, with ten kinds, configured blocked activities and a release authority per kind.
  - Holds are enforced at order release, the shop floor, issues, shipments, PO approval and receipts.
  - There is one hold register and a `holds` report.
  - It changes V3.9's hold API and tests.
* **V3.11, receiving inspection, FAI and returns.** It is packaged as `.forge-logs/v311.tgz` and not applied.
  - FAI determinations, FAI records with a signed report, and partial acceptance.
  - Supplier dispositions, returns to vendor with replacement as a new instance, and customer returns.

### In Progress

* **Current feature:** V3.9 testing on the PC: quick, then p7b, then verify, then commit.
* **Files being modified:** the 43 files in `.forge-logs/v39d.md5`. Highlights:
  - `src/Forge.Planning/Engines/ExecutableChecks.cs`, `OrderReadinessEngine.cs`, `OperationReadinessEngine.cs`, `SiteSnapshot.cs`, `SnapshotLoader.cs`;
  - `src/Forge.Resources/Contracts/ToolContracts.cs` and `src/Forge.Execution/HoldService.cs`;
  - `src/Forge.Engineering/DocumentService.Training.cs`;
  - migrations `resources/0005`, `execution/0007` and `engineering/0011`;
  - `tests/Forge.Integration.Tests/V39Tests.cs` and `tests/Forge.Planning.Tests/ExecutableReadinessTests.cs`.
* **Remaining work:**
  1. Run `quick.cmd`. The p7b filter is already set to V39, V23, V38, Phase5 and KernelRoundTrip.
  2. Run `p7b.cmd`, then `verify.cmd`.
  3. Fix any failures and commit V3.9 with `.forge-logs/v39msg.txt`.
  4. Do the same for V3.10, then V3.11.

### Planned Features

> **Historical (V3.9 era).** V3.9–V3.28 and V4 are complete. See "Claude review of the V4 handoff" near the top for the current UI and icon backlog.

The plan is the "V3 plan" Claude Doc. Its increments map the brief's sections A–CC; the next ones, in order:

1. Finish V3.9, then V3.10 and V3.11 (packaged).
2. **V3.12, business-record evidence.**
   - Packing slips, CoC, CoA, measurements, photos and FAI evidence on receipts, inspections and shipments.
   - Evidence is never a controlled document.
   - An attached CoC does not complete the structured fields, and complete fields do not remove a required signed document.
3. V3.13 to V3.28, per the plan doc. Read it before starting each increment.

## Known Issues

> The git-lock and computer-use items below still apply. The V3.10 package-order item is historical, because V3.9–V3.11 are committed. Open UI gaps are listed in "Claude review of the V4 handoff" near the top.

* **Git cannot delete its own temporary and lock files from the linked session.**
  - Severity: Low (housekeeping).
  - Affected files: `.git/objects/*/tmp_obj_*`, `.git/*.lock`.
  - Reproduction: run `git commit` through the cloud session's shell on this PC. It prints "unable to unlink … Operation not permitted".
  - Recommended solution: after each commit, move any `.git/*.lock` to `.git/stale-*` (as the commit routine does). Or grant delete permission once and remove the leftovers. Run `git gc` from a normal terminal when convenient.
* **Computer-use access expires after 30 minutes idle.**
  - Severity: Low.
  - Affected files: none. This is a workflow issue.
  - Reproduction: wait more than 30 minutes during a long `verify.cmd` run, then try to click.
  - Recommended solution: request access again before launching each script.
* **V3.10 changes V3.9's hold API.**
  - Severity: Medium if someone applies the packages out of order.
  - Affected files: `src/Forge.Execution/HoldService.cs` (V3.9, removed in V3.10); `/api/holds` request shape; `tests/Forge.Integration.Tests/V39Tests.cs`.
  - Reproduction: applying `v310.tgz` before V3.9 is committed mixes the two.
  - Recommended solution: apply and commit strictly in order: V3.9, then V3.10, then V3.11. V3.10 moves three Execution files to `.forge-logs/removed/` (they cannot be deleted from the session) and migrates `execution.production_hold` into `kernel.hold`.
* **Fixed in V3.8: an inspection with a new gauge failed with a foreign-key error.**
  - Severity: High (it was fixed before commit).
  - Affected files: `src/Forge.Quality/Infrastructure/Migrations/0004_gauges_are_tools.sql`.
  - Reproduction: in V3.8 before the fix, inspecting with a gauge registered as a tool hit `inspection_gauge_code_fkey`.
  - Fix: the foreign key now points at `resources.tool`. Listed here only so nobody reintroduces it.

## Recent Changes

> **Historical (V3.9 era).** V3.9–V3.28 and V4 are complete. See "Claude review of the V4 handoff" near the top for the current UI and icon backlog.

* **Summary of the latest session:**
  - Verified and committed V3.7 (goal attainment, with the paused goal-risk work taken over) and V3.8 (tooling and calibration).
  - Built V3.9 (executable capacity and presence), V3.10 (generalized holds) and V3.11 (receiving inspection, FAI and returns), and packaged each for the PC.
  - V3.9 is extracted on the PC and waiting for its test run.
* **Files created or modified:** see `AI_SESSION_LOG.md` (2026-10-11 entry) and each package's `.md5` list in `.forge-logs/`.
* **Important architectural decisions:**
  - ADR 0031 (V3.7): goals and reforecast.
  - ADR 0032 (V3.8): tools live in Resources and are judged from their history at the moment of use.
  - ADR 0033 (V3.9): readiness adds tooling, holds, point of use and training. A schedule is never presence.
  - ADR 0034 (V3.10): holds live in the kernel because every context must check them. Release authority is set per kind.
  - ADR 0035 (V3.11): determinations ship with their first users (FAI). A replacement from a return to vendor is a new lot linked by `Replaces`.

## Next Session Instructions (updated 2026-10-16)

1. Treat the V5-S1 checkpoint at the top as current. Inspect `git status` before editing; the V4 and V5 trees are uncommitted. Do not reset or clean the tree.
2. Rerun the full `verify.cmd` when NuGet connectivity and the two missing test packages are available. The 937-check V4 result is a pre-V5 baseline; current V5 evidence is the focused 7-test PostgreSQL run plus 212 UI checks.
3. V5 Phase 0/1 and the owner-approved V5-S1 are recorded in `docs/v5/phase0-1-audit.md`, `docs/v5/V5_SESSION_HANDOFF.md` and `docs/v5/V5-S1-implementation-notes.md`. Keep the change inside Inventory, Planning genealogy and existing Identification tests.
4. Do not start allocation optimization, forecasting, MRB development, external connectors, production migration or physical-device work without separate scope and validation.
5. Keep external normative review, organization policy, Nexus/Sentinel/email, physical scanner/printer/terminal exercises, accessibility study and production recovery validation separate from local code-test claims. Never read/print/copy/commit `forge.local.cmd` credentials, use non-test operational backups, push or deploy without authorization.

## Development Rules

* Preserve existing working functionality.
* Avoid unnecessary architectural rewrites.
* Follow existing coding conventions:
  - commands go through `CommandPipeline` with permissions, reasons, two-person rules, events and an audit;
  - contexts talk only through `.Contracts`;
  - one migration per change;
  - tests run through the real API.
* Never claim a feature is complete without verification.
* Do not expose secrets or credentials. The database password lives only in the gitignored `forge.local.cmd`.
* Document breaking changes and migrations in the ADR and the README.
* Keep modifications focused on the assigned task. Only one tool edits the repository at a time.
* Update the session log after every development session.
