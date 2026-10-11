# JIRSN Forge V3 Plan

Oct 10, 2026 · @John

## Summary

V3 covers both the rest of master spec v3.0 and every section, A to CC, of your V3 implementation brief. That takes 28 increments, V3.1–V3.28, in six stages, with no restart.

Phases 1–9 and V2 already deliver about a third of the brief:

- executable capacity and readiness;
- document control and change control;
- alternates, MRO, assets and projects;
- personnel composition and safety;
- serial genealogy;
- the Analyst and reporting.

All five critical integration tests (§77–81) pass at commit ad9a482.

The largest gap is the brief's material model:

- physical instances with quantities;
- transformation with remnants, and genealogy as a graph;
- custody kept apart from location;
- requested, allocated, picked and delivered kept apart.

After that come tooling, sourcing, holds, configurable authority, shift continuity and notifications.

Four things in the code conflict with the brief and need your decision before the stage that touches them:

- roles are fixed in code;
- one stock status mixes quality with availability;
- gauges have only a due date;
- Forge keeps PTO records that the brief gives to JIRSN Core.

Recommendation: approve the stages in order. Stage A (material foundations) goes first because most of the brief builds on it. V3.7 then takes over the paused goal-risk slice.

## Where Forge stands

24 of the spec's 32 milestones are done. Six are partial and two are missing. I checked each one against the code, ADRs 0001–0024 and the test names at ad9a482, not against plans.

| Milestone (spec §) | State | What exists | What is missing |
| --- | --- | --- | --- |
| M1–M13 core platform to scheduling (§3–41) | Done | Kernel, audit chain, events; items, BOM, inventory, programs, demand, MRP, resources, workforce, orders, readiness, composition, safety, executable capacity, scheduler | — |
| M14 constraints, drop-dead dates (§42–43) | Partial | Constraint and drop-dead engines | Goal risk: "behind plan but recoverable" kept apart from "goal at risk". The other tool's paused slice is in your folder, uncommitted |
| M15 availability, PTO forecast, reallocation (§19–20, §35–36) | Done | §78, §79, §81 pass | — |
| M16–M17 shop floor, badges, training, signoff (§21–25, §44–45) | Done | §80 passes; electronic signoff is append-only | Training documents (§26): acknowledgments are recorded, but nothing stops someone who hasn't acknowledged a new work instruction from working after it takes effect |
| M18 warehouse (§49) | Partial | Locations, receipts, issues, returns, transfers, holds, counts, lots and serials | Picking, kitting, staging to a work center, put-away tasks, scanner flows, cycle-count programs, staged material as a readiness input |
| M19–M22 procurement, quality, maintenance, shipping (§46–50) | Done | Including V2 suppliers, MRO, assets, outages | — |
| — indirect work (§34) | Partial | Machine-less activities exist for safety rules | Labor on support work (staging, kitting, cleaning, 5S, downtime assignment) |
| M23 workflow, approvals | Partial | 20+ two-person approvals, each on its own page | One inbox of everything waiting for you as the second person |
| M24 Horizon, CauseMap, What changed? (§51–55) | Partial | Factory and Program Horizon; windows from -24h to +1w; schedule comparison; history per subject (V2.12) | CauseMap; a "Why?" chain linking each step to its evidence; -7d, -8h and +2w windows; order, work-center, machine, workforce and contract lenses |
| M25 scenarios, impact preview (§56, §27) | Partial | Analyst simulate (machine down, absence); reallocation and outage previews | A scenario workspace for priority, operation moves, overtime, added operators or trainers, supplier delay; current and proposed side by side; Skills MRP deficit and "qualify two more" |
| M26–M28 reporting, Analyst, briefs (§57–67) | Done | 25 standard reports, exports, parser, briefs | — |
| — alerting (§68) | Missing | Constraint findings only | Alerts at levels Information, Watch, Action required and Critical, with confidence kept separate from severity; acknowledgment; grouping to avoid alert fatigue |
| M29 Nexus (§70) | Partial | Any OIDC provider validates sign-in | Identity lifecycle: someone deactivated in Nexus is deactivated in Forge (SCIM) |
| M30 Sentinel (§71) | Missing | Security events are marked for Sentinel | Forwarding them; a PIN lockout counter (the known gap in ADR 0005) |
| — program segregation (§72) | Partial | Program access and document markings | Contract-level access |
| M31 optional generative AI (§61) | Done | Narrator, off by default | — |
| M32 hardening | Missing | 314 automated tests | Load, backup and restore, outage tolerance, accessibility |

