# V3 completion checklist

**V3.1–V3.28 locally verified, 2026-10-13:** the recovered original BG/BN acceptance, closed source corrections and retained regressions pass the renewed completion gate. The Product Owner accepted the available V3.22 factual examples; numeric original section54 remains unlocated. The current all-28/governance-A–L map is in `docs/v3-source-reconciliation.md`. The 525-test result below is preserved as historical baseline evidence. V4 is approved to start after this V3 completion is recorded.

## Current completion evidence — 2026-10-13

Full `verify.cmd` **PASS: 565 tests** — Planning 303/303, Kernel 33/33, Architecture 32/32, restored PostgreSQL Integration 163/163 in 59.8970 minutes including fixture cleanup, and Node 34/34 with zero failures, cancellations, skips or todos. Release build has zero warnings/errors. Both accepted V3.22 examples and all four original-scale factory/extension cases passed. The restored-host marker drill and migration idempotency at 88 passed. All 80 changed source/test SHA-256 values remained unchanged throughout the gate; the source commit is recorded in `AI_HANDOFF.md`.

Local evidence: `.forge-logs/verify-v3-reconciled.log` and `.status` (PASS), `v3-reconciled-full-pass-manifest.json`, `v3-reconciled-js-syntax.log` (44 JS + 2 MJS = 46 modules), and `v3-reconciled-dependency-security.log` (refreshed direct/transitive NuGet scan: no vulnerable packages). No dependencies or migrations were added by this reconciliation. Earlier failed runs, including the first full 158/163 result, remain separately preserved and described in `AI_SESSION_LOG.md`; they have not been relabeled. The current per-version and governance A–L mapping below and in the reconciliation document is verified by this renewed gate, subject to the stated local/deployment limits.

| Affected gate / governance evidence | Current source and acceptance mapping |
| --- | --- |
| V3.27/V3.28 — original BN | OriginalReferenceFactory.cs and V327Tests.cs require three healthy/constrained/unusual-valid factories, each with a BN manifest of 400 parts, 200 each of fourteen other business populations (including registered workers and actual Qualified records), and 800 real worker events. Normal authenticated commands create the records; read-only persisted counts verify all sixteen populations after the real relay. Injected disruptions and exact source assertions cover nine BN category reads plus four retained BK queries, each under 10 seconds; construction is untimed. |
| V3.23 / governance L — original BG | RecoveryCatalog.cs, RecoveryTrialEvaluator.cs, RecoverySourceFacts.cs and RecoveryOptionTests.cs map the exact fifteen options to independent copied trials, source authority, consequences and explicit unsupported actions. Inventory.Contracts/RecoveryReadinessContracts.cs, InventoryRecoveryReadiness.cs and RecoveryInventoryReadinessTests.cs reuse actual source-command gates and require unchanged source/audit/event/idempotency state. RecoveryGovernanceTests.cs retains real failure/goal/notification/human restoration. |
| V3.9/V3.17 / governance A/H — actual start and controlled old WIP | OperationStartCheck.cs and V39Tests.cs evaluate all shared dimensions inside the command transaction for the selected machine/operator/trainer and controlled documents. Engine version 7 shares WipContinuationFacts.cs; V25Tests.cs Scenario_1 and OrderReadinessTests.cs require exact released-change/resolved CompleteAtOldRevision source authority, refuse fresh restart before resolution, and allow it after normal authorized Quality resolution while preserving the old revision/BOM and all other gates. |
| V3.15/V3.16/V3.24 / governance C — current replay actor | CommandReplayAuthorityTests.cs and AttendanceReplayAuthorityTests.cs cover current terminal/badge/operator binding, withdrawal and same-actor retry semantics, alongside retained terminal Node/session and qualification evidence. Attendance remains separate from direct production labor. |
| V3.1/V3.15/V3.17/V3.23/V3.25/V3.26 — planning source visibility | MaterialSourceVisibility.cs, MaterialSourceVisibilityTests.cs and V3PlanningSourceSecurityTests.cs cover private supplying-order/PO descriptions and links, hidden owned evidence, visible consumer evidence, current owning-source read scopes and withdrawal/restoration across material/MRP, readiness, priority previews, recovery, stored schedules/constraints, reports/exports/Analyst and historical Horizon. Full allocation/source keys, netting, canonical fingerprints and stored schedule/audit bytes remain authoritative. |
| V3.22 — owner-accepted available examples | V25Tests.cs uses actual normal-command EngineeringChangeReleased, OrderRevisionChanged and OrderDocumentationReissued IDs/source references/recorded causation through CauseMap. V322AvailableExampleTests.cs combines the real BI at-risk M-04 order, undelivered MR-22 point-of-use request, failed machine, qualified present crew and valid required tool; correlated machine/material events must remain unlinked. V322Tests.cs and PropagationVerificationTests.cs remain separate regressions. Numeric original section54 is still unlocated; owner acceptance does not claim exact-source reconstruction. |
| V3.28 — disposable fixture cleanup | PostgresFixture.cs external-server teardown targets only this fixture's created database names, drops them individually with a 300-second command limit and reports failures. Docker teardown removes the fixture container. This allowance is separate from read budgets and deployed recovery validation. |

