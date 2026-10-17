# V5 Full-System Digital Factory Simulation: Discovery and Proposed Test Architecture

**Date:** 2026-10-17
**Author:** Claude, in a cloud session linked to the owner's PC.
**Source:** the owner's "Forge V5 — Full-System Digital Factory Simulation Amendment", copied verbatim to [`V5_Full_System_Simulation_Amendment.md`](V5_Full_System_Simulation_Amendment.md).
**Status:** discovery and proposal only. No production code, schema, workflow or test code has been changed for this amendment.

The amendment asks for repository discovery and a proposed test architecture first, and for owner approval before any production code or schema change. This document is that first step. The decisions it needs from the owner are in §8.

---

## 1. What the repository already contains

### 1.1 Modules

There are 16 source projects, with 109 registered database migrations across their schemas. File counts exclude `bin` and `obj`.

| Project | C# files | Migrations | Owns |
|---|---:|---:|---|
| Forge.Kernel | 26 | — | Command pipeline, events, audit, access, holds, delegation |
| Forge.Kernel.Postgres | 11 | 16 | Unit of work, migrations runner, outbox relay, event subscriptions |
| Forge.Engineering | 29 | 15 | Items, revisions, BOM, routing, documents, changes, alternates |
| Forge.Planning | 80 | 7 | MRP, scheduling, readiness, goal risk, scenarios, Analyst, reports, genealogy graph |
| Forge.Inventory | 39 | 20 | Ledger, lots, receiving, issues, transformations, shipments, counts, containers, product release consumption |
| Forge.Quality | 66 | 14 | Inspections, NCR, CAPA, FAI, acceptance, investigations, QMS planning/review/audit, commercial quality |
| Forge.Execution | 17 | 11 | Production orders and actuals, shop floor, shift continuity |
| Forge.Procurement | 9 | 5 | Suppliers, purchase orders, sourcing |
| Forge.Maintenance | 6 | 7 | Assets, PM, downtime, maintenance readiness |
| Forge.Resources | 9 | 5 | Sites, work centers, machines, shifts, closures, tools and calibration |
| Forge.Workforce | 7 | 4 | Workers, presence, assignments, unavailability, attendance, badges |
| Forge.Competency | 4 | 1 | Qualification types, training, certification |
| Forge.SafetyRules | 3 | 1 | Staffing, lone-work and role-slot rules |
| Forge.Programs | 3 | 2 | Programs, contracts, milestones |
| Forge.Projects | 3 | 1 | Projects and project material |
| Forge.Host | 119 | — | HTTP API (about 650 routes in 45 endpoint files), identification and labels, notifications, propagation, Analyst narrator, SCIM, UI |

**Security catalog:** 109 permissions and 38 roles (`Forge.Kernel/Security/Permissions.cs`).

**Reports:** 34 standard reports, among them:
- `production-output`, `schedule-attainment`, `delivery-performance` and `goal-attainment`;
- `material-reconciliation`, `genealogy-trace` and `receiving-inspection`;
- `maintenance-readiness` and `tool-use-history`.

These are ready-made sources for simulation KPIs.

**Background jobs.** None run unless `Forge:BackgroundServices:Enabled` is true:
- `OutboxRelayService`;
- `SubscriptionPumpService`;
- `PropagationRetryService`, which re-runs engineering-change propagation;
- `NotificationDeliveryWorker`.

**Integrations:**
- SCIM provisioning.
- Sentinel security-event subscriber.
- Notification delivery.
- The Analyst narrator, an outbound HTTP client to an LLM.
- Label printing, as recorded print requests and observations (`LabelService.Print.cs`). There is no physical printer driver, so a "failed print" is a recorded observation.

No Nexus or email transport is present in source. Live configuration of these remains external, as earlier handoffs state.

### 1.2 Existing simulation and test infrastructure to reuse