## Coverage of the V3 brief

Every section of the brief maps to an increment or to work already done. I classified each one against the code at ad9a482. Sections that only state principles or working rules (BL, BO–BQ, BS–BW, BX–BZ, CB–CC) apply to every increment and are listed once at the end.

| § | Brief section | State | Today | Closed by |
| --- | --- | --- | --- | --- |
| A | Goal-driven manufacturing | Partial | Demand, MRP, schedule, attainment, constraints; goal risk in your folder, uncommitted | V3.7, V3.23 |
| B | Executable capacity | Partial | Equipment, safety, workforce, calendars, qualifications, material, maintenance, documentation dimensions | V3.9 (adds tooling, point of use, holds) |
| C | Material transformation | Missing | One output per order; scrap recorded; no byproducts or remnants | V3.3 |
| D | Material identity | Partial | Lots and serials; no roll, reel, sheet, piece, container or pallet | V3.2 |
| E | Material quantity, reconciliation | Partial | Ledger quantities per lot | V3.2, V3.3 |
| F | WIP and remnants | Missing | — | V3.3 |
| G | Genealogy graph | Partial | As-built record (V2.11) is one-way, order to component | V3.3 extends it; no second genealogy |
| H | Location, custody, status | Conflicting | Flat locations; one status mixes quality and availability; no custody | V3.1, decision 1 |
| I | Material movement | Partial | Transfers, issues, returns; no request, allocation, pick or delivery states | V3.4 |
| J | Return to storage | Partial | Returns to stock; identity kept for lots only | V3.3 |
| K | Split, merge, rework | Partial | NCR dispositions use as is, rework, scrap, return to supplier; no split or merge | V3.2, V3.5 |
| L | Production quantity states | Partial | Good and scrap reported; inspections; receipts | V3.5 |
| M | Aging, shelf life, storage | Partial | Shelf-life days on MRO policy only | V3.6 |
| N | Selection policy | Missing | — | V3.6 |
| O | Receiving and supplier quality | Partial | Receipt to inspection to accept or hold; NCR to supplier | V3.11 |
| P | Supplier return (RTV) | Missing | Return-to-supplier disposition only | V3.11 |
| Q | Business-record evidence | Partial | Reference attachments on orders, POs, work orders, projects, NCRs (V2.2) | V3.12 (receipts, inspections, shipments) |
| R | Receiving inspection, FAI | Missing | Pass or fail only | V3.1 (determinations), V3.11 |
| S–U | RFQ, technical configuration, quotes | Partial | Technical package frozen at PO approval (V2.5); no RFQ or quote | V3.13 |
| V | Document control | Implemented | V2.2–V2.3 | — |
| W | Change control | Implemented | V2.4–V2.5 | — |
| X | Alternates | Implemented | V2.6 | — |
| Y | Maintenance, facilities, MRO | Implemented | V2.7–V2.10 | Tooling and engineering demand in V3.8, V3.13 |
| Z–AA | Tooling, calibration, service | Partial | Gauges with a due date, checked at inspection | V3.8, decision 2 |
| AB | Workforce | Implemented | Qualifications, shifts, assignment, labor | Ownership with JIRSN Core: decision 3 |
| AC | Presence confidence | Partial | Clocked in, supervisor confirmed, projected; active on operation from labor | V3.9 |
| AD–AF | Nexus, standards, authentication ≠ authorization | Partial | OIDC sign-in, no Forge passwords; roles, qualifications and program access kept apart | V3.16 |
| AG | Configurable roles and permissions | Conflicting | 20+ roles fixed in code | V3.15, decision 4 |
| AH | Contextual authorization | Partial | Program access and markings | V3.15 |
| AI–AJ | Step-up, shared workstations | Missing | Terminal role and badge + PIN at the shop floor | V3.16 |
| AK | Electronic signoff | Partial | Person, method, action, subject, meaning, time, terminal | V3.16 (role, assurance, record version) |
| AL | Delegated authority | Missing | — | V3.15 |
| AM | Separation of duties | Partial | Two-person rules fixed per command | V3.15 |
| AN–AO | Safety, personnel composition | Implemented | Safety rules engine; composition engine | — |
| AP–AQ | Shift pass-on, supervisor handoff | Partial | Brief me (§67) builds a shift brief | V3.18 |
| AR | Priority governance | Partial | Priority set at order creation | V3.17 |
| AS | Generalized holds | Partial | Stock holds, NCR and inspection holds, separately | V3.10 |
| AT | Downtime and changeover | Partial | Unplanned and planned downtime, maintenance overdue | V3.5 |
| AU | Outside processing | Missing | — | V3.14 |
| AV | Packaging and shipping materials | Missing | Item classes include consumable | V3.14 |
| AW | Shipment traceability | Partial | Shipments with lots and serials | V3.11 (customer returns), V3.14 |
| AX–BD | Notifications, rules, security, assignment, arrival, deviation, resolution | Missing | Constraint findings and a bell that opens Scheduling | V3.19, V3.20 |
| BE | Shift closeout | Missing | — | V3.18 |
| BF | Data-integrity exceptions | Partial | Ledger, audit chain and database checks prevent many states | V3.21 |
| BG | Recovery planning | Partial | Reallocation and outage previews | V3.23 |
| BH–BI | What changed?, CauseMap | Partial | Schedule comparison; history per subject (V2.12) | V3.22 |
| BJ | Impact preview | Partial | Analyst simulate for machine down and absence | V3.23 |
| BK | Forge Analyst questions | Partial | 20+ intents | Each increment adds its questions; V3.26 |
| BM | Reporting | Implemented | XLSX, CSV, print, saved definitions | — |
| BN | Reference factories | Partial | Healthy and unusual-but-valid reference factories at tens of records; constrained cases spread across §77–81 | V3.27 |
| BR | Universal questions | Partial | Why, What changed and Show evidence on some screens | V3.25 |
| BU | Extensibility test | Missing | — | V3.27 |