The recovery acceptance retains its explicit limits: alternate-work-center and split-production selections are Infeasible without a controlled replacement routing or split source plan; outside-processing feasibility proves dispatch readiness; partial shipment proves release of already packed partial contents. Scheduling uses a 14-day horizon and alternate-machine exploration is bounded to 40 trials. Consigned-fixture ownership/loan/customer acceptance weaknesses remain recorded in ADR 0051. No new QMS release aggregate, fabricated numeric section54 example or V4 implementation is claimed.

## Historical completion checkpoint — 2026-10-12

The following recorded statements, tables, counts, logs and source-availability limits belong to the earlier checkpoint. They are preserved as audit history and do not establish the resumed gate's result. In particular, its V3.27 phrase “three 200-record factory variants” describes the narrower 100-order/100-receipt fixtures, rather than the current sixteen-population BN manifest.

Owner request: complete V3 and record the result. Last checkpoint: 2026-10-12.

V3.1–V3.28 implementation and completion-audit corrections are locally verified against the available requirements. The 2026-10-12 audit reopened governance acceptance (D/F/G/I/J/K), historical execution proof (B/E), evaluated recovery (L), and common historical Horizon lenses (V3.25); the final gate below covers their corrections. The supplied `JIRSN Forge V3 Plan.md` defines the numbered roadmap; use it with the governance expansion. Exact parity with unavailable original BN/section54/recovery catalogs remains unverified, and deployment validation is separate.

## Increment verification — historical checkpoints, historical final gate below