| Asset | What it gives the simulation |
|---|---|
| `ForgeApp` (`tests/Forge.Integration.Tests/ForgeApp.cs`) | The **real host** through `WebApplicationFactory`, with Development authentication. Each test signs in by subject through `As(subject)`. Background services are off, so tests drive the outbox relay deterministically. `TimeProvider` can be replaced, as `HistoricalExecutionGovernanceTests` already does. |
| `PostgresFixture` | A disposable database per test, restored from a migrated `pg_dump` baseline (V3.28), and dropped afterwards. **This meets the isolation requirement:** simulations never touch the live `forge` database. |
| `OriginalReferenceFactory` | The closest thing to a synthetic factory. It builds 200 records per population entirely through ordinary authenticated commands: items, BOMs, routings, orders, POs, workers, transformations, material requests, maintenance and shipments. It verifies the results with SQL. |
| `SlittingPlant` and the scenario builders (`AuditCapaScenario`, `QmsPlanningScenario`, `QmsPolicyScenario`, `ScanSessionScenario`) | Reusable fixtures for single domains: plant setup, roles, signed-in actors, `Ok` and `Try` helpers. |
| `HttpExtensions.CommandAsync(..., idempotencyKey)` | Replays the same command key, the basis for idempotency and "network interruption" checks. |
| Concurrency patterns | `Task.WhenAll` races already exist, for example in `V311Tests.Concurrent_customer_returns_cannot_exceed_the_shipped_quantity` and the CAPA write races. |
| Planning engines (`Forge.Planning/Engines`, about 20 engines plus calendar and snapshot helpers) | Production logic for capacity and scheduling: MRP, scheduling, executable capacity, personnel composition, reallocation impact, maintenance readiness, outage preview, goal risk, drop-dead dates, change impact and others. `PlanningScenarioService` already runs the real finite-capacity and skills engines on copied inputs. It is the existing in-product "what-if" engine, and it is limited to a 62-day horizon. |
| `Forge.Planning.Tests` | Fast engine-level tests (`TestFactory`) for scheduling, readiness, reallocation, MRP and shelf life. Useful for checking the engines' results at 30, 60 and 90 days without the full host. |
| `tools/seed-sample-plant.mjs` | Demo data through the real API, against a running server. It is not reproducible from a seed, so it suits demos, not validation. |
| UI suite (`tests/ui`, 213 checks) | Browser-free checks of page logic and request bodies. "The dashboard renders" is never counted as simulation evidence. |

**There is no existing seeded event generator, discrete-time clock driver, invariant checker library, KPI baseline store or long-running simulation runner.** These are the new components proposed in §3.

### 1.3 Existing integration coverage by API area

The table counts the integration test files that call each area, out of 113. It shows where a simulation can lean on existing behavior and where coverage is thin.

| Area | Files | Area | Files | Area | Files |
|---|---:|---|---:|---|---:|
| Orders | 54 | Kernel/access | 51 | Engineering items | 49 |
| Workforce | 36 | Resources | 38 | Programs | 29 |
| Procurement | 24 | Documents | 24 | Shop floor | 24 |
| Reports | 24 | Inventory issues | 22 | Analyst | 20 |
| Maintenance | 16 | Shipments | 14 | QMS | 13 |
| Demand | 12 | Schedules | 11 | Holds | 11 |
| Competency | 11 | Tools/calibration | 10 | Identification | 9 |
| MRP | 7 | Readiness | 7 | Execution actuals | 7 |
| Inspections | 6 | NCR | 6 | Transformations | 6 |
| Goal risk | 6 | CAPA | 5 | Projects | 5 |
| Scenarios | 5 | Engineering changes | 4 | Receiving | 4 |
| Containers | 4 | Counts | 3 | Alternates | 3 |
| Notifications | 3 | Safety rules | 2 | Acceptance | 2 |
| Commercial quality | 2 | Work inbox | 2 | Trace investigations | 1 |
| Internal audits | 1 | SCIM | 1 | | |

**Thin areas the simulation will add cross-module coverage to:**
- engineering changes;
- alternates;
- receiving and counts;
- safety rules;
- acceptance and investigations;
- MRB and use-as-is, including use-as-is approval;
- the propagation of a single event across modules.

---

## 2. Coverage matrix (output 1, first cut)

**Legend:**
- **I** is an individual-module scenario.
- **X** is a cross-module scenario.
- **F** is the full-factory run.
- *Exists* means current tests already exercise the area.
- *Gap* means no current test.
- *Not implemented* means the amendment names it but Forge does not have it. These are reported as untested, never as passing.