Principles that apply to every increment:

- **BL:** human authority.
- **BO–BP:** one event model and one audit trail, both already in place.
- **BQ:** the UX principle.
- **BS:** the core distinctions.
- **BX:** the test invariants.
- **BY–BZ:** security and performance.
- **CB:** each increment's report takes the brief's 20 headings.

## What V3 covers and leaves out

V3 covers every partial, missing or conflicting row in both tables above. It keeps every rule V1 and V2 established:

- deterministic engines;
- two-person commands;
- evidence for every claim;
- program segregation;
- nothing applied without an authorized person.

New state gets the same treatment as before: audited commands, outbox events, and tests through the real API.

Left out, with the reason:

- **Accounting and payroll.** Forge keeps operational cost, labor time and a financial reference. Ledgers and pay belong to the ERP and payroll systems; the spec says Forge does not calculate payroll (§18, §21).
- **Predictive analytics and machine learning.** Forge projects only from recorded facts. A prediction would sit beside evidence without being evidence (§85–86).
- **A free-form query language.** The native parser and the fixed report catalog keep every figure defined and testable (ADR 0010). M27's "manufacturing query language" is met by the parser plus conversational reporting.
- **Automatic recovery actions.** Scenarios, goal risk and alerts explain and propose; only an authorized person commits (§84).
- **SAML.** OIDC covers Nexus and the common enterprise providers. SAML is added only if a customer's provider needs it (§70, brief AE).
- **Entity Framework.** The brief mentions EF Core. Forge uses raw SQL through Npgsql, with ordered SQL migrations per module and 52 migrations already in place. Switching would rebuild working persistence, which the brief's own rules forbid (BV 4, 8, 9).
- **HR, payroll preparation and PTO administration.** These belong to JIRSN Core (brief BT). Forge keeps only what manufacturing needs: who is unavailable, and when (decision 3).
- **A second inventory, genealogy, workflow, authorization, evidence or event system.** Every increment extends the existing one (brief BU).

