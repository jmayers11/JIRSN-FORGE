# V3 architecture impact review

Updated 2026-10-11 by Codex against the supplied governance expansion and the local working tree. The supplied `JIRSN Forge V3 Plan.md` now provides the complete numbered roadmap and acceptance gates. This document is a review, not a claim that the missing features are implemented.

## Current foundation

Forge is a .NET modular monolith with bounded-context schemas, Npgsql stores, deterministic Planning engines, plain JavaScript UI, one command pipeline, one append-only audit/event/outbox path, and real PostgreSQL API integration tests. Extend these components. Context interactions remain through contracts; host target resolvers handle cross-context entity lookup.

V3.1–V3.9 are committed, with V3.9 full verification PASS at `411c3c9`. V3.10 is applied, its quick/API gates passed and its full regression is running. V3.11 is reviewed/staged, and V3.12 has an unapplied evidence draft. Neither packages nor staged fixes are completed releases.

## Identity and authentication assurance (brief 2–6, 11–14)

1. Existing: Host `Program.cs` configures JWT bearer authentication with authority/audience; development identity is explicitly separate. Kiosk badge/PIN identifies operators and training signatures.
2. Reuse: ASP.NET authentication, RequestPrincipal, command context and audit; manufacturing person remains mapped to identity subject.
3. Partial: token validation exists, but interactive sign-in, assurance and identity lifecycle integration are incomplete.
4. Missing: Nexus federation contract, assurance evidence, step-up challenge/return, signature binding and shared-browser lock/switch protection.
5. Owner: Nexus owns credentials, federation and authentication; Forge owns assurance requirements for manufacturing actions. Core owns employment administration; Sentinel owns security assessment.
6–9. Changes: add validated authentication evidence/session context and per-action policy; extend host authentication and command guards, step-up metadata API, explicit signer UI. Do not add password tables or a custom token issuer.
10–13. Events/UI/security/audit: record identity subject, issuer, authentication time/method/assurance, action/version/meaning and result, never credentials or token bodies. Browser lock clears active actor; assurance cannot be supplied by an untrusted request field.
14–16. Integration/failure/performance: accept configured standard issuer assertions; reject inadequate/expired assurance, prevent consequential actions during unverified step-up, use framework metadata/key caching. No speculative offline signoff.
17–20. Migration/tests/dependencies/decisions: migrate policy with compatible defaults for existing dev tests; test invalid issuer/audience, expired proof, stale signoff version, switching and Nexus outage. Product must configure issuer, audience, assurance vocabulary and permitted action age; a real Nexus issuer is an external dependency, not presumed available from its UI prototype.

## Authorization and signoff governance (7–10, 15–16, 32)

1–4. Existing: `ForgeAuthorizer` combines action permission, active identity, program membership and markings. Roles/permissions are currently a static catalog; separation rules are embedded in commands. Missing configurable role definitions, operational scopes, expiring delegation and policy-driven separation/signoff.
5. Owner: Kernel is the shared enforcement owner; business contexts determine relevant resource scope/qualification and authoritative record state.
6–9. Entities/DB/services/API: versioned role/action mappings, scope grants, delegation records and consequential-action policy in kernel schema; authorizer resolves effective grants at action time. Existing permission vocabulary becomes seeded defaults. Management commands use the same pipeline; contextual denials retain explanation.
10–13. Events/UI/authorization/audit: permission/policy/delegation events, administration UI and effective-authority explanation. Expiry never edits the base role. Prevent delegating powers outside the grantor's authority; enforce restricted records on reads and mutations. Capture policy/version used by approvals.
14–16. Integration/failure/performance: verified Nexus identity is only an input. Unknown authority fails closed. Resolve grants once per request/snapshot and invalidate caches on changes; never cache past delegation expiry.
17–20. Migration/tests/dependencies/decisions: preserve seeded existing permissions and rules while migrating policy; test time/scope bounds, escalation, separation, revoked program access and idempotent retries. Assurance precedes controlled signature support. Product selects local policy; supplied examples are not universal roles or separation requirements.

## Tooling (17–21)