| Amendment area | Forge implementation | Current evidence | Simulation level |
|---|---|---|---|
| Demand management | `planning/demand` | Exists (12 files) | I, X, F |
| Planning and scheduling (30/60/90 days) | `SchedulingEngine`, schedules | Exists. The 90-day horizon is a **gap**: scenarios are capped at 62 days. | I, X, F |
| MRP | `MrpEngine` | Exists (7) | I, X, F |
| Procurement and suppliers | `procurement`, sourcing | Exists (24) | I, X, F |
| Receiving and inspection (partial, FAI) | `inventory/receiving`, `quality/fai` | Thin (4) | I, X, F |
| Quality holds (scope propagation) | kernel holds | Exists (11). Downstream-operation effect across modules is a **gap**. | X, F |
| NCR and MRB dispositions (scrap, rework, repair, RTV, use-as-is, concession) | quality NCR, rejections, RTV, customer returns | Exists for each disposition. Cross-module consequences (capacity, supplier, delivery) are a **gap**. | X, F |
| CAPA | V4.5 CAPA governance | Exists (5 plus rules) | I, X |
| Engineering changes and revisions | `engineering/changes`, propagation | Thin (4) | I, X, F |
| Document control | `engineering/documents` | Exists (24) | I, X |
| Material allocation and approved alternates | MRP, issues, alternates | Thin (3). The V5 allocation optimizer is **not implemented**, so only current allocation is tested. | X, F |
| Manufacturing and execution | orders, shop floor, actuals, transformations | Exists | I, X, F |
| Workforce: shifts, callouts, PTO, transfers, overtime | workforce, presence, unavailability, reallocation preview | Exists (36). **Overtime is not modeled** as its own concept; it would be tested through shift and presence only, and reported as such. | X, F |
| Training and qualification | competency, document training | Exists (11) | I, X |
| Safety (two-person, lone work) | safety rules, readiness | Thin (2) | I, X, F |
| Maintenance (breakdowns, overdue PM) | maintenance, downtime, readiness | Exists (16) | I, X, F |
| Tooling and calibration expiry | resources/tools, measurement findings | Exists (10) | I, X, F |
| Warehouse (locations, counts, containers, shelf life) | inventory | Exists, with counts and containers thin | I, X |
| Shipping | shipments, product release consumption | Exists (14) | I, X, F |
| Identity, labels and scans (incorrect scans, reprint) | identification, scan sessions, labels | Exists (9) | I, X, F |
| Printing (failed printing) | label print request and observation | Exists as records only. **No device driver**, so physical printing is not testable. | I, X |
| Reporting and KPIs | 34 reports, Analyst | Exists (24) | used as KPI sources |
| Infrastructure: outbox, subscriptions, propagation retry, notifications, recovery | kernel, Host | Exists in part (V3.28 backup and restore) | resilience scenarios |
| Network interruptions | none in source | **Gap**. Simulated at the HTTP client with idempotent retries, no production change. | resilience |
| Concurrent transactions | command pipeline and optimistic versions | Exists in a few races | concurrency scenarios |
| Customer concessions | commercial quality and use-as-is | Exists (2) | X |
| Economic consequences ("economically meaningful") | partial: delivery, goal attainment, scrap. **No costing module**. | **Not implemented** as money. KPIs would be in quantities, hours and late days, unless the owner supplies standard costs as test input. | KPI design decision (§8) |

---

## 3. Proposed test architecture

Everything below is **test code**. It adds no production code, schema or workflow.

### 3.1 Placement

1. **New test project `tests/Forge.Simulation.Tests`.** It references `Forge.Host` the way the integration tests do and reuses `ForgeApp` and `PostgresFixture`. The shared fixtures could move into a small `tests/Forge.TestSupport` project; that is a refactor of test code only.
2. **Two run modes:**
   - **Scenario mode**, which belongs in the gate: a few minutes per scenario, small factory, each scenario on its own disposable database. It runs in `verify.cmd`, or in a separate `simulate.cmd` if gate time matters (§8).
   - **Factory mode**, the long run: 150–500 orders, 45–100 operators, 90 days of simulated time. It runs only through `simulate.cmd --factory --seed N`, never in the gate, and writes its outputs to `.forge-logs/simulation/<seed>/`.

### 3.2 Components (new)