## Increments

There are 28 increments in six stages, ordered as the brief's BW asks: foundations before what depends on them. Each one is built and verified like V2. Each one delivers a domain model, a migration, commands with reasons and two-person rules, events, an API, UI, a report and Analyst questions, an ADR, sample data, and tests through the real API.

| Increment | Brief / spec | What it builds | Needs |
| --- | --- | --- | --- |
| **Stage A — Material foundations** |  |  |  |
| V3.1 States and locations | H, R, BS | **Determinations.** Moved to V3.11, where receiving inspection and FAI first use them (no primitive without a user, brief BV 10).\<br>**Stock.** Quantity, location, custody, quality status, availability and allocation are separate fields.\<br>**Locations.** A hierarchy from facility to position, with kinds: receiving, inspection, quarantine, staging, machine-side, external processor, shipping, in transit. | — |
| V3.2 Material identity and quantity | D, E, K | Physical instances (lot, batch, roll, serial, reel, sheet, piece) whose quantity is the ledger sum. An instance keeps its identity through moves. Splitting creates new instances that add up exactly; merging only where a part's merge policy allows. Every split and merge is a recorded link (lineage). Packing into containers and pallets moves to V3.14. | V3.1 |
| V3.3 Transformation and genealogy graph | C, E, F, G, J | Operations record inputs, outputs, byproducts, scrap and remnants. Remnants are ordinary instances. Genealogy is a graph with the brief's 13 relationship kinds, and the as-built record becomes part of it. A move never creates a genealogy link. Issued = consumed + returned + scrap + loss + remaining ± tolerance; anything unexplained is flagged, never scrapped. Returns to storage keep their identity | V3.2 |
| V3.4 Movement and point of use | I, BB, §49 | Required, requested, allocated, picked, delivered, issued and consumed are separate states, and each can be partial. Staging, kitting and put-away are tasks confirmed by a scan. Cycle-count programs. A material-flow constraint ("in the warehouse, not at the machine") is kept apart from a shortage | V3.2 |
| V3.5 Production actuals and quantity states | L, K, AT | Quantity states from started through produced, pending inspection, accepted, released, hold, rework, scrap, packed and staged to shipped. Rework, repair, use as is and scrap are distinct, and rework never rewrites history. Downtime splits into planned, unplanned, setup, changeover, maintenance, waiting, starved and blocked. A reason code is kept apart from a proven root cause. Expected and actual changeover times | V3.3 |
| V3.6 Aging, shelf life and selection | M, N | Age, shelf life, expiration, open-container life, retest, storage conditions and excursions are tracked separately. A shelf-life extension needs authority and keeps the original date. FIFO and FEFO apply under compatibility, quality, program and shelf-life rules, never over them | V3.2 |
| **Stage B — Goals and capacity** |  |  |  |
| V3.7 Goal attainment and reforecast | A, AW, §42 | Takes over the paused goal-risk slice. Goals are broken down and required-by dates cascade. Remaining work and actuals give a projected completion. Behind plan is kept apart from goal at risk, and production goals from delivery goals | V3.5 |
| V3.8 Tooling and calibration | Z, AA | Tools as resources: identity, type, serial, location, custody, condition, eligible machines and operations, usage count and limit. Calibration intervals, history and certificates. Service cycles, such as a blade out for sharpening. Validity is judged at the time of use. Today's gauges move into this model | V3.1 |
| V3.9 Executable capacity and presence | B, AC, §26 | New readiness dimensions: tooling, material at point of use, holds, and training on controlled documents (policy off, warn or enforce). Presence levels: scheduled, clocked in, assigned to work center, active on operation. Every operation answers "Can this happen? Why not?" | V3.4, V3.8 |
| **Stage C — Quality, sourcing, evidence** |  |  |  |
| V3.10 Generalized holds | AS | Quality, material, production, engineering, customer, shipping, procurement, configuration, safety and administrative holds. Each has a scope, source, evidence, affected records, required resolution and its own release authority. Today's stock, NCR and inspection holds become kinds of these | V3.1 |
| V3.11 Receiving inspection, FAI and returns | O, P, R, AW | Partial acceptance. FAI states: required, not required, not applicable, pending determination. Structured results plus a signed document where policy asks. Supplier dispositions. The return-to-vendor lifecycle, where replacement material is a new instance. Customer returns | V3.3, V3.10 |
| V3.12 Business-record evidence | Q, R | Evidence on receipts, inspections and shipments: packing slip, CoC, CoA, measurements, photos, FAI. It extends the V2.2 reference attachments and is never treated as controlled engineering documents | V3.11 |
| V3.13 RFQ, sourcing and quotes | S, T, U, Y | Sourcing needs from any demand source, including tooling and engineering. RFQs carry the resolved technical package as sent. Supplier responses and structured quotes keep the original attached. Quote comparison, then authorized source selection, then requisition and PO. A proposed alternate is not an approved one | V3.12 |
| V3.14 Outside processing, packaging, shipping | AU, AV, AW | Material sent to an outside processor stays company-owned and changes custody. It returns with its certificates and is inspected. Instances are packed into containers and pallets (moved here from V3.2). Packaging and shipping materials get their own classes, and packaging can block shipment readiness. Partial shipments and corrections | V3.4, V3.11 |
| **Stage D — Authority** |  |  |  |
| V3.15 Configurable authority | AG, AH, AL, AM, §72 | Roles as data, seeded from today's roles, with business-action permissions. Scope by facility, work center, program, contract, customer and shift. Approval thresholds and configurable separation-of-duty rules. Delegated authority that expires on its own | — |
| V3.16 Identity, step-up, workstations, signatures | AD–AF, AI–AK, §70–71 | OIDC plus SCIM deactivation from Nexus. Step-up through the provider's assurance level. Shared terminals: the current user is visible, sessions lock after inactivity, users switch quickly. Signatures record role, assurance and record version. Security events go to Sentinel; repeated wrong PINs lock the badge | V3.15 |
| V3.17 Priority governance | AR | Each priority change is a command with a reason, the authority used and an impact preview ("moving A delays B"). Priority never overrides safety, quality, configuration, qualification, maintenance or compatibility | V3.15 |
| **Stage E — Shift continuity and notifications** |  |  |  |
| V3.18 Shift pass-on, handoff, closeout | AP, AQ, BE | Pass-on and handoff are built from current state; operators add context. Work-center checklists. Created, reviewed, acknowledged and action completed are separate states. Closeout reconciles production, material, WIP, downtime, tooling and open issues without re-entry | V3.5, V3.8 |
| V3.19 Notifications and alerting | AX–BD, §68 | Rules with trigger, condition, scope, severity, recipients by role, channel, template, delay, reminder, escalation, quiet hours and deduplication. In-app and email through a provider abstraction. Deep links grant nothing. A failed delivery never undoes the transaction. Alert levels with confidence kept separate. Resolution notices when the condition clears | V3.7, V3.15 |
| V3.20 Approvals inbox and assignments | M23, BA | Everything waiting for you as the second person, plus assigned work (NCRs, RFQs, inspections, work orders, reviews, material requests). Assigned, acknowledged, in progress and completed are separate states | V3.19 |
| V3.21 Data-integrity exceptions | BF | Detectors for the brief's impossible states, such as negative stock, a serial in two places, consumption before issue, or expired calibration in use. Each is reported as a data or process exception with evidence, never as misconduct | V3.3, V3.8 |
| **Stage F — Explain and simulate** |  |  |  |
| V3.22 What changed and CauseMap | BH, BI, §54–55 | When did this become a problem, and what was true then. A cause appears in the chain only when a record shows it acted on the next step. Constraints observed at the same time are shown as correlated, with no claim that one caused the other | V3.7 |
| V3.23 Scenarios and recovery options | BG, BJ, §27, §56 | Private, isolated scenarios. Skills MRP. The brief's 15 recovery options, each with feasibility and consequences. Apply runs the real commands with their own permissions | V3.7, V3.22 |
| V3.24 Indirect and support work | §34 | Badged support activities count as attendance, not direct labor. Reallocation offers them when a machine cannot run | V3.4 |
| V3.25 Horizon lenses and universal questions | §51–53, BR | Windows from -7d to +2w, with the past rebuilt from records. Lenses for factory, program, contract, work center, machine, order and workforce. Why, What changed, What does this affect, When did this become a problem, Show evidence, What are my options and What happens if, from shared engines | V3.22, V3.23 |
| V3.26 Analyst and reporting surfaces | BK, BM | The brief's remaining questions (custody, what material became, remnants, calibration at time of use, last shift, outside processors, recovery options) and their reports | Stage F |
| V3.27 Reference factories and extensibility | BN, BU | Healthy, constrained and strange-but-valid factories with hundreds of records and injected disruptions. One workflow the brief does not describe, modelled from existing primitives, with any weakness written up | V3.26 |
| V3.28 Hardening | M32, BY, BZ | Performance budgets at reference-factory scale. A backup and restore drill. Terminal behavior during outages. Accessibility. A dependency security scan | all |