| Increment | Current state | Remaining gate |
| --- | --- | --- |
| V3.1–V3.8 | Previously verified and committed through `0232d18`; historical details in AI log | Preserve regression coverage |
| V3.9 | Full verification PASS: Planning 257, architecture 32, kernel 33, integration 81; zero build warnings/errors; commit `411c3c9` | Complete |
| V3.10 | Full PASS: Planning 262, architecture 32, kernel 33, integration 85; zero warnings/errors; selected API 14/14; commit `f01f611` | Complete |
| V3.11 | Full PASS: Planning 266, architecture 32, kernel 33, integration 90; zero warnings/errors; commit `4f18d85` | Complete |
| V3.12 | Full verify PASS; Planning 266, architecture 32, kernel 33, integration 92; commit `5db186c` | Complete |
| V3.13 | Full verify PASS; Planning 266, architecture 32, kernel 33, integration 94; focused API 17/17; zero warnings/errors; commit `20bfd9f` | Complete |
| V3.14 | Full verify PASS; Planning 266, architecture 32, kernel 33, integration 95; focused PostgreSQL API 1/1; zero build warnings/errors; commit `2d83b5a` | Complete |
| V3.15 | Full verify PASS: Planning 266, Architecture 32, Kernel 33, Integration 100; zero build warnings/errors; all V3.15 PostgreSQL acceptance tests passed | Complete (commit recorded in AI handoff) |
| V3.16 | Full `verify.cmd` PASS: Planning 266, Architecture 32, Kernel 33, Integration 104; zero Release build warnings/errors; focused PostgreSQL gate 6/6 | Complete; production Nexus/Sentinel configuration and physical terminal exercise remain deployment validation |
| V3.17 | Full `verify.cmd` PASS: Release build 0 warnings/errors; Planning 266, Architecture 32, Kernel 33, Integration 106; focused PostgreSQL 2/2 | Complete; evidence `.forge-logs/verify-v317.*` |
| V3.18 | Full `verify.cmd` PASS: Release build 0 warnings/errors; Planning 266/266, Architecture 32/32, Kernel 33/33, Integration 107/107; committed `af70b13` | Complete |
| V3.19 | Full `verify.cmd` PASS: Release build 0 warnings/errors; Planning 266/266, Architecture 32/32, Kernel 33/33, Integration 108/108; focused PostgreSQL notification and migration-idempotency tests pass; committed `57448e6` | Complete |
| V3.20 | Full `verify.cmd` PASS: Release build 0 warnings/errors; Planning 266/266, Architecture 32/32, Kernel 33/33, Integration 109/109 (23.5747 minutes); focused PostgreSQL V3.20/V3.19 checks pass; migration idempotency 83; committed in current checkpoint | Complete |
| V3.21 | Full `verify.cmd` PASS: Release build 0 warnings/errors; Planning 266/266, Architecture 32/32, Kernel 33/33, Integration 110/110 (23.4600 minutes); focused PostgreSQL detector acceptance 1/1; migration idempotency remains 83 | Complete |
| V3.22 | Full `verify.cmd` PASS: Release build 0 warnings/errors; Planning 266/266, Architecture 32/32, Kernel 33/33, Integration 111/111 (21.8486 minutes); focused CauseMap PostgreSQL and production propagation causation tests pass; migration idempotency remains 83; committed `e4fdeff` | Complete |
| V3.23 | Full `verify.cmd` PASS: Release build 0 warnings/errors; Planning 266/266, Architecture 32/32, Kernel 33/33, Integration 112/112 (25.3356 minutes); focused PostgreSQL acceptance 1/1; migration idempotency count 84 | Complete; product commit recorded in AI handoff |
| V3.24 | Full suite PASS; attendance/direct-labor separation and permitted support-work options pass; no migration needed; ADR 0048 | Complete; accepted against existing implementation |
| V3.25 | Full `verify.cmd` PASS: Release build 0 warnings/errors; Planning 266/266, Architecture 32/32, Kernel 33/33, Integration 112/112 (25.4026 minutes); focused history test 1/1; Node syntax checks pass | Complete; ADR 0049 |
| V3.26 | Full `verify.cmd` PASS: Release build 0 warnings/errors; Planning 272/272, Architecture 32/32, Kernel 33/33, Integration 112/112 (23.7949 minutes); focused PostgreSQL 3/3; JS syntax and diff checks pass | Complete; ADR 0050 |
| V3.27 | Full `verify.cmd` PASS: Release build 0 warnings/errors; Planning 272/272, Architecture 32/32, Kernel 33/33, Integration 116/116 (25.0193 minutes); three 200-record factory variants and consigned-fixture workflow pass | Complete; ADR 0051 records ownership/loan limitation and missing canonical BN list |
| V3.28 | Full verify PASS: clean Release, Planning 278/278, Architecture 32/32, Kernel 33/33, Integration 148/148 (36.2743minutes), Node 34/34; restored databases and seeded restore/readback/cleanup; three factory budgets; no vulnerable NuGet packages | Locally verified; exact missing-source parity and deployment limits below |

## Historical reconciled completion-audit gate — 2026-10-12

Final full `verify.cmd` PASS: Release build zero warnings/errors; Planning 278/278, Architecture 32/32, Kernel 33/33, PostgreSQL Integration 148/148 in 36.2743 minutes, Node behavior/accessibility/business-authority/inactivity 34/34. All 46 product JavaScript modules pass syntax checks; the refreshed direct/transitive NuGet scan reports no vulnerable packages. Migration idempotency passes at 88. The suite uses restored migrated databases and separately proves seeded backup readback/cleanup. Local evidence is `.forge-logs/verify-v3-final.log`/`.status` (PASS); the tracked handoff/session retain the counts and source commit.

Earlier failed focused/full runs and the deliberately cancelled pre-scoped-correction run are preserved in the session log. Fixtures and assertions were corrected without weakening product gates. The final scoped sourcing focus passed API 10/10 in 3.8971 minutes and Architecture 32/32 before the full gate. Every reconciled requirement below is included in the final passing suite.