1–4. Existing: Resources tool types/tools, calibration/service/use events and historical `ToolValidity`; Quality gauges reference tools. V3.9 readiness uses required tool counts. Partial: certificate evidence is presently text; configurable state vocabulary and full metadata/reservation coverage need assessment.
5. Owner: Resources owns tool lifecycle; Quality owns inspection result; Engineering owns controlled documents; business evidence must not become a controlled instruction.
6–9. Entities/DB/services/API: extend the existing tool and tool-event records for required metadata, inspection, reservation/custody and evidence links; keep history and use ledger.
10–13. Events/UI/authorization/audit: emit tool lifecycle events, surface due/service readiness explanations, require tool-management authority and preserve historical usage evidence. Expired today does not invalidate a previously valid use.
14–16. Integration/failure/performance: external labs provide evidence only; unavailable evidence never manufactures a passing result. Batch historical validity per snapshot; avoid repeated whole-plant tool scans.
17–20. Migration/tests/dependencies/decisions: additive migrations, historical-validity regression tests and acceptance A/B; decide required tool metadata and per-type inspection/service rules. Notifications depend on shared event subscriptions, not tool-specific email code.

## Shift continuity and closeout (22–27, 41–43)

1–4. Existing: calendars, shifts, presence, direct labor, production reports, material ledger/tasks, downtime, quality and maintenance records; Analyst shift brief. Missing persistent pass-on/checklist versions, review/acknowledgment/actions and reconciliation closeout.
5. Owner: Execution owns handoff/pass-on and operational closeout; Planning composes read models; each original domain retains its problem/action state.
6–9. Entities/DB/services/API: handoff snapshots with source references and as-of time, contextual notes, work-center checklist versions, acknowledgments and explicit actions; closeout exceptions reference authoritative ledger/report evidence. Services derive facts rather than rewriting them; expose create/review/acknowledge/closeout APIs.
10–13. Events/UI/authorization/audit: handoff/action/exception events, shift view and incoming acknowledgment UI, scoped supervisor/operator permissions and immutable acknowledgments. Acknowledgment never closes a maintenance WO or quality hold.
14–16. Integration/failure/performance: notifications subscribe; missing source data is reported as unknown. Read one consistent snapshot, include only permitted records, aggregate by shift/timezone and paginate sources.
17–20. Migration/tests/dependencies/decisions: additive tables; test G/K, inherited unresolved actions, DST boundaries, duplicate closeout and missing identity/signoff. Requires shared evidence, notifications and scoped authorization; product configures checklist and reconciliation tolerances. Core time authority must not be duplicated.

## Notifications and alerting (V3.19)

1–4. Existing: transactional event outbox/log, at-least-once subscription checkpoints, current constraints, dynamic role/program authorization and the application bell. Missing: configurable event-condition rules, alert history/acknowledgment, resolution notices, recipient/channel delivery state and email provider boundary.
5. Owner: Kernel event log remains the integration spine; Host notification subscriber owns alert projections and delivery; source contexts retain operational state and resolution authority.
6–9. Entities/DB/services/API: additive rule, alert, inbox and delivery records. Rules select event and clear-event types, simple payload equality, global/program/work-center/entity scope, severity, separate confidence, recipient and escalation roles, channels, templates, local deep link, delay, reminder, escalation, IANA quiet hours and deduplication. Expose inbox, acknowledgment, rule management and available event types.
10–13. Events/UI/authorization/audit: rule changes and acknowledgments use the command pipeline; the subscriber is idempotent under replay. Each inbox read and acknowledgment rechecks active roles, program membership and markings. In-app alerts and header count link to notification UI. Deep links are app-local navigation only and confer no authority.
14–16. Integration/failure/performance: mail dispatch is a separate leased worker with provider idempotency keys and bounded retries. Provider failure updates delivery state only; it cannot roll back the triggering source command. Quiet hours defer delivery in the configured IANA zone. A configured clear event updates the alert and emits a resolution notice; resolution acknowledgment is distinct from initial acknowledgment.
17–20. Migration/tests/dependencies/decisions: additive Kernel migration; test rule scope, duplicate source-event replay, current access revocation, acknowledgment/resolution, delayed email failure and migration idempotency. Email address resolution and transport stay behind an HTTPS provider configured by deployment; no mail credential or address is stored in Forge.

## Approval inbox and assignments (V3.20)