| Component | Responsibility |
|---|---|
| `FactorySpec` and `FactoryBuilder` | Turns a seed and size into a synthetic organization, built **only through real authenticated API commands**, extending the `OriginalReferenceFactory` pattern. It creates: <ul><li>eight or more departments (work centers or safety areas) and shifts;</li><li>45–100 workers with qualifications;</li><li>multi-level BOMs and routings with WIP and alternates;</li><li>suppliers with lead-time distributions, customers and priorities;</li><li>programs and contracts;</li><li>tools and calibration;</li><li>assets with PM;</li><li>safety rules.</li></ul> |
| `SimClock` | A test `TimeProvider` advanced in fixed steps (for example 15 minutes, or a shift). All dates come from the clock, never from wall time. |
| `EventGenerator` | Seeded with `System.Random(seed)` and a pinned algorithm. It produces a timestamped, reproducible **event log** (JSON lines) covering every disruptive event in the amendment. The log is written before execution, so a run can be replayed from the log alone. |
| `EventAppliers` | One per event type. Each turns an event into the real commands a person or terminal would issue. Examples: <ul><li>a callout records unavailability;</li><li>a breakdown records downtime;</li><li>a supplier delay changes the PO date;</li><li>an inaccurate inventory event runs a count with a variance;</li><li>an incorrect scan is a wrong-identity scan;</li><li>a failed print records a failed observation;</li><li>a network interruption drops the response, then retries with the same idempotency key;</li><li>concurrency fires parallel commands.</li></ul> The applier **records the API outcome** (accepted, refused with code, or denied) without overriding it. |
| `ExpectedOutcomes` | Written **independently** of Forge's logic, from the scenario definition. Example: "unavailability of the only SLIT-II operator on shift B makes operation 20 of orders X and Y not executable, and the two-person rule blocks SLIT-AREA." These are hand-derived oracles for scenarios, and property-style bounds for the factory run. |
| `Invariants` | Run after each step and at the end. They are queried through APIs and reports, with read-only SQL for cross-checks only: <ol><li>inventory conservation, ledger sum equals stock, with issued equal to consumed plus scrap, loss, remnant and remaining;</li><li>unique physical identifiers;</li><li>complete genealogy: every output traces to inputs, with the V5-S1 parent edges;</li><li>idempotency: a replay is applied once;</li><li>authorization: no command succeeds without permission, and no disposition happens without its approver;</li><li>no silent release: held material never issues or ships;</li><li>schedule feasibility: no booking beyond capacity or on a down machine;</li><li>audit-chain integrity: `/api/kernel/audit/verify`;</li><li>reproducibility: the same seed gives the same event log and the same final hashes.</li></ol> |
| `KpiCollector` | Reads KPIs from existing reports and engines at baseline, immediately, and at 7, 30 and 90 days: <ul><li>attainment, on-time delivery and late days;</li><li>executable capacity hours by qualification;</li><li>WIP and inventory by status;</li><li>scrap, rework and yield;</li><li>holds and NCR age;</li><li>maintenance readiness and tool validity;</li><li>goal risk.</li></ul> It compares each scenario with a no-event baseline run on the same seed. |
| `ImpactMatrix` | Records, for each event, which modules' state changed and which reports or KPIs moved. That record becomes the cross-module dependency matrix (output 4), built from observed evidence, not drawn by hand. |
| `Reporter` | Produces outputs 5–10: KPI comparisons, the consequence windows, expected versus actual, the defect register (with seed, event index, request and response, and invariant evidence), performance (command latency percentiles, run time), concurrency and recovery results, and the readiness scorecard. Areas not tested or not implemented are listed explicitly. |

### 3.3 Scenario library (output 3, initial set)

Each scenario has a fixed seed, its own disposable database, explicit expected outcomes and pass/fail criteria. Each one also runs every invariant.

1. **Operator callout:** the only qualified operator calls out. Executable capacity and affected orders recalculate, the reallocation preview runs, and a trainee assignment is compared against the safety slot. Measured: the immediate gain versus the loss at 7 and 30 days.
2. **PTO and training restriction:** a planned absence plus a qualification suspension. The schedule and readiness change.
3. **Staffing transfer with overtime:** reported as a shift or presence change only, because overtime is not modeled.
4. **Machine breakdown and overdue PM:** downtime, rescheduling, maintenance readiness and delivery risk.
5. **Calibration expiry mid-run:** tool validity, blocked inspections, and the measurement-finding trace of past uses.
6. **Supplier delay:** PO date slip, MRP shortage, alternate approval, and the schedule effect.
7. **Inaccurate inventory:** a count variance, approval by a second person, a shortage and replanning.
8. **Receiving with partial inspection and FAI required:** partial accept or reject, a supplier rejection, an RTV with credit, and a replacement lot.
9. **Quality hold scope:** a lot hold blocks issue, start and ship for that lot and its descendants only; sibling lots continue. This tests the propagation rule.
10. **MRB dispositions, one per route:** scrap, rework, repair, RTV, use-as-is with its approval, and a customer concession. Each is checked for authorization, evidence, inventory, capacity, supplier and genealogy consequences, and an unauthorized attempt is refused.
11. **Engineering revision:** a change released mid-production, propagation, document training gating, and the effect on WIP.
12. **Expedited order:** a priority change, displaced orders and the delivery trade-off.
13. **Document error:** a superseded or withdrawn document blocks dependent work.
14. **Incorrect scan and failed print:** a wrong identity is refused, and a failed print is retried with no duplicate stock.
15. **Network interruption:** dropped responses on commands, retried with the same key, applied exactly once.
16. **Concurrent transactions:** parallel issue, ship, disposition and approve on the same material, with no over-consumption and one winner.
17. **Recovery:** a backup and restore mid-run (reusing V3.28); the restored host continues and the invariants hold.
18. **Full factory, combined:** 30, 60 and 90 days at the chosen size, with every event type mixed by the generator.

