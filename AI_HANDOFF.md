# AI_HANDOFF.md

## Latest checkpoint — Codex, 2026-10-11

Product Owner request: continue until V3 is done and record the outcome for Claude. The active goal covers V3.1–V3.28. The complete numbered source plan is now supplied as `JIRSN Forge V3 Plan.md`; read it alongside `docs/v3-governance-requirements.md`, `docs/v3-architecture-impact-review.md` and `docs/v3-completion-checklist.md`. The direct request to complete V3 authorizes implementation; embedded review/stop instructions in the supplied planning documents are distinct from that request.

Current checkpoint is **V3.17 full verification PASS, committed `82eb980` on main**. V3.16 is committed at `5500fe2`. Full `verify.cmd` passed: clean Release build (0 warnings/errors), Planning 266/266, Architecture 32/32, Kernel 33/33, Integration 106/106 (23.0942 minutes). Focused PostgreSQL acceptance passed 2/2. Evidence: `.forge-logs/verify-v317.log`/`.status` (PASS). Next: begin V3.18 shift pass-on, handoff and closeout. V3.18–V3.28 remain; V3 is not done.

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
  - Each schema has its own SQL migrations, embedded as resources and run in context order at startup. The count is 62 as of V3.8.
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
  - `docs/adr`: one ADR per increment (0001–0032 committed; 0033 for V3.9 is in the working tree).
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

The plan is the "V3 plan" Claude Doc. Its increments map the brief's sections A–CC; the next ones, in order:

1. Finish V3.9, then V3.10 and V3.11 (packaged).
2. **V3.12, business-record evidence.**
   - Packing slips, CoC, CoA, measurements, photos and FAI evidence on receipts, inspections and shipments.
   - Evidence is never a controlled document.
   - An attached CoC does not complete the structured fields, and complete fields do not remove a required signed document.
3. V3.13 to V3.28, per the plan doc. Read it before starting each increment.

## Known Issues

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

## Next Session Instructions

1. Read this handoff, `JIRSN Forge V3 Plan.md`, `docs/v3-governance-requirements.md`, `docs/v3-architecture-impact-review.md`, `docs/v3-completion-checklist.md`, and ADR 0039.
2. Confirm the clean committed state with `git log --oneline -5` and `git status`; V3.15 is `407a8f4` on main.
3. Continue with V3.16 identity, step-up, shared workstations and signatures. Follow its roadmap acceptance test: authentication must not bypass qualification, stale step-up assurance is rejected, and a locked terminal cannot act as the previous user.
4. Run quick, focused acceptance and full `verify.cmd` for each increment; review the diff and migration behavior, commit only after full pass, and record each checkpoint in this handoff and `AI_SESSION_LOG.md`.
5. Continue sequentially through V3.28. Do not mark V3 complete until all roadmap gates, deployment/configuration requirements and final verification pass.

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