&#91;embedded content: V3 roadmap · 6 stages, 28 increments\]

Increments with nothing in their Needs column (V3.15 authority) can run alongside Stage A when a long verification leaves room.

## Tests that close each increment

An increment is done when its own tests pass together with every earlier test (314 today, including §77–81 and V2 scenarios 1–10). Every increment also proves:

- denied paths;
- program segregation in screens, reports, exports and the Analyst;
- audit entries with reasons;
- idempotent retries.

The brief's invariants (BX) each become a named test in the increment that introduces them.

| Increment | Must be proven before it counts as done |
| --- | --- |
| V3.1 | A blank determination never reads as not applicable. Stock on hand, available, allocated and at point of use can all differ for the same lot. Existing stock migrates with every quantity unchanged |
| V3.2 | Moving an instance keeps its identity. Splitting 1000 into 900 + 70 + 30 gives three children whose sum equals the parent. A merge outside policy is refused |
| V3.3 | Movement does not alter genealogy. A remnant is ordinary inventory: it can be stored, transferred, re-issued and transformed again. Supplier lot → master roll → remnant → second slitting → component lot → finished serial → shipment traces both ways. Unexplained material is flagged, never scrapped. A return does not imply rejection |
| V3.4 | Required, requested, allocated, picked, delivered, issued and consumed differ in one partial-fulfilment scenario. Stock in the warehouse but not at the machine gives a material-flow constraint, not a shortage |
| V3.5 | Produced is not usable until accepted and released. Rework adds history and never rewrites it. Changeover and setup reduce capacity separately from breakdowns |
| V3.6 | Age alone never blocks. An expired lot is not offered under FEFO. A shelf-life extension without authority is refused, and an authorized one keeps the original date and evidence |
| V3.7 | Behind plan but recoverable is not goal at risk; goal at risk is reported without an intermediate miss; the same input gives the same answer. A production goal and a delivery goal can disagree |
| V3.8 | Expired calibration blocks an inspection where policy requires it. A gauge valid at the time of a past inspection stays valid in that history. A blade out for sharpening is not available |
| V3.9 | Machine ready, material ready, operator qualified, gauge expired: the answer is "not executable", with the reason. A scheduled employee is never counted as present |
| V3.10 | A hold blocks the configured downstream activity. The person who placed a hold cannot release it without release authority |
| V3.11 | Partial acceptance of a receipt. FAI pending determination is never shown as complete. A replacement from an RTV is a new instance, and the rejected one keeps its history |
| V3.12 | An attached CoC does not complete the structured fields, and complete fields do not remove a required signed document |
| V3.13 | The RFQ package is the applicable revision, not the latest. A quote is not supply. A proposed alternate in a quote is not approved |
| V3.14 | Material at a processor is owned but not in custody, and it returns with the same identity. Missing packaging blocks shipment readiness |
| V3.15 | Today's roles reproduce every current permission exactly. Delegation expires on its own. A separation-of-duty rule set by policy is enforced |
| V3.16 | Authentication does not bypass qualification. A step-up action without fresh assurance is refused. A locked terminal session cannot act as the previous user |
| V3.17 | A priority change without a reason or authority is refused. The preview shows which orders it delays. Priority never moves work past a safety or quality block |
| V3.18 | Handoff pulls machine status without re-entry. Acknowledging an item does not complete it |
| V3.19 | A failed notification does not reverse the production transaction. A deep link without access shows nothing. One condition raises one alert. A cleared condition sends a resolution notice |
| V3.20 | The inbox shows only items the reader may decide, never their own requests. Assigned, acknowledged, in progress and completed are distinct |
| V3.21 | Each detector fires on a seeded impossible state and stays silent on the reference factories. No label implies misconduct |
| V3.22 | §54's chain end to end, with each step linked. A correlated constraint is never stated as the cause |
| V3.23 | A scenario does not mutate production state until committed. Each of the 15 recovery options gives feasibility and consequences |
| V3.24 | Support time never counts as direct labor |
| V3.25 | The -7d view shows the old projection of an order re-planned since. Every lens agrees on counts |
| V3.26 | Each new question answers from services with the asker's access |
| V3.27 | All three reference factories at hundreds of records answer the brief's BN questions. One undescribed workflow is modelled without a second system |
| V3.28 | Budgets met at reference-factory scale. A restored backup passes the full suite |