1–4. Existing: each bounded context owns its authoritative approval state and second-person rules; the Host can read the shared PostgreSQL schemas and resolve the current Forge principal. Missing: one access-filtered view of pending decisions and a common lifecycle for explicitly assigned follow-up work.
5. Owner: source contexts retain decisions and separation of duties; Kernel owns assignment records/history; Host composes the read-only approval projection and exposes assignment actions.
6–9. Entities/DB/services/API: additive kernel assignment and history tables; inbox projects pending NCR, schedule, purchasing, project, outage, document, engineering-review, alternate and manufacturer-part decisions. It checks each required permission and excludes the requester's actor ID. Assignment types cover NCRs, inspections, work orders, RFQs, project reviews and material tasks; assignment metadata never changes source state.
10–13. Events/UI/authorization/audit: `/api/work-inbox` backs My Work. Supervisors and Security Administrators assign active Forge people with a reason; assignees alone move Assigned → Acknowledged → InProgress → Completed, each with history and a reason. Source links still invoke their ordinary APIs and permissions.
14–16. Integration/failure/performance: unsupported links/types, inactive assignees, missing source records, self-approval and skipped status transitions are rejected. Queries are bounded at 500 items; assignment status does not approve or complete the source record.
17–20. Migration/tests/dependencies/decisions: migration 0005 is additive; PostgreSQL acceptance covers self-exclusion, sequential lifecycle and source-state separation. Current approval rows include the mapped source schemas; adding another approval flow requires mapping its source-specific requester, status and permission.

## Data-integrity exceptions (V3.21)

1–4. Existing: inventory append-only ledger and balance view, transformation records, asset history, and tool calibration/use events. Missing: one evidence-first read surface for impossible cross-record states and historical validity exceptions.
5. Owner: source domains remain authoritative; Host composes read-only findings from their records. No exception workflow rewrites inventory, asset, transformation or tool history.
6–9. Entities/DB/services/API: no new source table; `GET /api/integrity/exceptions` detects negative stock, duplicate asset serials, consumption without a prior matching issue and use after recorded calibration due date.
10–13. Events/UI/authorization/audit: Data Integrity displays evidence, source and observed time. Each result uses the source-domain read permission and a neutral DataOrProcessException classification. Findings are not allegations or automatically assigned work.
14–16. Integration/failure/performance: the read is bounded to 500 rows. Impossible states are injected in isolated test PostgreSQL fixtures; healthy reference data returns empty. The view neither repairs data nor bypasses source APIs.
17–20. Migration/tests/dependencies/decisions: no migration required. V3.21 acceptance seeds every detector and verifies silence on the engineered reference plant. Any source-specific repair retains its normal owner and audit rules.

## Priority and generalized holds (28–32)

1–4. Existing: priorities influence deterministic scheduling; V3.9 production holds and separate quality/stock mechanisms. V3.10 package centralizes explicit holds and configures blocked activities/release authority; missing controlled priority proposals/impact, general downstream hold impact and signoff policies.
5. Owner: Kernel owns shared hold/policy gate; original contexts keep native quality state; Planning computes consequences; relevant contexts own priority changes.
6–9. Entities/DB/services/API: migrate V3.9 holds into kernel, add auditable proposal/decision history, reuse scheduler scenarios for impact and existing holds API/register. Do not create shadow NCR or stock balances.
10–13. Events/UI/authorization/audit: authoritative hold/priority events; display old/new priority, reason, impacts and decisions. Scope release and preview data by program. Identity of placer alone neither grants nor denies release authority.
14–16. Integration/failure/performance: assurance guards apply where configured; any gate error fails safely. Batch hold lookups and downstream references. Preview must identify source version/as-of and reject stale consequential commit where required.
17–20. Migration/tests/dependencies/decisions: retain migrated ids/history, test H/I and all blocked activity paths plus unauthorized releases. Product configures hold-kind activities, priority authority and approval thresholds. Staged V3.10 fixes address missing inspection register operations and unscoped program hold release.

## Downtime and changeovers (33–35)

1–4. Existing: Maintenance planned/unplanned outages and Execution stoppages/reasons/production actuals; capacity/calendar and goals use factual records. Missing planned-versus-actual changeover model and explicit complete impact coverage.
5. Owner: Execution owns observed operational stoppage/setup; Maintenance owns repair/outage lifecycle; Planning consumes both without double-counting.
6–9. Entities/DB/services/API: enrich existing stoppage categories and linked setup/changeover facts; planned requirements/durations in Engineering/Resources; APIs record observations and compare projections.
10–13. Events/UI/authorization/audit: downtime/setup events, time-line and deviation UI, scoped reporting and source-linked audit. Stated reason remains observation, not inferred root cause.
14–16. Integration/failure/performance: one capacity timeline unions overlapping downtime; unknown end stays open. Notifications/recovery consume authoritative events; interval calculations must remain deterministic and bounded.
17–20. Migration/tests/dependencies/decisions: additive categories/history; test L, overlap, machine alternatives, mid-shift failure and changeover variance. Product selects categories and how historical duration affects future estimates; explanations must distinguish facts from forecasts.