| Corrected requirement | Fresh focused evidence |
| --- | --- |
| V3.1 upgrade | Real tracked/NULL-lot pre-V3.1 upgrade preserves every balance/ledger row; reviewed old checksum remains unchanged; wrong hashes/IDs reject |
| V3.3 genealogy | Full supplier heat → roll → remnant → second transformation → component → finished serial → shipment, exact links both ways; transfer leaves graph unchanged |
| V3.5 downtime | Actual setup/changeover remove capacity/projection and affect goals independently of Maintenance breakdowns |
| V3.15 contextual decisions | Actual NCR program/operation context; all PO requisition programs; actual quote money/currency; delegated project budget and cancellation limits |
| V3.13/V3.15 scoped purchasing flow | Actual-A RFQ/quote/PO creation and source/package/WhyLine review; B/mixed/prospective B-line/revoked sources denied; identical-key replay and reasoned audit/event proof; API 10/10 PASS |
| V3.20 inbox/assignments | Current scopes/markings/amounts/SoD, genuine pending catalog, source-page access, revocation, retry-safe audit/events/history and preserved progress |
| V3.21 integrity | An inventory serial in multiple locations is detected; valid moves/divisible lots remain silent |
| V3.23 recovery | Real machine failure/goal/notification/human restoration; evaluated alternate gates; recorded versus hypothetical provenance |
| V3.25 archived lenses | Seven lenses agree at a common scope/window; old projection and contract commitment retained; wrong facility/program excluded |
| V3.26 tool history | Real private tool/gauge references appear only with current access in API/report/Analyst; revocation and missing sources hide them |
| V3.6/V3.10 selection | Direct/descendant material-issue holds exclude candidates; unrelated and shipping-only held material remains eligible; release restores ranks |

## Historical governance expansion validation — 2026-10-12

| Scenario | Current coverage | Remaining evidence |
| --- | --- | --- |
| A — expired required gauge | V39 API/executable readiness and planned-use tooling | Final full PASS |
| B — historical calibration | Real inspection/use/timestamp evidence | Final full PASS |
| C — shared terminal actor protection | Node 34/34 stale responses, overlap, idle/switch/disposal, focus and configured timeout | Final full PASS; physical terminal remains deployment validation |
| D — step-up hold release | Configurable policy, trusted fresh provider claims and immutable version-bound signoff | Final full PASS; real Nexus remains deployment validation |
| E — authenticated but unqualified | Same Supervisor denied after expiry, then recertified | Final full PASS |
| F — temporary authority | Approved subset/scope/start/expiry, current grantor and source/recipient monetary limits | Final full PASS, including Node UI authority |
| G — generated shift handoff | Actual downtime/priority/request/quality hold/partial roll; unresolved sources preserved | Final full PASS |
| H — controlled priority impact | Current preview, displacement, human commit and source gates after shared capacity corrections | Final full PASS |
| I — downstream hold impact | Factual genealogy/allocation/order/operation/shipment/goal impact and shared blocks | Final full PASS |
| J — outside-processing round-trip | Sent revision/specification/PO snapshot, readiness, partial return/certificate/results/accepted/rejected quantities and same identity | Final full PASS |
| K — closeout 20-unit discrepancy | Exact 1000/750/200/30 =20 unexplained; no inferred scrap; immutable closeout | Final full PASS |
| L — failure/recovery/communications | Real failure/capacity/goal/notification/human command and factual versus hypothetical provenance; saved history immutable | Final full PASS |

Historical source-availability statement (2026-10-12; superseded for BG/BN by the current checkpoint above): All reopened local gates listed in the reconciled matrix pass the final full restored-database suite. Original BN questions, the original section54 example chain and the original enumerated recovery-option catalog were not supplied locally; searches found no originals and the owner does not know BN's location. Available factual proxies, actual recorded causal propagation and the documented fifteen-option implementation have acceptance coverage, without an exact source-parity claim. ADR 0051 retains the consigned-fixture ownership/loan/customer-acceptance limits. Selected-question performance, targeted Node accessibility and local restore coverage do not certify every production workload, a full accessibility audit or deployed disaster recovery.

## Historical release checkpoint — 2026-10-12

The local implementation checkpoint requires implemented reconciled requirements, passing migration/regression gates, recorded source commits and final handoff/session evidence. These tests pass; commit references are recorded in the handoff. Do not claim every unavailable original acceptance criterion or a deployed release is proven. Forge's SCIM/Sentinel adapters have local contract coverage; live Nexus/Sentinel/email, physical terminals, production recovery and broader accessibility remain deployment validation. No push or deployment occurred.