### 3.4 Integrity rules (the amendment's prohibitions, as design rules)

- Every business effect goes through the real host and real services. Simplified mock calculations are never presented as validation. Engine-only checks are labeled "engine level".
- A test may fail for a correct reason. **Refusals are outcomes to compare, not errors to suppress.**
- No test is marked passing unless its expected-outcome assertion executed. Untested and not-implemented areas appear in the scorecard by name.
- Simulations never use the live `forge` database or real connectors.

---

## 4. Known limits and risks (stated up front)

- **Horizon:** `PlanningScenarioService` caps simulated outages at 62 days. Ninety-day consequences come from schedule, MRP and goal reads after advancing `SimClock`, not from the scenario workspace. If any engine has a hard horizon, the 90-day result is reported as bounded by that horizon.
- **Money:** there is no cost model. "Economically meaningful" is either measured in quantities and time, or uses owner-supplied standard costs applied **only in the reporter** (§8).
- **Overtime and transfer time** are not first-class concepts. They are modeled through shifts, presence and assignment, and reported as approximations.
- **Physical printing and scanners:** only their recorded behavior is tested. Device behavior stays external, as in V4.
- **Run time:** a full integration suite already takes about 1.7 hours. A 500-order, 100-operator, 90-day run through the real API could take hours. It is a separate command, with sizes chosen by the owner.
- **Background jobs** are driven explicitly in tests (outbox relay, subscriptions, propagation retry), so the order of effects is deterministic. One resilience scenario runs them as hosted services to check they converge.

## 5. What would need production approval

Nothing in phases A–C (§6) needs a production change. Approval would be needed only if the simulation finds a defect that requires a fix, or if the owner wants these first-class:
- a cost model;
- overtime as its own concept;
- a 90-day scenario workspace;
- a fault-injection hook inside the host.

Each would be raised as a separate, bounded proposal.

## 6. Proposed phases

| Phase | Output | Gate |
|---|---|---|
| A | `Forge.Simulation.Tests` skeleton, `SimClock`, seeded `EventGenerator` and log, invariant library, `FactoryBuilder` (small size). Scenarios 9, 10 and 15 as the first proof. | Scenarios pass or fail against independent expectations, and the same seed reproduces the same log and final state hashes. |
| B | Scenarios 1–17, KPI collector, impact matrix, defect register. | Every implemented module has at least one I and one X scenario, and the scorecard lists untested areas. |
| C | Full-factory mode (sizes chosen by owner), 7/30/90-day consequences, performance and concurrency findings, readiness scorecard. | Outputs 1–10 delivered from executed runs only. |
| D (only if approved) | Production fixes for defects found, or the model extensions listed in §5. | Separate approval each. |

## 7. Relationship to the current V5 state

- V5-S1 (child identity and genealogy) is implemented. As of this writing it is being reverified by a full `verify.cmd` on the final tree, and its parent-edge rule becomes one of the simulation invariants.
- The excluded V5 items stay excluded unless separately approved: allocation optimizer, forecasting, new MRB features and external connectors. The simulation tests **current** allocation, MRB and connector-free behavior, and reports the absence of the optimizer as "not implemented".

## 8. Decisions needed from the owner

1. Approve phase A (test code only, no production changes).
2. **Factory size** for the long run: for example 300 orders and 70 operators to start, with 500 and 100 as a later run.
3. **Gate placement:** add scenario mode to `verify.cmd` (adds an estimated 20–40 minutes), or keep it in a separate `simulate.cmd`.
4. **Economics:** report in quantities and time only, or supply standard costs per part, hour and scrap for the reporter.
5. Treat overtime as an approximation (shift and presence), or schedule a separate production proposal to model it.