## Decisions needed from you

There are twelve decisions, each with a recommendation. Approving the plan as written accepts all twelve. The first four are conflicts between the brief and the code.

| # | Decision | Recommendation | Alternative |
| --- | --- | --- | --- |
| 1 | One stock status today mixes quality with availability (brief H) | Split it into quality status, availability, allocation and custody in V3.1. Existing stock is migrated and every quantity is proven unchanged | Keep one status and add fields beside it. Less migration, but the brief's distinctions stay blurred |
| 2 | Gauges carry only a due date (brief Z) | Move them into the V3.8 tool model; past inspections keep the validity they had | Keep gauges and add a second tool system, which the brief forbids |
| 3 | Who owns PTO and absence (brief AB, BT) | JIRSN Core when it exists. Forge keeps who is unavailable and when (no reasons, as today), fed by Core | Forge stays the place PTO is entered, which duplicates Core |
| 4 | Roles are fixed in code (brief AG) | Roles become data in V3.15, seeded with today's roles so nobody's access changes | Keep fixed roles and add scopes only, which does not meet AG |
| 5 | The paused goal-risk slice (engine, tests, ADR 0020) | I review it and take it over in V3.7; the other tool stays paused | Leave it with the other tool. V3.7 then waits for it |
| 6 | Do scenarios persist? | Yes: named and private, shared on request, in their own schema | Throwaway previews only |
| 7 | Notification channels first | In-app and email through a provider abstraction (brief AX). Teams, SMS and webhooks later | In-app only first |
| 8 | Default for training on controlled documents | Warn, switchable to enforce per document type | Enforce from day one |
| 9 | Terminals when the server is unreachable | They refuse new starts and show the outage; nothing is back-dated | A local queue replayed later, with safety and qualification checked late |
| 10 | Contract-level access | Optional per contract; program access applies otherwise | Program access only |
| 11 | Identity from Nexus | OIDC plus SCIM deactivation and step-up through the provider's assurance level. Roles stay in Forge | Roles from Nexus groups, which splits manufacturing authority |
| 12 | Persistence technology | Keep raw SQL with per-module migrations (brief BV rules 4 and 8) | Move to EF Core, which means rebuilding working persistence |