## Outside processing and evidence (36–40)

1–4. Existing: Inventory custody/location/identity ledger and transformation/genealogy, Procurement orders/technical packages, controlled documents, Quality and prepared V3.11 receiving/FAI/returns. Missing external-process lifecycle, dispatch/return quantity reconciliation and shared business-record evidence.
5. Owner: Inventory owns physical identity, quantity/custody and ledger; Procurement owns processor purchase/service commitment; Engineering owns required definition; Quality owns acceptance. Shipping a lot does not transfer ownership implicitly.
6–9. Entities/DB/services/API: external operation links existing order/lot/PO/specification, dispatch/return ledger movement and required certifications; reusable business-evidence metadata/content references separate from controlled documents. Same identity continues for movement; actual transformation records inputs/outputs.
10–13. Events/UI/authorization/audit: dispatch/return/discrepancy/acceptance events, processor custody and readiness UI; check supplier/technical/package/hold/authorization gates and capture quantity/configuration history.
14–16. Integration/failure/performance: processor updates are evidence, not automatic quality release. Missing certification blocks required release. Reuse balances and indexed instance relations; no second external inventory system.
17–20. Migration/tests/dependencies/decisions: additive process/evidence relations; test J, partial returns, excessive returns, certification and transformation versus movement. V3.12 business evidence is the next known base-plan task. Product defines same/new instance criteria, required certificates, ownership and export/contract policy.

## Recovery, communications and integrity (43–47)

1–4. Existing: deterministic capacity/scheduler/material/goal engines, Analyst machine/person what-if and event subscriptions/outbox. Missing generalized configurable recovery proposals, notifications/delivery rules and broad reconciliation exception detection.
5. Owner: Planning owns projections and scenario evaluation; Kernel events remain the integration spine; notification delivery is a read-side consumer, source domains own outcomes.
6–9. Entities/DB/services/API: scenario inputs/results/fingerprints and explicit decision, notification rule/delivery/dedup records and exception observations with authoritative references. Reuse original commands to commit human choices; never have notification delivery mutate a business record.
10–13. Events/UI/authorization/audit: use domain events, source-linked exception and option comparison screens; filter recipient visibility, scope approvals and record decision/evidence. No automatic consequential option selection or accusation of misconduct.
14–16. Integration/failure/performance: provider adapters outside domains; retry outbox deliveries idempotently, surface provider failure while keeping domain state valid. Bound scenario horizons/options; deduplicate notifications and persist evaluated as-of/version.
17–20. Migration/tests/dependencies/decisions: additive subscriber/rule/scenario/exception tables; test retries, unknown quantities, false positives, unauthorized recipients and L. Product selects channels, escalation/tolerances and consequential commit policies. External email/identity credentials remain deployment configuration, never source.

## Architectural challenge: consigned customer fixtures used for repair

A customer sends a serialized fixture for repair and temporary use in production, then requests its return. This workflow is not enumerated in the brief. Inventory records customer ownership, physical custody/location and serial identity in the existing ledger; Resources registers the operational tool against that material instance; Maintenance owns repair and service evidence; Quality owns acceptance/inspection; Engineering supplies applicable instructions; Kernel authorization/holds gate use and return. A repair that preserves identity is recorded as lifecycle/maintenance history, not fabricated transformation. If a component is replaced, existing genealogy/installed-component records describe that fact. Procurement records external service only if needed. Event subscriptions communicate state changes without a second workflow engine, inventory, genealogy, authorization or audit path. Shared evidence links the same records. This challenges the design to separate ownership, custody, material identity and operational resource eligibility without duplicating authoritative quantities.

## Completion and decisions

This review exposes dependencies; it does not silently decide enterprise assurance levels, export rules, signature meanings or organization-specific approvals. Seed current behavior and make policy configurable where required. Base V3.12–V3.28 mapping must be reconciled with the Product Owner's original numbered plan before calling all of V3 complete. Each increment needs source/API/UI/report/Analyst coverage where applicable, migrations, ADR, meaningful tests and full verification before release.

